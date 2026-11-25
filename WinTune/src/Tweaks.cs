using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>一条注册表操作。Kind：dword / string / delete（删除该值）。</summary>
    public class RegOp
    {
        public string Hive = "HKLM";
        public string Path = "";
        public string Name = "";
        public string Kind = "dword";
        public int Dw = 0;
        public string Sz = "";
    }

    public class Tweak
    {
        public string Id = "";
        public string Cat = "";
        public string Name = "";
        public string Desc = "";
        public int Level = 1;            // 1 一般优化 / 2 中度优化 / 3 重度优化
        public int Eff = 0;              // 0 立即生效 / 1 重启资源管理器后生效 / 2 重启后生效
        public int Os = 0;               // 0 通用 / 10 仅 Win10 / 11 仅 Win11
        public string Risk = "safe";     // safe / adv / care（由 Level 派生，供旧逻辑复用）
        public bool NeedRestart = false;
        public bool NeedAdmin = false;
        public string Note = "";
        /// <summary>判定依据编号（见 Evidence）：这条"推荐"对应的官方说法。</summary>
        public string Ev = "";
        public List<RegOp> Ops = new List<RegOp>();
    }

    /// <summary>
    /// 系统调整项的目录与执行。原则：
    /// 1) 只碰白名单里的注册表键，绝不删键，只写值或删值；
    /// 2) 首次应用前把原值记到 state.tsv，撤销时按原值恢复；
    /// 3) 写 HKLM 前导出 .reg，并尝试建还原点。
    /// </summary>
    public static class Tweaks
    {
        private static List<Tweak> cat;

        public static List<Tweak> Catalog()
        {
            if (cat == null) cat = Build();
            return cat;
        }

        public static List<string> SafePreset()
        {
            List<string> ids = new List<string>();
            foreach (Tweak t in Catalog()) if (t.Level == 1 && Fits(t)) ids.Add(t.Id);
            return ids;
        }

        /* ================= 目录 ================= */

        /// <summary>生效方式：0 立即生效；1 需重启资源管理器；2 需重启系统。</summary>
        private static int EffOf(string id)
        {
            if (id == "classicmenu" || id == "copilot" ||
                id == "clocksec" || id == "clockweekday" || id == "windowanim") return 1;
            if (id == "faststart" || id == "ntfsaccess" || id == "searchindex" ||
                id == "smb1" || id == "llmnr" || id == "recall" ||
                id == "bitlocker" || id == "wpbt" ||
                id == "startupdelay" || id == "lockscreen") return 2;
            return 0;
        }

        /// <summary>0 通用；10 仅 Win10；11 仅 Win11（20H2 以后的新外壳专属项）。</summary>
        private static int OsOf(string id)
        {
            switch (id)
            {
                case "copilot":
                case "recall":
                case "startrecommend":
                case "endtask":
                case "searchhighlights":
                case "taskbarleft":
                case "widgets":
                case "chat":
                case "classicmenu":
                    return 11;
                case "newsfeeds":
                    return 10;
            }
            return 0;
        }

        /// <summary>当前系统是否 Windows 11（内部版本 22000 起算）。</summary>
        public static bool IsWin11()
        {
            try
            {
                Version v = Environment.OSVersion.Version;
                return v.Major >= 10 && v.Build >= 22000;
            }
            catch { return false; }
        }

        /// <summary>这一项在当前系统上是否适用。</summary>
        public static bool Fits(Tweak t)
        {
            if (t.Os == 0) return true;
            if (t.Os == 11) return IsWin11();
            return !IsWin11();
        }

        private static Tweak T(string id, string cat_, string name, string desc, int level, bool restart, params RegOp[] ops)
        {
            Tweak t = new Tweak();
            t.Id = id; t.Cat = cat_; t.Name = name; t.Desc = desc;
            t.Level = level;
            t.Risk = level == 1 ? "safe" : (level == 2 ? "adv" : "care");
            t.Eff = EffOf(id);
            t.Os = OsOf(id);
            t.NeedRestart = t.Eff > 0;
            foreach (RegOp o in ops)
            {
                t.Ops.Add(o);
                if (o.Hive == "HKLM") t.NeedAdmin = true;
            }
            return t;
        }

        /// <summary>给这一项挂判定依据编号，界面上点开能看到出处。</summary>
        private static Tweak E(Tweak t, string ev)
        {
            t.Ev = ev;
            return t;
        }

        private static RegOp Dw(string hive, string path, string name, int v)
        {
            RegOp o = new RegOp();
            o.Hive = hive; o.Path = path; o.Name = name; o.Kind = "dword"; o.Dw = v;
            return o;
        }

        private static RegOp Sz(string hive, string path, string name, string v)
        {
            RegOp o = new RegOp();
            o.Hive = hive; o.Path = path; o.Name = name; o.Kind = "string"; o.Sz = v;
            return o;
        }

        private static RegOp Rm(string hive, string path, string name)
        {
            RegOp o = new RegOp();
            o.Hive = hive; o.Path = path; o.Name = name; o.Kind = "delete";
            return o;
        }

        /// <summary>
        /// 任务栏日期读的是 HKCU 的 sShortDate。默认形如 yyyy/MM/dd，
        /// 在前面补 dddd 就会显示完整星期；已经带 ddd/dddd 的格式不再重复加。
        /// </summary>
        private static string ShortDateTarget()
        {
            string s = "";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International", false))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("sShortDate", null);
                        if (v != null) s = Convert.ToString(v).Trim();
                    }
                }
            }
            catch { }
            if (s.Length == 0) s = "yyyy/MM/dd";
            if (s.IndexOf("ddd", StringComparison.OrdinalIgnoreCase) >= 0) return s;
            return "dddd " + s;
        }

        /// <summary>
        /// 关闭“任务栏显示星期”时，把 sShortDate 里的 ddd / dddd 去掉。
        /// 不依赖 state.tsv：这项可能在 WinTune 之外已经被手动改过。
        /// </summary>
        private static bool RemoveWeekday()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International", true))
                {
                    if (k == null) return false;
                    object v = k.GetValue("sShortDate", null);
                    string s = v == null ? "" : Convert.ToString(v);
                    string clean = s.Replace("dddd", "").Replace("ddd", "").Trim();
                    clean = clean.Replace("  ", " ").Trim();
                    if (clean.Length == 0) clean = "yyyy/MM/dd";
                    k.SetValue("sShortDate", clean, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex) { Log.Warn("tweak", "恢复任务栏星期失败：" + ex.Message); return false; }
        }

        private static List<Tweak> Build()
        {
            List<Tweak> l = new List<Tweak>();
            const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

            /* —— 安全与授权 —— */
            l.Add(T("uacsecure", "安全", "关闭 UAC 安全桌面",
                "UAC 仍然开启，只把授权弹窗从独立安全桌面移到当前桌面。远程桌面 / 串流时点“是”不再黑屏、不再卡住。" +
                "代价是弹窗与普通程序同屏，隔离性下降；单机自用可开，公用电脑或经常运行不明程序不建议。", 3, false,
                Dw("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "PromptOnSecureDesktop", 0)));

            /* —— 隐私与遥测 —— */
            l.Add(E(T("telemetry", "隐私", "关闭诊断数据上报",
                "遥测级别降到 0（诊断数据关闭）。只对专业版 / 企业版 / 教育版 / IoT 生效，家庭版最低仍是「必需」；" +
                "设 0 后更新失败信息也不会上报，靠 Windows 更新排障的话建议留在「必需」。", 1, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0)), "E27"));
            l.Add(E(T("adid", "隐私", "关闭广告 ID",
                "关闭应用之间的广告标识符，减少个性化广告追踪。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)), "E28"));
            l.Add(T("activity", "隐私", "关闭活动历史记录",
                "停止记录你打开过的应用与文档，也不再上传到微软账户。时间线功能随之失效。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0)));
            l.Add(T("location", "隐私", "关闭定位服务",
                "关闭系统级定位，地图、天气等依赖定位的应用会退化为按 IP 估算。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1)));
            l.Add(T("feedback", "隐私", "关闭反馈与使用统计",
                "不再弹“反馈中心”邀请，也不收集使用习惯数据。", 1, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1),
                Dw("HKCU", @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)));
            l.Add(T("errorreport", "隐私", "关闭 Windows 错误报告",
                "程序崩溃后不再生成并上报错误报告，减少崩溃后的磁盘写入与联网动作。排查蓝屏、崩溃原因时会少一份 WER 记录。",
                1, false,
                Dw("HKLM", @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1)));
            l.Add(T("ceip", "隐私", "关闭客户体验改善计划",
                "退出 Windows CEIP，不再上传使用习惯与诊断样本；不影响安全更新和错误代码。", 1, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0)));
            l.Add(T("tips", "隐私", "关闭系统提示与建议",
                "关掉设置页、开始菜单里的“提示/建议/新功能推荐”推送。", 1, false,
                Dw("HKCU", CDM, "SoftLandingEnabled", 0),
                Dw("HKCU", CDM, "SubscribedContent-338389Enabled", 0),
                Dw("HKCU", CDM, "SubscribedContent-338388Enabled", 0)));
            l.Add(T("lockads", "隐私", "关闭锁屏广告与花絮",
                "锁屏不再显示微软推广的壁纸与“有趣的事实”。", 1, false,
                Dw("HKCU", CDM, "RotatingLockScreenOverlayEnabled", 0),
                Dw("HKCU", CDM, "SubscribedContent-338387Enabled", 0)));
            l.Add(T("welcome", "隐私", "关闭安装后的应用推荐",
                "重装或更新后不再自动推送“为你推荐”的商店应用。", 1, false,
                Dw("HKCU", CDM, "SubscribedContent-310093Enabled", 0),
                Dw("HKCU", CDM, "SilentInstalledAppsEnabled", 0)));
            l.Add(T("cloudcontent", "隐私", "关闭云内容同步",
                "停止从微软云端拉取建议内容；锁屏、开始菜单的云推荐一并失效。", 2, false,
                Dw("HKCU", CDM, "ContentDeliveryAllowed", 0)));
            l.Add(T("consumerfeatures", "隐私", "关闭系统推荐内容",
                "阻止开始菜单与锁屏自动推送并安装推广的应用/游戏（俗称预装广告），不影响商店手动安装。", 1, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)));
            l.Add(T("companion", "隐私", "关闭设备伴侣应用自动安装",
                "插入手机、耳机等设备时，系统不再自动装厂商的伴侣应用。", 1, false,
                Dw("HKCU", CDM, "PreventDeviceCompanionAppInstall", 1)));
            l.Add(T("syncnotify", "隐私", "关闭网盘同步的推广提示",
                "资源管理器里不再出现“把文件同步到云”这类提示与角标。", 1, false,
                Dw("HKCU", ADV, "ShowSyncProviderNotifications", 0)));
            l.Add(T("starttrackdocs", "隐私", "不记录最近打开的文件",
                "开始菜单与跳转列表不再记录你最近打开的文档名。", 1, false,
                Dw("HKCU", ADV, "Start_TrackDocs", 0)));
            l.Add(T("copilot", "隐私", "关闭 Copilot（Win11）",
                "Win11 专属；关掉任务栏上的 Copilot 入口与后台常驻，需重启资源管理器。", 1, false,
                Dw("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)));
            l.Add(T("recall", "隐私", "关闭 Recall 屏幕快照（Win11）",
                "Win11 24H2 Copilot+ 专属；不再周期性截取屏幕存档，已有快照由系统按策略清理。需重启系统。", 1, true,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0),
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)));
            l.Add(T("startrecommend", "隐私", "关闭开始菜单的推荐项（Win11）",
                "Win11 专属；开始菜单不再显示“推荐”里的提示与推广条目。", 1, false,
                Dw("HKCU", ADV, "Start_IrisRecommendations", 0)));
            l.Add(T("newsfeeds", "隐私", "关闭任务栏新闻与兴趣（Win10）",
                "Win10 专属；移除任务栏上的资讯流，减少后台联网与内存占用。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", 2)));
            l.Add(T("bitlocker", "隐私", "关闭设备自动加密",
                "新装或重置后的设备不再自动启用 BitLocker 设备加密；已经加密的磁盘不会被解密。需重启系统。", 2, true,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Control\BitLocker", "PreventDeviceEncryption", 1)));
            l.Add(E(T("wpbt", "隐私", "禁止主板固件自动装软件",
                "WPBT：阻止主板固件在开机时向系统写入并安装厂商工具（常见于品牌机与部分主板）。需重启系统。", 2, true,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager", "DisablePlatformBinaryTableExecution", 1)), "E30"));

            /* —— 系统与性能 —— */
            l.Add(T("longpaths", "性能", "启用长路径支持",
                "解除 260 字符路径限制，深层目录的工程文件不再报错。极少数老程序可能不适应。", 2, false,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1)));
            l.Add(T("gamedvr", "性能", "关闭游戏录制",
                "关闭 Xbox Game Bar 的后台录制，减少游戏时的 CPU/磁盘占用。不录屏的人基本无感。", 1, false,
                Dw("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0),
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)));
            l.Add(E(T("faststart", "性能", "关闭快速启动",
                "关机时完全关闭内核会话。开机略慢，但双系统、外设识别、更新异常会明显减少。", 2, true,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0)), "E29"));
            l.Add(T("ntfsaccess", "性能", "关闭文件最后访问时间",
                "NTFS 不再为每次读取写入访问时间戳，机械盘上可减少无效写入。少数取证/备份工具依赖该时间。", 2, true,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 1)));
            l.Add(T("mouseaccel", "性能", "关闭鼠标指针加速",
                "指针移动与手速 1:1，FPS 与绘图更稳；习惯系统加速的人会觉得变“慢”。", 2, false,
                Sz("HKCU", @"Control Panel\Mouse", "MouseSpeed", "0"),
                Sz("HKCU", @"Control Panel\Mouse", "MouseThreshold1", "0"),
                Sz("HKCU", @"Control Panel\Mouse", "MouseThreshold2", "0")));
            l.Add(T("menudelay", "性能", "加快菜单弹出速度",
                "菜单悬停延迟从默认 400ms 降到 200ms，操作更跟手。", 1, false,
                Sz("HKCU", @"Control Panel\Desktop", "MenuShowDelay", "200")));
            l.Add(T("hungapp", "性能", "缩短程序无响应等待",
                "程序卡住时系统等待链更短，切窗口不容易整机假死。代价是卡顿的程序更容易被判无响应。", 2, false,
                Sz("HKCU", @"Control Panel\Desktop", "HungAppTimeout", "2000"),
                Sz("HKCU", @"Control Panel\Desktop", "WaitToKillAppTimeout", "4000")));
            l.Add(T("bgapps", "性能", "禁止后台应用常驻",
                "商店应用退到后台后不再继续跑；邮件、日历等应用的实时通知可能延迟。", 2, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)));
            l.Add(T("fse", "性能", "关闭全屏优化",
                "不再把全屏程序强制套一层窗口合成，老游戏、老显卡的全屏切回更利落；个别新游戏的无边框观感会变。", 2, false,
                Dw("HKCU", @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2),
                Dw("HKCU", @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1),
                Dw("HKCU", @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1)));
            l.Add(T("startupdelay", "性能", "去掉开机启动项延迟",
                "登录后不再等系统“空闲”才拉起启动项，开机后程序更快出现。低配机同时启动多个程序时，进入桌面瞬间会更忙。",
                2, true,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", 0)));

            /* —— 界面与操作 —— */
            l.Add(T("showext", "界面", "显示文件扩展名",
                "资源管理器直接显示 .exe / .txt 等后缀，防伪装文件。", 1, false,
                Dw("HKCU", ADV, "HideFileExt", 0)));
            l.Add(T("showhidden", "界面", "显示隐藏文件",
                "显示隐藏项（系统文件仍隐藏），排查残留与配置时必需。日常使用会显得目录更乱。", 2, false,
                Dw("HKCU", ADV, "Hidden", 1)));
            l.Add(T("clocksec", "界面", "任务栏时钟显示秒",
                "时钟精确到秒，方便对时间敏感的操作；需要重启资源管理器才刷新。", 1, false,
                Dw("HKCU", ADV, "ShowSecondsInSystemClock", 1)));
            l.Add(T("clockweekday", "界面", "任务栏时钟显示星期",
                "在任务栏日期前加完整星期（如“星期三 2026/11/25”）。日期格式是全系统共用，资源管理器和部分程序里的短日期也会跟着变长。",
                1, false,
                Sz("HKCU", @"Control Panel\International", "sShortDate", ShortDateTarget())));
            l.Add(T("openpc", "界面", "打开资源管理器默认进此电脑",
                "不再停在“快速访问/主页”，直接看盘符。", 1, false,
                Dw("HKCU", ADV, "LaunchTo", 1)));
            l.Add(T("quickaccess", "界面", "关闭快速访问的历史记录",
                "不再记录最近使用与常用文件夹，保护隐私也提速。", 1, false,
                Dw("HKCU", ADV, "ShowFrequent", 0),
                Dw("HKCU", ADV, "ShowRecent", 0)));
            l.Add(T("searchbox", "界面", "隐藏任务栏搜索框",
                "任务栏腾出空间，Win 键搜索仍可用。", 1, false,
                Dw("HKCU", ADV, "SearchboxTaskbarMode", 0)));
            l.Add(T("taskbarleft", "界面", "任务栏图标靠左",
                "Win11 专属；Win10 上写入无害但无效果。", 1, false,
                Dw("HKCU", ADV, "TaskbarAl", 0)));
            l.Add(T("widgets", "界面", "关闭任务栏小组件",
                "Win11 专属；关掉天气/资讯小组件，减少后台网络请求。", 1, false,
                Dw("HKCU", ADV, "TaskbarDa", 0)));
            l.Add(T("chat", "界面", "关闭任务栏聊天图标",
                "Win11 专属；移除 Teams 聊天入口。", 1, false,
                Dw("HKCU", ADV, "TaskbarMn", 0)));
            l.Add(T("classicmenu", "界面", "恢复经典右键菜单（Win11）",
                "Win11 右键改回 Win10 的完整菜单，少点一次“显示更多选项”；需重启资源管理器，个别 Win11 新版会失效。",
                3, false,
                Sz("HKLM", @"SOFTWARE\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "")));
            l.Add(T("endtask", "界面", "任务栏右键直接结束任务（Win11）",
                "Win11 专属；右键任务栏图标时出现“结束任务”，卡住的窗口不必再开任务管理器。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1)));
            l.Add(T("aeroshake", "界面", "关闭窗口抖动最小化",
                "不再因为拖动窗口时抖一下就把其它窗口全部最小化，避免误触。", 1, false,
                Dw("HKCU", ADV, "DisallowShaking", 1)));
            l.Add(T("fullpath", "界面", "标题栏显示完整路径",
                "资源管理器标题栏显示完整路径，多窗口时一眼看出是哪个目录。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", 1)));
            l.Add(T("cliphist", "界面", "开启剪贴板历史（Win+V）",
                "开启后按 Win+V 可翻出最近复制过的多条内容，默认关闭需手动开。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Clipboard", "EnableClipboardHistory", 1)));
            l.Add(T("visualfx", "界面", "视觉效果设为最佳性能",
                "关闭窗口动画、淡入淡出与阴影，老机器与远程桌面下更跟手；观感会变朴素。", 2, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2)));
            l.Add(T("transparency", "界面", "关闭透明与模糊效果",
                "任务栏、开始菜单不再半透明/模糊，减少 GPU 合成。老机、远程桌面更省资源，观感会变朴素。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)));
            l.Add(T("windowanim", "界面", "关闭窗口动画",
                "最小化、最大化、关闭窗口不再播放缩放动画，切换更干脆；观感会变生硬。", 1, false,
                Sz("HKCU", @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0")));
            l.Add(T("lockscreen", "界面", "跳过锁屏界面",
                "开机或唤醒后直接到登录界面，不再先显示锁屏壁纸；仍然需要输入密码/PIN。适合远程机和固定桌面。",
                2, true,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1)));

            /* —— 搜索与网页结果 —— */
            l.Add(T("bingsearch", "搜索", "关闭开始菜单的网络搜索结果",
                "搜索只查本机，不再回传关键词给 Bing。搜不到本机没有的东西属于正常现象。", 2, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0)));
            l.Add(E(T("searchindex", "搜索", "关闭 Windows 搜索索引",
                "省下索引服务的磁盘与内存；代价是搜索变慢、结果不全，Outlook/开始菜单搜索都会受影响。",
                3, true,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearch", 1)), "E31"));
            l.Add(T("websearchpolicy", "搜索", "禁用搜索的 Web 建议",
                "组策略层面关闭搜索框的网页建议，企业环境常配。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0)));
            l.Add(T("cortana", "搜索", "禁用 Cortana",
                "关闭小娜语音助手，减少常驻进程。语音唤醒等功能一并停用。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0)));
            l.Add(T("searchhighlights", "搜索", "关闭搜索亮点（Win11）",
                "Win11 专属；搜索框不再展示节日图标与每日推荐内容，搜索更纯粹。", 1, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", 0)));

            /* —— 更新与商店 —— */
            l.Add(T("noreboot", "更新", "更新后不自动重启",
                "有登录用户时，更新完成后不强制重启，改为提示。安全补丁仍然会装，只是由你决定何时重启。",
                2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1)));
            l.Add(T("storeauto", "更新", "关闭商店应用自动更新",
                "商店应用不再后台自动升级，改由你手动点更新。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2)));
            l.Add(T("deliverylan", "更新", "传递优化只用局域网",
                "更新分发只从局域网取，不上传也不走互联网 P2P。局域网没有缓存源时下载会变慢。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1)));

            /* —— 网络与启动项 —— */
            l.Add(T("onedrive", "启动", "移除 OneDrive 开机启动",
                "只删开机启动项，不卸载、不动文件；需要时手动打开 OneDrive 仍可用。", 2, false,
                Rm("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive")));
            l.Add(T("edgeboost", "启动", "关闭 Edge 启动加速",
                "Edge 不再常驻后台预加载，省内存；首次启动会慢一点。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0)));
            l.Add(T("edgebg", "启动", "关闭 Edge 关闭后继续运行",
                "关掉“关闭浏览器后仍后台运行扩展”的行为。", 2, false,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0)));
            l.Add(T("autoplay", "启动", "关闭自动播放",
                "插入 U 盘、光盘不再自动弹窗播放，降低误触风险。", 1, false,
                Dw("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 1)));
            l.Add(E(T("smb1", "网络", "关闭 SMBv1 协议",
                "老漏洞协议，现代环境已不需要；有老 NAS、老打印机的机器会连不上共享。", 3, true,
                Dw("HKLM", @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", 0)), "E32"));
            l.Add(E(T("llmnr", "网络", "关闭 LLMNR",
                "减少局域网内名称解析的欺骗面；域环境、老式工作组共享请先确认。", 3, true,
                Dw("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableLLMNR", 0)), "E32"));

            return l;
        }

        /* ================= 状态 ================= */

        private static RegistryKey RootOf(string hive)
        {
            return hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        }

        public static bool OpApplied(RegOp op)
        {
            try
            {
                using (RegistryKey k = RootOf(op.Hive).OpenSubKey(op.Path, false))
                {
                    if (k == null) return op.Kind == "delete";
                    object v = null;
                    try { v = k.GetValue(op.Name, null); } catch { return false; }
                    if (op.Kind == "delete") return v == null;
                    if (v == null) return false;
                    if (op.Kind == "dword") return Convert.ToInt32(v) == op.Dw;
                    return Convert.ToString(v) == op.Sz;
                }
            }
            catch { return false; }
        }

        public static bool Applied(Tweak t)
        {
            foreach (RegOp op in t.Ops) if (!OpApplied(op)) return false;
            return true;
        }

        /// <summary>把一项的注册表操作展开成 JSON 数组，供界面详情抽屉逐条显示。</summary>
        private static string OpListJson(Tweak t)
        {
            StringBuilder b = new StringBuilder(256);
            b.Append('[');
            for (int k = 0; k < t.Ops.Count; k++)
            {
                RegOp o = t.Ops[k];
                if (k > 0) b.Append(',');
                string val;
                if (o.Kind == "dword") val = o.Dw.ToString();
                else if (o.Kind == "delete") val = "(删除该值)";
                else val = o.Sz;
                b.Append(new JB().Add("hive", o.Hive).Add("path", o.Path)
                    .Add("name", o.Name).Add("kind", o.Kind).Add("value", val).End());
            }
            b.Append(']');
            return b.ToString();
        }

        public static string ListJson()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"tweakList\",");
            List<Tweak> list = Catalog();
            List<Tweak> show = new List<Tweak>();
            foreach (Tweak t in list) if (Fits(t)) show.Add(t);
            int applied = 0;
            sb.Append("\"win11\":").Append(IsWin11() ? "true" : "false");
            sb.Append(",\"items\":[");
            for (int i = 0; i < show.Count; i++)
            {
                Tweak t = show[i];
                bool ap = Applied(t);
                if (ap) applied++;
                if (i > 0) sb.Append(',');
                sb.Append(new JB()
                    .Add("id", t.Id).Add("cat", t.Cat).Add("name", t.Name)
                    .Add("desc", t.Desc).Add("risk", t.Risk).Add("level", t.Level).Add("eff", t.Eff)
                    .Add("os", t.Os)
                    .Add("restart", t.NeedRestart).Add("admin", t.NeedAdmin)
                    .Add("applied", ap)
                    .Add("ev", t.Ev)
                    .Add("ops", t.Ops.Count)
                    .Raw("opList", OpListJson(t))
                    .End());
            }
            sb.Append("],\"total\":").Append(show.Count).Append(",\"appliedCount\":").Append(applied);
            sb.Append(",\"admin\":").Append(SysInfo.IsAdmin() ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        public static string PresetJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":\"optPreset\",");
            List<Tweak> list = Catalog();
            int n = 0;
            sb.Append("\"items\":[");
            for (int i = 0; i < list.Count; i++)
            {
                Tweak t = list[i];
                if (n++ > 0) sb.Append(',');
                sb.Append(new JB()
                    .Add("id", t.Id).Add("cat", t.Cat).Add("name", t.Name)
                    .Add("desc", t.Desc).Add("risk", t.Risk).Add("level", t.Level).Add("eff", t.Eff)
                    .Add("restart", t.NeedRestart).Add("admin", t.NeedAdmin)
                    .Add("ev", t.Ev)
                    .Add("applied", Applied(t)).End());
            }
            sb.Append("],\"total\":").Append(n);
            sb.Append('}');
            return sb.ToString();
        }

        /* ================= 原值记录 ================= */

        private static string StateFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "tweaks");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state.tsv");
        }

        private static Dictionary<string, string> LoadState()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(StateFile())) return map;
                foreach (string line in File.ReadAllLines(StateFile(), Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 5) continue;
                    string val = p[4];
                    if (p.Length >= 6) val = val + "\t" + p[5];
                    map[p[0] + "|" + p[1] + "|" + p[2] + "|" + p[3]] = val;
                }
            }
            catch { }
            return map;
        }

        private static void AppendState(string id, RegOp op, string cur)
        {
            try
            {
                using (StreamWriter w = new StreamWriter(StateFile(), true, Encoding.UTF8))
                {
                    w.WriteLine(id + "\t" + op.Hive + "\t" + op.Path + "\t" + op.Name + "\t" + cur);
                }
            }
            catch { }
        }

        private static string CurKind(RegOp op)
        {
            try
            {
                using (RegistryKey k = RootOf(op.Hive).OpenSubKey(op.Path, false))
                {
                    if (k == null) return "__NONE__";
                    RegistryValueKind vk;
                    object v = k.GetValue(op.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (v == null) return "__NONE__";
                    vk = k.GetValueKind(op.Name);
                    if (vk == RegistryValueKind.DWord) return "dword\t" + Convert.ToInt32(v);
                    return "string\t" + Convert.ToString(v);
                }
            }
            catch { return "__NONE__"; }
        }

        /* ================= 应用 / 撤销 ================= */

        private static Tweak Find(string id)
        {
            foreach (Tweak t in Catalog()) if (t.Id == id) return t;
            return null;
        }

        public static string Apply(List<string> ids, Action<string> onEvent)
        {
            List<Tweak> todo = new List<Tweak>();
            int skipped = 0;
            foreach (string id in ids)
            {
                Tweak t = Find(id);
                if (t == null) { skipped++; continue; }
                if (Applied(t)) { skipped++; Emit(onEvent, "skip", t, "已经是目标状态，不需要再改"); continue; }
                todo.Add(t);
            }

            string restore = "";
            if (todo.Count > 0) restore = Backup.TryRestorePoint("WinTune 系统调整前备份");

            // 写 HKLM 前先导出涉及的键
            int exported = 0;
            if (todo.Count > 0)
            {
                Dictionary<string, bool> done = new Dictionary<string, bool>();
                int seq = 0;
                foreach (Tweak t in todo)
                {
                    foreach (RegOp op in t.Ops)
                    {
                        if (op.Hive != "HKLM") continue;
                        string key = op.Hive + "\\" + op.Path;
                        if (done.ContainsKey(key)) continue;
                        done[key] = true;
                        string f = Backup.ExportReg(key, "tweak" + (++seq));
                        if (f.Length > 0) exported++;
                    }
                }
            }

            Dictionary<string, string> state = LoadState();
            int ok = 0, fail = 0;
            List<string> fails = new List<string>();
            foreach (Tweak t in todo)
            {
                bool good = true;
                foreach (RegOp op in t.Ops)
                {
                    string skey = t.Id + "|" + op.Hive + "|" + op.Path + "|" + op.Name;
                    if (!state.ContainsKey(skey))
                    {
                        string cur = CurKind(op);
                        AppendState(t.Id, op, cur);
                        state[skey] = cur;
                    }
                    if (!WriteOp(op)) { good = false; }
                }
                if (good) { ok++; Emit(onEvent, "ok", t, "已应用"); }
                else { fail++; fails.Add(t.Name); Emit(onEvent, "fail", t, "写入失败"); }
            }

            return new JB()
                .Add("type", "tweakDone")
                .Add("ok", ok).Add("fail", fail).Add("skipped", skipped)
                .Add("total", todo.Count)
                .Add("exported", exported)
                .Add("restore", restore)
                .Raw("fails", StrArray(fails))
                .End();
        }

        public static string Revert(List<string> ids, Action<string> onEvent)
        {
            Dictionary<string, string> state = LoadState();
            int ok = 0, fail = 0;
            List<string> fails = new List<string>();
            foreach (string id in ids)
            {
                Tweak t = Find(id);
                if (t == null) continue;
                if (id == "clockweekday")
                {
                    if (RemoveWeekday()) { ok++; Emit(onEvent, "ok", t, "已恢复默认"); }
                    else { fail++; fails.Add(t.Name); Emit(onEvent, "fail", t, "恢复失败"); }
                    continue;
                }
                bool good = true;
                foreach (RegOp op in t.Ops)
                {
                    string skey = id + "|" + op.Hive + "|" + op.Path + "|" + op.Name;
                    if (!state.ContainsKey(skey)) { continue; }  // 没有原值记录的不动
                    string rec = state[skey];
                    string[] parts = rec.Split('\t');
                    string kind = parts[0];
                    string val = parts.Length >= 2 ? parts[1] : "";
                    if (!RestoreOp(op, kind, val)) good = false;
                }
                if (good) { ok++; Emit(onEvent, "ok", t, "已恢复默认"); }
                else { fail++; fails.Add(t.Name); Emit(onEvent, "fail", t, "恢复失败"); }
            }
            return new JB()
                .Add("type", "tweakDone")
                .Add("ok", ok).Add("fail", fail).Add("skipped", 0).Add("total", ids.Count)
                .Add("exported", 0).Add("restore", "撤销不新建还原点")
                .Raw("fails", StrArray(fails))
                .End();
        }

        private static bool RestoreOp(RegOp op, string kind, string val)
        {
            try
            {
                if (kind == "__NONE__") return DeleteVal(op);
                using (RegistryKey k = RootOf(op.Hive).CreateSubKey(op.Path))
                {
                    if (kind == "dword") k.SetValue(op.Name, Convert.ToInt32(val), RegistryValueKind.DWord);
                    else k.SetValue(op.Name, val, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex) { Log.Warn("tweak", "恢复失败 " + op.Path + "\\" + op.Name + "：" + ex.Message); return false; }
        }

        private static bool WriteOp(RegOp op)
        {
            try
            {
                if (op.Kind == "delete") return DeleteVal(op);
                using (RegistryKey k = RootOf(op.Hive).CreateSubKey(op.Path))
                {
                    if (op.Kind == "dword") k.SetValue(op.Name, op.Dw, RegistryValueKind.DWord);
                    else k.SetValue(op.Name, op.Sz, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("tweak", "写入失败 " + op.Hive + "\\" + op.Path + "\\" + op.Name + "：" + ex.Message);
                return false;
            }
        }

        private static bool DeleteVal(RegOp op)
        {
            try
            {
                using (RegistryKey k = RootOf(op.Hive).OpenSubKey(op.Path, true))
                {
                    if (k == null) return true;
                    try { k.DeleteValue(op.Name, false); } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        private static void Emit(Action<string> cb, string status, Tweak t, string msg)
        {
            if (cb == null) return;
            cb(new JB().Add("type", "tweakItem")
                .Add("id", t.Id).Add("name", t.Name).Add("status", status).Add("msg", msg).End());
        }

        private static string StrArray(List<string> l)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json.Quote(l[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
