using System.Windows;
using System.Windows.Controls;
using AndboxOne.Core;

namespace AndboxOne;

/// <summary>新建虚拟机对话框：AVD 名称 + 设备预设 + 资源配额。</summary>
public partial class CreateVmDialog : Window
{
    public string AvdName { get; private set; } = "";
    public DeviceProfile SelectedProfile { get; private set; } = new();

    public CreateVmDialog()
    {
        InitializeComponent();

        foreach (string name in DeviceProfile.Presets().Keys)
            PresetCombo.Items.Add(name);
        PresetCombo.SelectedIndex = 0;
        PresetCombo.SelectionChanged += (_, _) => UpdatePreview();

        foreach (int c in Enumerable.Range(1, Math.Max(8, Environment.ProcessorCount)))
            CpuCombo.Items.Add($"{c} 核");
        CpuCombo.SelectedIndex = 3;

        foreach (int ram in new[] { 1024, 2048, 3072, 4096, 6144, 8192 })
            RamCombo.Items.Add($"{ram} MB");
        RamCombo.SelectedIndex = 1;

        UpdatePreview();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void UpdatePreview()
    {
        if (PresetCombo.SelectedItem is string name && DeviceProfile.Presets().TryGetValue(name, out DeviceProfile? p))
            PreviewText.Text = $"将创建: {p.Manufacturer} {p.Model} · {p.Width}×{p.Height} · {p.Dpi}dpi · 目标镜像 android-{p.ApiLevel} ({p.TagId})";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (!DeviceProfile.IsValidAvdName(name))
        {
            MessageBox.Show(this, "AVD 名称只允许字母、数字、点、下划线、连字符（≤64 字符）",
                "名称不合法", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DeviceProfile profile;
        if (PresetCombo.SelectedItem is string presetName &&
            DeviceProfile.Presets().TryGetValue(presetName, out DeviceProfile? preset))
            profile = preset.Clone();
        else
            profile = new DeviceProfile();

        if (CpuCombo.SelectedItem is string cpu && cpu.EndsWith(" 核") && int.TryParse(cpu.AsSpan(0, cpu.Length - 2), out int cores))
            profile.CpuCores = cores;
        if (RamCombo.SelectedItem is string ram && ram.EndsWith(" MB") && int.TryParse(ram.AsSpan(0, ram.Length - 3), out int mb))
            profile.RamMb = mb;

        AvdName = name;
        SelectedProfile = profile;
        DialogResult = true;
    }
}
