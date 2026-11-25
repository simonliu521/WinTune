WinTune 1.5.46 · 工程说明（交付 / 二次开发请先读这一页）
===========================================================

一、这是什么
  Windows 系统优化与清理工具，单文件界面 + C# 引擎。
  界面跑在 WebView2 里（app\index.html，一个文件装下 HTML / CSS / JS，不拆不引外部库），
  引擎是 WinTune.exe（.NET Framework 4.8，csc.exe 直接编译，不依赖 MSBuild / NuGet 还原）。

二、目录结构
  WinTune\
    WinTune.exe            主程序，含图标；双击即用
    WinTune.config          应用配置：版本 / 构建日期（文件名与软件同名，不带 .exe）
    app\index.html         界面，全部页面与交互
    runtime\               27 个运行期 dll（WebView2、LibreHardwareMonitor 及其依赖）
    MiniMonitor\           MiniMonitor.exe（硬件悬浮条，主程序用相对路径拉起）+ MiniMonitor.config（版本号写在里面，见第二十七节）
    assets\                icon.ico（16/24/32/48/64/128/256 七种尺寸）
    src\                   完整源码，22 个 .cs
    build.ps1              一键重编译
    vendor\                构建依赖缓存（WebView2 nupkg 解包），只给 build.ps1 用，可删

  运行期只认：WinTune.exe + WinTune.config + app\ + runtime\ + MiniMonitor\ + assets\。
  src\ / build.ps1 / vendor\ 是开发侧的东西，运行不需要。

三、重新编译
  1. 关掉正在运行的 WinTune（单实例互斥，占着 exe 会编译失败）。
  2. 在 WinTune 目录执行 .\build.ps1。
     脚本做的事：补齐硬件依赖 → 统一 UTF-8 BOM → csc 编译 src\*.cs → 校验必要文件与 dll 数量。
  3. 产物只有 WinTune.exe 一个文件；dll 全部留在 runtime\，由 Program 里的
     AssemblyResolve 从 runtime\ 按同名程序集加载，原生库再由 InitRuntimeDir() 的 SetDllDirectory 定位。
  4. vendor\ 不存在时脚本会跳过 WebView2 复制（runtime\ 里已有就不影响编译）。

四、源码地图（22 个文件，按职责分，不互相抢活）
  Program.cs        入口：命令行开关（--selftest / --repairjson / --depsjson / --diag / --elevated）、单实例、依赖自愈后拉起窗口
  MiniBoot.cs       启动时只检查一次 MiniMonitor：先确保 PawnIO 驱动在跑（已装但停了就 sc start），再拉起 MiniMonitor；退出不管
  MainForm.cs       窗口与 WebView2 宿主，消息分发（init / hwSample / scan / exec / tweak / maint / repair / upd / deps / mm / net）
  SplashOverlay.cs  启动过渡层：深色底 + W 呼吸 + 旋转弧 + 进度微光，页面导航完成后淡出
  Json.cs           极简 JSON 构建与解析（JB 链式 API + Json.Str/Bool/StrList）
  SysInfo.cs        系统版本、管理员判定、还原点等基础环境（Log 日志类也在这个文件里，写 %LOCALAPPDATA%\WinTune\logs）
  Guard.cs          删除白名单与安全校验（AllowedRoots、符号链接跳过、系统关键目录保护）
  Scanner.cs        空间清理的扫描项、体积统计、ItemJson 输出（含 force 标记）
  Residue.cs        旧软件残留扫描/占用检测；已不再接入「一键清理」，避免把正在使用的应用误判为残留
  Cleaner.cs        删除执行：预检、清单备份、还原点、逐项删除与结果回报
  Registry.cs       注册表清理项的扫描与删除
  Maintenance.cs    启动项 / 计划任务 / 后台服务 三类维护项的读写（注册表只列残留，启动项与计划任务全量列出）
  Tweaks.cs         系统优化 48/57 项：档位、应用、撤销、原值备份
  Apps.cs           已安装程序读取与卸载入口
  Repair.cs         维护修复 12 个动作：体检（Probe）、执行、状态记录（repair-state.log）
  Hardware.cs       硬件采样（LibreHardwareMonitor），fast/全量两种口径
  UpdateResidue.cs  更新残留：旧版本目录、安装包缓存、软件自带备份的判定与强制删除
  Dependency.cs     WebView2 运行时检测与静默安装
  Tools.cs          运行依赖检测（WebView2 运行时判定 + deps 回包），不含任何第三方软件
  SelfTest.cs       --selftest 自检：残留、删除链路、调整往返、占用停止删除

五、运行时唯一协议
  界面 → 引擎：window.chrome.webview.postMessage({cmd:"...", ...})
  引擎 → 界面：PostWebMessageAsJson({type:"...", ...})
  命令：init / hwSample（hwFast 保留兼容）/ evidence / scan / cancel / prepare / exec /
        regScan / maintScan / maintApply / tweakList / tweakApply / tweakRevert /
        apps / uninstall / updScan / updApply / repairList / repairRun / deps /
        mmGet / mmSet / openPath / netInfo / netRun
  新增命令必须同时在 MainForm.cs 的 switch 和 app\index.html 的消息表里登记，两处不漏。

六、数据与状态文件（都在 %LOCALAPPDATA%\WinTune\，不写用户文档目录）
  logs\           每次运行一个日志
  backup\         删除清单（manifest-时间戳.txt）
  webview2\       界面缓存
  repair-state.log 维护修复的执行记录（id|时间|需重启|成功）
  repair.json     --repairjson 导出的体检结果，排查用
  tweaks\state.tsv 系统优化的原值备份（键、值名、原值）
  selftest.log    --selftest 输出

七、硬规则（改代码时不要破）
  · 只删白名单内路径；系统关键目录、文档、桌面、Program Files 本体一律拒绝。
  · 普通清理只清空目录内容、保留目录；必须整目录删的（Windows.old 等）单独放行。
  · 明确会导致系统/软件损坏的项既不列出，也不提供修改入口（DriverStore、程序本体、在用软件数据目录）。
  · “不建议”≠“不能”：不建议删除的项可勾选强制删除，但必须二次确认并写明代价。
  · 停服务、杀进程只针对“可执行文件确实落在该残留目录内”的对象，系统关键进程不动。
  · 界面文字只有三档生效方式：立即生效 / 重启资源管理器 / 重启生效。
  · 不编造数据：读不到就不展示，不占位、不估算体积、不写“读取中”。

八、验收清单（发布前跑一遍）
  1. .\build.ps1 无警告、无报错，输出 runtime 内 27 个 dll。
  2. WinTune.exe --selftest，日志末尾的自检全部“通过 ✔”，没有 ✘。
  3. WinTune.exe --repairjson，repair.json 里 12 项都有 state / stateText。
  4. 打开界面：概览数字每秒在动、内存是平面横条、左下角显示版本号、图标为新图标。
  5. 一键清理扫一遍：不存在的项不出现，完成行有“另有 N 项……”；
     勾一个不建议删除的项，行尾变“强制删除”，底栏红字提示，点删除有二次确认。
  6. 维护修复：执行带重启的动作后回卡片看，状态应为“需要重启”；重启后再看变“已修复”。

九、1.5.6 这一轮改了什么（以及改在哪）

  1. 维护三类全量列出（Maintenance.cs）
     （注：注册表这一条在 1.5.7 已改回“只列残留”，见第十节；启动项与计划任务仍是全量）
     · ScanReg / ScanUninstallKey / ScanAppPaths：不再只挑失效项。HKLM 64 位、WOW6432Node、
       HKCU 三处的卸载登记 + 两个 App Paths 全列，按 name 去重，上限 600 条；
       指向不存在的标“建议删除”（Act=del），正常登记标“不建议修改”（同样只给 del，
       代价写进 Detail：删了软件会从“应用和功能”里消失，但本体还在盘上）。
     · ScanStartup：Run / RunOnce（HKCU + HKLM，含 WOW6432Node）+ Policies\Explorer\Run
       （组策略写的启动项，原来漏了）+ 两个启动文件夹；
       新增 ScanUwpStartup（“设置 → 启动应用”那批：读 HKCU\...\AppModel\SystemAppData 的
       State，id 前缀 w:，2=启用 1=停用，普通权限可改）与 ScanWinlogon（Userinit / Shell，
       id 前缀 g:，只列不改，被改写时标“已被改写”）。
     · ScanTasks：弃用 System32\Tasks 目录遍历（普通用户读不到，实测顶层 0 条），
       改走 schtasks /Query /FO CSV /V 全量查询（普通权限实测 207+ 条，含 \Microsoft\），
       配套 RunText（后台线程读 stderr，避免管道满写阻塞）与 ParseCsv（引号内逗号/换行转义）。
     · 动作层：新增 task disable / enable（schtasks /Change /TN ... /DISABLE|/ENABLE）、
       startup del（删值 + 清 StartupApproved 标记）、UwpStartupSet（写 State）；
       StartupOff 改成“只写 StartupApproved 禁用标记、原值不删”（组策略项没有标记可挂时才退回删值）；
       TaskBackup 先读任务文件、失败再用 /Query /TN /XML 取 XML 备份。

  2. 修复执行不再“卡住”也不再乱码（Repair.cs）
     · RunCmd 改走 Pump：按 \r 与 \n 双分隔断行（\r\n 算一次），丢弃 <0x20 控制符，
       对 \r 进度刷新做 150ms 限流。SFC / DISM 是原地刷新进度的，原来按行读会把几十段
       进度拼成一行并夹进控制符，显示成“阶段 19 朔…”。
     · 启动即回两行：已启动（命令名）+ 这类工具常常先静默一段才吐第一行，结束回“退出码 N”。

  3. 界面（app\index.html）
     · 弹窗体系：所有原生 confirm / alert 换成 tipModal / confirmModal，样式统一到 .modal/.mbox
       （620px、#141a22、左侧 3px 蓝色强调条、右上关闭、底部按钮右对齐、min-width 104px）。
     · 执行面板 repExecPanel：脉冲点 + “已启动，正在等工具输出” + 每 500ms 的“已运行 N 秒”
       （>20 秒改文案说明不是卡死）+ 不确定进度条 + 日志占位说明；首行输出到达后切“工具正在输出…”。
     · 完成弹窗：标题“完成 · 名称”，正文结果/当前状态/耗时/提示/日志，按钮“打开日志”+“知道了”。
     · 维护卡片：repFixable 决定是否给按钮（bad / pending / unknown 才给），文字一律“修复”，
       ok / fixed 不渲染按钮；状态词 unknown→“未检查”、bad→“异常”。
     · 注册表 / 启动项 / 计划任务页：新增筛选框 #mSearch 与计数 #mCount，按 name/sub/path/detail 过滤，
       切模块清空；全选只覆盖“建议删除 / 建议修改”，其余手勾。
     · 更新残留列宽：.crow 与 .rghead 网格改 22px 200px minmax(0,1fr) 84px 152px 78px，
       .sz 加右内边距、.tg 加左内边距与 gap，“37.5 MB”与“可强制删除”不再贴在一起。
     · 概览：开头说明句重写；MiniMonitor 卡新增“版本”行，读 Hardware.MmJson 的 version / build
       （exe 无版本资源时显示构建日期并注明“exe 未标版本号”，不编造版本）。
     · maintApply 动作摘要修正：id 里本身含 |（如 u:HKLM|SOFTWARE\...|{GUID}），
       原来 split("|")[1] 会显示成注册表路径，改成取最后一段动作名。

  4. 验收补充：本机（非管理员）实测 注册表 98 条 / 启动项 3 条 / 计划任务 208 条，
     扫描耗时 13ms / 1.57s；vm 上以 mock 数据渲染 11 张页面截图核对布局与文案。

十、1.5.7 这一轮改了什么（以及改在哪）

  1. 空间清理页不再挂“注册表残留”（Scanner.cs / app\index.html）
     · Scanner.ScanAsJson 里删掉 RegistryScan.DetectUninstall / DetectRun 的统计与 regSummary 事件，
       也删掉那条 Id=“registry”、Action=“registry”的 care 项 —— 注册表不再混进磁盘页。
     · MainForm：删 lastUninst / lastRuns 字段与扫描时的那两次 Detect；StartExecute 去掉 doReg
       与 RegCleaner.Clean 调用，cleanDone 不再回 regDeleted / regFailed / regBackup。
     · 前端：删 S.reg 与 renderGroups 里的“注册表残留”分组块、case “regSummary”（保留空分支
       兼容旧消息）、onCleanDone 里的“注册表清理 N 条 / 注册表备份”两行、.crow 的 registry 特殊样式。
     · src\Registry.cs（RegCleaner）与 RegistryScan 的 Detect* 保留：自检里仍在用它们打印
       失效卸载项 / 失效启动项清单，只是不再参与清理链路。

  2. 注册表清理改回“只列残留”（Maintenance.ScanUninstallKey / ScanAppPaths）
     · 过滤顺序：无 DisplayName → 跳过；ParentKeyName（补丁）→ 跳过；SystemComponent=1 → 跳过；
       DisplayName 以 Microsoft 开头或 Publisher 含 Microsoft → 跳过（微软运行库的卸载程序常在
       Package Cache，缓存被清过就会误判失效）；同名的 64/32 位重复 → 只留一条。
     · 判定：InstallLocation 写了且目录存在 → 视为装着，跳过；目录没写、卸载程序也在 → 跳过；
       只剩“目录写了但不存在”或“卸载程序写全路径但文件不在”才列，状态一律“建议删除”、Act=del。
     · App Paths：只保留默认值指向的 exe 已不存在的；指向有效的直接跳过。
     · 实测：本机 98 条 → 0 条；上限仍是 600 条。

  3. 删掉“策略清理”整个模块
     · 后端：ScanJson 的 policy 分支、ScanPolicy / ReadPolicy / PolDef + Policies 表、
       Do 的 policy 分支与 PolicyDelete 全部移除（约 80 行）。
     · 前端：cleanSeg 去掉 policy 按钮（六标签变五标签）、MODNAME / MODTIP 去掉 policy 键、
       页头说明改“分五个方向”。
     · 说明：它查的是“禁用任务管理器 / 注册表编辑器 / 控制面板”这类锁系统开关，正常机器恒为 0 条；
       SysInfo.PolicyHint（概览与检测规则页显示“组策略：未配置”）是另一件事，保留。

  4. 各页说明文案按实际行为重写（app\index.html 的 .pagehead）
     · 一键清理：五个方向各管什么、注册表只列残留、启动项与计划任务全量、切标签保结果、动手前写备份。
     · 系统优化：档位卡与“自定义”的联动、点一行看键、点“应用”才写入、可逐项撤销。
     · 应用卸载：清单来自卸载登记、调用软件自身卸载器、无可卸载入口的单独标出。
     · 维护修复：12 个官方工具、五种状态、只有异常/待重启/未检查才出“修复”、跑完写结果与耗时。
     · 工具箱、检测规则各补一句“这页是干什么的”；检测规则“留证”改为
       “删除清单 + 还原点（注册表改动另存备份）”；概览硬件说明里残留的“立体圆环”改回平面。
     · 空间清理相关表述同步：扫描完成行不再追加“· 注册表 N 条”，删除确认弹窗不再提“导出注册表备份”，
       备份卡文案改为“删除清单、维护备份、系统优化的原值导出”。

  5. 验收：build.ps1 零警告（374,272 字节 / runtime 27 dll）；--selftest 四项通过；
     反射实测 reg 0 条（16ms）、startup 3 条、task 208 条、service 9 条；
     vm 渲染断言 cleanSeg 为五个标签、注册表页 2 条 mock 残留正常列出、无 pageerror。


