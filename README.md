# ANDBOXONE

> 离线打包的极客级安卓虚拟机控制中枢 —— 黑底、荧光绿、等宽字体，工程师看得很舒服。

```
[ENV] AndboxOne 启动 · 基目录: D:\AndboxOne\
[ENV] [OK] adb.exe 就位: D:\AndboxOne\RuntimeSdk\platform-tools\adb.exe
[ENV] [OK] emulator.exe 就位: D:\AndboxOne\RuntimeSdk\emulator\emulator.exe
[ENV] 环境校验通过：ANDROID_HOME / ANDROID_SDK_ROOT 已指向离线 RuntimeSdk，进入主界面
```

---

## 一、项目哲学（写进代码注释的那一套）

**AndboxOne 是一个自带全套 Android 虚拟化引擎的离线模拟器。**

用户只需要下载一个安装包，双击即可运行——

- ❌ 无需安装 Android Studio
- ❌ 无需配置环境变量
- ❌ 无需在线下载镜像
- ❌ 无账号系统 · 无广告推送 · 无云同步 · 无任何遥测
- ✅ 所有 Android SDK、系统镜像、ADB、Emulator 引擎，全部随主程序**离线打包发布**
- ✅ 只做纯粹的本地极客工具，一切数据留在本机

## 二、离线打包架构

### 目录约定

```
AndboxOne.exe
RuntimeSdk\                          ← 随主程序一起发布的离线引擎
    emulator\                        ← Android Emulator 引擎（emulator.exe + lib\）
    platform-tools\                  ← adb.exe / fastboot.exe
    system-images\
        android-30\google_apis\x86_64\   ← Android 11 系统镜像（google_apis 支持 adb root）
    build-tools\                     ← 可选：放入后 aapt 可用，APK 解析信息更全
```

### 工作机制

1. `.csproj` 中通过 `<Content Include="RuntimeSdk\**\*">` 把整个文件夹标记为内容文件，
   `CopyToOutputDirectory=PreserveNewest` + `CopyToPublishDirectory=PreserveNewest`，
   构建 / 发布时原样随 EXE 复制；
2. 启动时 `EnvironmentManager` 通过 `Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RuntimeSdk")`
   定位 SDK，自动扫描校验 `emulator.exe`、`adb.exe`、`system-images` 是否存在；
3. 缺失 → 明确错误日志 **“RuntimeSdk 不完整，请重新安装 AndboxOne”**；
   完整 → 自动设置 `ANDROID_HOME` / `ANDROID_SDK_ROOT` / `ANDROID_AVD_HOME` 等环境变量并进入主界面；
4. AVD 数据放 `%LOCALAPPDATA%\AndboxOne`（可用环境变量 `ANDBOXONE_DATA_HOME` 重定向实现绿色便携）。

### 填充 RuntimeSdk（构建你自己的离线安装包）

```powershell
# 方式 A：脚本自动下载（约 1.7GB，含引擎 + 镜像）
powershell -ExecutionPolicy Bypass -File scripts\prepare_runtimesdk.ps1
powershell -ExecutionPolicy Bypass -File scripts\prepare_runtimesdk.ps1 -Only platform-tools   # 只补 adb

# 方式 B：手动搬运（完全离线）
#   从任意一台装了 Android Studio 的机器，把
#   %LOCALAPPDATA%\Android\Sdk\ 下的 emulator\、platform-tools\、
#   system-images\android-30\google_apis\x86_64\ 三个目录原样复制到 RuntimeSdk\ 即可。
```

> 选型说明：系统镜像必须选 **google_apis**（而非 google_apis_playstore），它允许 `adb root`，
> 这是 AndboxOne「一键 Root」功能的前提。

## 三、编译与发布

要求：Windows 10/11 + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（本项目零第三方 NuGet 依赖，还原极快）。

