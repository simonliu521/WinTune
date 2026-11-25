# BCU 卸载引擎（第三方）

WinTune 的“BCU 卸载 / 批量卸载”使用 **Bulk Crap Uninstaller 4.16 portable** 的 `BCU-console.exe` 作为外部卸载引擎。

- 项目主页：https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- 版本：4.16 portable
- 许可证：Apache License 2.0
- 只调用 `uninstall` 子命令，不启用 `/J` 残留清理。
- `BCU` 目录不提交到源码仓库，发布完整包时放入程序根目录的 `BCU` 文件夹。

```text
WinTune.exe
BCU\
  BCU-console.exe
  BCU-console.exe.config
  UninstallTools.dll
  UniversalUninstaller.exe
  ...
```

从官方 Release 下载 `BCUninstaller_4.16_portable.zip`，解压后把**整个目录内容**放进 `BCU`，不要只复制 `BCU-console.exe`。BCU 自带的 `Licence.txt` 和 `NOTICE` 必须一起保留。