十一、1.5.8 这一轮改了什么（四张截图 → 四处改造）

  1. 修复弹窗尺寸固定（app\index.html 的 CSS + 弹窗构造处）
     · 新增 .mbox.fixed{width:760px;height:640px;max-height:84vh}，内层 .mb 改纵向 flex：
       日志区 flex:1 且 min-height:120px，超出在弹窗内部滚动，弹窗本身不再随日志条数变形。
     · repExecPanel 打开时给 .mbox 加 fixed；onRepairDone 的完成弹窗同样加 fixed；
       共用 closeModal 关窗时移除 fixed，其它小弹窗（删除确认、错误提示）不受影响。
     · 小屏（如 1366×768）由 max-height:84vh 兜底，保证底部按钮始终可见。

  2. 进度行原地刷新，不刷屏（src\Repair.cs 的 Pump）
     · Pump 从“遇 \r 或 \n 就切行”改成预读 pendingCR：区分 \r\n（真换行 endLine）与单 \r（重画 repaint）。
     · 同一逻辑行的首帧照常 append（rep=false），其后 150ms 内的重画帧发 Line(s, true) 让前端替换最后一行；
       有重画历史的行在收尾时也用替换，避免同一行出现两次。
     · 前端 onRepairOut 按 rep 字段维护 lines 数组：rep 时替换末行、否则 push；超过 4000 行裁到 3000 行。
     · 实测（test_pump.ps1 反射调 RunCmd，喂含 \r 的文本）：事件 7 条，
       10% 帧 rep=False（新行）、90% 帧 rep=True（替换上一行）、收尾与普通行照常 append。

  3. 两处文案（app\index.html）
     · 空间清理“不建议删”的徽章：保留 · 可强制 → 建议保留（与红色风险色、组内不可点保持一致）。
     · 修复完成弹窗按钮：知道了 → 完成（“打开日志”保留）。

  4. 概览内存卡补内存条信息（src\Hardware.cs）
     · 新增 MemModulesJson()：WMI Win32_PhysicalMemory 取 Capacity / Speed / ConfiguredClockSpeed /
       SMBIOSMemoryType / DeviceLocator / Manufacturer / PartNumber，输出
       [{gb,speed,cfg,type,slot,vendor,part}]；300 秒内存缓存，避免每次采样都查 WMI。
     · SampleJson 在 memTemp 之后挂 memModules 字段（协议新增，前端按数组读）。
     · 前端：同容量同频率的条目合成一条「16 GB × 2 · DDR4-3600」，频率行「3600 MT/s（标称 N）」；
       无 WMI 数据时回退旧的 memName 单行，读不到不占位。
     · 依赖：仍是 System.Management.dll（GAC 自带，build.ps1 早已 /r 引用，Cleaner.cs 也在用），
       未新增任何第三方组件；普通权限可读。
     · 本机实测：2 根 16 GB、Speed=3600、ConfiguredClockSpeed=3600、SMBIOSMemoryType=26(DDR4)、
       DeviceLocator=Controller0-ChannelA/B-DIMM1、Manufacturer=0x0B92；
       反射调 MemModulesJson 输出 16 GB / DDR4 / 3600 MT/s × 2。

  5. 验收：build.ps1 零警告（376,832 字节 / runtime 27 dll）；test_pump.ps1 事件序列符合预期；
     vm 渲染断言：概览内存卡显示「16 GB × 2 · DDR4-3600 / 3600 MT/s」、空间清理徽章为「建议保留」、
     执行弹窗 760×640 且进度只有一行（62.3% 原地更新）、完成弹窗按钮为「打开日志 / 完成」、无 pageerror。


十二、1.5.9 这一轮改了什么（软件残留分组：可折叠 + 组头勾选）

  1. 组头加折叠三角（app\index.html 的软件残留渲染段）
     · .rghead .nm 改 flex，名字前放 .tri（▾ 展开 / ▸ 收起）；点三角或点软件名 = 收起/展开该软件下的目录清单。
     · 每个软件组用 <div class="rgrp" data-grp="软件名"> 包住组头与子行，收起态加 .fold，
       CSS：.rgrp.fold .crow.sub{display:none}；只改 DOM 的 class 与三角字符，不整页重渲染。
  2. 默认展开规则：组内有可删目录（建议删除 / 有残留）→ 默认展开；整组都是「在用 · 不可删」→ 默认收起。
     · 渲染时 open = S.resFold 有记录 ? 记录值 : (可删数 > 0)，不再把默认值写回 S.resFold。
       否则增量扫描时先到的 vendor 项（可删 0）会把组锁成收起，后面补上可删项也不展开。
     · 只有用户手动点过三角的组才写入 S.resFold，重绘后按用户的选择走；重新扫描时清空。
  3. 组头复选框拆出来单独响应：点 .rghead .ck → toggleResGroup（勾上 = 该软件下全部可删目录，半选 = 部分）；
     点行内其它位置改为折叠切换，不再触发全选，避免点名字时误勾。
  4. 子在用·不可删项点不动：原先点击子行会把 res_ 项写进 S.sel，预检阶段才被 Guard 拒；
     现在点击时先按 resDeletable 判断，不可删的直接 return（与徽章口径一致）。
  5. 组头路径文案补可删数：「5 个目录（5 个可删） · C:\...」；整组没有可删目录时勾选框置灰（off）。
  6. 验收：build.ps1 零警告（376,832 字节 / runtime 27 dll）；vm mock 25 个软件组断言
     BAD=[]（有可删必展开、全不可删必收起）；三角收起后子行 offsetParent 全为 null、再点恢复；
     组头勾选 5/5 项 → 再点剩 0、勾选框回空；在用项点击 selected=false；无 pageerror。


十三、1.5.10 这一轮改了什么（“有残留但打开目录是空的/位置不可用”）

  1. 现象与根因
     · 传递优化缓存（C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\
       DeliveryOptimization）在列表里显示 3.67 GB，点「打开目录」却弹「位置不可用 / 拒绝访问」。
     · 根因不是目录被删、也不是体积算错：这个目录在 NetworkService 的系统配置文件里，
       ACL 只给 SYSTEM / NetworkService，资源管理器是普通权限，进不去；本程序有管理员权限，
       所以体积打得开、能统计。
  2. 打开目录改成分三态判断（src\MainForm.cs 的 OpenFolder / ProbePath）
     · Directory.Exists 对“存在但没权限”的目录也返回 false（Test-Path 直接抛拒绝访问），
       改用 File.GetAttributes 分三态：1=可访问、0=真的不存在、-1=存在但被拒。
     · 新增 CanList（能否列出顶层条目）兜底：列不出来的目录，资源管理器同样打不开。
     · 新增 IsExplorerLocked：Windows\ServiceProfiles 下的 NetworkService / LocalService
       子目录一律不交给 explorer（ServiceProfiles 本身仍可打开）。
  3. 行为
     · 可访问：照旧 explorer 打开。
     · 存在但打不开：先往上找最近一个能列出的上级（NearestReadable），打开它；
       同时回一条 openPathResult{ok:false, reason:"denied", path, opened}。
     · 真的不存在：回 openPathResult{ok:false, reason:"gone"}。
  4. 前端（app\index.html 的 onOpenPath）
     · 不再让资源管理器弹系统「位置不可用」框，改用自绘弹窗说清楚：路径、为什么打不开、
       文件还在、体积是管理员权限实测、已打开的上级目录、想清就勾选走清理流程。
     · 目录不存在时提示“可能刚被清理，点重新扫描刷新”。
     · 消息名：openPathResult（新增，挂在消息 switch 里）。
  5. 验收：build.ps1 零警告（377,856 字节 / runtime 27 dll）；反射实测 ProbePath：
     DeliveryOptimization = -1、ServiceProfiles\NetworkService = 1 且 IsExplorerLocked=True、
     C:\Windows = 1 / False、不存在路径 = 0；NearestReadable 对受限路径返回
     C:\Windows\ServiceProfiles；vm 渲染断言：denied 弹窗标题「这个目录打不开 · 系统保护目录」、
     按钮「知道了」、gone 弹窗「目录已经不在了」、分组折叠回归 BAD=[]、无 pageerror。


十四、1.5.11 这一轮改了什么（MiniMonitor 目录补齐依赖）

  1. 现象：概览 MiniMonitor 卡点「打开」弹原生提示「程序目录里缺文件，CPU 温度会读不到：
     LibreHardwareMonitorLib.dll / PawnIOLib.dll / System.Memory.dll / System.Buffers.dll /
     System.Runtime.CompilerServices.Unsafe.dll / MiniMonitor.exe.config」。
  2. 根因：这个提示是 MiniMonitor.exe 自己做的依赖自检。1.5.10 及更早的包里
     MiniMonitor\ 子目录只放了 MiniMonitor.exe（98,304 字节，无版本资源、无 config），
     依赖的 4 个 dll 与 config 都只存在于 runtime\，而 .NET 只从 exe 所在目录加载，
     不会去父目录的 runtime\ 找。
  3. 修复：把 MiniMonitor 官方发行包 MiniMonitor-v1.5-win64 的配套文件原样放进
     WinTune\MiniMonitor\（与自检列表一一对应，不新增其它组件）：
     · MiniMonitor.exe.config（907 字节，含 System.Memory / System.Buffers / Unsafe 的 bindingRedirect）
     · LibreHardwareMonitorLib.dll（1,203,200）
     · PawnIOLib.dll（39,248）
     · System.Buffers.dll（20,856）
     · System.Memory.dll（142,240）
     · System.Runtime.CompilerServices.Unsafe.dll（17,000）
     旧的 98,304 字节 exe 原样保留（只补依赖、不换程序本体），备份在 %TEMP%\MiniMonitor_98304_backup_*.exe。
  4. 说明
     · WinTune 启动时已设 WorkingDirectory = exe 所在目录（Hardware.StartMiniMonitor），
       子进程按自身目录加载，不依赖父进程当前目录。
     · LHM 另外引用的 HidSharp / DiskInfoToolkit / RAMSPDToolkit-NDD / BlackSharp.Core 属可选件
       （缺了只是对应传感器读不到，不会拦启动）；WinTune 自己的 runtime 里有，MiniMonitor 目录
       按官方发行包原样交付，不额外复制，避免包体膨胀。
  5. 验收：build.ps1 零警告（377,856 字节 / runtime 27 dll）；--selftest 显示
     MiniMonitor=MiniMonitor\MiniMonitor.exe、随包=True、四项通过；打包 60 条目
     （MiniMonitor\ 下 7 个文件：exe + config + 4 dll + LHM）。
     注：MiniMonitor 的悬浮窗属 GUI，按用户要求未在本机做界面测试，仍由用户点「打开」确认。


十五、1.5.12（空间清理卡片标题改字）

  · app\index.html 第 769 行：card("元安全可删", …) → card("安全可删", …)。
    纯文案，卡片数值口径（随意删项目合计）不变，无逻辑改动。
  · 验收：build.ps1 零警告（377,856 字节 / runtime 27 dll）；vm 渲染取卡标题
    得到 "安全可删"，页面全文不再出现「元安全可删」，无 pageerror。

十六、1.5.13（系统优化页底部功能条恢复）

  1. 现象：切到「系统优化」页后，列表底部没有那条带「已选 N 项 / 清空 / 恢复默认 / 应用」的 dock，
     整页只剩列表，勾了项也没地方点应用。
  2. 根因：样式表第 271 行有 [hidden]{display:none!important}，而 #tuneDock 的 HTML 默认带 hidden 属性；
     renderTune() 结尾只写了 $("tuneDock").style.display = "flex"，被 !important 的 [hidden] 规则压死，
     所以内联 display 永远不生效——dock 从改版起就没显示过，不是加载失败。
  3. 修法：app\index.html 的 renderTune()（原 2176 行）改为先摘 hidden 属性再设 display：
       var dockEl = $("tuneDock");
       if(dockEl){ dockEl.hidden = false; dockEl.style.display = "flex"; }
     其余页面条（#cleanbar / #ubar / #pbar 等）一直用 el.hidden 属性切换，不受这条 !important 影响，无需改动。
  4. 验收：vm 1440×900 mock 渲染，切到系统优化页后 #tuneDock 的 hidden 属性=false、computed display=flex、
     实际尺寸 1224×60 贴在视口底部；勾 3 项后文案联动为「已选 3 项（自定义）· 立即生效 2 · 重启后生效 1」、
     按钮变「应用 3 项」；点「清空」回到「已选 0 项 / 应用」；无 pageerror。
  5. 编译与打包：build.ps1 零警告（377,856 字节 / runtime 27 dll）；--selftest 四项通过。

