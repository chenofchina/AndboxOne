# scripts/prepare_runtimesdk.ps1 —— RuntimeSdk 离线填充脚本
# 用途：把 Android Emulator 引擎、platform-tools、Android 11 系统镜像
#       下载并展开到项目根目录 RuntimeSdk\，形成随主程序离线打包的完整引擎。
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\prepare_runtimesdk.ps1            # 全部组件
#   powershell -ExecutionPolicy Bypass -File scripts\prepare_runtimesdk.ps1 -Only platform-tools
# 说明：下载源为 Google 官方仓库 (dl.google.com)。国内网络通常可直连；
#       若失败可按 README「手动填充」章节从镜像站离线搬运。

param(
    [ValidateSet('all', 'platform-tools', 'emulator', 'system-image')]
    [string]$Only = 'all'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Sdk = Join-Path $Root 'RuntimeSdk'
$Tmp = Join-Path $env:TEMP "andboxone-runtimesdk-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $Sdk, $Tmp | Out-Null

function Get-RepoXml {
    # 拉取 Google 仓库清单，供解析 emulator / system-image 的最新包名
    $xmlPath = Join-Path $Tmp 'repository2-3.xml'
    Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/repository2-3.xml' -OutFile $xmlPath -UseBasicParsing
    return [xml](Get-Content $xmlPath -Raw)
}

function Get-PackageUrl([xml]$repo, [string]$pkgPath) {
    # remotePackage path="emulator;12345" / "system-images;android-30;google_apis;x86_64"
    $pkg = $repo.'sdk:sdk-repository'.'remote-package' | Where-Object { $_.path -eq $pkgPath } |
        Sort-Object { [version]($_.revisions.revision.major ?? '0') } -Descending |
        Select-Object -First 1
    if (-not $pkg) { throw "仓库清单中未找到包: $pkgPath" }
    $archive = $pkg.archives.archive | Where-Object { $_.host-os -eq 'windows' } | Select-Object -First 1
    if (-not $archive) { throw "包 $pkgPath 无 windows 归档" }
    return "https://dl.google.com/android/repository/$($archive.url)"
}

function Install-Zip([string]$url, [string]$dest) {
    $zip = Join-Path $Tmp (Split-Path $url -Leaf)
    Write-Host ">> 下载 $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Write-Host ">> 解压到 $dest"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Expand-Archive -Path $zip -DestinationPath $dest -Force
}

$want = if ($Only -eq 'all') { @('platform-tools', 'emulator', 'system-image') } else { @($Only) }
$repo = $null

try {
    foreach ($item in $want) {
        switch ($item) {
            'platform-tools' {
                # platform-tools 有稳定的 latest 地址
                Install-Zip 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' $Sdk
                Write-Host "[OK] platform-tools 就位: $Sdk\platform-tools\adb.exe"
            }
            'emulator' {
                if (-not $repo) { $repo = Get-RepoXml }
                $url = Get-PackageUrl $repo 'emulator' | Select-Object -First 1
                # emulator zip 展开后即为 emulator\ 目录
                Install-Zip $url $Sdk
                Write-Host "[OK] emulator 就位: $Sdk\emulator\emulator.exe"
            }
            'system-image' {
                if (-not $repo) { $repo = Get-RepoXml }
                $url = Get-PackageUrl $repo 'system-images;android-30;google_apis;x86_64' | Select-Object -First 1
                # system-image zip 展开后即为 system-images\android-30\google_apis\x86_64\ 目录
                Install-Zip $url $Sdk
                Write-Host "[OK] 系统镜像就位: $Sdk\system-images\android-30\google_apis\x86_64\"
            }
        }
    }
    Write-Host ''
    Write-Host '全部组件就绪。执行 dotnet build / dotnet publish 即可把 RuntimeSdk 随主程序离线打包。'
}
catch {
    Write-Error "填充失败: $_（可参考 README『手动填充 RuntimeSdk』章节离线搬运）"
}
finally {
    Remove-Item -Recurse -Force $Tmp -ErrorAction SilentlyContinue
}