```bash
# 编译
dotnet build -c Release

# 逻辑自检（无 UI，退出码 = 失败用例数）
bin\Release\net8.0-windows\AndboxOne.exe --selftest

# 界面自检（渲染主窗口截图到 .uitest\ 后退出）
bin\Release\net8.0-windows\AndboxOne.exe --uitest

# 发布 A：框架依赖（体积小，要求目标机装有 .NET 8 Desktop Runtime）
dotnet publish -c Release -r win-x64 --self-contained false -o publish

# 发布 B：完全自包含（推荐，符合“双击即可运行”哲学，约 +150MB）
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

发布完成后，`publish\` 里就是完整安装包：`AndboxOne.exe + RuntimeSdk\`，打包整个文件夹即可分发。

### 虚拟化前提（宿主机）

Android Emulator 在 Windows 上依赖硬件加速，满足其一即可：

- Intel: BIOS 开启 VT-x；AMD: 开启 SVM；
- 并在「启用或关闭 Windows 功能」中开启 **Windows 虚拟机监控程序平台（Windows Hypervisor Platform）**。

## 四、功能地图

| 模块 | 文件 | 能力 |
|---|---|---|
| 日志与状态总线 | `Core/EventBus.cs` | 全局 `Action<string, LogLevel>`，毫秒级时间戳，UI 分色实时渲染 |
| 环境自适应 | `Core/EnvironmentManager.cs` | RuntimeSdk 扫描校验、环境变量注入、PATH 预置 |
| 设备配置定义 | `Core/DeviceProfile.cs` | 厂商/型号/分辨率/DPI/CPU/内存；内置 Pixel 5、Pixel 6、小米 11、三星 S21；克隆、JSON 导入导出；游戏兼容模式；**纯手写 AVD INI 自举（不依赖 avdmanager/Java）** |
| 虚拟机编排 | `Core/EmulatorOrchestrator.cs` | `emulator.exe -avd <名字> -writable-system` 拉起；ADB 两级优先级命令队列严格顺序执行；批量启动/关闭/Root；引导轮询；CPU/内存 2 秒采样；APK 安装流水线 |
| 网络策略调度 | `Core/NetworkManager.cs` | NAT（`-netdelay none -netspeed full`）/ 桥接（`-qemu -net tap`，实验性）；`adb shell ping -c 4 8.8.8.8` 诊断丢包与延迟 |
| APK 检查器 | `Core/ApkInspector.cs` | 内置二进制 AXML 解析引擎：离线提取包名/版本/权限/Launcher Activity/图标（不依赖 aapt）；图标 PNG→ICO 封装 |
| 桌面级集成 | `Core/ShortcutCreator.cs` | 安装 APK 后生成桌面 .lnk，双击自动启动对应虚拟机并拉起 App（COM 创建，失败降级 .cmd） |
| 安装报告 | `Core/InstallReportGenerator.cs` | 包名/版本/安装时间/APK 大小/安装后占用（du）/权限中文释义列表；HTML 报告 + 复制 + 导出 |

### 操作速查

- **创建虚拟机**：顶栏「＋ 新建虚拟机」→ 选预设 → 创建（AVD 文件由 AndboxOne 纯手写生成）；
- **批量操作**：勾选左侧卡片 → 「全部启动 / 全部关闭 / 全部 Root」（未勾选则作用于全部）；
- **安装 APK**：点「⇩ 安装 APK」选文件，或**直接把 .apk 拖进窗口**；
- **自定义机型**：右栏拖滑块/点比例按钮（16:9、18:9、19.5:9、20:9）实时预览 → 「应用到此虚拟机」；
- **游戏适配**：右栏「游戏适配」选兼容模式一键应用（降分辨率 / 切渲染 / 调 DPI）；
- **桌面快捷方式**：安装报告页 → 「生成桌面快捷方式」；
- **快捷启动协议**：`AndboxOne.exe --launch-app --avd <名字> --package <包名> --activity <Activity>`。

## 五、隐私与安全

- 不联网上报任何数据；不收集设备信息；不内置任何更新器；
- 全部读写仅发生在本机：`RuntimeSdk\`（只读引擎）与 `%LOCALAPPDATA%\AndboxOne`（AVD/日志/图标缓存）；
- 日志仅写本地文件（`%LOCALAPPDATA%\AndboxOne\logs`），随时可删。

## 六、法律风险边界声明

- **AndboxOne 不做任何“反检测”和“设备特征伪装”。** 本工具仅用于合法的应用测试与开发场景。
- **不提供任何绕过反作弊或风控系统的功能。**
- Root 能力来自 Android 官方 google_apis 镜像的 `adb root` 机制，仅用于本地调试与测试；
- 用户应遵守当地法律法规，不得利用本工具从事任何非法活动；因用户使用行为产生的责任由用户自行承担；
- Android、Google Play 等为 Google LLC 商标；本项目不附带、不分发任何 Google 专有二进制，
  RuntimeSdk 组件需由使用者按上述章节自行获取并遵守其许可协议（Android SDK 许可协议）。

## 七、项目结构

```
AndboxOne.csproj              含 RuntimeSdk 离线打包配置
App.xaml / App.xaml.cs        暗黑主题样式库 + 启动模式分发（常规/selftest/uitest/launch-app）
MainWindow.xaml(.cs)          三栏布局：多开管理器 | 核心操作+日志 | 高级配置面板
CreateVmDialog.xaml(.cs)      新建虚拟机对话框
Core/
    EventBus.cs               日志总线
    EnvironmentManager.cs     离线 SDK 校验与环境变量
    DeviceProfile.cs          设备配置 + 预设 + 游戏适配 + AvdWriter
    VmInstance.cs             实例状态与资源采样模型
    EmulatorOrchestrator.cs   编排引擎（进程/ADB 队列/批量/安装流水线）
    NetworkManager.cs         网络策略与诊断
    ApkInspector.cs           APK 离线解析（AXML 引擎 + 图标 + ICO）
    ShortcutCreator.cs        桌面快捷方式
    InstallReportGenerator.cs 安装报告（HTML/文本）
    SelfTest.cs               构建自检套件
scripts/prepare_runtimesdk.ps1  RuntimeSdk 离线填充脚本
RuntimeSdk/                   离线引擎（见第二章）
```

---

*ANDBOXONE v1.0 // OFFLINE ANDROID CONTROL HUB —— 无账号 · 无广告 · 无遥测 · 纯本地*