十七、1.5.14（按钮统一「确定」、清理项去重、传递优化改走服务、更新残留并入底栏）

  1. 弹窗按钮：全页面把最后那个「知道了」统一改成「确定」（判定依据弹窗、打开目录失败提示、
     提示类弹窗的默认按钮等 6 处）。现在所有只读弹窗右下角都只有一个「确定」。
  2. 重复项：Scanner.BuildPlan 里 INetCache 被登记了两次（「网页临时文件」和「临时 Internet 文件」），
     列表里就出现两行同名项。删掉前一条，只保留「临时 Internet 文件」。
  3. 传递优化缓存删不掉：这个目录归 NetworkService 所有，ACL 只放 SYSTEM / NetworkService，
     普通进程（含管理员）直接删文件一律拒绝访问，旧逻辑只能把文件排进「重启后删除」甚至直接失败。
     现在 do_cache 走 Windows 自带的清理接口 Delete-DeliveryOptimizationCache（由 DO 服务执行，
     非管理员也能成功），不再逐文件硬删；接口失败时给一条明确原因，不刷一屏失败明细。
     本机实测：3.67 GB 缓存由该接口清空成功。
  4. 软件更新残留：卡片里原来那条「清空选择 / 应用」小功能栏整个去掉。勾选结果直接并进页面底部
     那条功能栏（统计「已选 N 项 · 预计释放」含更新残留），按钮统一叫「彻底删除」；
     只勾更新残留时点它直接弹「删除确认」；和空间项一起勾时，先做空间清理、做完自动接着删更新残留，
     最后合成一个完成窗（多一行「更新残留 完成 X 项」）。
  5. 验收：build.ps1 零警告（378,880 字节 / runtime 27 dll）；vm 渲染：临时 Internet 文件只剩一行、
     传递优化缓存保留为一项；只勾更新残留 → 底栏「已选 1 项 · 218 MB」→「删除确认」→ 发出 updApply；
     空间项 + 更新残留同选 → prepare → execute → 自动 updApply → 完成窗「更新残留 完成 1 项，失败 0 项」；
     弹窗按钮文本为「确定」；全程无 pageerror。

十八、1.5.15（完成窗耗时换单位、按钮改「完成」）

  1. 清理完成窗的「耗时」不再写成毫秒：不到 1 分钟按秒（保留 1 位小数），不到 1 小时按分钟，
     再往上按小时；四舍五入正好到 60 的直接进位到上一级（不会出现「60.0 秒」）。1160 ms 显示「1.2 秒」。
  2. 完成窗右下角「重新扫描」改成「完成」：点一下只关弹窗，不再自动重扫；要刷新列表回页面上方点「开始扫描」。
     打开目录失败提示里那句「点重新扫描可以刷新列表」同步改成「点开始扫描」。
  3. 验收：build.ps1 零警告（378,880 字节 / runtime 27 dll）；vm 渲染 1160 ms 显示「1.2 秒」，
     7 组耗时样本（0.5 秒 / 1.0 分钟 / 2.1 分钟 / 1.0 小时…）全部符合；点「完成」后弹窗关闭且没有新发 scan 请求；无 pageerror。
十九、1.5.16（回收站体积与项数）

  1. 症状：清理页「谨慎删」里那行「回收站」体积一直显示「—」。原因是 recycle 是动作项
     （action=empty_recycle，没有 Path），老的 MeasureItem 走目录遍历，回收站目录本身
     没权限展开，测出来就是 0，前端拿到 0 就渲染成「—」。
  2. Scanner.cs 新增 RecycleBin 类：P/Invoke SHQueryRecycleBin(string, ref RBINFO)
     （RBINFO = { cbSize, i64Size, i64NumItems }），Query(out bytes, out items) 传空路径
     统计所有驱动器。ScanAsJson 的 action 分支遇到 empty_recycle 时填 it.Bytes / it.Files。
     项数或字节为 0 → Available=false、SkipReason=「回收站是空的」；接口失败 →
     「读不到回收站内容」。两种情况都不再列这一行（与其它空项同一口径）。
  3. Cleaner.EmptyRecycle 改为无参：SHEmptyRecycleBin(IntPtr.Zero, null, 0x1|0x2|0x4)
     静默清空（不弹系统确认框、不显示进度条、不提示重启），清空前后的 Query 差值即本次
     实际释放量，累加进 res.Freed；清完仍有残余（属于其他用户或被占用）直接抛异常
     「回收站还有 N 项没清掉」，不虚报成功。
  4. selftest 增加「[回收站实测]」一行，直接打印项数 / 字节，便于核对界面数字。
  5. 验收：build.ps1 零警告（380,416 字节 / runtime 27 dll）；--selftest 通过 8/8、3/3、
     三条「结果=通过 ✔」，回收站实测 20 项 / 5,545,829 字节；vm 渲染 mock（5,231,224 字节
     / 10 条）显示「4.99 MB」，全程无 pageerror。
二十、1.5.17（卸载程序丢失的项：清除残留）

  1. 症状：应用管理里有的程序点「卸载」没有任何反应。原因是注册表登记的 UninstallString
     指向的 exe 已经不在了（或被清理软件清掉了）；还有一类压根没写卸载命令。
     旧逻辑只要 UninstallString 非空就认为「可卸载」，按钮照样可点，点了必然失败。
  2. 判定：Apps.UninstallerExe 解析命令里的可执行文件 —— 带引号直接取引号内；不带引号时
     先按 .exe 截断（E:\Program Files\Foxmail 7.2\uninst.exe 这种路径带空格的不会再被截成
     E:\Program）；相对名（MsiExec.exe）到 System32 找。得到 ok / missing / none 三态。
     canUninstall 只给 ok 的项；missing / none 且不在运行库保护名单里的，给 canRemove。
  3. 交互：状态列显示「卸载程序丢失」或「无卸载命令」（红），按钮由「卸载」换成「清除残留」。
     点开后（appLeftover）先算出安装目录、文件数、总字节、开始菜单/桌面里指向它的快捷方式数量，
     目录里若出现 Storage / Mail / Chats / userdata 这类目录会单独标红提示可能是邮件或聊天数据。
     两个动作：仅移除登记（文件不动） / 删除目录并移除登记。
  4. 执行（Apps.Remove）：先把这条卸载登记导出成 .reg 备份（Backup\app-<键名>.reg），
     再 DeleteSubKeyTree；full 模式下 DeleteTree 逐文件删、删不掉先排 MoveFileEx 重启删除；
     仍有失败且是管理员时 takeown /R + icacls 接管目录权限重试一轮；最后删快捷方式。
     目录一律过 Guard.CanDelete（盘根、系统关键目录、联接点直接拒绝），不做越权删除。
  5. 卸载失败的兜底：点「卸载」时启动卸载器抛异常，Apps.MarkFail 记住这一项，
     列表刷新后它自动变成「清除残留」，不用重启软件。
  6. 运行库保护不变：VC++ / .NET / WebView2 / DirectX 等即使卸载入口坏了也不给清除按钮，
     删了别的软件会起不来；它们只会出现在自检清单里。
  7. 顺带修正：Find 比较 id 时忽略大小写（注册表路径本身不区分大小写，回传 id 可能大小写不同，
     旧实现会「找不到这个程序」）。
  8. 自检：新增「[清除残留自检]」——在 HKCU 建一条沙箱卸载登记 + 沙箱目录（Storage/mail.eml + app.dat），
     验证 Leftover 报出 2 文件 / 3072 字节 / 敏感=Storage，再 Remove(full) 验证登记与目录都被删掉。
  9. 验收：build.ps1 零警告（390,656 字节 / runtime 27 dll）；--selftest 通过 8/8、3/3、
     回收站与清除残留两项「结果=通过 ✔」；vm 渲染：KPI「卸载入口失效 2」、Foxmail 行
     「卸载程序丢失 + 清除残留」、弹窗「1.99 GB · 5162 个文件 · 快捷方式 2 个 + Storage 警告」、
     执行中进度行、完成窗「1.99 GB / 5162 个文件 / 登记已移除 / 快捷方式删了 2 个」，全程无 pageerror。

二十一、1.5.18（网络修复：DNS / 网卡 / 网关 / hosts / 测速）

  1. 新增独立标签页：左侧栏「工具 → 网络修复」，排「维护修复」之后。
     页头四张 KPI：连接状态（已连接 / 未连接、物理口在线数、网卡总数）、默认网关、DNS（主 + 备）、
     hosts（可疑条数或映射条数、总行数、可写状态）。
     下面四段（与清理页同一种分段条，切换保留各自结果）：
       网卡与 DNS    每块网卡的名称 / 描述 / 速率 / MAC / IPv4 / 网关 / DNS / 地址来源 / 虚拟标记，
                     每块卡上「重启这块网卡」「DNS 改自动」；右侧 DNS 区：刷新缓存、全部改自动、
                     阿里 / 腾讯 / 114 三个预设、自定义最多 3 个地址
       网关与路由    默认网关列表、连通性测试、重新获取地址（续租）、重置 Winsock 与 TCP/IP
       hosts 文件    路径 / 总行 / 有效映射 / 可疑条目 / 能否写；可疑条目与全部映射逐行列出，
                     每行可「删除这行」；另有记事本打开、恢复默认、只删可疑、添加一条映射
       网络测速      大数字（平均）+ 进度条 + 平均 / 峰值 / 下载量 / 耗时，下面复用同一份延迟与解析结果
  2. 后端新增 src\NetFix.cs（约 1100 行）。读取走系统接口（NetworkInterface 枚举名称、描述、类型、
     状态、速率、MAC、IPv4、网关、DNS、DHCP、是否虚拟）；改动只调系统自带命令：
       ipconfig /flushdns、ipconfig /release|/renew
       netsh interface ipv4 set dnsservers（source=dhcp 或 source=static address=… + add dnsservers index=…）
       netsh interface set interface name="X" admin=disable|enable
       netsh interface ip delete arpcache、netsh winsock reset、netsh int ip reset
       ping（连通性）、hosts 走文件读写。全程不引第三方组件、不改驱动。
  3. 十二个动作，改配置的一律先弹确认（写明会不会断网、要不要管理员、代价是什么）：
       刷新 DNS 缓存       只读，不改任何设置
       DNS 改为自动获取    已连接的物理网卡 source=dhcp；固定 IP 的网卡退一步清空自定义 DNS
       设置自定义 DNS      最多 3 个 IPv4，可指定单块网卡；改完立刻刷一次缓存
       重启网卡            禁用 → 等状态 → 启用 → 等状态 → 对这块网卡 /renew，失败逐段报
       重置网络栈          winsock reset + int ip reset，结果里写明「需要重启电脑才生效」
       重新获取 IP 与网关  先清 ARP 缓存让网关 MAC 重新学习，再 release + renew
       hosts 打开 / 恢复默认 / 只删可疑 / 添加一条 / 删除某一行（按 line:N 精确删，防行号漂移）
       连通性测试          默认网关、公网 223.5.5.5、www.baidu.com 域名解析，三项分开给回应数与耗时
       网络测速            见第 5 条
  4. hosts 只认三条能验证的证据，私有内网 IP 不算可疑：
       ① IP 写法不合法（不是合法 IPv4 / IPv6）
       ② 指向 127.0.0.1 / 0.0.0.0 / ::1，且域名不是本机名（对应网站会被屏蔽或劫持）
       ③ 同一个域名同时指向两个不同 IP
     解析抽成 NetFix.ParseText，界面与 --selftest 共用同一套规则，注释行与空行原样保留。
     写之前一律先备份 %LOCALAPPDATA%\WinTune\backup\hosts-时间戳.txt，再按 Encoding.Default
     （与记事本一致）整文件写回，写完 flushdns；任何一步失败就把异常原文写进结果，不假装成功。
  5. 测速：直连优先（请求上手动关掉系统代理），直连失败才用系统代理重试并在结果里标 proxy；
     端点 speed.cloudflare.com/__down（20 MB，失败退 10 MB），上限 20 MB / 25 秒；
     每 200 ms 回一帧（已下字节 / 瞬时 / 平均 / 百分比 / 文字），结束回报节点、总字节、总耗时、
     平均、峰值、连接耗时。上传要服务端配合，不做、也不估算。
  6. 权限：界面版首次启动会走 UAC 提权，动作都在提权后的引擎里执行；万一没提权，
     netfix 会直接返回「改 hosts 需要管理员权限，请以管理员身份重新打开本软件」这类原文，
     DNS / hosts 的写入失败也会把命令输出贴出来，不写「已完成」。
  7. 自检新增三段，跟着 --selftest 一起跑：
       [网络诊断]        网卡明细、已连接 / 物理口数量、管理员状态、hosts 汇总
       [hosts 解析自检]  6 行样本文本，期望「有效映射=6、可疑=3」，三条判定逐条命中
       [测速节点连通性]  TcpClient 连 speed.cloudflare.com:443，5 秒超时
     本机实测：7 块网卡 / 已连接 1 / 物理口 1 / 管理员=False；WLAN 192.168.10.109、网关 192.168.10.1、
     DNS 114.114.114.114 + 119.29.29.29、DHCP=True；hosts 总 22 行、映射 0、可疑 0、可写=False；
     样本文本 6 / 3 通过；speed.cloudflare.com:443 通（233~238 ms）；通过 8/8、通过 3/3、
     六处「结果=通过 ✔」、无 ✘。
  8. 前端（app\index.html）：新增导航 data-view="net"（地球经纬图标）与视图 data-v="net"，
     四段用 .seg2 与清理页同一套样式；消息表新增 netInfo / netOut / netPing / netSpeed /
     netSpeedDone / netDone；执行沿用 760×640 固定弹窗（实时贴命令输出与已运行秒数），
     测速不进弹窗、在页面上边下边更新，完成后只弹一条 toast。
  9. 这一轮 mock 渲染发现并修掉两处：
     · 执行弹窗只填了标题、正文、按钮，没有把弹层加上 open，点「开始测试」后界面看起来没反应
       （实际在跑）。现在 netExec 与 onNetDone 都显式打开弹层，先「执行中 · 已运行 0 秒」再转完成窗。
     · 「DNS」KPI 把两个地址拼成一串，1440 宽也折行把卡片撑高；改成主 DNS 大字 +
       「网卡 WLAN · 备 x.x.x.x」小字。
 10. 验收：build.ps1 零警告（420,352 字节 / runtime 27 dll）；--selftest 通过 8/8、3/3、
     六处「结果=通过 ✔」；vm 用 mock 数据（取本机真实读数：7 网卡 / 1 已连接 / 可疑 1 条）渲染：
     KPI「已连接 / 192.168.10.1 / 114.114.114.114 / 1 条可疑」，四段切换正常，
     连通性 ping 表 4/4 · 3 ms、4/4 · 18 ms、1/1 · 24 ms（含域名解析 IP），
     hosts 可疑行 L17 与「删除这行」，测速进行中 41.8 Mbps / 76% 进度、完成 56.3 Mbps 平均 /
     71.2 峰值 / 20.0 MB / 3.0 秒，全程无 pageerror。


