# WinTune 构建脚本
# 用法：右键“使用 PowerShell 运行”，或在终端执行 .\build.ps1
# 产物：根目录 WinTune.exe（单文件界面 app\index.html，运行依赖统一在 runtime\）
$ErrorActionPreference = "Stop"

$root   = $PSScriptRoot
$src    = Join-Path $root "src"
$rt     = Join-Path $root "runtime"
$exe    = Join-Path $root "WinTune.exe"
$ico    = Join-Path $root "assets\icon.ico"
$csc    = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wv2    = Join-Path $root "vendor\wv2\lib\net45"
$wv2nat = Join-Path $root "vendor\wv2\runtimes\win-x64\native\WebView2Loader.dll"

if (-not (Test-Path $rt)) { New-Item -ItemType Directory -Path $rt | Out-Null }

# 1) 硬件传感器依赖（来自 MiniMonitor 发行包，同一个 LibreHardwareMonitor）
$mm = Join-Path $root "..\MiniMonitor\MiniMonitor-v1.5-win64"
$mmRoot = Join-Path $root "..\MiniMonitor"
$hwDlls = @("LibreHardwareMonitorLib.dll", "PawnIOLib.dll", "System.Buffers.dll", "System.Memory.dll",
            "System.Runtime.CompilerServices.Unsafe.dll", "RAMSPDToolkit-NDD.dll", "DiskInfoToolkit.dll",
            "BlackSharp.Core.dll", "HidSharp.dll")
foreach ($d in $hwDlls) {
    $f = Join-Path $mm $d
    if (-not (Test-Path $f)) { $f = Join-Path $mmRoot $d }
    if (Test-Path $f) { Copy-Item $f (Join-Path $rt $d) -Force }
}

# 2) WebView2 托管库与原生 loader（vendor 只用于首次准备；runtime 里已有时跳过）
if (Test-Path $wv2) {
    Copy-Item (Join-Path $wv2 "Microsoft.Web.WebView2.Core.dll") $rt -Force
    Copy-Item (Join-Path $wv2 "Microsoft.Web.WebView2.WinForms.dll") $rt -Force
    Copy-Item $wv2nat $rt -Force
} elseif (-not (Test-Path (Join-Path $rt "Microsoft.Web.WebView2.Core.dll"))) {
    "缺少 WebView2 托管库：vendor\wv2 不存在，runtime 里也没有。"
    exit 1
}

# 3) 源码统一 UTF-8 BOM（csc 读中文必须）
Get-ChildItem $src -Filter *.cs | ForEach-Object {
    [IO.File]::WriteAllText($_.FullName, [IO.File]::ReadAllText($_.FullName), (New-Object Text.UTF8Encoding($true)))
}

# 4) 编译：所有 /r 都指向 runtime，避免引用与运行时两套路径
$argList = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    ("/out:" + $exe),
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll",
    "/r:System.Web.Extensions.dll",
    "/r:System.Management.dll",
    ("/r:" + (Join-Path $rt "Microsoft.Web.WebView2.Core.dll")),
    ("/r:" + (Join-Path $rt "Microsoft.Web.WebView2.WinForms.dll")),
    ("/r:" + (Join-Path $rt "LibreHardwareMonitorLib.dll")),
    ("/r:" + (Join-Path $rt "System.Buffers.dll")),
    ("/r:" + (Join-Path $rt "System.Memory.dll")),
    ("/r:" + (Join-Path $rt "System.Runtime.CompilerServices.Unsafe.dll"))
)
if (Test-Path $ico) { $argList += ("/win32icon:" + $ico) }
Get-ChildItem $src -Filter *.cs | Sort-Object Name | ForEach-Object { $argList += $_.FullName }

& $csc $argList
if ($LASTEXITCODE -ne 0) { "编译失败，退出码 $LASTEXITCODE"; exit 1 }

# 5) 自检：界面、配置、运行依赖是否齐全
$need = @("WinTune.exe", "WinTune.config", "app\index.html", "MiniMonitor\MiniMonitor.exe")
foreach ($n in $need) {
    if (-not (Test-Path (Join-Path $root $n))) { "缺少必要文件：" + $n }
}
$dllCount = (Get-ChildItem $rt -Filter *.dll).Count

"编译完成：{0} 字节，runtime 内 {1} 个 DLL" -f (Get-Item $exe).Length, $dllCount
