using System;
using System.Collections.Generic;
using System.Text;

namespace WinTune
{
    /// <summary>一条判定依据：界面上每个「建议删除 / 不建议删除 / 推荐」都要能追到这句话。</summary>
    public class EvRow
    {
        public string Id = "";
        public string Group = "";     // 空间清理 / 更新残留 / 系统优化 / 维护修复
        public string Item = "";
        public string Verdict = "";
        public string Basis = "";     // 为什么这么判
        public string Source = "";    // 出处：文档名 / 命令 / 系统行为
        public string Url = "";       // 官方链接，没有就留空
    }

    /// <summary>
    /// 判定依据表。原则：
    /// 1) 能引官方文档的一律引官方链接（微软 Learn / 微软支持页），链接都在编码时逐条查过；
    /// 2) 引不到文档的，只写「Windows 自带功能 / 可验证的系统行为」，不写厂商推荐口径，也绝不编链接；
    /// 3) 与官方口径冲突的判定必须按官方改（例如 Prefetch、ProgramData\Package Cache）。
    /// </summary>
    public static class Evidence
    {
        private static List<EvRow> rows;

        private static EvRow R(string id, string group, string item, string verdict,
            string basis, string source, string url)
        {
            EvRow r = new EvRow();
            r.Id = id; r.Group = group; r.Item = item; r.Verdict = verdict;
            r.Basis = basis; r.Source = source; r.Url = url;
            return r;
        }