二十二、1.5.19（网络修复：进页面自动体检 + 代理 / 服务 / MTU / 静态路由）

  1. 起因：1.5.18 交付后确认过 GitHub 上的网络修复项目（xecvas/Net-Toolkit、espinozan/NetMedic、
     SysAdminDoc/NetForge、OsageP/Reparador-Windows-Pro）本质都是包同一批 netsh / ipconfig 命令，
     搬过来的代码与自己写的等价，还会带进 GPL 之类的许可约束；所以只借鉴功能清单，代码全自写。
  2. 体检：src\NetFix.cs 新增 CheckJson()，一次跑完九类判定，返回
     { type:"netCheck", count, items[{id,level,title,detail,fix,a1,a2,fixName}], ms, at }。
     无问题就返回空数组，前端据此整块隐藏、不占位、不弹提示（用户明确要求「没问题的什么都不显示」）。
     九类判定与对应修复动作：
       ① 没有物理网卡在线（虚拟卡不算）           → nicrestart
       ② 有网卡停在 169.254 自分配地址             → gwrenew
       ③ 已连接网卡没有默认网关                    → gwrenew
       ④ DNS 为空；或 DNS=127.0.0.1 且本机 53 端口没监听 → dnsset 223.5.5.5,119.29.29.29
       ⑤ hosts 可疑条目 > 0（复用 1.5.18 的三条证据，带行号与原因） → hostsclean
       ⑥ WinHTTP 仍挂着代理；或系统代理指向本机死端口 → proxyreset
       ⑦ Dnscache / Dhcp 服务没在运行               → svcrestart
       ⑧ 网卡 MTU ≥1500 且实测路径 MTU 比它小 8 以上 → mtuset
       ⑨ 持久静态路由的网关 ping 不通且不是当前网关（最多 3 条）→ routesdel
     ④ 的 53 端口判定用 TcpClient 连 127.0.0.1:53，避免把「本机自己跑着 DNS 服务」误判成劫持。
     ⑧ 只在网卡写 ≥1500 时才探（ProbeMtu 从 1472 起，能通就说明路径就是 1500，不报问题），避免每次进页面都慢。
  3. 读取层（只读，不改任何设置）：
       ReadProxy()        HKCU\...\Internet Settings 的 ProxyEnable / ProxyServer / AutoConfigURL，
                         加 netsh winhttp show proxy 解析「代理服务器:」行；LocalProxyHostPort() 从
                         127.0.0.1:7890 或 http=... 里抽端口，TcpAlive() 用 350 ms 超时判断端口有没有程序听
       SvcRunning()       sc query 取 STATE 行，RUNNING 或末位 4 视为在跑；WaitSvcState() 轮询等状态
       ReadMtu()          netsh interface ipv4 show subinterfaces，按 Idx/Met/MTU/State/Name 五列取 p[2]、p[4]
       ReadPersistentRoutes()  HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes，
                             值名格式 dest,mask,gw,metric
       ProbeMtu()         PingOptions(64,true) 不分片，包长阶梯 1472→1464→1452→1442→1422→1402→1382→
                         1352→1322→1292→1272，第一个通的即路径 MTU（+28 头），连续两次超时就给「探不出来」原因
       ProbeMtu 与 CheckJson 都走 ShellCapture()（不往界面推流的静默命令，CP936 + 双流 + 超时 Kill）。
  4. 五个新动作（都能在页面上单独点，不只给体检按钮用）：
       proxyreset  netsh winhttp reset proxy；先 BackupReg 导出代理注册表项，再把 HKCU ProxyEnable 置 0，
                    PAC 地址指向本机死端口时一并删除
       svcrestart  依次 stop / start Dnscache、Dhcp 并 WaitSvcState 等回 RUNNING，报告成功个数
       mtuprobe    对主网卡按网关或 223.5.5.5 探测，发 netMtu 消息（iface/declared/mtu/target/note/ok）
       mtuset      netsh interface ipv4 set subinterface "X" mtu=N store=persistent，改完回读校验
       routesdel   先备份 PersistentRoutes 项 → route delete dest mask → 删注册表值；a1 格式 dest|mask|gw
  5. 信息接口扩展：netInfo 里多了 proxy{ie,server,auto,winhttp,localPort,localAlive}、
     services[{Dnscache,Dhcp 的 found+running}]、routes[{dest,mask,gw,metric}]、mtuList[{iface,mtu}]，
     原有 nicList / gateways / dnsList / hosts* 不变，旧前端字段不会因为新增字段而失效。
  6. 前端（app\index.html）：
       页头下面 #netCheck 体检条：标题「检查到 N 个问题」+ 一句「X 个会让网络直接不能用」，
         每行一个色点（risk 红 / warn 黄）+ 标题 + 详情 + 右侧修复按钮；无 fix 的写「要手动处理」；
         0 条时 hidden=true 并清空 innerHTML，重新有内容时自动再出现
       进页面流程：loadNet() → netInfo → renderNet() → 首次 netCheckRun()，修完一个动作自动再查一遍
       网卡与 DNS 段尾部加 MTU 卡：当前 MTU（主网卡名 + 数值）、最近一次探测、
         「探测路径 MTU」、探到后才可点「按探测结果设置」（没探过点了会提示先去探测）
       网关与路由段「修复」两卡之后加「代理」两卡与「静态路由」卡：
         IE 代理开启态 + 端口是否有程序听（死端口标红）、WinHTTP「直连/有代理」、
         Dnscache / Dhcp 运行态（运行中绿 / 已停止红 / 不存在灰）、持久路由逐行「删除」
       NET_META 由 12 条扩到 17 条，新增 proxyreset / svcrestart / mtuprobe / mtuset / routesdel；
       netFix() 与 netConfirm() 都补了参数行（DNS / MTU / 路由 / 网卡）与权限行（需要管理员身份运行）
  7. 一处结构修正：1.5.19 前半段时 MTU 卡被一个提前的 </div> 关到了 netNicPane 外面，
     页面上看着正常但切段时它永远显示、也进不了「按探测结果设置」的启用逻辑；
     本轮在 vm 渲染断言里发现（按钮数 20 但 pane 里查不到 #netMtuNow），删掉那个多余 </div> 修好。
  8. 自检：src\SelfTest.cs 在「[测速节点连通性]」前插入「[网络体检（进页面时跑的那一套）]」，
     打印条目数、耗时，逐条打印 [level] title → fixName，空则打印「未发现问题：界面不显示任何提示」，
     末行「结果=通过 ✔」。本机实测：条目=0、578 ms。
  9. 验收：build.ps1 零警告（435,200 字节 / runtime 27 dll）；--selftest 通过 8/8、3/3、
     六处「结果=通过 ✔」、无 ✘；vm 用 mock 渲染：体检条 3 条（hosts warn / 死端口代理 warn /
     DHCP 服务 risk）、代理卡「开启 · 127.0.0.1:7890（7890 无程序监听）」、WinHTTP「直连」、
     Dnscache 运行中 / Dhcp 已停止红字、MTU「WLAN · 1500」探测后「路径 1492 · 最大不分片包 1464」并启用设置按钮、
     静态路由 1 条（10.20.0.0 / 255.255.0.0 / 192.168.1.1，度量 1）、点 risk 项弹「修 · DHCP Client 服务没在运行」
     含权限行、把体检结果置空后整块 hidden=true，全程无 pageerror。

二十三、1.5.20（视觉与流程融合：首页 dashboard / 分层 / 骨架 / 空状态）

  1. 起因：用户要求「认真学习别人的电脑助手、Windows 工具的 UI 界面，把优秀的界面和 UI 整理融合进来，
     流程也尽可能学习」。本轮先调研四处可对照的参考，再决定改什么、不改什么，不做无依据的换皮。
  2. 参考与实际只借了什么：
       Windows 11 Fluent Design（微软官方规范）
         两层结构：base layer 放导航与命令，content layer 放内容卡片；卡片 elevation=8（layer 1）、
         1px 描边、圆角、Segoe UI Variable 字体、Segoe Fluent Icons 线性图标。
         → 本轮把「导航 + 卡片」这两个层次做成显式的：导航深一档、卡片有描边和内高光。
       Microsoft PC Manager（微软电脑管家）
         左侧图标 + 文字窄导航；首页 hero 卡「先给结论 + 一个大按钮」；2×3 快捷小卡（健康检查 / 进程 /
         深度清理 / 开机管理）；扫描态逐类「扫描中…」；完成后「清理完成 + 逐项已清理 + 完成按钮」。
         → 首页 hero、6 张快捷卡、骨架条、「还没有扫描」空状态都来自这里。
       SophiApp
         左侧分类、顶部搜索框、悬停出说明、底部「Apply / Cancel + 已改动 N 项」变更栏。
         → 底部变更栏与「扫描 → 勾选 → 应用 → 报告」的流程本来就和它同构，本轮不动结构，
           只把底部栏与按钮的状态反馈（hover / active / focus 环）补齐。
       Optimizer（hellzerg）
         顶部标签、逐项开关、风险项脚注警告。
         → 风险色沿用 1.5.x 已有口径（建议删除=绿、不建议/强制=红），不新增风险弹窗噪音。
  3. 结论：WinTune 原有流程（扫描 → 结果 → 勾选 → 底部变更栏 → 应用 → 报告）已经和 SophiApp、
     PC Manager 是同一套骨架，缺的是「首页先给结论」「视觉分层」「加载与空状态反馈」。
     所以 1.5.20 只补这三块，不重排页面、不搬别人的代码、不引第三方 UI 库。
  4. app\index.html 末尾新增整块「1.5.20 视觉统一」CSS：
       :root 补 --r3 / --stroke / --hair / --soft 四个变量（统一圆角、描边、分隔线、弱底色）
       .card      1px 描边 + 顶部内高光 + hover 时描边变亮（卡片在深色底上不再糊成一片）
       .btn       hover 提亮、active 下沉、focus-visible 加 2px 外环（键盘走查也能看见焦点）
       .topbar    sticky + backdrop blur，滚动时页头不跟着跑
       .nav       竖向渐变底；.navitem.on 加描边，选中项与未选中项不再只靠底色深浅区分
       .seg2      胶囊容器 + 内嵌高亮块（四段切换沿用 1.5.18 结构，只换外观）
       .searchbox / .sbox   focus 时外发光
       .hero      网格布局 + 径向与线性双层渐变 + 右上光晕；hdot 状态点、hm 事实胶囊、右侧纵向按钮
       .qgrid / .qcard / .qi / .qb / .ar   3 列快捷卡（1 行 3 张、2 行 6 张）、图标块、hover 抬升 + 阴影、右箭头
       .kbar / .kbar i   KPI 卡底部细进度条（数字之外再给一个长度参照）
       .empty     虚线框空状态（图标 + 标题 + 说明 + 主按钮）
       .sk        骨架条 + @keyframes sks（列表「读取中…」不再是一行灰字）
       .trow / .pgrow 表格行 hover；另加自定义滚动条
  5. 概览页结构改为 PC Manager 式四段：
       pagehead（标题 + 一句话说明）
       → #ovHero  hero 卡：左侧状态点 + 结论标题 + 一句说明 + 事实胶囊行，右侧「一键清理」「网络体检」两个按钮
       → #ovQuick 6 张快捷卡：空间清理 / 系统优化 / 应用管理 / 维护修复 / 网络修复 / 工具箱
         （空间清理卡的副标题 #qFree 写真实「C 盘可用 X GB · 扫临时文件、缓存、更新残留」）
       → #kpiRow  原有的 4 张 KPI 卡（内存 / CPU / 温度 / 系统盘）
       → 硬件状态（1.5.x 原样）
  6. hero 文案由真实数据推导，不写死：新函数 ovPatch() 用 S.sys.disk 与 S.hw 判断，
     只有内存占用 ≥85%、CPU ≥85%、C 盘可用 <10% 三者之一成立时才把对应项列成问题，
     否则标题固定为「运行状态正常」+「内存、处理器与系统盘都在正常范围…」；
     #heroMeta 依次列「内存 X% · CPU X% · C 盘可用 X GB · CPU 温度 X °C · 当前未提权 / 已提权」。
     读不到的项不写、不占位（沿用既有口径）。
  7. 三个新跳转函数（插在 ensureView 之前）：
       go(v)        找到 data-view=v 的 navitem 并 .click()，等价于用户自己点左侧导航
       ovBoost()    loaded.clean=false + go("clean")；借 ensureView 的「首访自动扫描」触发扫描，
                    不另外调 startScan，避免重复扫两遍
       ovNet()      loaded.net=false + go("net")；同理复用网络页进页面自动体检
  8. KPI 卡底部细进度条：kpMemBar / kpCpuBar / kpDiskBar 三条（温度卡不加），
     在 kpiPatch 末尾写入宽度与颜色，然后调 ovPatch() 让 hero 跟同一批数据一起刷新。
     阈值统一为 ≥80 黄、≥90 红（内存、CPU、磁盘同档），避免同一屏里三条条三个标准。
     hero 仍用 85 / 10% 的口径判断「是否要列问题」，两者不冲突：67% / 18% 这类常见值在两边都落在蓝区。
  9. 空状态与骨架：
       清理页 #resultArea 初始内容换成 .empty 空状态（图标 +「还没有扫描」+ 一句说明 +「开始扫描」）
       #appList / #tuneList 的「读取中…」换成 5 条 .sk 骨架，等数据到了再替换
     这两处只改首屏等待时的样子，数据到达后的渲染逻辑不变。
  10. 一处渲染修正：快捷卡与空状态的图标是内联 SVG，但没有继承 .ico 的 fill:none / stroke 规则，
      Chromium 默认按「实心黑填充」画，6 个图标全变成黑团、放大镜变黑圆。补一条
      .qcard .qi svg,.empty .ei svg{width:19px;height:19px;fill:none;stroke:currentColor;stroke-width:1.7;
      stroke-linecap:round;stroke-linejoin:round} 后恢复线性图标（vm 截图对比确认）。
  11. 首屏细节再修两处（vm 截图看出来才动）：骨架条底色原来太接近卡片底，几乎看不见，
      调亮到 #26313d / #33414f；#tuneStat、#appNote 这两行状态文字初值是「读取中…」，
      和骨架条重复，改成初值留空 + :empty 时 display:none，数据到了再填「共 N 项…」。
  12. 验收：app\index.html 218,658 字节（LF，与 vm 渲染副本 sha256 一致）；
      build.ps1 零警告（435,200 字节 / runtime 27 dll）；--selftest 见第八节清单；
      vm 用 mock 渲染断言：heroTitle=运行状态正常、heroMeta 五项齐全、qcards=6、qFree 有值、
      memBar=67%、diskBar=63%、点第 2 张快捷卡后 view=tune/title=系统优化、skRows=5、
      清理页 emptyTitle=还没有扫描、网络页 checkRows=3 且体检条可见、errs=[]。

