# MiniMonitor 一键重编译（csc.exe，输出 MiniMonitor.exe 到本目录）
# 用法：先结束正在跑的 MiniMonitor，再在本目录执行 .\build.ps1

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Get-Process MiniMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$src = @('MiniMonitor.cs', 'Shot.cs', 'ShotUi.cs', 'Snip.cs', 'LhmTemp.cs', 'TempBus.cs') |
       ForEach-Object { Join-Path $dir $_ } |
       Where-Object { Test-Path $_ }
foreach ($s in $src) { if (-not (Test-Path $s)) { throw "缺少源文件：$s" } }

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll',
  (Join-Path $dir 'LibreHardwareMonitorLib.dll'),
  (Join-Path $dir 'System.Buffers.dll'),
  (Join-Path $dir 'System.Memory.dll'),
  (Join-Path $dir 'System.Runtime.CompilerServices.Unsafe.dll')
) | ForEach-Object { '/r:' + $_ }

$out = Join-Path $dir 'MiniMonitor.exe'
$ico = Join-Path $dir 'assets\MiniMonitor.ico'
$icoArg = @()
if (Test-Path $ico) { $icoArg += ('/win32icon:' + $ico) }
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$out" $icoArg $refs $src
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

# 配置文件名固定为 MiniMonitor.config（和程序放在一起，永远不再叫 MiniMonitor.exe.config）
# 只存本程序自己的 appSettings；CLR 的版本重定向由 MiniMonitor.cs 里的 AssemblyResolve 完成。
$ver = ([regex]::Match((Get-Content (Join-Path $dir 'MiniMonitor.cs') -Encoding UTF8 -Raw),
        'AppVersion\s*=\s*"([^"]+)"')).Groups[1].Value
if ($ver.Length -eq 0) { throw '没在 MiniMonitor.cs 里找到 AppVersion' }

$build = (Get-Item $out).LastWriteTime.ToString('yyyy-MM-dd')
$cfg = Join-Path $dir 'MiniMonitor.config'
$text = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <appSettings>
    <add key="Version" value="$ver" />
    <add key="Build" value="$build" />
  </appSettings>
</configuration>
"@
[IO.File]::WriteAllText($cfg, $text, (New-Object System.Text.UTF8Encoding($false)))

# 旧命名不再生成，顺手清掉历史遗留
$oldClr = Join-Path $dir 'MiniMonitor.exe.config'
if (Test-Path $oldClr) { Remove-Item -LiteralPath $oldClr -Force }

$len = (Get-Item $out).Length
"编译完成：MiniMonitor.exe {0} 字节，版本 {1}（写进 MiniMonitor.config 的 appSettings）" -f $len, $ver