        public static List<EvRow> All()
        {
            if (rows != null) return rows;
            List<EvRow> l = new List<EvRow>();

            /* ---------- 空间清理 ---------- */
            l.Add(R("E01", "空间清理", "用户临时文件 / 系统临时目录", "随意删",
                "程序运行中的临时副本，退出后即失效。微软支持页把「临时文件」列为可用存储感知或磁盘清理直接删除的一类。",
                "Microsoft Support · 释放 Windows 中的驱动器空间",
                "https://support.microsoft.com/zh-CN/Windows/Experience/Storage-FileManagement/free-up-drive-space-in-windows"));
            l.Add(R("E02", "空间清理", "Windows 更新缓存（SoftwareDistribution\\Download）", "随意删",
                "微软排障步骤原文：停 Windows Update 服务后删除该目录内的文件与文件夹，但不要删目录本身；缓存会自动重建。",
                "Microsoft Support · 解决更新 Windows 时遇到的问题",
                "https://support.microsoft.com/zh-CN/Windows/Deployment/Updates-Lifecycle/troubleshoot-problems-updating-windows"));
            l.Add(R("E03", "空间清理", "传递优化缓存", "随意删",
                "微软官方 FAQ：缓存会自动清理，磁盘紧张时可手动清；清掉只影响下次更新从局域网/缓存服务器取件的速度。",
                "Microsoft Learn · Delivery Optimization FAQ",
                "https://learn.microsoft.com/en-au/windows/deployment/do/waas-delivery-optimization-faq"));
            l.Add(R("E04", "空间清理", "错误报告队列与归档（WER）", "随意删",
                "WER 是基于事件的问题反馈机制，只负责收集问题信息并上报；待上传的报告属于可选诊断数据，删掉不影响系统运行。",
                "Microsoft Learn · Windows 错误报告与诊断启用指南",
                "https://learn.microsoft.com/zh-cn/troubleshoot/windows-client/system-management-components/windows-error-reporting-diagnostics-enablement-guidance"));
            l.Add(R("E05", "空间清理", "Edge / Chrome 缓存", "随意删",
                "浏览器用户数据目录里，历史、书签、Cookie 与缓存是各自独立的文件/子目录；这里只清 Cache / Code Cache / GPUCache / CacheStorage 这类缓存目录，不碰登录状态与书签。",
                "Chromium · User Data Directory（配置文件与缓存分目录存放）",
                "https://chromium.googlesource.com/playground/chromium-org-site/+/refs/heads/main/user-experience/user-data-directory.md"));
            l.Add(R("E06", "空间清理", "预读取文件（Prefetch）", "不建议删",
                "微软答复原文：Prefetch 由系统自行维护、会自动清理，不需要删；清空后头几次开机与应用启动反而更慢。旧教程里的「删 Prefetch 提速」已被官方否掉。",
                "Microsoft Q&A · can I delete the data in the prefetch folder",
                "https://learn.microsoft.com/en-us/answers/questions/2432551/can-i-delete-the-data-in-the-prefetch-folder?page=5"));
            l.Add(R("E07", "空间清理", "系统日志（CBS / DISM 等）", "谨慎删",
                "CBS.log / DISM.log 是组件服务与 DISM 的排障日志，出问题时是唯一现场记录；删了不影响系统，但排障时就没有依据，所以默认不勾、标谨慎。",
                "Microsoft Learn · 部署故障排除和日志文件（%windir%\\logs\\cbs）",
                "https://learn.microsoft.com/zh-cn/windows-hardware/manufacture/desktop/deployment-troubleshooting-and-log-files?view=windows-11"));
            l.Add(R("E08", "空间清理", "字体缓存", "谨慎删",
                "字体缓存数据文件由字体缓存服务生成；微软给出的修复流程是停掉字体缓存服务、删掉缓存数据文件后由系统重建，首次显示文字会短暂变慢。",
                "Microsoft Support · 字体缓存导致 CPU 过高/报错时的处理",
                "https://support.microsoft.com/zh-cn/help/937135/error-message-or-high-cpu-utilization-when-running-a-windows-presentat"));
            l.Add(R("E09", "空间清理", "旧系统备份 Windows.old", "谨慎删",
                "微软支持页：升级后 10 天系统会自动删；提前删可以立刻回收空间，但删掉就再也回不到旧版本。需管理员权限。",
                "Microsoft Support · 删除以前版本的 Windows",
                "https://support.microsoft.com/zh-CN/Windows/Deployment/Install-Upgrade/delete-your-previous-version-of-windows"));
            l.Add(R("E10", "空间清理", "WinSxS 组件回收", "谨慎删（执行 DISM）",
                "微软明确：不要手工删 WinSxS；只能用 DISM /Online /Cleanup-Image /StartComponentCleanup。加了 /ResetBase 之后已装的更新将无法卸载。",
                "Microsoft Learn · 清理 WinSxS 文件夹",
                "https://learn.microsoft.com/zh-cn/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11"));
            l.Add(R("E11", "空间清理", "休眠文件 hiberfil.sys", "谨慎删（关闭休眠）",
                "大小约等于物理内存；只能用 powercfg /hibernate off 关闭休眠来释放，直接删文件无效。关闭后不能休眠，混合睡眠与快速启动也会受影响。",
                "Microsoft Learn · 如何禁用和重新启用休眠",
                "https://learn.microsoft.com/zh-cn/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernate"));
            l.Add(R("E12", "空间清理", "回收站", "随意删",
                "微软存储感知文档：回收站中的项目会按设定时间自动删除；清空回收站即删除其中已删文件，不可恢复，这一步是用户明确动作，不涉及系统文件。",
                "Microsoft Support · 使用存储感知管理驱动器空间",
                "https://support.microsoft.com/zh-cn/windows/experience/storage-filemanagement/manage-drive-space-with-storage-sense"));
            l.Add(R("E13", "空间清理", "缩略图 / 图标缓存", "随意删",
                "微软支持页：磁盘清理默认就勾选「缩略图」这一类，说明它是可安全删除的缓存；删掉后资源管理器翻目录时重建，不丢任何原图与原文件。",
                "Microsoft Support · 释放 Windows 中的驱动器空间（磁盘清理类别）",
                "https://support.microsoft.com/zh-CN/Windows/Experience/Storage-FileManagement/free-up-drive-space-in-windows"));
            l.Add(R("E14", "空间清理", "DirectX 着色器缓存（D3DSCache / 显卡厂商缓存）", "谨慎删",
                "微软 DirectX 规范原文：磁盘上的着色器缓存可以被磁盘清理工具枚举清除；清掉后首次进游戏或首次播放视频会重新编译、略慢，之后自动重建，不影响画质与存档。",
                "Microsoft · D3D12 Shader Cache APIs（ShaderCache 规范）",
                "http://microsoft.github.io/DirectX-Specs/d3d/ShaderCache.html"));
            l.Add(R("E15", "空间清理", "远程桌面位图缓存", "随意删",
                "远程桌面客户端的持久位图缓存只存过去会话的画面小块，删掉后重连自动重建，不含登录凭据；也可以在 mstsc 里取消「持久位图缓存」不再生成。",
                "Microsoft TechNet 论坛 · Windows 10 remote desktop cache",
                "https://social.technet.microsoft.com/Forums/windows/en-US/6e365bdc-2ce7-4347-a654-ffb9eb64502e/windows-10-remote-desktop-cache"));
            l.Add(R("E16", "空间清理", "内核实时报告 LiveKernelReports", "随意删",
                "微软文档写明实时转储默认就存在 C:\\Windows\\LiveKernelReports，是驱动/组件出问题时的现场文件，只在提交问题单时用；删掉不影响系统。",
                "Microsoft Learn · 内核实时转储代码参考",
                "https://learn.microsoft.com/zh-cn/windows-hardware/drivers/debugger/kernel-live-dump-code-reference"));
            l.Add(R("E38", "空间清理", "崩溃转储（Minidump / MEMORY.DMP）", "随意删",
                "WER 的转储文件用于事后分析崩溃原因，只在提交问题给厂商时有用；删掉不影响系统运行，下次崩溃会重新生成。",
                "Microsoft Learn · About WER（转储与错误报告流程）",
                "https://learn.microsoft.com/en-us/Windows/Win32/wer/about-wer"));
            l.Add(R("E39", "空间清理", "软件缓存 / 日志（Cache、Code Cache、GPUCache、Crashpad）", "随意删",
                "只处理应用公开的缓存、着色器缓存、日志与崩溃记录目录；这些内容由应用按需重建，不含账号登录态、聊天记录、下载文件与用户文档。删后首次启动或首次打开内容可能略慢，但不会丢用户数据。",
                "应用数据目录约定（缓存与用户数据分目录存放）", ""));
            l.Add(R("E17", "空间清理", "微信 / QQ 聊天记录", "不建议删",
                "消息、图片、接收的文件都在这里，删了不可恢复，且没有云端全量备份。",
                "应用数据目录（用户数据，不是缓存）", ""));
            l.Add(R("E18", "空间清理", "驱动存储 DriverStore", "不建议删",
                "微软文档：驱动存储是受信任的驱动包集合，只有这里的驱动包才能装到设备上；删了可能导致设备无法安装或工作异常。要清只能靠 pnputil 逐个卸载不用的驱动包。",
                "Microsoft Learn · 驱动程序存储（Driver Store）",
                "https://learn.microsoft.com/zh-cn/windows-hardware/drivers/install/driver-store"));
            l.Add(R("E19", "空间清理", "已安装程序目录", "不建议删",
                "仍在使用的软件本体，删除等于破坏安装；卸载请走应用管理里的卸载程序。",
                "程序安装目录", ""));

            /* ---------- 更新残留 ---------- */
            l.Add(R("E20", "更新残留", "浏览器旧版本目录", "建议删除",
                "Chrome / Edge 升级后把旧版本留在 Application 下的版本号目录里，运行时只用当前版本；删旧版不影响书签与登录状态。",
                "Chromium 更新机制（按版本号分目录存放）", ""));
            l.Add(R("E21", "更新残留", "Electron / Squirrel 应用旧版本目录（app-x.y.z）", "建议删除",
                "这类应用把每个版本放在一个 app-x.y.z 目录里、只从最新那个启动；正常升级后旧目录会被清掉，升级中断时才会留下来，占空间但不影响启动。",
                "Squirrel 应用目录结构（app-x.y.z 版本目录 + packages）",
                "https://www.squirreldisk.com/blog/squirreltemp-squirrel-exe"));
            l.Add(R("E22", "更新残留", "软件自己的 backup / prev / .bak 目录", "不建议删除",
                "这是软件留的回滚快照，删掉可能失去升级失败时回退的机会；空间够就留着，所以只展示占用、不进勾选。",
                "应用自建备份目录", ""));
            l.Add(R("E23", "更新残留", "安装包缓存（全机）%ProgramData%\\Package Cache", "不建议删除",
                "微软官方口径：包缓存是修复、修改、卸载时用的安装包来源；删了可能无法修复/卸载/重装相关程序。要省空间应改用安装器的 --nocache 或迁移缓存。",
                "Microsoft Learn · 禁用或移动包缓存",
                "https://learn.microsoft.com/zh-cn/visualstudio/install/disable-or-move-the-package-cache?view=visualstudio"));
            l.Add(R("E24", "更新残留", "安装包缓存（当前用户）%LOCALAPPDATA%\\Package Cache", "建议删除",
                "当前用户的安装器缓存，删掉后修复安装需要重新联网下载；不影响已装程序运行。",
                "Microsoft Learn · 禁用或移动包缓存（同一机制，按用户存放）",
                "https://learn.microsoft.com/zh-cn/visualstudio/install/disable-or-move-the-package-cache?view=visualstudio"));
            l.Add(R("E25", "更新残留", "更新临时目录 SquirrelTemp", "建议删除",
                "Squirrel 安装器解包安装包用的临时目录，装完就无用；删掉不影响已装应用，下次安装/升级会自己重建。",
                "SquirrelTemp 目录说明（Squirrel 安装器临时目录）",
                "https://www.squirreldisk.com/blog/squirreltemp-squirrel-exe"));
            l.Add(R("E26", "更新残留", "错误报告（待发送）", "建议删除",
                "尚未上传的崩溃报告，只用于排障；删除不影响系统与软件运行。",
                "Microsoft Learn · Windows 错误报告与诊断启用指南",
                "https://learn.microsoft.com/zh-cn/troubleshoot/windows-client/system-management-components/windows-error-reporting-diagnostics-enablement-guidance"));
            l.Add(R("E36", "更新残留", "已装软件自己的 update / updater / old 子目录", "建议删除",
                "升级过程把安装包解到这里、或把旧文件挪到这里，当前版本运行时不再读它；删掉不影响启动。",
                "应用安装目录结构（按目录名逐个判定，并列出所属软件）", ""));

            /* ---------- 系统优化 ---------- */
            l.Add(R("E27", "系统优化", "关闭诊断数据上报（AllowTelemetry=0）", "可选（有代价）",
                "微软文档：0 对应「诊断数据关闭（Security）」，但该策略只覆盖专业版/企业版/教育版/IoT Enterprise；家庭版最低仍是「必需」。另外安全级别不上传更新失败信息，依赖 Windows Update 排障时不建议设 0。",
                "Microsoft Learn · 配置 Windows 诊断数据 + 策略 CSP System",
                "https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization"));
            l.Add(R("E28", "系统优化", "关闭广告 ID", "推荐",
                "微软隐私文档：在设置里关掉「允许应用使用广告 ID」，就是关闭广告 ID 而不只是重置；对应 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo\\Enabled=0，只影响跨应用个性化广告。",
                "Microsoft Learn · 管理 Windows 组件到 Microsoft 服务的连接",
                "https://learn.microsoft.com/zh-cn/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services"));
            l.Add(R("E29", "系统优化", "关闭快速启动（HiberbootEnabled=0）", "可选（有代价）",
                "快速启动把内核会话休眠到磁盘，关机不彻底；关掉后开机略慢，但双系统、外设识别、更新异常会少很多。",
                "Windows 快速启动机制（电源选项里的「启用快速启动」开关）", ""));
            l.Add(R("E30", "系统优化", "禁止主板固件装软件（WPBT）", "推荐（进阶）",
                "WPBT 允许固件在开机时向 Windows 写入并执行厂商程序，厂商预装工具就是这么装进来的；关掉后品牌机自带的固件工具不会再自动安装。",
                "Microsoft Intune 文档 · WPBT 说明（可关闭）", ""));
            l.Add(R("E31", "系统优化", "关闭 Windows 搜索索引", "重度（有代价）",
                "微软排障文档讨论的就是 Windows 搜索索引器本身：索引项越多越吃资源，但关掉索引服务后，开始菜单、Outlook、资源管理器搜索都会变慢或搜不全。",
                "Microsoft Learn · Windows 搜索性能疑难解答",
                "https://learn.microsoft.com/zh-cn/troubleshoot/windows-client/shell-experience/windows-search-performance-issues"));
            l.Add(R("E32", "系统优化", "禁用 SMB1 / LLMNR", "推荐（安全）",
                "微软安全基线文档原文：基线在客户端与服务器上同时禁用 SMBv1、强制 SMB 3.0 以上，并关闭 LLMNR 与 NetBIOS over TCP/IP，改为只用 DNS，目的就是减少局域网攻击面。",
                "Microsoft Learn · Windows Server 安全基线",
                "https://learn.microsoft.com/en-gb/windows-server/security/osconfig/osconfig-how-to-configure-security-baselines"));
            l.Add(R("E37", "空间清理", "优先用系统自带清理入口（存储感知 / 清理建议）", "方法论",
                "微软支持页把「存储感知」和「清理建议」作为官方推荐的空间回收方式，并说明清理建议会标出可安全删除的文件类型；本软件的清理范围与这两处一致，不额外扩大。",
                "Microsoft Support · 释放空间以进行 Windows 更新",
                "https://support.microsoft.com/zh-CN/Windows/Deployment/Updates-Lifecycle/free-up-space-for-windows-updates"));

            /* ---------- 维护修复 ---------- */
            l.Add(R("E33", "维护修复", "SFC 扫描 / DISM 组件修复", "可执行",
                "微软官方修复顺序：先 DISM /Online /Cleanup-Image /RestoreHealth 修组件存储，再 sfc /scannow 校验系统文件；两者都只读修复，不改用户数据。",
                "Microsoft Learn · 清理 WinSxS（DISM 参数）",
                "https://learn.microsoft.com/zh-cn/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11"));
            l.Add(R("E34", "维护修复", "chkdsk 只读扫描 / winsock 与 TCP-IP 重置 / DNS 刷新", "可执行",
                "都是 Windows 自带命令（chkdsk /scan 只读校验、netsh winsock reset、ipconfig /flushdns），只重置网络栈或校验文件系统，不改个人文件。",
                "Windows 自带命令行工具", ""));
            l.Add(R("E35", "维护修复", "Windows 更新组件重置", "可执行",
                "停 wuauserv / bits / cryptsvc 后重命名 SoftwareDistribution 与 catroot2，正是微软排障页给出的同一套步骤；结束后自动把服务恢复。",
                "Microsoft Support · 解决更新 Windows 时遇到的问题",
                "https://support.microsoft.com/zh-CN/Windows/Deployment/Updates-Lifecycle/troubleshoot-problems-updating-windows"));

            rows = l;
            return rows;
        }

        public static EvRow Find(string id)
        {
            if (String.IsNullOrEmpty(id)) return null;
            foreach (EvRow r in All()) if (r.Id == id) return r;
            return null;
        }

        public static string Json()
        {
            List<string> arr = new List<string>();
            foreach (EvRow r in All())
            {
                arr.Add(new JB()
                    .Add("id", r.Id)
                    .Add("group", r.Group)
                    .Add("item", r.Item)
                    .Add("verdict", r.Verdict)
                    .Add("basis", r.Basis)
                    .Add("source", r.Source)
                    .Add("url", r.Url)
                    .End());
            }
            return new JB().Add("type", "evidence")
                .Raw("rows", "[" + String.Join(",", arr.ToArray()) + "]").End();
        }
    }
}