二十四、1.5.21（工具箱：官方免安装版随包携带 + 一键启动）· 已在 1.5.22 整体撤销

  0. 本节只作历史记录：用户随后要求「彻底删除工具箱与其相关功能」，1.5.22 已把
     导航项、概览快捷卡、整页视图、前端全部 JS、后端清单与三个命令、自检段、tools\ 目录
     全部删掉，当前工程里不存在任何工具箱代码。下面内容仅用于回看当时的取舍，不要再照着做。

  1. 起因：用户要求"把工具箱里推荐的软件找免安装版放到软件对应文件夹，能找多少找多少；
     没有的还是这么显示、给官网链接；有的直接生成启动按钮，点启动直接启动"。
  2. 定下的三条规矩（这轮所有取舍都按它走）：
       只用官方发布的免安装包（zip / 单文件 exe / GitHub Release 资产），
         不用任何第三方"绿色版""精简版""汉化版"——那类包的来源无法核验，放进来就是把风险转给用户；
       官方只给安装器、或下载页只能拿到安装器的，就不硬凑，卡片继续显示官网链接；
       包内文件一律原样解压，不改 exe、不加壳、不捆绑、不写注册表，启动就是直接跑那个 exe。
  3. 目录约定（tools\<工具 id>\）：
       exe.txt      第一行写启动哪个 exe（相对本工具目录，可带子目录，如 ventoy-1.1.18\Ventoy2Disk.exe）
       ver.txt      官方包版本号（有些 exe 内部版本号和包版本不一致，比如 Ventoy 包 1.1.18 / exe 1.1.0.3）
       SOURCE.txt   官方来源 URL、下载日期、exe 的 SHA256、许可说明
       其余文件     官方包原样
  4. 后端（src\Tools.cs）：
       PortableDir(id)      取 AppDomain.BaseDirectory + "tools\" + id
       PortableExe(id)      先读 exe.txt，再回退到目录里第一个 exe；文件不存在就返回空
       PortableVersion(id)  先读 ver.txt，没有才用 FileVersionInfo（并把 3,02 这类逗号版本归一成 3.02）
       Run(id)              Process.Start(exe, WorkingDirectory=exe 所在目录, UseShellExecute=true)；
                            没有便携版就返回 ok=false + "点右边的「官网」去下载"
       PortableReport()     自检用：列出带了几个、每个的相对路径与版本
       ListJson()           每条新增 portable / pexec / pver 三个字段，旧前端字段不变
  5. 主程序（src\MainForm.cs）：新增 case "toolRun" → Tools.Run(id)。不改任何既有命令。
  6. 前端（app\index.html）：
       卡片三态：已安装（绿）/ 免安装（绿）/ 未安装（灰）；
       有便携版的卡片并排两个按钮：「启动」（主按钮，post toolRun）+「官网」（次按钮），
         右侧小字显示 exe 名，鼠标悬停有完整说明；
       没有便携版的保持原样：已安装给"打开官网 / 下载"，没装给"去官网下载"；
       新增 runTool(id) 与消息分支 case "toolRun"（成功/失败都走 toast）。
  7. 这轮实际带上的 5 个（都在 tools\ 下，共 44.2 MB）：
       cpuz      CPU-Z 3.02 中文版       官方 zip，cpuz_x64.exe      12.69 MB  签名有效（CPUID）
       traffic   TrafficMonitor 1.86 x64 GitHub Release zip           4.12 MB  未签名（开源项目惯例）
       dism      Dism++ 10.1.1002.1B     GitHub Release zip           7.44 MB  未签名（开源项目惯例）
       rufus     Rufus 4.16              GitHub Release 单文件 exe    1.89 MB  签名有效（Akeo Consulting）
       ventoy    Ventoy 1.1.18           GitHub Release zip          18.05 MB  未签名（开源项目惯例）
     两个有签名的都当场用 Get-AuthenticodeSignature 验过 Status=Valid；
     三个开源包虽然没签名，但都来自作者自己的 GitHub Release（域名 github.com/<作者>/<仓库>/releases），
     每个包的 exe 都记了 SHA256，日后要复核直接重算即可。
  8. 没带上的 10 个与原因（都保持官网链接，界面不显示"免安装"）：
       HWiNFO64       官网下载页被 Cloudflare 拦（403），拿不到文件
       CrystalDiskInfo / CrystalDiskMark   官方现在只放安装器（Portable 的 redirect 产品名已失效）
       GPU-Z          下载按钮是 JS 点击流，页面里没有可直接取的 exe 地址
       DiskGenius / 7-Zip / Everything / Snipaste / SumatraPDF / Bulk Crap Uninstaller
                      官方只提供安装器（7-Zip 的 msi 管理安装提取本机非管理员被拒 1603；
                      Everything 与 Snipaste 的下载域在本机网络下超时）
     这一条写在这里是为了以后复核：不是"没找"，是"官方没有可直接分发的免安装包"或"本机拿不到"。
  9. 自检（src\SelfTest.cs）新增「工具箱便携版（tools 子目录是否就位）」：
     打印"便携版数量=N / 15"、每个工具的相对路径与包版本，末行"结果=通过 ✔"。
     本机实测：5 / 15，五项全部列出，无异常。
  10. 验收：WinTune.exe 437,248 字节 / runtime 27 个 DLL / 零警告；
      app\index.html 219,305 字节；--selftest 通过 8/8、3/3、8 处「结果=通过 ✔」、无 ✘；
      vm 用 mock 渲染工具箱页：15 张卡、5 个「启动」、5 个绿色「免安装」徽标、
      点第一个「启动」弹出"已启动: cpuz"，全程无 pageerror（截图 v43-tools.png）。

二十五、1.5.22（彻底删除工具箱与其相关功能）

  1. 起因：用户明确要求"彻底删除工具箱与其相关功能"。删的是功能本身，不是隐藏入口，
     所以要保证代码、目录、文档、自检里都不再留任何痕迹。
  2. 前端（app\index.html）删掉的东西：
       · 工具箱整段 CSS（.toolgrid / .tcard / .tcard-h / .tcard .btn / 1240px 媒体查询）；
       · 左侧导航「工具」组里的「工具箱」按钮，该组只剩检测规则，组名改为「参考」；
       · 概览 #ovQuick 第 6 张快捷卡（原「工具箱 / 15 个系统自带或免费工具」），
         换成「检测规则 / 每条建议与判定都能追到出处」，go('checks')，6 卡布局不变；
       · 整个 <div class="view" data-v="tools">（页头、五分段 #toolSeg、重新检测按钮、#toolGrid）；
       · titles 映射里的 tools 键、全局点击处理里的 #toolSeg 分支；
       · 整段工具箱 JS（S.tools / loadTools / onTools / TOOLCAT / renderTools / openTool / runTool）；
       · 消息路由 case "toolList" / "toolOpen" / "toolRun"；初始化里的 loadTools(); 调用。
     · 「本机资料」三卡（环境报告 / 备份与清单 / 运行日志）原来挂在工具箱视图里，属于通用功能，
       不随工具箱删除：整体搬到概览页 #envRow 之后，单独一节「本机资料 · 都在本机，不上传」。
     · 保住不动的：.toolstrip 是通用工具条样式，清理 / 优化 / 网络页仍在用，不能跟着删。
  3. 后端：
       · src\Tools.cs 从"15 个工具清单 + 安装检测 + 便携版启动"精简为只做运行依赖检测，
         只留 HasWebView2()（查两处 EdgeUpdate 注册表键，再兜底 %LOCALAPPDATA%\Microsoft\EdgeWebView 目录）
         与 DepsJson()；ToolDef / List / InstalledNames / ListJson / Open / FindExe / Portable* 全删。
       · src\MainForm.cs 删 case "toolList" / "toolOpen" / "toolRun"，deps 保持在原位；
       · src\SelfTest.cs 删「工具箱便携版（tools 子目录是否就位）」整段（原来打印"便携版数量=N / 15"）。
  4. 目录：tools\ 整个删除（5 个子目录、165 个文件、44.2 MB 全部移除），打包不再带它。
  5. 文档：README 目录树、运行期清单、命令表、源码地图全部去掉 tools / toolList / toolOpen；
     使用说明删除「五、工具箱」整章、概览第 6 项改为检测规则，并补 1.5.22 变更说明。
  6. 验收：build.ps1 零警告（428,544 字节 / runtime 27 个 DLL）；app\index.html 214,912 字节（LF）；
     --selftest 通过 8/8、3/3、7 处「结果=通过 ✔」、无 ✘（比 1.5.21 少一处，就是删掉的便携版段）；
     vm 用 mock 渲染断言：ver=1.5.22、无 data-v="tools"、无 navitem[data-view="tools"]、
     toolgrid/tcard/#toolGrid/#toolSeg 计数=0、titles 无 tools、S 里无 tool* 键、
     #ovQuick 仍是 6 卡且第 6 张为「检测规则」、点它跳到检测规则页（visibleViews=["checks"]、页头「检测规则」）、
     概览「本机资料」三卡在 #envRow 之后正常渲染、errs=[]。

二十六、1.5.24（MiniMonitor 版本号写进它自己的配置文件，WinTune 直接读）

  1. 起因：MiniMonitor.exe 没有版本资源，WinTune 概览只能显示构建日期。用户要求把版本号放进
     MiniMonitor 项目自己的配置文件里、和程序放一起，让 WinTune 读到这个"真实的 MiniMonitor 版本"；
     这样升级 MiniMonitor 只改它自己，不用动 WinTune。
  2. MiniMonitor 改动（工程在 ..\MiniMonitor\）：
       · MiniMonitor.cs 增加 public const string AppVersion = "1.6.0"，这是唯一的版本来源；
       · 新增 SyncVersionConfig()：每次启动（界面模式与 --agent 温度代理模式都算）把 AppVersion
         和 exe 的构建日期写进同目录 MiniMonitor.exe.config 的 <appSettings>：
           <add key="Version" value="1.6.0" /> / <add key="Build" value="2026-11-23" />
         只在值变化时才写文件，装在没有写权限的位置（如 Program Files）写失败就静默跳过，
         配置里保留打包时的值，不影响程序其他功能；
       · MiniMonitor.exe.config 自带 appSettings Version/Build，保证"第一次运行前"读到的也是真版本；
       · 新增 MiniMonitor\build.ps1：csc 编译 MiniMonitor.cs / Shot.cs / ShotUi.cs / Snip.cs /
         LhmTemp.cs / TempBus.cs → MiniMonitor.exe，编完把源码里的 AppVersion 同步进 exe.config。
         实测：编译 99,328 字节；故意把 config 的 Version 改成 0.0.9 再启动，文件被改回 1.6.0。
  3. WinTune 读取（src\Hardware.cs）：
       MmConfigValue(exe, key)   读 <exe>.config 的 /configuration/appSettings/add[@key=...]
       MmVersion(exe)           先用配置里的 Version；读不到再退回 exe 文件版本资源；都没有返回空
       MmVersionFromConfig(exe) 版本是否来自配置文件，给前端标来源
       MmBuild(exe)             先用配置里的 Build，没有再退回 exe 写入时间
       MmJson                    verSrc 取值 config / exe / 空
     兼容性：老 MiniMonitor（没有 appSettings）照样能跑，WinTune 只会退回版本资源或显示构建日期。
  4. 前端（app\index.html）「版本」行：MiniMonitor 1.6.0 + 小字「来自 MiniMonitor.exe.config」；
     都没有时写「配置文件里没有版本号，exe 也没标」，不编造。
  5. 自检 [运行依赖]：MiniMonitor 版本=1.6.0（读自 MiniMonitor.exe.config）。
  6. 打包：WinTune\MiniMonitor\ 仍是 7 个文件（exe + exe.config + 5 个 dll），上一版临时加的
     version.txt 已删除；exe.config 比原来大 141 字节，就是多出来的 appSettings 两项。
  7. 升级约定：以后改 MiniMonitor.cs 的 AppVersion 一行，跑 MiniMonitor\build.ps1，
     把新的 exe + exe.config 换进 WinTune\MiniMonitor\ 即可；WinTune 不用改也不用重编译。
  8. 验收：MiniMonitor.exe 99,328 字节（版本 1.6.0）；WinTune.exe 429,568 字节 / runtime 27 个 DLL
     零警告；app\index.html 215,130 字节（LF）；--selftest 通过 8/8、3/3、7 处「结果=通过 ✔」、无 ✘，
     且打印 MiniMonitor 版本=1.6.0（读自 MiniMonitor.exe.config）；
     vm 用 mock 渲染断言：#mmRow 含「MiniMonitor 1.6.0」与「来自 MiniMonitor.exe.config」、errs=[]。

二十七、1.5.25（配置文件改名 MiniMonitor.config + 菜单版本号 + 圆环立体）

  1. 起因：用户指出 MiniMonitor.exe.config 不是标准命名，配置文件应当叫 MiniMonitor.config。
  2. 改名与实测：
       · SyncVersionConfig() 改成写 exe 同目录 MiniMonitor.config（XML 结构仍是
         <configuration><appSettings>），启动时把 AppVersion 与构建日期写进去；
       · 实测把 exe 放进只有 5 个 dll + MiniMonitor.config 的临时目录（不放 <exe>.config），
         程序正常启动并常驻，说明 Memory / Buffers / Unsafe 这套依赖不需要 bindingRedirect，
         MiniMonitor.exe.config 可以彻底不打包；
       · 实测把 MiniMonitor.config 的 Version 改成 0.9.9 再启动，被程序自己写回 1.6.0，
         Build 写成本次构建日期 2026-11-23；
       · MiniMonitor\build.ps1 改为生成 MiniMonitor.config（Version 取自源码 AppVersion，
         Build 取 exe 写入日期），并顺手删掉历史遗留的 MiniMonitor.exe.config；产物 100,352 字节；
       · 启动自检（DependencyCheck）的依赖清单里去掉了 MiniMonitor.exe.config：它只管版本号，
         不参与读温度，缺了也不该报“CPU 温度会读不到”，现在清单只剩 5 个 dll。
  3. 右键菜单：BuildItems() 在「截图设置」下面新增一行「版本　1.6.0」。这一行没有图标、
     Click 为空，绘制时按不可点项的暗色 CTxt2 画（不参与 hover 高亮），点它只会收起菜单，
     不会误触发动作。
  4. 内存圆环（悬浮条左侧 27px 小环）立体化：在原来的“底槽 + 44 段渐变弧”上加了三层——
       · 底槽外沿压暗弧、内沿提细高光，做成浅凹槽；
       · 进度弧先画一层右下偏移、压暗 55% 的投影，再画略窄的主弧；
       · 内侧再压一条细亮高光，给出厚度。
     全部仍在缓存位图里完成，百分比不变就不重画。放大预览见 _preview\ring44_new.png、ring77_new.png。
  5. WinTune 读取侧：MmConfigValue 先读 MiniMonitor.config，找不到再兼容老的
     MiniMonitor.exe.config；前端版本行小字改「来自 MiniMonitor.config」；自检打印
     「读自 MiniMonitor.config」。
  6. 验收：MiniMonitor.exe 100,352 字节、MiniMonitor.config 187 字节；
     WinTune.exe 429,568 字节 / runtime 27 个 DLL 零警告；app\index.html 215,126 字节（LF）；
     --selftest 通过 8/8、3/3、7 处「结果=通过 ✔」、无 ✘，
     且打印 MiniMonitor 版本=1.6.0（读自 MiniMonitor.config）。
  7. 升级约定不变：改 AppVersion 后跑 MiniMonitor\build.ps1，把新的 exe + MiniMonitor.config
     换进 WinTune\MiniMonitor\ 即可，WinTune 不用改。
