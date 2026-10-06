using System.Text;

namespace AndboxOne.Core;

/// <summary>
/// 一次 APK 安装的全部报告数据（编排引擎的安装流水线产出）。
/// </summary>
public sealed class ApkInstallInfo
{
    public string Package { get; set; } = "";
    public string VersionName { get; set; } = "";
    public int VersionCode { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Launcher Activity（快捷方式启动用；AXML/aapt 解析取得，可能为空）。</summary>
    public string LauncherActivity { get; set; } = "";
    public string ApkPath { get; set; } = "";
    public long ApkSizeBytes { get; set; }
    public DateTime InstallTime { get; set; }
    public string CodePath { get; set; } = "";
    public long InstalledSizeBytes { get; set; }
    public string TargetDevice { get; set; } = "";
    public string? FirstInstallTime { get; set; }
    public List<string> Permissions { get; set; } = new();
    /// <summary>从 APK 提取的应用图标（PNG，供快捷方式使用；可能为 null）。</summary>
    public byte[]? IconPng { get; set; }

    public string ApkSizeText => HumanSize(ApkSizeBytes);
    public string InstalledSizeText => InstalledSizeBytes > 0 ? HumanSize(InstalledSizeBytes) : "未取得（du 不可用）";

    public static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>
/// InstallReportGenerator —— 应用安装报告生成器。
///
/// 在右侧面板生成 HTML 格式安装报告，包含：包名、版本号、安装时间、
/// APK 大小、安装后占用空间、权限列表（中文释义）。
/// 附带纯文本版本（供"复制报告"）与 HTML 导出（供"导出为 HTML"）。
/// </summary>
public static class InstallReportGenerator
{
    /// <summary>常见 Android 权限的中文释义（覆盖绝大多数应用；未收录项显示原始权限名）。</summary>
    private static readonly Dictionary<string, string> PermissionZh = new()
    {
        ["android.permission.INTERNET"] = "访问网络",
        ["android.permission.ACCESS_NETWORK_STATE"] = "查看网络状态",
        ["android.permission.ACCESS_WIFI_STATE"] = "查看 Wi-Fi 状态",
        ["android.permission.CHANGE_WIFI_STATE"] = "改变 Wi-Fi 状态",
        ["android.permission.READ_CONTACTS"] = "读取通讯录",
        ["android.permission.WRITE_CONTACTS"] = "修改通讯录",
        ["android.permission.ACCESS_FINE_LOCATION"] = "访问精确定位 (GPS)",
        ["android.permission.ACCESS_COARSE_LOCATION"] = "访问粗略定位",
        ["android.permission.ACCESS_BACKGROUND_LOCATION"] = "后台定位",
        ["android.permission.CAMERA"] = "使用摄像头",
        ["android.permission.RECORD_AUDIO"] = "录音（麦克风）",
        ["android.permission.READ_PHONE_STATE"] = "读取手机状态",
        ["android.permission.CALL_PHONE"] = "直接拨打电话",
        ["android.permission.READ_CALL_LOG"] = "读取通话记录",
        ["android.permission.WRITE_CALL_LOG"] = "修改通话记录",
        ["android.permission.ADD_VOICEMAIL"] = "添加语音邮件",
        ["android.permission.USE_SIP"] = "使用 SIP 通话",
        ["android.permission.SEND_SMS"] = "发送短信",
        ["android.permission.RECEIVE_SMS"] = "接收短信",
        ["android.permission.READ_SMS"] = "读取短信",
        ["android.permission.RECEIVE_MMS"] = "接收彩信",
        ["android.permission.READ_EXTERNAL_STORAGE"] = "读取外部存储",
        ["android.permission.WRITE_EXTERNAL_STORAGE"] = "写入外部存储",
        ["android.permission.MANAGE_EXTERNAL_STORAGE"] = "管理全部文件",
        ["android.permission.READ_MEDIA_IMAGES"] = "读取图片",
        ["android.permission.READ_MEDIA_VIDEO"] = "读取视频",
        ["android.permission.READ_MEDIA_AUDIO"] = "读取音频",
        ["android.permission.POST_NOTIFICATIONS"] = "发送通知",
        ["android.permission.VIBRATE"] = "振动",
        ["android.permission.WAKE_LOCK"] = "阻止休眠",
        ["android.permission.RECEIVE_BOOT_COMPLETED"] = "开机自启",
        ["android.permission.REQUEST_INSTALL_PACKAGES"] = "安装其他应用",
        ["android.permission.SYSTEM_ALERT_WINDOW"] = "悬浮窗",
        ["android.permission.WRITE_SETTINGS"] = "修改系统设置",
        ["android.permission.BLUETOOTH"] = "使用蓝牙",
        ["android.permission.BLUETOOTH_CONNECT"] = "连接蓝牙设备",
        ["android.permission.BLUETOOTH_SCAN"] = "扫描蓝牙设备",
        ["android.permission.ACCESS_NOTIFICATION_POLICY"] = "勿扰策略",
        ["android.permission.FOREGROUND_SERVICE"] = "前台服务",
        ["android.permission.GET_ACCOUNTS"] = "获取账户列表",
        ["android.permission.USE_CREDENTIALS"] = "使用账户凭据",
        ["android.permission.MANAGE_ACCOUNTS"] = "管理账户",
        ["android.permission.AUTHENTICATE_ACCOUNTS"] = "验证账户",
        ["android.permission.NFC"] = "使用 NFC",
        ["android.permission.BODY_SENSORS"] = "读取身体传感器",
        ["android.permission.ACTIVITY_RECOGNITION"] = "识别运动状态",
        ["android.permission.QUERY_ALL_PACKAGES"] = "查看全部应用列表",
        ["android.permission.PACKAGE_USAGE_STATS"] = "查看应用使用统计",
        ["com.android.launcher.permission.INSTALL_SHORTCUT"] = "创建桌面快捷方式",
        ["com.android.vending.BILLING"] = "应用内购买",
        ["com.google.android.c2dm.permission.RECEIVE"] = "接收推送 (FCM)",
    };

    /// <summary>是否为敏感权限（报告中红色标注）。</summary>
    private static readonly string[] SensitiveMarkers =
    {
        "CONTACTS", "LOCATION", "CAMERA", "RECORD_AUDIO", "SMS", "CALL",
        "READ_EXTERNAL_STORAGE", "WRITE_EXTERNAL_STORAGE", "MANAGE_EXTERNAL",
        "READ_MEDIA", "INSTALL_PACKAGES", "SYSTEM_ALERT_WINDOW", "ACCOUNTS",
        "BOOT_COMPLETED", "QUERY_ALL_PACKAGES", "BODY_SENSORS",
    };

    public static string Describe(string permission) =>
        PermissionZh.TryGetValue(permission, out string? zh) ? zh : "（未收录释义）";

    private static bool IsSensitive(string permission) =>
        SensitiveMarkers.Any(m => permission.Contains(m, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    //  HTML 报告
    // ------------------------------------------------------------------

    public static string GenerateHtml(ApkInstallInfo info)
    {
        string permsHtml;
        if (info.Permissions.Count == 0)
        {
            permsHtml = "<div class='none'>无任何权限声明（或 dumpsys 未能提取）</div>";
        }
        else
        {
            var sb = new StringBuilder();
            foreach (string p in info.Permissions)
            {
                bool sensitive = IsSensitive(p);
                string cls = sensitive ? "perm sensitive" : "perm";
                string mark = sensitive ? "<span class='danger'>⚠</span> " : "<span class='ok'>✓</span> ";
                sb.Append($"<div class='{cls}'>{mark}<span class='zh'>{HttpEncode(Describe(p))}</span>"
                          + $"<span class='raw'>{HttpEncode(p)}</span></div>");
            }
            permsHtml = sb.ToString();
        }

        string label = string.IsNullOrEmpty(info.Label) ? "（未解析到显示名）" : info.Label;
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine($"<title>AndboxOne 安装报告 - {HttpEncode(info.Package)}</title>");
        html.AppendLine("<style>");
        html.AppendLine("  body { background:#0a0c0a; color:#c9d1c9; font-family:Consolas,'Microsoft YaHei UI',monospace; font-size:12px; margin:10px; }");
        html.AppendLine("  h1 { color:#39e639; font-size:15px; letter-spacing:1px; border-bottom:1px solid #26302a; padding-bottom:6px; }");
        html.AppendLine("  h1 small { color:#5f6f5f; font-size:10px; }");
        html.AppendLine("  table { border-collapse:collapse; width:100%; margin:8px 0 14px; }");
        html.AppendLine("  td { border:1px solid #1c241e; padding:4px 8px; }");
        html.AppendLine("  td.k { color:#6e7b6e; width:110px; background:#0f120f; }");
        html.AppendLine("  td.v { color:#d8e2d8; }");
        html.AppendLine("  .acc { color:#39e639; }");
        html.AppendLine("  .perms { border:1px solid #1c241e; padding:6px; }");
        html.AppendLine("  .perm { padding:2px 4px; border-bottom:1px dashed #161c17; display:flex; justify-content:space-between; gap:8px; }");
        html.AppendLine("  .perm .raw { color:#4c584c; word-break:break-all; text-align:right; }");
        html.AppendLine("  .perm .zh { color:#b9c4b9; white-space:nowrap; }");
        html.AppendLine("  .sensitive .zh { color:#ff6b6b; }");
        html.AppendLine("  .danger { color:#ff5555; }");
        html.AppendLine("  .ok { color:#39e639; }");
        html.AppendLine("  .none { color:#6e7b6e; padding:6px; }");
        html.AppendLine("  .foot { margin-top:12px; color:#3d483d; font-size:10px; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("  <h1>ANDBOXONE // 应用安装报告 <small>OFFLINE · LOCAL ONLY</small></h1>");
        html.AppendLine("  <table>");
        html.AppendLine($"    <tr><td class=\"k\">应用名</td><td class=\"v acc\">{HttpEncode(label)}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">包名</td><td class=\"v\">{HttpEncode(info.Package)}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">版本号</td><td class=\"v\">{HttpEncode(info.VersionName)} <span style=\"color:#4c584c\">(versionCode {info.VersionCode})</span></td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">安装时间</td><td class=\"v\">{info.InstallTime:yyyy-MM-dd HH:mm:ss}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">目标设备</td><td class=\"v\">{HttpEncode(info.TargetDevice)}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">APK 大小</td><td class=\"v\">{info.ApkSizeText}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">安装后占用</td><td class=\"v\">{info.InstalledSizeText}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">存储路径</td><td class=\"v\">{HttpEncode(info.CodePath)}</td></tr>");
        html.AppendLine($"    <tr><td class=\"k\">来源文件</td><td class=\"v\">{HttpEncode(info.ApkPath)}</td></tr>");
        html.AppendLine("  </table>");
        html.AppendLine($"  <div style=\"color:#6e7b6e;margin-bottom:4px\">权限列表（{info.Permissions.Count} 项 · <span class=\"danger\">⚠</span> 为敏感权限）</div>");
        html.AppendLine($"  <div class=\"perms\">{permsHtml}</div>");
        html.AppendLine("  <div class=\"foot\">AndboxOne · 本报告仅存于本机，不做任何上传 · 用于合法测试与开发</div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    // ------------------------------------------------------------------
    //  纯文本报告（复制用）
    // ------------------------------------------------------------------

    public static string ToPlainText(ApkInstallInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("════════ AndboxOne 应用安装报告 ════════");
        sb.AppendLine($"应用名      : {(string.IsNullOrEmpty(info.Label) ? "（未解析到显示名）" : info.Label)}");
        sb.AppendLine($"包名        : {info.Package}");
        sb.AppendLine($"版本号      : {info.VersionName} (versionCode {info.VersionCode})");
        sb.AppendLine($"安装时间    : {info.InstallTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"目标设备    : {info.TargetDevice}");
        sb.AppendLine($"APK 大小    : {info.ApkSizeText}");
        sb.AppendLine($"安装后占用  : {info.InstalledSizeText}");
        sb.AppendLine($"存储路径    : {info.CodePath}");
        sb.AppendLine($"来源文件    : {info.ApkPath}");
        sb.AppendLine($"权限列表    : {info.Permissions.Count} 项");
        foreach (string p in info.Permissions)
            sb.AppendLine($"  {(IsSensitive(p) ? "[敏感]" : "      ")} {Describe(p)} ({p})");
        sb.AppendLine("════════ 本报告仅存于本机 ════════");
        return sb.ToString();
    }

    private static string HttpEncode(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
