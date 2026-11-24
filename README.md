# WinTune / MiniMonitor

Windows 10/11 系统优化、清理与硬件监控工具源码整理。

当前源码版本：

- WinTune：1.5.38
- MiniMonitor：1.6.11

## 目录

- `WinTune/src`：WinTune 的 C# 后端源码（清理、优化、修复、硬件采集、WebView2 界面桥接等）
- `WinTune/app/index.html`：WinTune 的单文件前端界面
- `WinTune/assets`：WinTune 图标
- `WinTune/build.ps1`：WinTune 的 csc 构建脚本
- `WinTune/README.txt`：原始功能说明与版本记录
- `MiniMonitor`：MiniMonitor 源码、截图/录屏模块、温度代理、构建脚本与图标
- `MiniMonitor/build.ps1`：MiniMonitor 的 csc 构建脚本
- `MiniMonitor/MiniMonitor.config`：MiniMonitor 版本配置

## 构建说明

项目使用 Windows 自带的 `.NET Framework` `csc.exe` 编译，不依赖 Visual Studio 工程。

- WinTune 运行依赖 WebView2、LibreHardwareMonitor、PawnIO 等 DLL，以及 `runtime` 目录；这些二进制依赖没有放入源码仓库。
- MiniMonitor 编译需要 `LibreHardwareMonitorLib.dll`、`System.Buffers.dll`、`System.Memory.dll`、`System.Runtime.CompilerServices.Unsafe.dll` 等同目录依赖。
- 构建脚本保留了作者本地环境的依赖路径，使用前请按自己的目录调整。
- 发布的完整运行包不包含在本仓库中，可在 Releases 或本地发布包中获取。

## 说明

本仓库只整理源码与构建所需文本文件，不包含：

- 旧版本完整包、ZIP、EXE、DLL
- 测试截图、日志、临时文件
- PawnIO 安装程序
- 本地配置、备份与诊断输出

源码主要用于学习、维护和备份。使用清理、注册表、计划任务、驱动安装等功能前，请先理解风险并做好备份。
