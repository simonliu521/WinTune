using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WinTune
{
    public class RepairAction
    {
        public string Id = "";
        public string Name = "";
        public string Desc = "";
        public string Risk = "safe";      // safe / adv / care
        public bool NeedAdmin = false;
        public bool NeedRestart = false;
        public string Minutes = "1";
        public string Exe = "";
        public string Args = "";
        public string Kind = "cmd";       // cmd / custom
        public string BaseId = "";        // 子操作所属的项目 id（体检与记录按项目走）
        public string Label = "";         // 按钮文字：扫描 / 修复 / 重置 / 重启 …
        public string OpDesc = "";        // 这个按钮具体做什么
        public List<RepairAction> Ops = new List<RepairAction>();
    }

    /// <summary>单项体检结论：ok 正常 / bad 异常可修复 / pending 待重启 / fixed 已修复 / unknown 未检测。</summary>
    public class ProbeResult
    {
        public string State = "unknown";
        public string Text = "";
    }

    /// <summary>
    /// 维护与修复。只调用系统自带官方工具（sfc / DISM / netsh / chkdsk / wsreset），不删用户数据。
    /// 每项都有一次轻量体检：执行过的动作会记到 repair-state.log，靠“执行时间 vs 开机时间”
    /// 分清“已执行等重启”“重启后已生效”“执行过但仍有问题”，不再一律显示同一个风险徽章。
    /// </summary>
    public static class Repair
    {
        private static volatile bool cancelReq;

        /// <summary>界面点“强制结束”时调用：把当前正在跑的系统工具连同子进程收掉。</summary>
        public static void Cancel()
        {
            /* 只置标志，真正的收尾交给正在跑命令的后台线程：
               它会先 taskkill /T 收掉整棵进程树，再等 3 秒。
               这样点“结束”不会把界面线程卡在 taskkill 上。 */
            cancelReq = true;
        }

        private static void KillTree(Process p, bool wait)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    "/PID " + p.Id + " /T /F");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                Process k = Process.Start(si);
                if (k == null) return;
                if (wait) { try { k.WaitForExit(3000); } catch { } }
                try { k.Dispose(); } catch { }
            }
            catch { }
        }

        public static List<RepairAction> Catalog()
        {
            List<RepairAction> l = new List<RepairAction>();

            RepairAction sfc = Base("sfc", "系统文件检查（SFC）", "扫描 Windows 系统文件完整性；扫描可以只看不修，修复会就地换回微软的原版文件。", "safe");
            AddOp(sfc, "sfc_scan", "扫描", "只扫描系统文件完整性，不改任何文件，先看清有没有损坏。", "15-40", true, false, "cmd", "sfc.exe", "/verifyonly");
            AddOp(sfc, "sfc_fix", "修复", "扫描并修复受损的系统文件（sfc /scannow）。", "20-40", true, true, "cmd", "sfc.exe", "/scannow");
            l.Add(sfc);

            RepairAction dism = Base("dism", "组件存储（DISM）", "检查 WinSxS 组件存储的健康状况；缺件时从微软源拉取正确副本修回来。", "safe");
            AddOp(dism, "dism_scan", "扫描", "深度扫描组件存储，找出损坏与缺失的组件（ScanHealth）。", "5-15", true, false, "cmd", "dism.exe", "/Online /Cleanup-Image /ScanHealth");
            AddOp(dism, "dism_fix", "修复", "从微软服务器拉取正确组件副本修回 WinSxS（RestoreHealth）。", "10-30", true, false, "cmd", "dism.exe", "/Online /Cleanup-Image /RestoreHealth");
            l.Add(dism);

            RepairAction chk = Base("chkdsk", "磁盘错误扫描", "只读扫描 C 盘文件系统错误，不修、不锁盘，可以随时中断。", "safe");
            AddOp(chk, "chkdsk_scan", "扫描", "只读扫描 C 盘文件系统（chkdsk C: /scan）。", "5-15", true, false, "cmd", "chkdsk.exe", "C: /scan");
            l.Add(chk);

            RepairAction winsock = Base("winsock", "Winsock 网络栈", "协议目录损坏时，部分软件会连不上网；重置后由系统重建。", "adv");
            AddOp(winsock, "winsock_reset", "重置", "把 Winsock 协议目录恢复到系统默认（netsh winsock reset），重启后重建。", "1", true, true, "cmd", "netsh.exe", "winsock reset");
            l.Add(winsock);

            RepairAction tcpip = Base("tcpip", "TCP/IP 协议栈", "网络层参数异常时用来恢复默认；重置后需要重启。", "adv");
            AddOp(tcpip, "tcpip_reset", "重置", "重置 TCP/IP 协议栈到默认（netsh int ip reset）。", "1", true, true, "cmd", "netsh.exe", "int ip reset");
            l.Add(tcpip);

            RepairAction dns = Base("dns", "DNS 缓存", "网页打不开、域名解析到旧 IP 时清一次缓存。", "safe");
            AddOp(dns, "dns_flush", "刷新", "清空本机 DNS 缓存（ipconfig /flushdns），瞬时完成。", "1", false, false, "cmd", "ipconfig.exe", "/flushdns");
            l.Add(dns);

            RepairAction icon = Base("iconcache", "图标缓存", "图标变白纸、显示错乱时重建缓存；会重启资源管理器（桌面闪一下）。", "safe");
            AddOp(icon, "iconcache_rebuild", "重置", "重建图标缓存，并重启资源管理器让新缓存生效。", "1", false, false, "custom", "", "");
            l.Add(icon);

            RepairAction exp = Base("explorer", "资源管理器", "任务栏卡死、桌面不刷新时重启它；桌面会闪一下，不影响其它程序。", "safe");
            AddOp(exp, "explorer_restart", "重启", "重启 explorer.exe（任务栏与桌面），打开的窗口不受影响。", "1", false, false, "custom", "", "");
            l.Add(exp);

            RepairAction print = Base("printqueue", "打印服务", "打印卡在“正在打印”、队列堵住时，清队列或重启打印服务。", "safe");
            AddOp(print, "printqueue_clear", "清空队列", "停掉打印服务、清空卡住的打印任务，再把服务拉起来。", "1", true, false, "custom", "", "");
            AddOp(print, "printqueue_restart", "重启", "只重启 Print Spooler 打印服务，不动队列里的文件。", "1", true, false, "custom", "", "");
            l.Add(print);

            RepairAction upd = Base("updatereset", "Windows 更新组件", "更新一直失败、卡在下载 0% 时用；会重命名 SoftwareDistribution 与 catroot2。", "adv");
            AddOp(upd, "updatereset_reset", "重置", "重置更新缓存与组件目录，下次更新重新下载。", "3-5", true, true, "custom", "", "");
            l.Add(upd);

            RepairAction store = Base("wsreset", "微软商店缓存", "商店打不开、下载卡住时清缓存；会顺带打开一次商店窗口。", "safe");
            AddOp(store, "wsreset_reset", "重置", "清空商店缓存（wsreset.exe），完成后会自动打开商店。", "1", false, false, "cmd", "wsreset.exe", "");
            l.Add(store);

            return l;
        }

        private static RepairAction Base(string id, string name, string desc, string risk)
        {
            RepairAction a = new RepairAction();
            a.Id = id; a.Name = name; a.Desc = desc; a.Risk = risk;
            a.NeedAdmin = true; a.NeedRestart = false; a.Minutes = "1";
            a.Kind = "custom";
            return a;
        }

        private static RepairAction AddOp(RepairAction baseItem, string id, string label, string desc, string min,
            bool admin, bool restart, string kind, string exe, string args)
        {
            RepairAction op = new RepairAction();
            op.Id = id; op.BaseId = baseItem.Id; op.Label = label; op.OpDesc = desc;
            op.Name = baseItem.Name; op.Desc = desc; op.Risk = baseItem.Risk;
            op.NeedAdmin = admin; op.NeedRestart = restart; op.Minutes = min;
            op.Kind = kind; op.Exe = exe; op.Args = args;
            baseItem.Ops.Add(op);
            if (admin) baseItem.NeedAdmin = true;
            if (restart) baseItem.NeedRestart = true;
            return op;
        }

        private static RepairAction A(string id, string name, string desc, string risk, bool admin, bool restart, string min, string exe, string args)
        {
            RepairAction a = new RepairAction();
            a.Id = id; a.Name = name; a.Desc = desc; a.Risk = risk;
            a.NeedAdmin = admin; a.NeedRestart = restart; a.Minutes = min;
            a.Exe = exe; a.Args = args; a.Kind = "cmd";
            return a;
        }

        private static RepairAction C(string id, string name, string desc, string risk, bool admin, bool restart, string min)
        {
            RepairAction a = new RepairAction();
            a.Id = id; a.Name = name; a.Desc = desc; a.Risk = risk;
            a.NeedAdmin = admin; a.NeedRestart = restart; a.Minutes = min;
            a.Kind = "custom";
            return a;
        }

        public static string ListJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":\"repairList\",\"admin\":").Append(SysInfo.IsAdmin() ? "true" : "false");
            sb.Append(",\"items\":[");
            List<RepairAction> l = Catalog();
            for (int i = 0; i < l.Count; i++)
            {
                RepairAction a = l[i];
                ProbeResult pr = Probe(a.Id);
                if (i > 0) sb.Append(',');
                sb.Append(new JB()
                    .Add("id", a.Id).Add("name", a.Name).Add("desc", a.Desc)
                    .Add("risk", a.Risk).Add("admin", a.NeedAdmin)
                    .Add("restart", a.NeedRestart).Add("min", a.Minutes)
                    .Add("state", pr.State).Add("stateText", pr.Text).End());
                sb.Length -= 1;                       // 去掉刚写上的结尾 }，接 ops
                sb.Append(",\"ops\":[");
                for (int k = 0; k < a.Ops.Count; k++)
                {
                    RepairAction op = a.Ops[k];
                    if (k > 0) sb.Append(',');
                    sb.Append(new JB().Add("id", op.Id).Add("label", op.Label).Add("desc", op.OpDesc)
                        .Add("admin", op.NeedAdmin).Add("restart", op.NeedRestart).Add("min", op.Minutes).End());
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>最新一条状态，供单点复查用。</summary>
        public static string StateJson(string id)
        {
            ProbeResult pr = Probe(id);
            return new JB().Add("type", "repairState").Add("id", id)
                .Add("state", pr.State).Add("stateText", pr.Text).End();
        }

        private static RepairAction Find(string id)
        {
            foreach (RepairAction a in Catalog())
            {
                foreach (RepairAction op in a.Ops) if (op.Id == id) return op;
                if (a.Id == id) return a;          // 兼容只传项目 id 的老调用
            }
            return null;
        }

        /// <summary>执行一个动作。阻塞调用，调用方放后台线程。</summary>
        public static void Run(string id, Action<string> onEvent)
        {
            cancelReq = false;
            RepairAction a = Find(id);
            if (a == null)
            {
                Emit(onEvent, new JB().Add("type", "repairDone").Add("id", id).Add("ok", false).Add("msg", "未知动作").End());
                return;
            }
            if (a.NeedAdmin && !SysInfo.IsAdmin())
            {
                Emit(onEvent, new JB().Add("type", "repairDone").Add("id", id).Add("ok", false)
                    .Add("msg", "这个动作需要管理员权限，请以管理员身份重新打开本软件").End());
                return;
            }

            DateTime t0 = DateTime.Now;
            string baseId = a.BaseId.Length > 0 ? a.BaseId : a.Id;
            string runName = a.Label.Length > 0 ? (a.Name + " · " + a.Label) : a.Name;
            Emit(onEvent, new JB().Add("type", "repairStart").Add("id", id).Add("name", runName).End());
            Log.Info("repair", "开始 " + a.Id + "：" + a.Exe + " " + a.Args);

            int code = 0;
            try
            {
                if (a.Kind == "cmd") code = RunCmd(a.Exe, a.Args, onEvent);
                else code = RunCustom(a.Id, onEvent);
            }
            catch (Exception ex)
            {
                Log.Error("repair", a.Id + " 异常：" + ex.Message);
                Record(baseId, false, a.NeedRestart);
                Emit(onEvent, new JB().Add("type", "repairDone").Add("id", id).Add("ok", false)
                    .Add("msg", "执行异常：" + ex.Message)
                    .Add("ms", (long)(DateTime.Now - t0).TotalMilliseconds)
                    .Add("state", "bad").Add("stateText", "执行时出错，再点一次重试").End());
                return;
            }

            long ms = (long)(DateTime.Now - t0).TotalMilliseconds;
            bool ok = code == 0;
            bool cancelled = cancelReq;
            if (!cancelled) Record(baseId, ok, a.NeedRestart);   // 用户主动结束不算“修过但失败”
            ProbeResult pr = Probe(baseId);
            Log.Info("repair", "结束 " + a.Id + " 退出码=" + code + " 用时=" + ms + "ms 状态=" + pr.State);
            string msg = cancelled
                ? "已强制结束卡住的工具。已经写入的改动会保留；需要的话可以再运行一次。"
                : (ok ? "完成" : ("退出代码 " + code + "，请把上方输出发给懂行的人看"));
            Emit(onEvent, new JB().Add("type", "repairDone").Add("id", id)
                .Add("ok", ok && !cancelled)
                .Add("msg", msg)
                .Add("ms", ms).Add("restart", a.NeedRestart)
                .Add("state", pr.State).Add("stateText", pr.Text).End());
        }

        /* ================= 体检 ================= */

        public static ProbeResult Probe(string id)
        {
            ProbeResult r = new ProbeResult();
            try { ProbeBase(id, r); }
            catch (Exception ex) { r.State = "unknown"; r.Text = "体检没跑起来：" + ex.Message; }

            Rec rec = LastRec(id);
            if (rec != null)
            {
                bool afterReboot = !(rec.NeedRestart && rec.When > BootTime());
                if (r.State == "bad")
                {
                    if (!afterReboot) { r.State = "pending"; r.Text = "已执行过，还差一次重启；重启后如果还报异常可以再修一次"; }
                    else r.Text = r.Text + "（" + Stamp(rec.When) + " 修过一次没解决，可以再修）";
                }
                else if (!afterReboot)
                {
                    r.State = "pending";
                    r.Text = "已执行，等重启生效（" + Stamp(rec.When) + "）";
                }
                else if (r.State == "ok" && !rec.Ok)
                {
                    /* 执行时退出码非 0，但现在看起来正常：如实说清楚 */
                    r.State = "fixed";
                    r.Text = r.Text + "；上次执行报过错（" + Stamp(rec.When) + "），现已恢复正常";
                }
                else
                {
                    r.State = "fixed";
                    r.Text = "已修复（" + Stamp(rec.When) + "）" + (r.Text.Length > 0 ? " · " + r.Text : "");
                }
            }
            if (r.Text.Length == 0) r.Text = "还没有检查记录，点右边的按钮开始";
            return r;
        }

        private static void ProbeBase(string id, ProbeResult r)
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

            if (id == "winsock")
            {
                int n = RegSubCount(@"SYSTEM\CurrentControlSet\Services\Winsock2\Parameters\Protocol_Catalog9\Catalog_Entries");
                n += RegSubCount(@"SYSTEM\CurrentControlSet\Services\Winsock2\Parameters\NameSpace_Catalog5\Catalog_Entries");
                if (n >= 6) { r.State = "ok"; r.Text = "协议目录完整，共 " + n + " 个条目"; }
                else { r.State = "bad"; r.Text = "协议目录只剩 " + n + " 个条目，被清空或损坏了"; }
                return;
            }
            if (id == "tcpip")
            {
                string basePath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                string[] ifs = RegSubNames(basePath + @"\Interfaces");
                bool hasIp = false;
                for (int i = 0; i < ifs.Length; i++)
                {
                    string p = basePath + @"\Interfaces\" + ifs[i];
                    if (RegHasValue(p, "DhcpIPAddress") || RegHasValue(p, "IPAddress")) { hasIp = true; break; }
                }
                if (ifs.Length == 0) { r.State = "bad"; r.Text = "网络接口参数整块缺失"; }
                else if (!hasIp) { r.State = "bad"; r.Text = "有 " + ifs.Length + " 个接口，但没有一个存着 IP 配置"; }
                else { r.State = "ok"; r.Text = ifs.Length + " 个接口参数完整"; }
                return;
            }
            if (id == "updatereset")
            {
                int start = RegInt(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", -1);
                int olds = DirCount(windir, "SoftwareDistribution.old-*");
                int cato = DirCount(Path.Combine(windir, "System32"), "catroot2.old-*");
                bool dirOk = Directory.Exists(Path.Combine(windir, "SoftwareDistribution"));
                if (start == 4) { r.State = "bad"; r.Text = "Windows 更新服务被禁用（启动类型 4），更新会一直失败"; }
                else if (!dirOk) { r.State = "bad"; r.Text = "SoftwareDistribution 目录不在原位，更新缓存是坏的"; }
                else if (olds > 0 || cato > 0) { r.State = "ok"; r.Text = "更新组件正常；重置留下的旧目录还有 " + (olds + cato) + " 个（在系统盘的 Windows 目录下，可手动删）"; }
                else { r.State = "ok"; r.Text = "更新组件目录正常"; }
                return;
            }
            if (id == "dns")
            {
                int start = RegInt(@"SYSTEM\CurrentControlSet\Services\Dnscache", "Start", -1);
                if (start == 4) { r.State = "bad"; r.Text = "DNS 客户端服务被禁用（启动类型 4）"; }
                else { r.State = "ok"; r.Text = "DNS 客户端服务正常"; }
                return;
            }
            if (id == "explorer")
            {
                if (ProcRunning("explorer.exe")) { r.State = "ok"; r.Text = "资源管理器在运行"; }
                else { r.State = "bad"; r.Text = "资源管理器没在跑，桌面和任务栏会缺失"; }
                return;
            }
            if (id == "printqueue")
            {
                string pq = Path.Combine(sys32, @"spool\PRINTERS");
                int files = 0;
                try { if (Directory.Exists(pq)) files = Directory.GetFiles(pq).Length; } catch { }
                if (files > 0) { r.State = "bad"; r.Text = "队列里还压着 " + files + " 个任务，打印机会一直卡"; }
                else { r.State = "ok"; r.Text = "打印队列是空的"; }
                return;
            }
            if (id == "iconcache")
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
                int n = 0;
                try
                {
                    if (Directory.Exists(dir)) n = Directory.GetFiles(dir, "iconcache_*.db").Length + Directory.GetFiles(dir, "thumbcache_*.db").Length;
                }
                catch { }
                if (n > 0) { r.State = "ok"; r.Text = "图标缓存有 " + n + " 个文件，正常"; }
                else { r.State = "bad"; r.Text = "图标缓存文件不见了，图标会变成白纸"; }
                return;
            }
            if (id == "wsreset")
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe");
                if (Directory.Exists(p)) { r.State = "ok"; r.Text = "商店数据目录在，能正常重置"; }
                else { r.State = "unknown"; r.Text = "这台机器没装微软商店，这项用不上"; }
                return;
            }
            if (id == "dism" || id == "dism_check" || id == "dism_restore")
            {
                string log = Path.Combine(windir, @"Logs\DISM\dism.log");
                string tail = TailText(log, 96 * 1024);
                if (tail.Length == 0) { r.State = "unknown"; r.Text = "还没跑过 DISM，没有日志可看"; return; }
                if (tail.IndexOf("0x800f", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tail.IndexOf("error:", StringComparison.OrdinalIgnoreCase) >= 0)
                { r.State = "bad"; r.Text = "上次 DISM 日志里有报错，组件存储可能还有问题"; }
                else if (tail.IndexOf("The operation completed successfully", StringComparison.OrdinalIgnoreCase) >= 0)
                { r.State = "ok"; r.Text = "上次 DISM 正常结束"; }
                else { r.State = "unknown"; r.Text = "日志里没看到明确结论，建议再跑一次"; }
                return;
            }
            /* sfc / chkdsk：系统只在事件日志里给结论，读取成本高，这里只认执行记录 */
            r.State = "unknown";
            r.Text = id == "sfc" ? "还没做过系统文件检查" : "还没扫描过磁盘";
        }

        /* ================= 执行记录 ================= */

        private class Rec
        {
            public string Id = "";
            public DateTime When = DateTime.MinValue;
            public bool NeedRestart = false;
            public bool Ok = true;
        }

        private static string StateFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "repair-state.log");
        }

        private static void Record(string id, bool ok, bool needRestart)
        {
            try
            {
                string line = id + "|" + DateTime.Now.Ticks + "|" + (needRestart ? "1" : "0") + "|" + (ok ? "1" : "0") + "\r\n";
                File.AppendAllText(StateFile(), line, Encoding.UTF8);
            }
            catch { }
        }

        private static Rec LastRec(string id)
        {
            try
            {
                string p = StateFile();
                if (!File.Exists(p)) return null;
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s[0] == '#') continue;
                    string[] f = s.Split('|');
                    if (f.Length < 4) continue;
                    if (f[0] != id) continue;
                    Rec r = new Rec();
                    r.Id = f[0];
                    try { r.When = new DateTime(long.Parse(f[1])); } catch { r.When = DateTime.MinValue; }
                    r.NeedRestart = f[2] == "1";
                    r.Ok = f[3] == "1";
                    return r;
                }
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private static DateTime BootTime()
        {
            try { return DateTime.Now.AddMilliseconds(-(double)GetTickCount64()); }
            catch { return DateTime.Now.AddHours(-24); }
        }

        private static string Stamp(DateTime t)
        {
            return t.ToString("MM-dd HH:mm");
        }

        /* ================= 小工具 ================= */

        private static int RegSubCount(string path)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return 0;
                    return k.GetSubKeyNames().Length;
                }
            }
            catch { return 0; }
        }

        private static string[] RegSubNames(string path)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return new string[0];
                    return k.GetSubKeyNames();
                }
            }
            catch { return new string[0]; }
        }

        private static bool RegHasValue(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return false;
                    object v = k.GetValue(name);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        private static int RegInt(string path, string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    if (v == null) return def;
                    return Convert.ToInt32(v);
                }
            }
            catch { return def; }
        }

        private static int DirCount(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                return Directory.GetDirectories(dir, pattern).Length;
            }
            catch { return 0; }
        }

        private static bool ProcRunning(string exe)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(exe.Replace(".exe", ""));
                return ps.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>读文件尾部一段文本；只用于看日志结论，不整读大文件。</summary>
        private static string TailText(string path, int bytes)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long start = len > bytes ? len - bytes : 0;
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[(int)(len - start)];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    Encoding enc = Encoding.UTF8;
                    try { enc = Encoding.GetEncoding(936); } catch { }
                    return enc.GetString(buf, 0, read);
                }
            }
            catch { return ""; }
        }

        /* ================= 命令行执行 ================= */

        private static int RunCmd(string exe, string args, Action<string> onEvent)
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, args);
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            try { si.StandardOutputEncoding = Encoding.GetEncoding(936); si.StandardErrorEncoding = Encoding.GetEncoding(936); }
            catch { }
            Emit(onEvent, Line("已启动：" + Path.GetFileName(exe) + (args.Length > 0 ? " " + args : "") +
                               "。这类系统工具常常先静默跑一段时间才吐第一行输出，窗口里的进度条会一直在动。"));
            using (Process p = Process.Start(si))
            {
                int[] lastOut = new int[] { Environment.TickCount };
                Action<string> emit = delegate (string s)
                {
                    lastOut[0] = Environment.TickCount;
                    onEvent(s);
                };

                /* 两条输出都放后台泵：以前 stdout 在前台 ReadToEnd，
                   DISM/SFC 一旦不出字，主循环根本走不到超时判断，界面就真卡死。 */
                Thread to = new Thread(delegate ()
                {
                    try { Pump(p.StandardOutput, emit); } catch { }
                });
                to.IsBackground = true;
                to.Start();
                Thread te = new Thread(delegate ()
                {
                    try { Pump(p.StandardError, emit); } catch { }
                });
                te.IsBackground = true;
                te.Start();

                int t0 = Environment.TickCount;
                int code = -1;
                bool killed = false;
                while (true)
                {
                    int now = Environment.TickCount;
                    bool noOut = (now - lastOut[0]) > 12 * 60 * 1000;       // 12 分钟一个字都没有，按卡死处理
                    bool tooLong = (now - t0) > 60 * 60 * 1000;             // 总时长硬上限 60 分钟
                    /* 先看取消：用户点“强制结束”时 Cancel 已经杀了进程，
                       如果先 WaitForExit 就会把这次收尾当成“正常退出码 1”。 */
                    if (cancelReq || noOut || tooLong)
                    {
                        killed = true;
                        KillTree(p, true);
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        try { p.WaitForExit(3000); } catch { }
                        Emit(onEvent, Line(cancelReq
                            ? "已收到“强制结束”，正在收掉工具进程…"
                            : (noOut ? "工具超过 12 分钟没有任何输出，判定为卡死，已结束进程。" : "工具超过 60 分钟仍未结束，已结束进程。")));
                        code = cancelReq ? 124 : 125;
                        break;
                    }
                    if (p.WaitForExit(500))
                    {
                        code = p.ExitCode;
                        break;
                    }
                }
                try { to.Join(1500); } catch { }
                try { te.Join(1500); } catch { }
                Emit(onEvent, Line(killed ? ("已结束，退出代码 " + code + "。") : ("命令结束，退出码 " + code + "。")));
                return code;
            }
        }

        /// <summary>
        /// 读工具输出。两种结束符含义不同：
        ///   \r\n 或 \n —— 真换行，输出一行；
        ///   单个 \r    —— 回到行首重画（SFC / DISM 的进度条就这样刷），
        ///               输出时打成“替换上一行”，界面原地更新，不会把几十帧进度堆成一屏。
        /// 过快的刷新（旋转符、百分比动画）限流 150ms；控制符一律丢掉。
        /// </summary>
        private static void Pump(StreamReader r, Action<string> onEvent)
        {
            char[] buf = new char[8192];
            StringBuilder sb = new StringBuilder(256);
            long last = 0;
            string lastShown = "";
            bool repainting = false;    /* 这一行已经作为一行发过，后续 \r 重画就改成“替换上一行” */
            bool pendingCR = false;     /* 刚读到 \r，等下一个字符判断它是换行还是重画 */

            /* 一行结束（\n 或 \r\n）：repainting 为真说明它是重画行的最终版，用替换，避免多出一行 */
            Action endLine = delegate ()
            {
                if (sb.Length > 0)
                {
                    string s = sb.ToString().TrimEnd();
                    sb.Length = 0;
                    if (s.Length > 0 && !string.Equals(s, lastShown, StringComparison.Ordinal))
                        onEvent(Line(s, repainting));
                    if (s.Length > 0) lastShown = s;
                }
                repainting = false;
            };

            /* \r 重画：第一次作为新行追加，之后就地替换；150ms 限流，进度条不会刷屏 */
            Action repaint = delegate ()
            {
                if (sb.Length == 0) return;
                string s = sb.ToString().TrimEnd();
                sb.Length = 0;
                if (s.Length == 0) return;
                if (!repainting)
                {
                    onEvent(Line(s, false));
                    lastShown = s;
                    repainting = true;
                    last = (long)Environment.TickCount;
                    return;
                }
                long now = (long)Environment.TickCount;
                if (last == 0 || now - last >= 150)
                {
                    last = now;
                    if (!string.Equals(s, lastShown, StringComparison.Ordinal)) onEvent(Line(s, true));
                    lastShown = s;
                }
            };

            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (pendingCR)
                    {
                        pendingCR = false;
                        if (c == '\n') { endLine(); continue; }      /* \r\n：普通换行 */
                        repaint();                                   /* \r 后面不是换行：原地重画 */
                    }
                    if (c == '\r') { pendingCR = true; continue; }
                    if (c == '\n') { endLine(); continue; }
                    if (c < (char)0x20 && c != '\t') continue;       /* 退格、响铃之类的控制符一律丢掉 */
                    sb.Append(c);
                    if (sb.Length > 8192)
                    {
                        onEvent(Line(sb.ToString().TrimEnd(), false));
                        sb.Length = 0;
                    }
                }
            }
            /* 收尾：最后一段没有换行符的内容也要吐出来 */
            if (pendingCR) repaint();
            else endLine();
        }

        private static string Line(string s, bool rep)
        {
            return new JB().Add("type", "repairOut")
                .Add("line", s == null ? "" : s.TrimEnd())
                .Add("rep", rep)
                .End();
        }

        private static string Line(string s) { return Line(s, false); }

        private static void Emit(Action<string> cb, string json)
        {
            if (cb != null) cb(json);
        }

        /* ================= 自定义动作 ================= */

        private static int RunCustom(string id, Action<string> onEvent)
        {
            if (id == "iconcache" || id == "iconcache_rebuild") return DoIconCache(onEvent);
            if (id == "explorer" || id == "explorer_restart") return DoExplorer(onEvent);
            if (id == "printqueue" || id == "printqueue_clear") return DoPrintQueue(onEvent);
            if (id == "printqueue_restart") return DoPrintRestart(onEvent);
            if (id == "updatereset" || id == "updatereset_reset") return DoUpdateReset(onEvent);
            return 1;
        }

        private static int DoIconCache(Action<string> onEvent)
        {
            Emit(onEvent, Line("停止资源管理器…"));
            RunQuiet("taskkill.exe", "/f /im explorer.exe");
            Thread.Sleep(800);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] files = new string[]
            {
                Path.Combine(local, @"IconCache.db"),
                Path.Combine(local, @"Microsoft\Windows\Explorer\iconcache_16.db"),
            };
            string dir = Path.Combine(local, @"Microsoft\Windows\Explorer");
            int deleted = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try { if (File.Exists(files[i])) { File.Delete(files[i]); deleted++; } } catch { }
            }
            try
            {
                foreach (string f in Directory.GetFiles(dir, "iconcache_*.db"))
                {
                    try { File.Delete(f); deleted++; } catch { }
                }
                foreach (string f in Directory.GetFiles(dir, "thumbcache_*.db"))
                {
                    try { File.Delete(f); deleted++; } catch { }
                }
            }
            catch (Exception ex) { Emit(onEvent, Line("读取缓存目录失败：" + ex.Message)); }
            Emit(onEvent, Line("已删除 " + deleted + " 个缓存文件，重新启动资源管理器…"));

            try { Process.Start("explorer.exe"); } catch { }
            Thread.Sleep(600);
            Emit(onEvent, Line("完成。桌面图标会重新生成，图标可能短暂空白。"));
            return 0;
        }

        private static int DoExplorer(Action<string> onEvent)
        {
            Emit(onEvent, Line("结束 explorer.exe 进程…"));
            RunQuiet("taskkill.exe", "/f /im explorer.exe");
            Thread.Sleep(1200);
            try
            {
                Process.Start("explorer.exe");
                Emit(onEvent, Line("已重新启动资源管理器。"));
                return 0;
            }
            catch (Exception ex)
            {
                Emit(onEvent, Line("重启失败：" + ex.Message));
                return 1;
            }
        }

        private static int DoPrintQueue(Action<string> onEvent)
        {
            Emit(onEvent, Line("停止打印服务…"));
            RunQuiet("net.exe", "stop spooler");
            Thread.Sleep(600);
            string pq = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\PRINTERS");
            int n = 0;
            try
            {
                if (Directory.Exists(pq))
                {
                    foreach (string f in Directory.GetFiles(pq))
                    {
                        try { File.Delete(f); n++; } catch { }
                    }
                }
            }
            catch (Exception ex) { Emit(onEvent, Line("清队列失败：" + ex.Message)); }
            Emit(onEvent, Line("已删除 " + n + " 个队列文件。"));
            RunQuiet("net.exe", "start spooler");
            Emit(onEvent, Line("打印服务已重新启动。"));
            return 0;
        }

        private static int DoPrintRestart(Action<string> onEvent)
        {
            Emit(onEvent, Line("重启打印服务（不动队列里的文件）…"));
            RunQuiet("net.exe", "stop spooler");
            Thread.Sleep(700);
            RunQuiet("net.exe", "start spooler");
            Emit(onEvent, Line("打印服务已重启。"));
            return 0;
        }

        private static int DoUpdateReset(Action<string> onEvent)
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Emit(onEvent, Line("停止更新相关服务…"));
            RunQuiet("net.exe", "stop wuauserv");
            RunQuiet("net.exe", "stop bits");
            RunQuiet("net.exe", "stop cryptsvc");
            RunQuiet("net.exe", "stop msiserver");
            Thread.Sleep(900);

            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            RenameDir(Path.Combine(windir, "SoftwareDistribution"), Path.Combine(windir, "SoftwareDistribution.old-" + stamp), onEvent);
            RenameDir(Path.Combine(windir, @"System32\catroot2"), Path.Combine(windir, @"System32\catroot2.old-" + stamp), onEvent);

            Emit(onEvent, Line("重新启动服务…"));
            RunQuiet("net.exe", "start cryptsvc");
            RunQuiet("net.exe", "start bits");
            RunQuiet("net.exe", "start wuauserv");
            RunQuiet("net.exe", "start msiserver");
            Emit(onEvent, Line("完成。下次检查更新会重新建立缓存目录。"));
            return 0;
        }

        private static void RenameDir(string from, string to, Action<string> onEvent)
        {
            try
            {
                if (!Directory.Exists(from)) { Emit(onEvent, Line("不存在，跳过：" + from)); return; }
                Directory.Move(from, to);
                Emit(onEvent, Line("已重命名：" + Path.GetFileName(from) + " → " + Path.GetFileName(to)));
            }
            catch (Exception ex)
            {
                Emit(onEvent, Line("重命名失败（可能被占用）：" + ex.Message));
            }
        }

        private static void RunQuiet(string exe, string args)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    p.WaitForExit(15000);
                }
            }
            catch { }
        }
    }
}
