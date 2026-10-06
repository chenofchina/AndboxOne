using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using AndboxOne.Core;

namespace AndboxOne;

/// <summary>
/// 主窗口 —— 三栏暗黑极客布局：
///   左：多开管理器（虚拟机卡片列表，实时 CPU/内存）
///   中：核心操作区（启动/Root/安装 APK/日志/网络诊断）
///   右：高级配置面板（DeviceProfile 编辑 / 自定义机型 / 网络配置 / JSON 导入导出 / 安装报告）
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>UITest 自检模式：不弹任何对话框（由 App 设置）。</summary>
    public static bool UiTestMode = false;

    private readonly EnvironmentManager _env = new();
    private readonly NetworkManager _net = new();
    private readonly ObservableCollection<VmInstance> _vms = new();
    private readonly EmulatorOrchestrator _orch;
    private LogFileSink? _logSink;

    private VmInstance? _activeVm;
    private DeviceProfile _workProfile = new();
    private ApkInstallInfo? _lastReport;
    private bool _suspendSync;
    private bool _initDone;

    private readonly ConcurrentQueue<(string Line, LogLevel Level)> _logQueue = new();
    private readonly DispatcherTimer _logPump;

    public MainWindow()
    {
        InitializeComponent();
        _orch = new EmulatorOrchestrator(_env);

        DataContext = _vms;
        _vms.CollectionChanged += (_, _) => VmCountText.Text = $"{_vms.Count} 实例";

        EventBus.LogEmitted += (line, level) => _logQueue.Enqueue((line, level));
        _logPump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _logPump.Tick += (_, _) => FlushLog();
        _logPump.Start();

        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _orch.Dispose();

        // 配置面板联动事件
        PresetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        DpiCombo.SelectionChanged += (_, _) => SyncPanelToProfile();
        RamCombo.SelectionChanged += (_, _) => SyncPanelToProfile();
        GpuCombo.SelectionChanged += (_, _) => SyncPanelToProfile();
        GameTuningCombo.SelectionChanged += GameTuningCombo_SelectionChanged;
        NetNatRadio.Checked += NetMode_Changed;
        NetBridgeRadio.Checked += NetMode_Changed;
    }

    // ================================================================
    //  启动流程
    // ================================================================

    private async Task InitializeAsync()
    {
        if (_initDone) return; // Loaded 可能多次引发（模态框开关等），初始化严格幂等
        _initDone = true;

        InitCombos();

        try
        {
            _logSink = new LogFileSink(Path.Combine(_env.LogsDir, $"andboxone-{DateTime.Now:yyyyMMdd}.log"));
        }
        catch { /* 日志文件不可用时忽略 */ }

        _env.Initialize();
        UpdateEnvStatus();

        // ---- 规范：RuntimeSdk 不完整 → 明确错误；完整 → 进入主界面 ----
        if (_env.IsValid)
        {
            string? adbVer = _env.QueryAdbVersion();
            StatusAdb.Text = $"ADB: {adbVer ?? "未知版本"}";
            EventBus.Info("ENV", "RuntimeSdk 完整性校验通过，进入主界面");
        }
        else
        {
            StatusSdk.Text = "SDK: ✗ 不完整";
            StatusAdb.Text = "ADB: ✗";
            EnvStatusText.Text = $"RuntimeSdk 不完整（{_env.Problems.Count} 项缺失）";
            EnvStatusText.Foreground = FindResource("Danger") as Brush ?? Brushes.Red;
            BtnStart.IsEnabled = BtnStartAll.IsEnabled = BtnCreateVm.IsEnabled = false;
            BtnInstallApk.IsEnabled = BtnRoot.IsEnabled = BtnRootAll.IsEnabled = BtnDiagnose.IsEnabled = false;

            string detail = "RuntimeSdk 不完整，请重新安装 AndboxOne：\n\n" +
                            string.Join("\n", _env.Problems.Select(p => "  • " + p)) +
                            "\n\n（详见 scripts\\prepare_runtimesdk.ps1 与 README）";
            EventBus.Error("ENV", detail);
            if (!UiTestMode)
                MessageBox.Show(this, detail, "AndboxOne · 环境校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _orch.LoadVms();
        foreach (VmInstance vm in _orch.Vms) _vms.Add(vm);

        if (_env.CoreBinariesOk)
            await _orch.AttachExternalVmsAsync(); // 附加上次会话遗留的运行实例

        _orch.StartSampling();
        LoadWorkProfileIntoPanel(_workProfile);

        EventBus.Info("UI", "AndboxOne 就绪 · 拖入 .apk 即可安装 · 无账号无广告无遥测");
    }

    private void UpdateEnvStatus()
    {
        StatusSdk.Text = _env.IsValid ? "SDK: ✓ 完整" : $"SDK: ✗ 缺失 {_env.Problems.Count} 项";
        if (_env.IsValid)
        {
            EnvLed.Fill = FindResource("LedOnline") as Brush ?? Brushes.Green;
            EnvStatusText.Text = "ENV READY · 离线引擎就绪";
            EnvStatusText.Foreground = FindResource("Accent") as Brush ?? Brushes.Green;
        }
    }

    private void InitCombos()
    {
        foreach (string name in DeviceProfile.Presets().Keys) PresetCombo.Items.Add(name);
        PresetCombo.Items.Add("（自定义）");

        foreach (int dpi in new[] { 120, 160, 240, 320, 440, 480, 560, 640 })
            DpiCombo.Items.Add(dpi.ToString());

        foreach (int ram in new[] { 1024, 2048, 3072, 4096, 6144, 8192, 12288 })
            RamCombo.Items.Add($"{ram} MB");

        foreach (string gpu in new[] { "auto", "host", "angle_indirect", "swiftshader_indirect" })
            GpuCombo.Items.Add(gpu);

        foreach (GameTuning.Tuning t in GameTuning.All)
            GameTuningCombo.Items.Add(t);
        GameTuningCombo.SelectedIndex = 0;
    }

    // ================================================================
    //  日志泵（EventBus → RichTextBox，荧光绿/红/黄分色）
    // ================================================================

    private void FlushLog()
    {
        if (_logQueue.IsEmpty) return;
        int processed = 0;

        while (processed < 300 && _logQueue.TryDequeue(out (string Line, LogLevel Level) item))
        {
            AppendLogLine(item.Line, item.Level);
            processed++;
        }

        if (processed == 0) return;

        // 截断：最多保留 800 段，防止长时间多开撑爆内存
        FlowDocument doc = LogBox.Document;
        while (doc.Blocks.Count > 800)
            doc.Blocks.Remove(doc.Blocks.FirstBlock);
        LogBox.ScrollToEnd();
    }

    private void AppendLogLine(string line, LogLevel level)
    {
        Brush brush = level switch
        {
            LogLevel.Error => FindResource("Danger") as Brush ?? Brushes.Red,
            LogLevel.Warn => FindResource("Amber") as Brush ?? Brushes.Orange,
            LogLevel.Debug => FindResource("TextFaint") as Brush ?? Brushes.Gray,
            _ => line.Contains("[EMU]") || line.Contains("[ADB]")
                ? FindResource("TextPrimary") as Brush ?? Brushes.White
                : FindResource("Accent") as Brush ?? Brushes.Green,
        };

        Paragraph p = new(new Run(line) { Foreground = brush });
        LogBox.Document.Blocks.Add(p);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    // ================================================================
    //  卡片选择 / 删除
    // ================================================================

    private void MenuDeleteVm_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VmInstance vm) return;
        if (MessageBox.Show(this, $"确定删除虚拟机 {vm.AvdName}？其数据分区将被清空。",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _orch.DeleteAvd(vm);
            _vms.Remove(vm);
            if (_activeVm == vm) SetActiveVm(null);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SetActiveVm(VmInstance? vm)
    {
        _activeVm = vm;
        ActiveVmRun.Text = vm is null ? "（未选中实例）" : $"（当前：{vm.AvdName}）";
        if (vm is not null)
        {
            _workProfile = vm.Profile.Clone();
            LoadWorkProfileIntoPanel(_workProfile);
            EventBus.Debug("UI", $"选中实例: {vm.AvdName}");
        }
    }

    /// <summary>从鼠标位置选中卡片（挂到整窗 PreviewMouseDown）。</summary>
    private VmInstance? HitTestVmCard(MouseButtonEventArgs e)
    {
        // RichTextBox 内部的 Run/FlowDocument 是 ContentElement 而非 Visual，
        // 必须按节点类型选择正确的树遍历方式，否则点击日志区会抛 InvalidOperationException。
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not Window)
        {
            if (d is Border bd && bd.Tag is VmInstance vm) return vm;
            d = d switch
            {
                TextElement te => te.Parent, // Run/Paragraph 等富文本元素（ContentElement 主体）
                Visual or System.Windows.Media.Media3D.Visual3D => System.Windows.Media.VisualTreeHelper.GetParent(d),
                _ => System.Windows.LogicalTreeHelper.GetParent(d),
            };
        }
        return null;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        VmInstance? vm = HitTestVmCard(e);
        if (vm is not null && !ReferenceEquals(vm, _activeVm)) SetActiveVm(vm);
    }

    // ================================================================
    //  新建 / 刷新
    // ================================================================

    private void BtnCreateVm_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateVmDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            VmInstance vm = _orch.CreateAvd(dlg.AvdName, dlg.SelectedProfile);
            _vms.Add(vm);
            SetActiveVm(vm);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        EventBus.Info("UI", "手动刷新：重新扫描 AVD 目录并附加外部实例…");
        _orch.LoadVms();
        _vms.Clear();
        foreach (VmInstance vm in _orch.Vms) _vms.Add(vm);
        if (_env.CoreBinariesOk) await _orch.AttachExternalVmsAsync();
        SetActiveVm(null);
    }

    // ================================================================
    //  单体 / 批量操作
    // ================================================================

    private List<VmInstance> Targets()
    {
        var checked_ = _vms.Where(v => v.IsChecked).ToList();
        return checked_.Count > 0 ? checked_ : _vms.ToList();
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVm is null) { EventBus.Warn("UI", "请先在左侧点击选择一个虚拟机"); return; }
        try { await _orch.StartVmAsync(_activeVm, _net); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVm is null) return;
        try { await _orch.StopVmAsync(_activeVm); }
        catch (Exception ex) { EventBus.Error("UI", $"关闭失败: {ex.Message}"); }
    }

    private async void BtnRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVm is null) return;
        await _orch.RootVmAsync(_activeVm);
    }

    private async void BtnStartAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = Targets().Where(v => !v.IsRunning).ToList();
        if (targets.Count == 0) { EventBus.Warn("UI", "没有可启动的实例"); return; }
        EventBus.Info("UI", $"批量启动 {targets.Count} 台: {string.Join(", ", targets.Select(t => t.AvdName))}");
        await _orch.StartManyAsync(targets, _net);
    }

    private async void BtnStopAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = Targets().Where(v => v.IsRunning).ToList();
        if (targets.Count == 0) { EventBus.Warn("UI", "没有运行中的实例"); return; }
        EventBus.Info("UI", $"批量关闭 {targets.Count} 台");
        await _orch.StopManyAsync(targets);
    }

    private async void BtnRootAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = Targets().Where(v => v.State == VmState.Online).ToList();
        if (targets.Count == 0) { EventBus.Warn("UI", "没有在线的实例（Root 需先启动完成）"); return; }
        EventBus.Info("UI", $"批量 Root {targets.Count} 台");
        await _orch.RootManyAsync(targets);
    }

    // ================================================================
    //  APK 安装（按钮 + 拖拽）
    // ================================================================

    private async void BtnInstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVm is null) { EventBus.Warn("UI", "请先选择一个在线的虚拟机"); return; }
        var dlg = new OpenFileDialog { Filter = "Android 应用包|*.apk|所有文件|*.*", Title = "选择要安装的 APK" };
        if (dlg.ShowDialog(this) != true) return;
        await InstallApkFlow(_activeVm, dlg.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DragOverlay.Visibility = Visibility.Visible;
            DragTargetText.Text = _activeVm is null
                ? "⚠ 未选中虚拟机——请先在左侧点击一个卡片"
                : $"安装到: {_activeVm.AvdName}（{_activeVm.StateText}）";
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DragOverlay.Visibility = Visibility.Collapsed;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (_activeVm is null) { EventBus.Error("INSTALL", "拖拽安装失败：未选中虚拟机"); return; }

        foreach (string f in files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            await InstallApkFlow(_activeVm, f);
    }

    private async Task InstallApkFlow(VmInstance vm, string apkPath)
    {
        ApkInstallInfo? report = await _orch.InstallApkAsync(vm, apkPath);
        if (report is null) return;

        _lastReport = report;
        ReportMetaText.Text = $"最近安装: {report.Package} v{report.VersionName} @ {report.TargetDevice}";
        ReportBrowser.NavigateToString(InstallReportGenerator.GenerateHtml(report));
        EventBus.Info("REPORT", $"安装报告已渲染到右侧面板（{report.Permissions.Count} 项权限）");
    }

    // ================================================================
    //  网络诊断
    // ================================================================

    private void BtnTileWindows_Click(object sender, RoutedEventArgs e)
    {
        EventBus.Info("UI", "窗口归位：扫描全部运行中实例的模拟器窗口…");
        _orch.RepositionAllWindows();
    }

    private async void BtnDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVm is null || _activeVm.State != VmState.Online)
        {
            EventBus.Warn("NET", "请先选择一个在线的虚拟机再执行诊断");
            NetDiagSummary.Text = "⚠ 无在线实例";
            return;
        }
        BtnDiagnose.IsEnabled = false;
        NetDiagSummary.Text = "诊断中…";
        try
        {
            NetworkDiagnosis d = await _net.DiagnoseAsync(_orch, _activeVm.Serial);
            NetDiagSummary.Text = d.Success
                ? $"✓ 收发 {d.Received}/{d.Sent} · 丢包 {d.LossPercent:0}% · 平均 {d.AvgLatencyMs:0.#} ms"
                : "✗ 网络不可达";
            NetDiagDetail.Text = string.IsNullOrWhiteSpace(d.RawOutput) ? d.FallbackNote : d.RawOutput.Trim();
        }
        finally { BtnDiagnose.IsEnabled = true; }
    }

    private void NetMode_Changed(object sender, RoutedEventArgs e)
    {
        _net.Mode = NetBridgeRadio.IsChecked == true ? NetworkMode.Bridge : NetworkMode.Nat;
        EventBus.Info("NET", $"网络策略切换为 {_net.Mode}（下次启动生效）");
    }

    // ================================================================
    //  设备配置面板（DeviceProfile 编辑 / 自定义机型 / 实时预览）
    // ================================================================

    private void LoadWorkProfileIntoPanel(DeviceProfile p)
    {
        _suspendSync = true;
        try
        {
            ProfileNameBox.Text = p.Name;
            ProfileVendorBox.Text = p.Manufacturer;
            ProfileModelBox.Text = p.Model;
            WidthSlider.Value = p.Width;
            HeightSlider.Value = p.Height;
            WidthBox.Text = p.Width.ToString();
            HeightBox.Text = p.Height.ToString();
            CpuSlider.Value = p.CpuCores;
            CpuBox.Text = p.CpuCores.ToString();
            DpiCombo.SelectedItem = p.Dpi.ToString();
            RamCombo.SelectedItem = $"{p.RamMb} MB";
            GpuCombo.SelectedItem = p.GpuMode;
            HeadlessCheck.IsChecked = p.Headless;
            NoSnapshotCheck.IsChecked = p.NoSnapshot;
            PresetCombo.SelectedItem = DeviceProfile.Presets().ContainsKey(p.Name)
                ? p.Name
                : PresetCombo.Items.Count > 0 ? PresetCombo.Items[^1] : null; // 末项为"（自定义）"
            UpdatePreview();
            ProfileSummary.Text = p.ToString();
        }
        finally { _suspendSync = false; }
    }

    private void SyncPanelToProfile()
    {
        if (_suspendSync) return;
        _suspendSync = true;
        try
        {
            _workProfile.Name = ProfileNameBox.Text;
            _workProfile.Manufacturer = ProfileVendorBox.Text;
            _workProfile.Model = ProfileModelBox.Text;
            _workProfile.Width = ParseInt(WidthBox.Text, _workProfile.Width);
            _workProfile.Height = ParseInt(HeightBox.Text, _workProfile.Height);
            _workProfile.CpuCores = ParseInt(CpuBox.Text, _workProfile.CpuCores);
            _workProfile.Dpi = ParseInt(DpiCombo.SelectedItem as string ?? "", _workProfile.Dpi);
            _workProfile.RamMb = ParseInt((RamCombo.SelectedItem as string ?? "").Replace(" MB", ""), _workProfile.RamMb);
            _workProfile.GpuMode = GpuCombo.SelectedItem as string ?? _workProfile.GpuMode;
            _workProfile.Headless = HeadlessCheck.IsChecked == true;
            _workProfile.NoSnapshot = NoSnapshotCheck.IsChecked == true;

            // 反向同步滑块（超出滑块量程时钳制显示）
            WidthSlider.Value = Math.Clamp(_workProfile.Width, WidthSlider.Minimum, WidthSlider.Maximum);
            HeightSlider.Value = Math.Clamp(_workProfile.Height, HeightSlider.Minimum, HeightSlider.Maximum);
            CpuSlider.Value = Math.Clamp(_workProfile.CpuCores, CpuSlider.Minimum, CpuSlider.Maximum);

            UpdatePreview();
            ProfileSummary.Text = _workProfile.ToString();
        }
        finally { _suspendSync = false; }
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s, out int v) ? v : fallback;

    private void UpdatePreview()
    {
        int w = ParseInt(WidthBox.Text, 1080), h = Math.Max(1, ParseInt(HeightBox.Text, 2340));
        double boxH = 130, boxW = Math.Min(220, boxH * w / h);
        PreviewRect.Width = Math.Max(24, boxW);
        PreviewRect.Height = boxH;
        PreviewText.Text = $"{w}×{h} · {ParseInt(DpiCombo.SelectedItem as string ?? "440", 440)}dpi";
    }

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendSync || WidthBox is null) return;
        WidthBox.Text = ((int)e.NewValue).ToString();
        SyncPanelToProfile();
    }

    private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendSync || HeightBox is null) return;
        HeightBox.Text = ((int)e.NewValue).ToString();
        SyncPanelToProfile();
    }

    private void CpuSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendSync || CpuBox is null) return;
        CpuBox.Text = ((int)e.NewValue).ToString();
        SyncPanelToProfile();
    }

    private void ResBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendSync) return;
        SyncPanelToProfile();
    }

    private void Ratio_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        string[] parts = tag.Split(':');
        double ratio = double.Parse(parts[0]) / double.Parse(parts[1]);
        int w = ParseInt(WidthBox.Text, 1080);
        HeightBox.Text = (w / ratio / 8 * 8).ToString("0");
        SyncPanelToProfile();
        EventBus.Debug("UI", $"比例锁定 {tag}: {WidthBox.Text}x{HeightBox.Text}");
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSync || PresetCombo.SelectedItem is not string name) return;
        if (DeviceProfile.Presets().TryGetValue(name, out DeviceProfile? preset))
        {
            _workProfile = preset.Clone();
            LoadWorkProfileIntoPanel(_workProfile);
            EventBus.Info("UI", $"载入预设: {name}");
        }
    }

    private void GameTuningCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameTuningCombo.SelectedItem is GameTuning.Tuning t)
            GameTuningDesc.Text = t.Description;
    }

    private void BtnApplyTuning_Click(object sender, RoutedEventArgs e)
    {
        if (GameTuningCombo.SelectedItem is not GameTuning.Tuning t)
        {
            EventBus.Warn("UI", "请先选择一个兼容模式");
            return;
        }
        SyncPanelToProfile();
        t.Apply(_workProfile);
        LoadWorkProfileIntoPanel(_workProfile);
        EventBus.Info("UI", $"已应用游戏适配「{t.Name}」→ {_workProfile}");
    }

    private void BtnApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        SyncPanelToProfile();
        if (_activeVm is null)
        {
            EventBus.Warn("UI", "请先在左侧选中一个虚拟机，再应用配置");
            return;
        }
        try
        {
            _orch.UpdateAvdConfig(_activeVm, _workProfile.Clone());
            LoadWorkProfileIntoPanel(_activeVm.Profile);
            EventBus.Info("UI", $"配置已写入 {_activeVm.AvdName}（重启后生效）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "应用配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        SyncPanelToProfile();
        var dlg = new SaveFileDialog
        {
            Filter = "AndboxOne 设备配置|*.andboxone.json|JSON|*.json",
            FileName = $"{_workProfile.Name}.andboxone.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, _workProfile.ToJson(), new System.Text.UTF8Encoding(false));
        EventBus.Info("UI", $"设备配置已导出: {dlg.FileName}");
    }

    private void BtnImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "AndboxOne 设备配置|*.andboxone.json|JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _workProfile = DeviceProfile.FromJson(File.ReadAllText(dlg.FileName));
            LoadWorkProfileIntoPanel(_workProfile);
            EventBus.Info("UI", $"设备配置已导入: {dlg.FileName} → {_workProfile}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败: {ex.Message}", "JSON 导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================================================================
    //  安装报告（复制 / 导出 HTML / 桌面快捷方式）
    // ================================================================

    private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) { EventBus.Warn("REPORT", "尚无报告可复制"); return; }
        Clipboard.SetText(InstallReportGenerator.ToPlainText(_lastReport));
        EventBus.Info("REPORT", "报告已复制到剪贴板");
    }

    private void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null) { EventBus.Warn("REPORT", "尚无报告可导出"); return; }
        var dlg = new SaveFileDialog
        {
            Filter = "HTML 报告|*.html",
            FileName = $"install-report-{_lastReport.Package}-{DateTime.Now:yyyyMMdd-HHmmss}.html",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, InstallReportGenerator.GenerateHtml(_lastReport), new System.Text.UTF8Encoding(false));
        EventBus.Info("REPORT", $"报告已导出: {dlg.FileName}");
    }

    private void BtnMakeShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport is null)
        {
            EventBus.Warn("SHORTCUT", "请先安装一个 APK（报告含包名与 Launcher Activity）");
            return;
        }
        try
        {
            string path = ShortcutCreator.CreateDesktopShortcut(
                _env, _activeVm?.AvdName ?? _lastReport.TargetDevice.Split(' ')[0],
                _lastReport.Package, _lastReport.LauncherActivity,
                string.IsNullOrEmpty(_lastReport.Label) ? _lastReport.Package : _lastReport.Label,
                _lastReport.IconPng);
            MessageBox.Show(this, $"快捷方式已生成:\n{path}", "桌面快捷方式", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "快捷方式生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================================================================
    //  退出保护
    // ================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        var running = _vms.Where(v => v.IsManaged && v.IsRunning).ToList();
        if (running.Count == 0 || UiTestMode) return;

        MessageBoxResult r = MessageBox.Show(
            this, $"有 {running.Count} 台由 AndboxOne 启动的虚拟机正在运行。\n\n" +
                  "是 = 关闭全部虚拟机后退出\n否 = 保留虚拟机运行，仅退出控制台\n取消 = 返回",
            "退出确认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes)
        {
            foreach (VmInstance vm in running)
            {
                try { _orch.StopVmAsync(vm).Wait(8000); } catch { /* 退出路径尽力而为 */ }
            }
        }
    }

    // ================================================================
    //  UI 自检（--uitest）：渲染窗口 PNG 供构建验证
    // ================================================================

    public async Task RunUiSelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        await Task.Delay(2500); // 等待初始化与日志渲染

        foreach (string log in new[]
        {
            "[2026-10-06 12:00:00.123] [ORCH] [INFO] 自检样例行：虚拟机编排引擎在线",
            "[2026-10-06 12:00:00.456] [ADB] [INFO] > adb devices",
            "[2026-10-06 12:00:00.789] [INSTALL] [WARN] 拖拽 APK 到窗口即可安装",
            "[2026-10-06 12:00:01.001] [ENV] [ERROR] 这是一条错误样例（RuntimeSdk 不完整时会出现）",
        })
        {
            LogLevel lv = log.Contains("[ERROR]") ? LogLevel.Error : log.Contains("[WARN]") ? LogLevel.Warn : LogLevel.Info;
            AppendLogLine(log, lv);
        }

        await Task.Delay(300);
        SaveWindowShot(Path.Combine(outDir, "mainwindow.png"));
        EventBus.Info("UITEST", $"截图已保存: {outDir}");
    }

    private void SaveWindowShot(string path)
    {
        var rtb = new RenderTargetBitmap((int)(ActualWidth * 1.25), (int)(ActualHeight * 1.25),
            96 * 1.25, 96 * 1.25, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(path);
        enc.Save(fs);
    }
}