二十八、1.5.26（双击清理手感 + 菜单清理动画 + 截图功能文案）

  1. 截图功能状态文字（app\index.html）：开启时显示绿色「已开启」（原来写「开启」），
     关闭时写「未开启」，和「开机自启」那一行口径一致；右侧按钮仍是「关闭 / 开启」。
  2. 双击圆环要按两次才生效、过程中悬浮条还会闪一下，三个原因一起改：
       · CleanMemory 原来连 explorer.exe 的工作集也压，任务栏连同挂在任务栏上的悬浮条
         一起重绘，看起来就像 UI 消失又出来；
       · MouseDown 一按下就进入拖动状态，双击时手抖一两像素会把悬浮条挪走，圆环跟着移位，
         第二下就落在环外；
       · Windows 的第二次按下发的是 WM_LBUTTONDBLCLK（0x0203），原来只处理 0x0201。
     改法：清理时跳过外壳进程（explorer / ShellExperienceHost / StartMenuExperienceHost /
     SearchHost / TextInputHost / dwm / csrss / winlogon）和自己的进程；拖动加 5px 阈值，
     按下只记起点，移动超过 5px 才真的挪窗口；WndProc 补上 0x0203，第二下直接触发清理。
  3. 右键菜单「清理内存」改走和双击同一个入口 BeginClean()：先起步 0.8 秒倒数到 0、
     再 0.8 秒回到真实占用的圆环动画，然后后台清理。原来菜单只调 CleanMemory()，所以没有动画。
  4. 验收：MiniMonitor.exe 100,864 字节、MiniMonitor.config 187 字节；
     WinTune.exe 429,568 字节 / runtime 27 个 DLL 零警告；app\index.html 215,132 字节（LF）；
     --selftest 通过 8/8、3/3、7 处「结果=通过 ✔」、无 ✘，仍打印
     「MiniMonitor 版本=1.6.0（读自 MiniMonitor.config）」。
二十九、1.5.27（系统修复按钮全铺开 + 真实网卡恢复初始化 + 测速重做）

  1. 「维护修复」整页改名「系统修复」：左侧导航、页面标题、概览快捷卡、事件分组、窗口标题
     全部统一；页头文案从“12 个动作 / 只在异常时出修复按钮”改成“11 个项目、14 个动作，全部铺成按钮”。
  2. 系统修复改成“一个项目 + 多个操作”的模型（src\Repair.cs）：
       · RepairAction 新增 BaseId / Label / OpDesc / Ops；Catalog() 里每个项目用 Base() 建，
         再用 AddOp() 挂具体动作，ListJson() 输出 ops 数组；Find() 既认操作 id 也认老的项 id；
         Record() / Probe() 一律按 BaseId 记，所以点“扫描”或“修复”更新的是同一个项目的状态。
       · 14 个按钮：SFC 扫描 / 修复；DISM 扫描 / 修复；磁盘 扫描；Winsock 重置；TCP/IP 重置；
         DNS 刷新；图标缓存 重置；资源管理器 重启；打印服务 清空队列 / 重启；更新组件 重置；
         商店缓存 重置。
       · 前端 renderRepair() 不再按状态藏按钮：正常、已修复、待重启、未检查都照样铺出按钮，
         每颗按钮带 title（做什么、约几分钟、要不要重启）；权限不够时置灰。
       · 修复弹窗改为按“项目 · 动作”显示，执行日志、计时、进度条、完成弹窗逻辑沿用。
  3. 网络修复只保留真实网卡（src\NetFix.cs ReadNics）：
       · 只收有线（Ethernet / Gigabit / FastEthernet）、无线（Wireless80211）、蓝牙；
         蓝牙个人热点常被系统报成 Ethernet，按名称/描述含 bluetooth 或 蓝牙 单独认，类型显示「蓝牙」；
         虚拟网卡、VPN、Hyper-V、WSL、TAP、隧道类全部不再列出。
       · 每块真实网卡三颗按钮：恢复初始化 / 重启这块网卡 / DNS 改自动。
       · 新增 nicreinit（DoNicReinit）：netsh 把该网卡改回 DHCP 取 IP、DHCP 取 DNS，flushdns、
         renew，再禁用→启用一次让租约重新协商，最后回读 IPv4 / 网关 / DNS 并在结果里写明；
         改地址、改 DNS、重启用任一步失败都会如实报失败，不吞错误。
  4. 网络测速重做（src\NetFix.cs + app\index.html）：
       · 服务器可选：自动（就近先试、连不上自动换）、Cloudflare（就近节点）、WTnet 德国（LibreSpeed）；
         地址都是公开可实测端点，50 MB 分片下载、64 KB 起步自适应的 POST 上传。
       · 流程：TCP 443 握手测延迟（1 次热身 + 5 个样本，取最小值为延迟、标准差为波动）→
         连续下载 10 秒 → 连续上传 10 秒；两段都按“实测字节 / 实测时长”算平均，不估算、不补数。
       · 上传分块自适应：一块超过 3.5 秒就减半，半秒内写完就翻倍（8 KB ~ 256 KB），
         慢线不被大包拖长、快线不被小包拖慢；窗口到 10 秒就收尾，已测字节照实计入。
       · 界面：服务器下拉、阶段灯（连接 / 延迟 / 下载 / 上传）呼吸动画、进度条流光、
         大号实时 Mbps、10 秒倒计时；结果六格：下载平均、上传平均、延迟、波动、本次流量、总耗时；
         直连失败才退到系统代理并在结果里标注。完成行同时给峰值与服务器名。
       · 新增无人值守自检：WinTune.exe --speedtest [auto|cloudflare|wtnet]，
         不开窗，整段过程与结果写 %LOCALAPPDATA%\WinTune\speedtest.log，便于核对是否真跑满 10 秒。
  5. 系统优化页的开关改成“预览 + 应用”模型（app\index.html）：
       · 每个开关代表“希望这一项处于的状态”，拨动只改预览，不发任何命令；
       · 「清空」按钮已删除；恢复默认 = 把所有开关关掉（已应用的项会排进待恢复），只改界面，不执行；
       · 点「应用」时按差异执行：开关开且未应用 → 写入；开关关且已应用 → 恢复原值。
         两类都有时先恢复、再写入，结果合并成一个汇总；没有改动时点应用只提示，不发命令。
       · 底部统计改成「待应用 X 项 · 待恢复 Y 项」，按钮写「应用（X 写入 / Y 恢复）」；
         开关右上角提示写明“点应用才写入 / 才恢复”。
  6. MiniMonitor 1.6.2（换机器后 CPU 温度显示 -- 的修复）：
       · 根因不是驱动：硬件库引用的 System.Memory 是 4.0.5.0，而包里实际是 4.0.1.2，
         Computer.Open() 会报 FileLoadException，温度采集在打开硬件监控那一步就失败。
         旧机器碰巧有 ACPI 温区兜底值，看着像有温度；新桌面没有这个温区，就直接显示 --。
       · 修复：随 exe 增加标准的 MiniMonitor.exe.config，写入 System.Memory 与
         System.Runtime.CompilerServices.Unsafe 两条绑定重定向；版本号仍只写 MiniMonitor.config。
       · 启动逻辑：MiniMonitor 启动后先看温度能不能读到——读得到就不动；读不到才拉起温度代理，
         驱动或任务缺失时用一次 UAC 的 --bootstrap 补齐（装 PawnIO + 建任务 + 启动代理）。
       · 生命周期：代理任务改成“无登录触发器”的手动任务，不再开机自己跑；
         每次 MiniMonitor 启动时 /Run，退出（右键退出或窗口关闭）时先通知代理收手、再 /End 任务。
         下次启动由 MiniMonitor 重新拉起，不在后台常驻。
       · 代理重启时用 OpenFileMapping 接管界面已打开的共享内存，避免新代理写不进去、温度卡在旧值。
       · 交付包 MiniMonitor 文件夹保留 PawnIO_setup.exe；代理自身在驱动缺失时也会静默补装。
       · 仍然是真实内核温度，不伪造；驱动被拒绝安装时继续显示 --，右键菜单保留「装温度驱动」。
  7. 验收：WinTune.exe 439,808 字节 / runtime 27 个 DLL 零警告；app\index.html 221,826 字节（LF）；
     --selftest 通过 8/8、3/3、7 处「结果=通过 ✔」、无 ✘，MiniMonitor 版本=1.6.2（读自 MiniMonitor.config）；
     网卡只列 2 个有线 + 1 个无线 + 1 个蓝牙（虚拟=False）。
三十、1.5.28（依赖随启随停、卡死可中止、完成后自动刷新）

  1. 运行依赖完整检测（新增 src\RuntimeDeps.cs）
       · 启动前检查 WebView2 / runtime / MiniMonitor 文件 / PawnIO 服务 / 代理任务 / 悬浮条；
         缺驱动或任务就用一次静默 --bootstrap 补齐，缺悬浮条就拉起。
       · 软件运行期间每 4 秒看门狗复查：悬浮条被杀、驱动服务被停、代理任务被删，都会自动恢复。
       · 退出时先往共享内存写退出标志，再关悬浮条、结束 MiniMonitorWidget 任务；
         另建带 KILL_ON_JOB_CLOSE 的 Job 把悬浮条挂进去，即使主程序被强杀也不会留下子进程。
       · 概览 MiniMonitor 卡和“运行依赖”页现在直接显示：文件齐全 / PawnIO 运行 / 代理运行 / 当前温度。
  2. MiniMonitor 1.6.3 生命周期加固
       · 代理任务被结束、代理进程被杀，界面 5 秒内重新 /Run；依赖 75 秒复查一次，
         UAC 引导 5 分钟内最多一次，避免反复弹窗。
       · 界面每秒写心跳；界面崩了或被杀，代理 25 秒后自行退出，不再留下孤儿进程。
       · 退出标志在界面下次启动时清零，不会出现“关一次后下次拉不起代理”。
       · 安装器用完后收进 %LOCALAPPDATA%\WinTune\deps，程序目录不再留一个没跑过的 exe；
         以后驱动真被移除，还能从备份位置静默重装。
  3. 系统修复不再假死（src\Repair.cs + app\index.html）
       · stdout/stderr 全部后台流式读取；12 分钟无输出或总时长 60 分钟自动结束工具；
         运行 45 秒后出现「结束卡死工具」，点了马上杀进程并回一条“已强制结束”。
       · 用户主动结束不记为“修复失败”；完成窗按钮改为强制解锁后关闭，关掉再刷新整页状态。
  4. 网络修复不再等命令跑完才显示（src\NetFix.cs + app\index.html）
       · Command 输出边跑边贴；单条命令 2 分钟超时，运行 20 秒后出现「结束卡死命令」。
       · 完成后关闭弹窗会重新读取网卡/DNS/hosts，并自动再跑一次体检，问题条不会停在旧结果。
  5. 界面流畅化（src\MainForm.cs）
       · repairList / netInfo / appsList / mmGet / deps 等会跑 WMI、schtasks、进程枚举的命令
         全部移出 WebView 消息线程，放后台线程执行；打开页面不再整窗卡几秒。
       · 查看依赖状态：WinTune.exe --depsjson 写 %LOCALAPPDATA%\WinTune\deps.json；
         起停自检：--sidecartest 拉起悬浮条、写 sidecar-test.json、再收掉并结束代理任务。
  6. 验收：WinTune.exe 452,096 字节 / runtime 27 个 DLL / 21 个 .cs 零警告；
     app\index.html 225,752 字节（LF）；MiniMonitor.exe 101,888 字节（1.6.3）。
     旧机非管理员实测 --sidecartest：启动后 sidecar=true，退出后进程 0、任务 Ready；
     杀界面后 30 秒内代理自行退出（任务 Ready）；/End 代理后 12 秒内界面自动拉起。
     JS 语法 node --check 通过；--repairjson 正常导出 11 个项目状态。

三十一、1.5.29（MiniMonitor 彻底独立，WinTune 只管自己）

  1. 起因：上一轮把 MiniMonitor 的依赖和悬浮条挂进了 WinTune 的启动/退出生命周期，
     这不是用户要的。MiniMonitor 本身可以直接双击启动，依赖也应当由它自己检查。
  2. WinTune 这一侧全部撤回：
       · 删除 src\RuntimeDeps.cs（源码回到 20 个 .cs），Program 不再 Begin/StopAll；
       · MainForm 删掉 4 秒依赖看门狗，关闭窗口不再隐藏、不再杀 MiniMonitor 进程、不结束代理任务；
       · 启动只做自己的事（WebView2 / .NET / VC++），--depsjson 也只输出这三项；
       · 概览的「运行依赖」不再列温度组件、PawnIO、代理任务、悬浮条；
       · 「启动 / 重启悬浮条」改为直接 Process.Start 独立拉起，不挂 Job、不接管退出。
  3. MiniMonitor 自己负责依赖（1.6.4）：
       · 双击启动后先检查 5 个 dll；缺文件提示，不缺就继续；
       · 温度已能读到 → 什么都不做；
       · 驱动或代理任务缺失 → 一次 --bootstrap 静默补齐；两者都在但代理没跑 → 只 /Run 一次；
       · /Run 前先查任务状态，已经在 Running 就跳过，不重复拉起；
       · 退出时仍是界面通知代理收手 + /End 任务，只影响它自己的温度链路。
  4. 开机启动选项恢复：
       · MiniMonitor 右键菜单新增「开机自启　已开启 / 未开启」，点一下切换；
       · WinTune 概览「悬浮条」卡也恢复「开机自启」一行，同样可开 / 关；
       · 两处读写同一个 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 MiniMonitor 值，
         开启后登录由 MiniMonitor 自己启动，和 WinTune 是否运行无关；
       · 关闭只删除这一个值，不动 MiniMonitorWidget 温度代理任务。
  5. 文案清理：MiniMonitor 右键菜单删掉「温度监控　运行中 / 未启动」这一行；
     WinTune 概览卡删掉「温度代理」「依赖检查」「随 WinTune 启动…」字样，改为
     「独立运行；启动时自己检查驱动和温度代理，WinTune 不检测也不接管」。
  6. 验收：WinTune.exe 442,880 字节 / runtime 27 个 DLL / 20 个 .cs 零警告；
     MiniMonitor.exe 102,912 字节（1.6.4）；app\index.html JS node --check 通过；
     WinTune.exe --depsjson 输出仅 webview2 / dotnet48 / vcredist，运行前后
     MiniMonitor 进程数都是 0，任务保持 Ready（确认 WinTune 不再拉起或结束它）；
     反射调用 MmAutoStartSet(true/false)：Run 值写入带引号的 MiniMonitor.exe 路径，
     读取为 True，关闭后值删除、读取为 False。

三十二、1.5.30（开机自启置顶 + 开关视觉 + 自己的项不再建议修改 + 启动只检查一次）

  1. MiniMonitor 开机自启（1.6.5）
       · 右键菜单第一行就是「开机自启」，不再夹在截图和版本之间；
       · 右侧不再写「已开启 / 未开启」，改成绿/灰胶囊开关（36×18，开启时绿色轨道 + 亮绿圆点）；
       · WinTune 概览里的「开机自启」也同步改成同样的绿色胶囊开关，不再写「已开启」+「关闭」按钮；
       · 点击切换 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 MiniMonitor 值，
         不碰 MiniMonitorWidget 温度代理任务。
  2. 维护页保护自己的程序和条目（src\Maintenance.cs）
       · 启动项里 basename 是 WinTune.exe / MiniMonitor.exe 的，一律「不建议修改」，Act 为空，
         不再出现「停用自启 / 删除」按钮，也不因为路径失效被标「建议删除」；
       · 计划任务 MiniMonitorWidget、名字含 WinTune 的任务同样只展示、不给禁用/删除；
       · 服务名含 PawnIO / MiniMonitor / WinTune，或 ImagePath 指向这三个 exe 的，也只展示，
         不给停止、禁用、删除、改启动类型；
       · 其它第三方启动项、任务、服务仍按原规则给建议，不受影响。
  3. WinTune 启动检查（新增 src\MiniBoot.cs）
       · 只在启动时后台跑一次，不做看门狗、不常驻；
       · 进程在、PawnIO 服务 RUNNING、MiniMonitorWidget 任务 RUNNING → 直接返回，不打扰；
       · 驱动或任务缺失 → 静默 `MiniMonitor.exe --bootstrap` 一次补齐；进程没起再拉起界面；
       · 驱动在但服务停了 → `sc start PawnIO`；任务在但代理没跑 → `schtasks /Run`；
       · WinTune 关闭时仍然完全不管 MiniMonitor，不结束任何进程、任务或服务。
  4. 验收：WinTune.exe 445,952 字节 / 21 个 .cs / runtime 27 DLL 零警告；
     MiniMonitor.exe 103,936 字节（1.6.5）；index.html 224,024 字节，JS node --check 通过；
     用离屏绘制渲染菜单首行：开机自启在顶部、右侧为开启态绿色胶囊开关，无「已开启」文字；
     HKCU 写入 MiniMonitor 启动项后扫描 startup，返回 status=不建议修改、act=""；
     扫描 task，MiniMonitorWidget 返回 status=不建议修改、act=""；
     WinTune --depsjson 仍只输出 webview2 / dotnet48 / vcredist。

三十三、1.5.31（WinTune / MiniMonitor 图标重做，透明无边）

  1. 图标改成矢量绘制后逐级缩样，不再用带底色的大图直接压成 ico。
       · WinTune：深色圆角方块 + 薄荷绿到青蓝渐变的 W 字标，右上角一个很小的亮点，
         方块外全部透明，没有白底，也没有白色描边。
       · MiniMonitor：同一套深色圆角底 + 青绿脉搏曲线，线条在 16px 仍然是一条完整
         可辨认的折线，不靠细描边。
       · 两个 ico 都含 16 / 24 / 32 / 48 / 64 / 128 / 256 七档；48 以上用 PNG 压缩，
         32 以下用 32 位 BMP + AND 掩码，保证资源管理器、任务栏、Alt-Tab 各取所需。
  2. 嵌入方式
       · WinTune 继续由 build.ps1 的 /win32icon 编译进 exe，运行时窗口图标仍读
         assets\icon.ico；
       · MiniMonitor 的 build.ps1 也加了 /win32icon assets\MiniMonitor.ico，以前没有
         图标，用的是系统默认 exe 图标。
  3. 验收
       · 两个 ico 的 256/128/64/48/32/24 帧四角 alpha=0，16px 帧四角 alpha=2（全透明）；
         从两个 exe 里 ExtractAssociatedIcon 取 32px 图，角点 alpha=0、上边缘 alpha=3，
         没有任何不透明白边。
       · WinTune.exe 368,128 字节（1.5.31）；MiniMonitor.exe 158,720 字节（1.6.6）；
         两个 exe 都带新图标；WinTune 源码仍 21 个 .cs。

三十四、1.5.32（远程桌面/远控启动卡死修复）

  1. 现象与原因
       · 双击 WinTune 后，本机界面其实已经加载（日志先写“界面已加载”），
         但 CrossDesk / 向日葵 / ToDesk / RustDesk 这类远控画面整个冻住，
         必须断开重连才能恢复。
       · 不是 WinTune 主线程在算东西，而是 WebView2 默认启用 GPU 合成，
         会建 DirectComposition 设备。远控工具在控制台镜像上抓屏时，
         这个 GPU 设备和远控的显示通道互相卡住，整个画面停住。
       · Windows RDP 还会在会话重建时把显示设备拆掉，WebView2 的 GPU 进程
         丢掉设备后更容易挂死；表现同样是“必须重连”。
       · CrossDesk 这类工具走控制台镜像，GetSystemMetrics(SM_REMOTESESSION)
         仍然是 0，所以不能只靠 RDP 标志判断。
  2. 修复（src\MainForm.cs）
       · 创建 WebView2 之前先判断：SM_REMOTESESSION、SESSIONNAME 以 RDP- 开头，
         或进程里出现 crossdesk / rustdesk / todesk / sunlogin / anydesk /
         teamviewer / parsec / vnc / mstsc / awesun / splashtop 等远控进程。
       · 命中远程/远控时给 CoreWebView2CreationProperties.AdditionalBrowserArguments
         传 `--disable-gpu --disable-features=CalculateNativeWinOcclusion`，
         强制软件合成，并关掉窗口遮挡节流；本地桌面不受影响，仍用硬件加速。
       · 日志会写“远程桌面/远控会话：WebView2 已切换软件渲染”，便于确认是否命中。
  3. 验收
       · 本机正在运行 CrossDesk：IsRemoteSession 返回 True，RemoteAgentRunning 返回 True，
         BrowserArgsFor(true) 返回上述两个参数；BrowserArgsFor(false) 返回空。
       · WinTune.exe 369,664 字节（1.5.32）；MiniMonitor.exe 158,720 字节（1.6.6）。

三十五、1.5.33（MiniMonitor 右键菜单增加“打开 WinTune”）

  1. 菜单位置：紧跟“开机自启”下面，排在“清理内存”之前；图标是金色 W。
  2. 点击行为
       · WinTune 已经在跑：ShowWindow 还原 + SetForegroundWindow，直接把窗口拉到前面，
         不再开第二个实例；
       · 没在跑：从 MiniMonitor.exe 所在目录开始，向上找三级，分别检查
         同目录\WinTune.exe、同目录\WinTune\WinTune.exe，命中后以 runas 启动。
         这样只弹一次 UAC，不会出现“先普通启动、WinTune 自己再提权”的两次提示；
       · 找不到 exe：弹提示，让用户保持 WinTune 和 MiniMonitor 文件夹的相对位置；
       · 用户点“否”取消 UAC 时不报错，其它启动失败才弹提示。
  3. 不改变生命周期：打开 WinTune 不代表 MiniMonitor 退出；退出 MiniMonitor 也不会关 WinTune。
  4. 验收：离屏渲染 6 行菜单，第一行开机自启+绿色开关，第二行“打开 WinTune”+W 图标，
     下面是清理内存、截图设置、版本 1.6.7、退出；菜单高度 232px，无文字截断。
     菜单由 MiniMonitor.exe 159,744 字节（1.6.7）渲染；WinTune.exe 369,664 字节（1.5.33）。

三十六、1.5.34（启动过渡动画，盖住 WebView2 白屏）

  1. 白屏原因：窗口自己已经是深色，但 WebView2 首次创建环境、启动浏览器进程、
     建虚拟主机映射要几秒，这段时间控件默认刷白底，所以看起来先白几秒再出界面。
  2. 处理（src\SplashOverlay.cs + MainForm.cs）
       · MainForm 构造时先设 WebView2.DefaultBackgroundColor=#0E1116，白底不再出现；
       · 再叠一层 Dock.Fill 的自绘 SplashOverlay，覆盖整个客户区：深色渐变底、
         中间 W 字呼吸缩放、两道旋转弧、底部来回扫的进度微光、“正在准备界面···”；
         30fps，纯 GDI+，远控/软件渲染下也能跑；
       · 页面触发 NavigationCompleted 后先停 300ms 让首帧画完，再用 280ms 把前景
         （W、文字、进度条）淡到 0，最后移除遮罩；遮罩底色和页面 #0E1116 完全一致，
         揭开时不会闪；
       · 导航失败或 WebView2 初始化异常时直接 Fail，不让遮罩一直盖着。
  3. 验收：离屏渲染 1280×860 过渡层，W、标题、状态、进度条位置正常，无白边；
     WinTune.exe 375,296 字节（1.5.34）；源码 22 个 .cs；MiniMonitor 仍 1.6.7。









三十七、1.5.35（配置文件去 .exe + 启动层等硬件数据就绪）

  1. 配置文件命名
       · 实际文件名只叫 WinTune.config / MiniMonitor.config，永远不再出现
         带 .exe 的旧式配置名；build.ps1 也不再生成那种文件。
       · .NET Framework 的 CLR 配置文件名固定是 <exe>.config，所以这次不再依赖 CLR 配置：
         Program.Main 第一行注册 AssemblyResolve，按同名程序集从 runtime\（WinTune）
         或 exe 同目录（MiniMonitor）加载；System.Memory 4.0.5.0 → 本地 4.0.1.2 这类
         版本差也由它直接返回本地这份，效果等价于原来的 bindingRedirect。
       · 实测：去掉 .exe.config 后，LhmProbe 调 Computer.Open() 成功；
         WinTune 的 Hardware.SampleJson(false) 返回 ok=true、memTotalMb=32576，
         说明 LibreHardwareMonitor 与 System.Memory 都能正常加载。
  2. 启动动画撤除时机
       · 不再在 NavigationCompleted 后撤罩，那只是 HTML 外壳加载完；
       · 前端 onInit 完成且首个 onHw 带回 cpuTemp>0、memTotalMb>0 后，
         等 220ms 让数字真正画到屏幕上，再 post({cmd:"uiReady"})；
       · MainForm 收到 uiReady 后才 splash.Finish()；NavigationCompleted 只把状态文字
         改成“正在读取硬件数据”；
       · 传感器确实读不到时 9 秒兜底强制进入，避免遮罩永远卡住。
  3. 验收
       · 目录里只有 WinTune.config（189 字节）与 MiniMonitor.config；
         不存在任何带 .exe 的配置名文件。
       · 前端脚本 node --check 通过；WinTune 编译 376,320 字节；
         MiniMonitor 1.6.7 编译 160,256 字节（无 exe.config 依赖）。
三十八、1.5.36（版本号统一 + MiniMonitor 免 UAC 代启 WinTune）

  1. 版本号不一致
       · 现象：WinTune 概览卡写 MiniMonitor 1.6.5，MiniMonitor 右键菜单写 1.6.7。
       · 原因：WinTune\MiniMonitor\MiniMonitor.config 还是旧内置包的 1.6.5；
         菜单读的是已经装在源码目录、正在运行的 1.6.7。
       · 处理：内置 MiniMonitor 整体同步到 1.6.9（exe + config），删掉残留的
         MiniMonitor.exe.config；WinTune 的 MmJson 实测返回 version=1.6.9、bundled=true，
         概览卡与菜单现在同一版本。
  2. 远程桌面启动卡死
       · 原因：MiniMonitor 右键“打开 WinTune”先用 runas 启动，必然弹 UAC；
         UAC 安全桌面在 CrossDesk / RDP 抓屏下会把远程画面卡住。1.5.32 只处理了
         WebView2 GPU 合成，没有覆盖这条 UAC 路径。
       · 处理：MiniMonitor 1.6.9 在共享内存 Local\MiniMonitorTempShared 的 offset 28
         写“打开 WinTune”请求；管理员 --agent 每秒取一次并清零，用自己的提权令牌
         Process.Start(WinTune.exe)，不经过 runas / UAC。已有 WinTune 窗口则只还原到前台。
         界面保留 2.5 秒兜底：代理不在时才回退 UAC。
       · 注意：offset 28 不能再用 24。offset 20..27 是 8 字节心跳，
         24 是心跳的高 4 字节；写 24 会把心跳改坏，代理会反复判定界面消失并重启。
  3. 验收
       · MiniMonitor.exe 161,280 字节（1.6.9）；WinTune.exe 376,320 字节（1.5.36）。
       · 共享内存 temp 连续刷新（37–43°C），agent 常驻，MiniMonitorWidget=Running。
       · 手工写 offset 28 请求：WinTune PID 14312 由 agent PID 3136 拉起，
         TokenElevation 无法从普通进程打开（管理员令牌），全过程没有 UAC。
       · 内置 MiniMonitor.config 为 1.6.9，目录内 *.exe.config 数量为 0。
三十九、1.5.37（WinTune 退出不影响 MiniMonitor）

  1. 现象：关闭 WinTune 后十几秒，MiniMonitor 窗口/托盘一起消失。
  2. 处理（两侧都改）
       · WinTune 的 MiniBoot.StartUi 不再直接 CreateProcess 起 MiniMonitor，而是交给
         explorer.exe 代启。界面进程和 WinTune 彻底脱钩：WinTune 退出、崩溃都不带走它，
         界面也不会继承 WinTune 的管理员令牌。
       · MiniMonitor 1.6.10 的界面 FormClosing 不再写 stop 标志；只有右键「退出」走
         ExitAll 才真正结束代理。界面被外部关掉/杀掉时，管理员代理继续活着。
       · 代理发现界面心跳丢了（10 秒）会把界面重新拉起来，而不是自己退出；只有读到
         stop 标志才收手。
  3. 验收
       · 用 Stop-Process 强杀 MiniMonitor 界面：代理 PID 仍在，约 10 秒后自动拉起新界面，
         任务状态一直是 Running。
       · 从 MiniMonitor 右键打开 WinTune，关闭 WinTune 后连续观察 30 秒：
         MiniMonitor 进程数保持 2（代理 + 界面），温度继续刷新。
四十、1.5.38（PawnIO 缺失 / CPU 温度读不到时明确提示安装）

  1. 现象：MiniMonitor 启动后没有 PawnIO，CPU 温度一直 --；WinTune 启动后 CPU 温度也读不到，
     但原来的逻辑只是静默重试/静默 bootstrap，用户不知道要运行安装程序。
  2. MiniMonitor 1.6.13
       · 启动自检等待温度约 10 秒后，如果仍然读不到：
         - 未检测到 PawnIO 服务：弹「未检测到 PawnIO 驱动…是否运行 PawnIO 安装程序？」
         - 已安装但温度仍为 -1：弹「PawnIO 已安装但温度读不到…是否修复驱动？」
         - 用户点“是”才 runas 执行 --bootstrap（装驱动 + 建任务）；点“否”不静默强装。
         - 程序目录和 %LOCALAPPDATA%\WinTune\deps 都找不到 PawnIO_setup.exe 时，明确提示把安装器放回目录。
         - 5 分钟内不重复弹，避免反复打扰。
  3. WinTune 1.5.38
       · hw 数据新增 pawnio 字段（读 HKLM\SYSTEM\CurrentControlSet\Services\PawnIO）。
       · 管理员模式且 cpuTemp<=0 时，概览页顶部出现「CPU 温度不可用」提示条：
         未检测到驱动时提示运行安装程序；已安装但无读数时提示修复/重装；
         按钮「运行 PawnIO 安装程序」走 mmSet key=pawnio，后台静默安装（非管理员自动走一次 UAC）。
       · 安装完成后 6 秒重新拉取 hw；温度恢复后提示条自动消失。
  4. 验收
       · MiniMonitor.exe 163,840 字节（1.6.11）；WinTune.exe 377,344 字节（1.5.38）。
       · 无 PawnIO/无温度时 MiniMonitor 弹窗可点“是/否”；WinTune 概览出现提示条与按钮。
四十一、1.5.39（应用卸载接入 BCU 4.16，支持单个 / 批量卸载）

  1. 背景：原生卸载只能调用软件自己登记的 UninstallString / QuietUninstallString。
     大量软件没有卸载程序，原生列表只能“打开目录”，无法真正卸载。
  2. 方案：引入 Bulk Crap Uninstaller 4.16 portable 的 BCU-console.exe 作为外部引擎。
       · 只调用 uninstall <list.bcul> /Q /U /V，不启用 /J，不做残留 / Junk 清理。
       · 没有卸载入口的条目增加“BCU 卸载”按钮；支持复选框批量选择、全选可卸载、清空选择。
       · .bcul 用 DisplayName + RegistryKeyName 精确匹配，避免误选同名条目。
       · BCU 用 UniversalUninstaller 删除安装目录；目录确实消失后，才补删本次选中的那条注册表登记，不扫描其它条目。
       · BCU 引擎需要管理员权限：WinTune 非管理员时通过 UAC 启动；取消授权会明确报错。
       · Guard 会跳过盘符根、Windows、Program Files 根、用户目录根等危险安装位置。
  3. 目录：BCU 引擎放在程序根目录 BCU\，源码仓库不提交二进制；放置方式见 BCU-README.md。
  4. 未做：不清理 BCU 定义的残留目录、注册表残留、快捷方式残留；不把 BCU 的全盘 Junk 扫描接进一键清理。
四十二、1.6.13（PawnIO 提示修复与安装器保留）

  1. PawnIO_setup.exe 不再安装后从 MiniMonitor 目录删除；安装器始终留在程序目录，备份仍写 %LOCALAPPDATA%\WinTune\deps。
  2. “已安装但温度读不到”时，点“是”改为强制执行 `--bootstrap --repair`，不再因为注册表键存在就直接跳过驱动安装。
  3. 修复结果写 `%APPDATA%\MiniMonitor\repair.result`；失败会明确弹窗，成功后 7 天内不再重复开机提示。
  4. 如果界面已经有 ACPI/PDH 兜底温度（cpuTemp > 0），启动自检不再弹“温度读不到”，避免误报。
  5. 右键菜单新增“修复温度驱动”，随时可手动重试。
  6. 已知限制：当前 LibreHardwareMonitorLib 0.9.6 在部分 11 代 Intel 上 CPU Package 温度为空；此时显示的是 ACPI/PDH 兜底值，不是内核封装温度。
  7. 温度正常时，右键菜单不再显示“修复温度驱动”；只有三条温度链路都拿不到值时才出现。
四十三、1.5.40（卸载完成后自动刷新列表）

  1. 原生卸载：启动官方卸载程序后，前端每 2 秒重新读取一次卸载登记表；该条从列表消失后自动停止轮询，最多 60 秒。
  2. BCU 卸载：完成弹窗原本就会重新读取列表，保持现状。
  3. 不再需要用户卸载完手动点“刷新”。
四十四、1.5.41 / 1.6.14（PawnIO 启动顺序与依赖提示）

  1. 先说明检测口径：PawnIO 实际是内核驱动服务，不是任务管理器里的用户态进程；代码检测 HKLM\SYSTEM\CurrentControlSet\Services\PawnIO 的服务状态（RUNNING）。
  2. WinTune 启动：先看 PawnIO；已安装但服务停了就 `sc start PawnIO`，然后再拉起 MiniMonitor。没安装时不在后台静默安装，交给 MiniMonitor 提示。
  3. 驱动在跑但温度代理任务被删：WinTune 用管理员进程静默执行 MiniMonitor.exe --bootstrap-task，只补任务、不碰 PawnIO，不弹 UAC。
  4. WinTune 概览：只有“PawnIO 未运行且找到了 PawnIO_setup.exe”才显示提示条。未安装时标题为「获取温度需安装依赖 PawnIO」，按钮「安装」；已装但停了显示「PawnIO 已安装但未运行」，按钮「启动」。点击后启动服务或运行安装程序。
  5. MiniMonitor 启动：先检测 PawnIO；已在跑就直接采集真实 CPU 温度；已装但停了先 `sc start`；没装或拉不起来才检查安装包。
  6. 有安装包：弹自定义中文对话框，按钮固定为「安装」「取消」；取消后本次运行不再重复提示。
  7. 没有安装包：只提示一次，标记写入 %APPDATA%\MiniMonitor\pawnio.noinstaller.prompted，后续开机不再重复。
  8. 不再因为“温度读不到”弹修复提示：PawnIO 在跑就不再提示。右键菜单保留手动入口：安装温度驱动 / 启动温度驱动 / 修复温度驱动。
  9. 真实温度：MiniMonitor 代理补齐 LHM 需要的 RAMSPDToolkit-NDD / DiskInfoToolkit / System.Numerics.Vectors 等运行库后，已能读到 CPU Package 温度（本机实测 pkg=39 / max=39 / 12 个核心传感器）；任务栏温度来自管理员代理共享内存。WinTune 硬件面板直接读 LHM，也显示同一颗 CPU 的 Package 温度。

四十五、1.5.42（软件缓存清单 + BCU 全量应用卸载）

  1. 一键清理新增「软件缓存」组：与系统缓存分开显示，只列 Cache / Code Cache / GPUCache / ShaderCache / Crashpad / 日志这类可重建目录；默认勾选。
  2. 已知软件缓存：Edge / Chrome / Brave / Vivaldi / Opera / Firefox 的每个账户配置；Office 文档缓存；Adobe 媒体缓存；微信日志与崩溃记录；QQ 音乐日志；npm / NuGet 缓存；Steam 客户端缓存。
  3. 动态识别：扫描当前用户 %LOCALAPPDATA% 与 %APPDATA% 下已装软件的缓存子目录，向下最多 4 层（覆盖 User Data\Default\Cache、Partitions\xxx\Cache 这类结构），只认明确可重建的目录名；users、WebStorage、blob_storage、Local Storage、IndexedDB、Session Storage 等可能承载登录态或用户数据的目录一律不碰。
  4. 不加入聊天记录、接收文件、下载文件、文档、项目目录；原「微信 / QQ 聊天记录」强制删除项已从清理清单移除。
  5. 应用卸载只走 BCU：不再调用软件自带 UninstallString，不再显示原生「卸载」按钮；单个和批量都生成 .bcul 交给 BCU-console。
  6. 状态分三档：可以卸载（个人软件且官方卸载命令可用）；谨慎卸载（系统组件、运行库/驱动、没有官方命令由 BCU 通用卸载器接手）；不可卸载（WinTune 自身组件或缺少 BCU 引擎）。
  7. 系统软件不再在 BCU 层静默跳过：只要 BCU 能接手就显示卸载按钮，风险由「谨慎卸载」状态和二次确认提示承担；WinTune / MiniMonitor 自身仍禁止卸载。
  8. 验收：应用清单 24 个「可以卸载」、25 个「谨慎卸载」；软件缓存扫描本机 29 项、约 4.17 GB，其中豆包缓存/日志约 2.7 GB、抖音缓存 346 MB、Edge 缓存 172 MB、微信日志 33 MB。

四十六、1.5.43（卸载确认与进度弹窗）

  1. 确认弹窗标题改为「将要卸载 xxx 软件」或「将要卸载 N 个软件」，正文只写「您确定要卸载 xxx 软件吗？」，不再出现卸载引擎名称。
  2. 左下角新增「详细 ▾」：展开后显示每个软件的名称、版本、发布者、安装目录、预计占用、安装目录顶层文件名，以及文件 / 保留范围；系统组件没有固定安装目录时明确标注。
  3. 右下角按钮固定为「取消」「开始卸载」。点开始后进入进度页，显示百分比进度条、状态文字和可展开的详细日志。
  4. 卸载期间隐藏右上角关闭按钮，避免误关；卸载完成后只显示「详细」和「完成」。
  5. 点「完成」才关闭弹窗并重新读取应用列表，刷新可卸载项、状态和筛选计数；卸载结束时不再提前刷新列表。
  6. 概览检测不到 CPU 温度时，在快捷入口上方显示「检测不到 CPU 温度，请安装依赖程序」，按钮为「安装依赖」；安装后自动重新读取温度。

四十七、1.5.44（卸载深度清理）

  1. 卸载确认框新增「删除所有个人数据与残留（不可恢复）」勾选，个人软件默认勾选；只选系统组件时自动禁用，并提示深度清理不会执行。
  2. 勾选后，BCU 卸载成功且该软件已从应用清单消失，再执行深度清理：删除安装目录、自己的 %LOCALAPPDATA% / %APPDATA% / %PROGRAMDATA% 配置与缓存、Documents / Downloads / Desktop 下同名数据目录、开始菜单与桌面快捷方式。
  3. 注册表只删该软件自己的键（Software\<软件名>、Software\<发布者>\<软件名>、卸载登记和 WOW6432Node 对应项），不整棵删除发布者公共目录，不碰系统组件、运行库和驱动。
  4. 删除目录逐项写日志；占用中的文件先清只读/隐藏属性再删，失败会计数并在完成页显示，不会假装全部清理成功。
  5. 不勾选时保持原行为：只卸载程序，个人数据、缓存、账号数据和注册表残留保留。

四十八、1.5.45（系统优化新增：UAC 安全桌面、任务栏时钟与一批 Windows 微调）

  1. 新增「关闭 UAC 安全桌面」：只把提升授权弹窗从独立安全桌面移到当前桌面，UAC 本身仍开启；解决远程桌面 / 串流时点“是”后黑屏卡住。属于降低隔离性的重度项，详情里写明风险，可一键恢复原值。
  2. 任务栏时钟：保留「显示秒」，新增「显示星期」。显示秒改注册表 ShowSecondsInSystemClock，显示星期改 HKCU\Control Panel\International\sShortDate，在现有格式前补 dddd，已带星期的不重复加。
  3. 新增「去掉开机启动项延迟」：写 Explorer\Serialize 的 StartupDelayInMSec=0 与 WaitForIdleState=0，登录后启动项不再等系统空闲；低配机同时拉起多个程序会更忙。
  4. 新增「关闭透明与模糊效果」「关闭窗口动画」「跳过锁屏界面」：分别减少任务栏/开始菜单的 GPU 合成、关闭窗口缩放动画、开机/唤醒直接到登录界面。
  5. 新增「关闭 Windows 错误报告」「关闭客户体验改善计划」：减少崩溃后的报告生成/上报与 CEIP 样本上传。
  6. 所有新增项沿用原有机制：首次应用前记录原值到 tweaks\state.tsv，写 HKLM 前导出 .reg 并尝试建还原点；关掉开关再点「应用」即可恢复。
  7. 本机验证：Win10 调整项从 48 项增至 56 项；新增 8 项全部出现在列表，UAC 项标记为需要管理员、重度；任务栏“显示秒/显示星期”当前已是目标值，显示为已应用。

四十九、1.5.46（图标统一、概览减字、系统优化重启资源管理器）

  1. WinTune 的 exe 内嵌图标和界面左上角品牌标统一改为同一套深色圆角底 + 青绿仪表盘 / 上行箭头图标，16 / 24 / 32 / 48 / 64 / 128 / 256 七档，四角透明，不再保留旧的 W 字标。
  2. 概览 hero 卡去掉“内存、处理器与系统盘都在正常范围，没有需要立刻处理的地方；要做维护就按下面的模块单独来”这一行说明；告警出现时也只保留标题和指标，不再插入第二段解释。
  3. 系统优化底部操作栏新增「重启资源管理器」按钮，位置在「恢复默认」左侧。点击先弹确认，确认后后台执行 taskkill /f /im explorer.exe 并重新启动 explorer.exe；任务栏和桌面会闪一下，已打开的文件窗口不受影响。
  4. 该按钮复用现有后台线程，不阻塞 WebView2；完成后恢复按钮文字并 toast 提示结果，失败时写日志。
  5. 版本号改为 1.5.46。


