using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 一键清理的五个补充模块：注册表登记、启动项、计划任务、后台服务、受限策略。
    /// 只读扫描；改动前一律先备份，动作按 id 精确执行。
    /// </summary>
    public static class Maintenance
    {
        public class Item
        {
            public string Id = "";
            public string Mod = "";
            public string Name = "";
            public string Sub = "";
            public string Path = "";
            public long Bytes = 0;
            public string Status = "不建议修改";   // 建议删除 / 不可删除 / 建议修改 / 不建议修改
            public string Detail = "";
            public string Act = "";            // 逗号分隔的可用动作
            public string Cur = "";            // 当前值：启动类型 / 值数据
        }

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "maintenance");
            }
        }

        public static string BackupFile(string tag)
        {
            Directory.CreateDirectory(Dir);
            return Path.Combine(Dir, tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        }

        /* ==================== 扫描入口 ==================== */

        public static string ScanJson(string mod)
        {
            List<Item> items = new List<Item>();
            try
            {
                if (mod == "reg") ScanReg(items);
                else if (mod == "startup") ScanStartup(items);
                else if (mod == "task") ScanTasks(items);
                else if (mod == "service") ScanServices(items);
            }
            catch (Exception ex) { Log.Warn("maint", mod + " 扫描异常：" + ex.Message); }

            long bytes = 0;
            int clean = 0;
            foreach (Item it in items)
            {
                bytes += it.Bytes;
                if (it.Status == "建议删除") clean++;
            }

            StringBuilder arr = new StringBuilder();
            arr.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (i > 0) arr.Append(',');
                arr.Append(new JB()
                    .Add("id", it.Id)
                    .Add("mod", it.Mod)
                    .Add("name", it.Name)
                    .Add("sub", it.Sub)
                    .Add("path", it.Path)
                    .Add("bytes", it.Bytes)
                    .Add("status", it.Status)
                    .Add("detail", it.Detail)
                    .Add("act", it.Act)
                    .Add("cur", it.Cur)
                    .End());
            }
            arr.Append(']');
            return new JB()
                .Add("type", "maintList")
                .Add("mod", mod)
                .Raw("list", arr.ToString())
                .Add("total", items.Count)
                .Add("clean", clean)
                .Add("bytes", bytes)
                .End();
        }

        /* ==================== 注册表：卸载登记 / App Paths（全量列出） ==================== */

        private static void ScanReg(List<Item> items)
        {
            string[] uninstallRoots = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string sub in uninstallRoots)
                ScanUninstallKey(RegistryHive.LocalMachine, sub, items);
            ScanUninstallKey(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", items);
            ScanAppPaths(items);
        }

        private static void ScanUninstallKey(RegistryHive hive, string sub, List<Item> items)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string keyName in k.GetSubKeyNames())
                    {
                        if (items.Count > 600) return;
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(keyName))
                            {
                                if (sk == null) continue;
                                string name = Str(sk.GetValue("DisplayName"));
                                if (name.Length == 0) continue;
                                if (Str(sk.GetValue("ParentKeyName")).Length > 0) continue;   // 补丁
                                bool sysComp = Convert.ToInt32(sk.GetValue("SystemComponent", 0)) == 1;
                                if (sysComp) continue;      /* 微软自带组件不参与判断 */
                                /* 微软自家运行库 / 组件不列：它们的卸载程序常在 Package Cache 里，
                                   缓存被清过就会显得"失效"，但组件本身是装着的，删登记只有坏处 */
                                string pub = Str(sk.GetValue("Publisher"));
                                if (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                                    || pub.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                /* 同一软件在 64 位和 WOW6432Node 下常各有一条，只留先扫到的那条 */
                                bool dup = false;
                                for (int i = 0; i < items.Count; i++)
                                    if (items[i].Mod == "reg" && string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                                if (dup) continue;

                                string loc = Str(sk.GetValue("InstallLocation"));
                                string un = Str(sk.GetValue("UninstallString"));
                                string ver = Str(sk.GetValue("DisplayVersion"));
                                string exe = FirstExe(un);
                                bool hasLoc = loc.Length > 0;
                                bool locAlive = hasLoc && Directory.Exists(loc.Trim('"'));
                                /* 只有写全路径的卸载程序才判"文件不存在"；MsiExec.exe 这类系统命令不算 */
                                bool exeMissing = exe.Length > 0 && Path.IsPathRooted(exe) && !File.Exists(exe);
                                bool locMissing = hasLoc && !locAlive;

                                /* 安装目录还在 = 软件本体装着，这条登记对得上，不列 */
                                if (locAlive) continue;
                                /* 目录没写、卸载程序也在 = 登记完整，不列 */
                                if (!locMissing && !exeMissing) continue;

                                Item it = new Item();
                                it.Id = "u:" + HiveName(hive) + "|" + sub + "|" + keyName;
                                it.Mod = "reg";
                                it.Name = name;
                                /* 只留残留：登记还在、指向的东西已经不在了 */
                                it.Sub = locMissing ? "卸载登记 · 安装目录不存在" : "卸载登记 · 卸载程序不存在";
                                it.Path = locMissing ? loc.Trim('"') : exe;
                                it.Status = "建议删除";
                                it.Detail = "建议删除：这条卸载登记指向的位置已经不在了，留着只会在「应用和功能」里留一条点不动的条目";
                                it.Act = "del";
                                it.Cur = ver;
                                items.Add(it);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("maint", "读取卸载项失败：" + ex.Message); }
        }

        private static void ScanAppPaths(List<Item> items)
        {
            string[] subs = new string[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths" };
            foreach (string sub in subs)
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                    using (RegistryKey k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        foreach (string keyName in k.GetSubKeyNames())
                        {
                            if (items.Count > 600) return;
                            /* 32 位视图的 App Paths 常与 64 位登记同一程序，已经列过就跳过，避免同一软件出多条 */
                            bool dup = false;
                            for (int i = 0; i < items.Count; i++)
                                if (items[i].Mod == "reg" && string.Equals(items[i].Name, keyName, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                            if (dup) continue;
                            using (RegistryKey sk = k.OpenSubKey(keyName))
                            {
                                if (sk == null) continue;
                                string val = Str(sk.GetValue(""));
                                if (val.Length == 0) continue;
                                string exe = val.Trim('"');
                                bool missing = !File.Exists(exe);
                                if (!missing) continue;      /* 程序还在，这条登记就是有效的，不列 */
                                Item it = new Item();
                                it.Id = "u:HKLM|" + sub + "|" + keyName;
                                it.Mod = "reg";
                                it.Name = keyName;
                                it.Path = exe;
                                it.Sub = "App Paths · 路径不存在";
                                it.Status = "建议删除";
                                it.Detail = "建议删除：运行框里敲这个程序名会失败，这条登记是空的";
                                it.Act = "del";
                                items.Add(it);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        /* ==================== 启动项 ==================== */

        private static readonly string[] RunKeys = new string[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            /* 组策略写的启动项，公司机器和某些安装器也会往这里塞 */
            @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run"
        };

        private static void ScanStartup(List<Item> items)
        {
            ScanRunKey(RegistryHive.CurrentUser, RegistryView.Default, items);
            ScanRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, items);
            ScanRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, items);
            ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "当前用户启动文件夹", items);
            ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "公共启动文件夹", items);
            ScanUwpStartup(items);        /* 设置里「启动应用」那一批 UWP 应用 */
            ScanWinlogon(items);          /* 登录时的系统外壳程序 */
        }

        /// <summary>
        /// UWP 应用的启动任务（设置 → 应用 → 启动、任务管理器启动页里那一类）。
        /// 状态存在 HKCU 下，普通权限就能改，不用管理员。
        /// </summary>
        private static void ScanUwpStartup(List<Item> items)
        {
            string sub = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string pkg in k.GetSubKeyNames())
                    {
                        if (items.Count > 340) return;
                        try
                        {
                            using (RegistryKey pk = k.OpenSubKey(pkg))
                            {
                                if (pk == null) continue;
                                foreach (string taskId in pk.GetSubKeyNames())
                                {
                                    using (RegistryKey tk = pk.OpenSubKey(taskId))
                                    {
                                        if (tk == null) continue;
                                        object st = tk.GetValue("State");
                                        if (st == null) continue;
                                        int state = Convert.ToInt32(st);
                                        if (state != 1 && state != 2) continue;   /* 1 禁用 / 2 启用，其它不是启动任务 */
                                        bool off = state == 1;

                                        Item it = new Item();
                                        it.Id = "w:" + pkg + "|" + taskId;
                                        it.Mod = "startup";
                                        it.Name = pkg.IndexOf('_') > 0 ? pkg.Substring(0, pkg.IndexOf('_')) : pkg;
                                        it.Sub = "UWP 启动应用 · " + taskId + " · " + (off ? "已停用" : "启用中");
                                        it.Path = pkg;
                                        it.Status = "不建议修改";
                                        it.Detail = off
                                            ? "不建议修改：这个应用的开机启动已经关掉了，需要它开机就在就选「启用」"
                                            : "不建议修改：应用商店类程序的启动入口，属于「设置 → 启动应用」那一栏；不常驻就选「停用」，之后随时能开回来";
                                        it.Act = off ? "on" : "off";
                                        it.Cur = off ? "已停用" : "启用中";
                                        items.Add(it);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("maint", "读取 UWP 启动项失败：" + ex.Message); }
        }

        /// <summary>登录时先跑的系统程序（Winlogon 的 Userinit / Shell），被塞了额外命令的要能看出来。</summary>
        private static void ScanWinlogon(List<Item> items)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (RegistryKey k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                {
                    if (k == null) return;
                    string[][] defs = new string[][]
                    {
                        new string[] { "Userinit", @"C:\Windows\system32\userinit.exe," },
                        new string[] { "Shell", "explorer.exe" }
                    };
                    foreach (string[] d in defs)
                    {
                        string val = Str(k.GetValue(d[0])).Trim();
                        if (val.Length == 0) continue;
                        bool normal = val.Replace(" ", "").ToLowerInvariant() == d[1].Replace(" ", "").ToLowerInvariant();

                        Item it = new Item();
                        it.Id = "g:HKLM|Winlogon|" + d[0];
                        it.Mod = "startup";
                        it.Name = "登录程序 · " + d[0];
                        it.Sub = @"HKLM\...\Winlogon · 系统登录必需项" + (normal ? "" : " · 已被改写");
                        it.Path = val;
                        it.Status = "不建议修改";
                        it.Detail = normal
                            ? "不建议修改：系统原样，登录时按它拉起桌面，删改会导致登录异常"
                            : "不建议修改：这一项和系统默认值不一样，可能被安装器或别的软件改过；先看清内容再决定要不要恢复默认";
                        it.Act = "";      /* 只列出，不给动作，避免把登录搞坏 */
                        it.Cur = val;
                        items.Add(it);
                    }
                }
            }
            catch { }
        }

        private static void ScanRunKey(RegistryHive hive, RegistryView view, List<Item> items)
        {
            foreach (string sub in RunKeys)
            {
                string tag = (hive == RegistryHive.CurrentUser ? "HKCU" : (view == RegistryView.Registry32 ? "HKLM32" : "HKLM"));
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        foreach (string vn in k.GetValueNames())
                        {
                            if (items.Count > 320) return;
                            string cmd = Str(k.GetValue(vn));
                            if (cmd.Length == 0) continue;
                            string exe = FirstExe(cmd);
                            bool missing = exe.Length > 0 && !File.Exists(exe);
                            bool disabled = IsStartupDisabled(hive, view, sub, vn);

                            Item it = new Item();
                            it.Id = "s:" + tag + "|" + sub + "|" + vn;
                            it.Mod = "startup";
                            it.Name = vn.Length > 0 ? vn : "(默认)";
                            it.Sub = tag.Replace("HKLM32", "HKLM\\WOW6432Node") + " · " + (disabled ? "已禁用" : "启用中");
                            it.Path = cmd;
                            /* 系统启动内容也全列出来，只是标成不建议动；真要停用仍然给入口 */
                            bool sys = IsSystemStartup(vn, exe);
                            bool own = IsOwnProgram(exe);
                            if (own)
                            {
                                /* 本软件自己的启动项不在这里给“停用/删除”建议：
                                   开机自启请到 MiniMonitor 右键菜单或概览卡里开关。 */
                                it.Status = "不建议修改";
                                it.Detail = "不建议修改：这是本软件自己的开机启动项，保持原样；要开关请用 MiniMonitor 的「开机自启」";
                                it.Act = "";
                            }
                            else if (missing)
                            {
                                it.Status = "建议删除";
                                it.Detail = "建议删除：指向的程序文件不存在，开机时这条会静默失败";
                                it.Act = "del";
                            }
                            else if (disabled)
                            {
                                it.Status = "不建议修改";
                                it.Detail = "不建议修改：这一项已经是停用状态，不动就用「恢复」重新打开";
                                it.Act = "on|del";
                            }
                            else if (sys)
                            {
                                it.Status = "不建议修改";
                                it.Detail = "不建议修改：系统或安全组件的开机项，停掉会影响系统功能；确实要停就选「停用」，之后还能恢复";
                                it.Act = "off|del";
                            }
                            else
                            {
                                it.Status = "建议修改";
                                it.Detail = "建议修改：开机自启项，不需要它开机跑就停用，随时能在列表里恢复";
                                it.Act = "off|del";
                            }
                            it.Cur = exe.Length > 0 ? exe : cmd;
                            items.Add(it);
                        }
                    }
                }
                catch { }
            }
        }

        private static bool IsSystemStartup(string valueName, string exe)
        {
            string v = (valueName + " " + exe).ToLowerInvariant();
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (v.IndexOf("securityhealth") >= 0 || v.IndexOf("windowsdefender") >= 0) return true;
            /* 系统目录里、且不是第三方安装器塞进去的，算系统项 */
            return exe.Length > 0 && exe.ToLowerInvariant().StartsWith(sysRoot + "\\", StringComparison.Ordinal);
        }

        /// <summary>WinTune / MiniMonitor 自己的程序：维护页只展示，不建议停用、删除或改启动类型。</summary>
        private static bool IsOwnProgram(string exe)
        {
            try
            {
                string fn = Path.GetFileName(exe ?? "").ToLowerInvariant();
                if (fn == "wintune.exe" || fn == "minimonitor.exe") return true;
            }
            catch { }
            return false;
        }

        private static bool IsOwnTask(string taskName)
        {
            return string.Equals(taskName, "MiniMonitorWidget", StringComparison.OrdinalIgnoreCase)
                || taskName.IndexOf("WinTune", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsOwnService(string name, string exe)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.StartsWith("pawnio") || n.IndexOf("minimonitor") >= 0 || n.IndexOf("wintune") >= 0) return true;
            try
            {
                string fn = Path.GetFileName(exe ?? "").ToLowerInvariant();
                if (fn == "wintune.exe" || fn == "minimonitor.exe" || fn == "pawnio.sys") return true;
            }
            catch { }
            return false;
        }

        private static bool IsStartupDisabled(RegistryHive hive, RegistryView view, string runSub, string valueName)
        {
            if (!IsRunKeyPath(runSub)) return false;      /* Policies 这类键没有 StartupApproved 映射 */
            try
            {
                string approved = ApprovedPath(runSub);
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey k = root.OpenSubKey(approved))
                {
                    if (k == null) return false;
                    byte[] b = k.GetValue(valueName) as byte[];
                    return b != null && b.Length > 0 && (b[0] & 1) == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>只有 CurrentVersion\Run / RunOnce 有对应的 StartupApproved 键。</summary>
        private static bool IsRunKeyPath(string sub)
        {
            string low = sub.ToLowerInvariant();
            return low.EndsWith(@"\currentversion\run") || low.EndsWith(@"\currentversion\runonce");
        }

        private static string ApprovedPath(string sub)
        {
            string low = sub.ToLowerInvariant();
            if (low.EndsWith(@"\currentversion\runonce"))
                return sub.Substring(0, sub.Length - @"\runonce".Length) + @"\Explorer\StartupApproved\RunOnce";
            return sub.Substring(0, sub.Length - @"\run".Length) + @"\Explorer\StartupApproved\Run";
        }

        private static void ScanStartupFolder(string dir, string label, List<Item> items)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (items.Count > 220) return;
                    string leaf = Path.GetFileName(f);
                    if (leaf.StartsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = f;
                    string exe = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) : f;
                    if (exe.Length > 0) target = exe;
                    bool missing = exe.Length > 0 && !File.Exists(exe);

                    Item it = new Item();
                    it.Id = "f:" + f;
                    it.Mod = "startup";
                    it.Name = Path.GetFileNameWithoutExtension(f);
                    it.Sub = label;
                    it.Path = target;
                    it.Status = missing ? "建议删除" : "建议修改";
                    it.Detail = missing ? "建议删除：快捷方式指向的程序已经不在"
                                        : "建议修改：开机启动文件夹里的快捷方式，删掉就不再随开机运行";
                    it.Act = "del";
                    items.Add(it);
                }
            }
            catch { }
        }

        private static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "";
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                object target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                return target == null ? "" : target.ToString();
            }
            catch { return ""; }
        }

        /* ==================== 计划任务 ==================== */

        /// <summary>
        /// 直接问任务计划服务（schtasks /Query）：普通权限也能拿到全部任务，
        /// 包括 \Microsoft\ 下的系统任务。System32\Tasks 目录普通用户常常读不进去，所以不再依赖它。
        /// </summary>
        private static void ScanTasks(List<Item> items)
        {
            string st = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
            string text = RunText(st, "/Query /FO CSV /V", 120000);
            if (text.Length == 0) { Log.Warn("maint", "schtasks 没有返回内容，计划任务列表为空"); return; }
            List<string[]> rows = ParseCsv(text);
            int seen = 0;
            foreach (string[] r in rows)
            {
                if (r.Length < 12) continue;
                string full = r[1];
                if (full.Length == 0 || full == "TaskName" || full.IndexOf('\\') < 0) continue;
                if (++seen > 900) break;
                try
                {
                    string tn = full.TrimStart('\\');
                    string name = tn.Substring(tn.LastIndexOf('\\') + 1);
                    string where = tn.IndexOf('\\') >= 0 ? @"\" + tn.Substring(0, tn.LastIndexOf('\\') + 1) : @"\";
                    string author = Clean(r[7]);
                    string toRun = Clean(r[8]);
                    string desc = Clean(r[10]);
                    string state = r[11];
                    bool disabled = state.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0
                                 || state.IndexOf("已禁用", StringComparison.Ordinal) >= 0
                                 || r[3].IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0
                                 || r[3].IndexOf("已禁用", StringComparison.Ordinal) >= 0;
                    bool sys = tn.IndexOf(@"Microsoft\", StringComparison.OrdinalIgnoreCase) >= 0
                            || author.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool own = IsOwnTask(tn);
                    string exe = FirstExe(toRun);
                    bool missing = exe.Length > 0 && Path.IsPathRooted(exe) && !File.Exists(exe);

                    Item it = new Item();
                    it.Id = "t:" + tn;
                    it.Mod = "task";
                    it.Name = name;
                    it.Sub = where + " · " + (author.Length > 0 ? author : "未知来源")
                           + (desc.Length > 0 ? " · " + Cut(desc, 24) : "")
                           + (disabled ? " · 已禁用" : "");
                    it.Path = toRun.Length > 0 ? toRun : "（任务计划服务没有给出命令行）";
                    if (own)
                    {
                        it.Status = "不建议修改";
                        it.Detail = "不建议修改：这是 MiniMonitor 的温度代理任务，由 MiniMonitor 自己按需启动和结束；不要在这里禁用或删除";
                        it.Act = "";
                    }
                    else if (missing)
                    {
                        it.Status = "建议删除";
                        it.Detail = "建议删除：任务指向的程序已经不在了，每次触发都会失败";
                        it.Act = disabled ? "del|enable" : "del|disable";
                    }
                    else if (disabled)
                    {
                        it.Status = "不建议修改";
                        it.Detail = "不建议修改：这条任务现在处于禁用状态，不占运行资源；需要它工作就选「启用任务」";
                        it.Act = "enable|del";
                    }
                    else if (sys)
                    {
                        it.Status = "不建议修改";
                        it.Detail = "不建议修改：系统自带的任务，禁用会让对应功能（更新、维护、索引等）不按时跑；真要停就选「禁用任务」，比删掉好恢复";
                        it.Act = "disable|del";
                    }
                    else
                    {
                        it.Status = "建议修改";
                        it.Detail = "建议修改：第三方计划任务，不确定它做什么就先看下面的命令行，确认没用再禁用或删除";
                        it.Act = "disable|del";
                    }
                    items.Add(it);
                }
                catch { }
            }
        }

        /* ==================== 后台服务 ==================== */

        private static void ScanServices(List<Item> items)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                using (RegistryKey k = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (k == null) return;
                    foreach (string name in k.GetSubKeyNames())
                    {
                        if (items.Count > 220) continue;
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(name))
                            {
                                if (sk == null) continue;
                                object typeObj = sk.GetValue("Type");
                                if (typeObj == null) continue;
                                int type = Convert.ToInt32(typeObj);
                                if ((type & 0x30) == 0) continue;              // 驱动类不列
                                string disp = Str(sk.GetValue("DisplayName"));
                                if (disp.Length == 0 || disp.StartsWith("@", StringComparison.Ordinal)) disp = name;
                                string img = Str(sk.GetValue("ImagePath"));
                                string exe = FirstExe(img);
                                bool missing = exe.Length > 0 && !File.Exists(exe);
                                bool ms = IsMicrosoftService(exe, name);
                                bool own = IsOwnService(name, exe);
                                int start = Convert.ToInt32(sk.GetValue("Start", 3));
                                string startTxt = start == 2 ? "自动" : (start == 3 ? "手动" : (start == 4 ? "已禁用" : "系统"));
                                if (ms && !missing && !own) continue;           // 系统服务不列，避免长列表

                                Item it = new Item();
                                it.Id = "v:" + name;
                                it.Mod = "service";
                                it.Name = disp;
                                it.Sub = name + " · 启动类型：" + startTxt;
                                it.Path = exe.Length > 0 ? exe : img;
                                if (own)
                                {
                                    it.Status = "不建议修改";
                                    it.Detail = "不建议修改：这是 WinTune / MiniMonitor 自己的服务，保持原样；不要在这里停止、禁用或删除";
                                    it.Act = "";
                                }
                                else if (missing)
                                {
                                    it.Status = "建议删除";
                                    it.Detail = "建议删除：服务指向的程序文件不存在，会拖慢开机；先停止再删除";
                                    it.Act = "del|demand|auto|disabled|stop";
                                }
                                else if (start <= 1)
                                {
                                    it.Status = "不可删除";
                                    it.Detail = "不可删除：系统启动阶段就要加载的服务，删掉可能开不了机";
                                    it.Act = "stop|demand";
                                }
                                else if (start == 2)
                                {
                                    it.Status = "建议修改";
                                    it.Detail = "建议修改：第三方服务，开机就自己启动；不常用的改成「手动」，需要时系统会自己拉起";
                                    it.Act = "demand|auto|disabled|stop";
                                }
                                else
                                {
                                    it.Status = "不建议修改";
                                    it.Detail = "不建议修改：已经是「" + startTxt + "」启动，不会白占开机时间，不用再动";
                                    it.Act = "demand|auto|disabled|stop";
                                }
                                it.Cur = startTxt;
                                items.Add(it);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("maint", "读取服务失败：" + ex.Message); }
        }

        private static bool IsMicrosoftService(string exe, string name)
        {
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (exe.Length == 0) return true;
            string lower = exe.ToLowerInvariant();
            if (lower.StartsWith(sysRoot + "\\", StringComparison.Ordinal)
                && lower.IndexOf("\\program files", StringComparison.Ordinal) < 0) return true;
            /* Defender、更新组件这类虽然不在 Windows 目录下，也是系统自己的 */
            if (lower.IndexOf(@"\programdata\microsoft\windows", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf(@"\windowsapps\microsoft.", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf(@"\microsoft\edgeupdate\", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf(@"\windows defender", StringComparison.Ordinal) >= 0) return true;
            if (lower.IndexOf(@"\windowsapps\windowsdefender", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /* ==================== 执行 ==================== */

        public static string Apply(string mod, List<string> actions, Action<string> log)
        {
            int ok = 0, fail = 0;
            StringBuilder detail = new StringBuilder();
            List<string> lines = new List<string>();
            foreach (string a in actions)
            {
                string id = a;
                string act = "";
                int bar = a.LastIndexOf('|');
                if (bar > 0 && bar + 1 < a.Length)
                {
                    id = a.Substring(0, bar);
                    act = a.Substring(bar + 1);
                }
                try
                {
                    string msg = Do(mod, id, act, lines);
                    if (msg.Length == 0) { fail++; detail.Append("× ").Append(id).Append('\n'); }
                    else
                    {
                        ok++;
                        if (log != null) log(msg);
                        detail.Append("√ ").Append(msg).Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    detail.Append("× ").Append(id).Append("：").Append(ex.Message).Append('\n');
                }
            }

            string bk = "";
            if (lines.Count > 0)
            {
                bk = BackupFile(mod);
                try { File.WriteAllLines(bk, lines, Encoding.UTF8); } catch { bk = ""; }
            }

            return new JB()
                .Add("type", "maintResult")
                .Add("mod", mod)
                .Add("ok", ok)
                .Add("fail", fail)
                .Add("backup", bk)
                .Add("detail", detail.ToString())
                .End();
        }

        /// <summary>返回空串表示失败。</summary>
        private static string Do(string mod, string id, string act, List<string> lines)
        {
            if (mod == "reg") return RegDelete(id, lines);
            if (mod == "startup")
            {
                if (id.StartsWith("w:", StringComparison.Ordinal))
                {
                    if (act == "on") return UwpStartupSet(id, true, lines);
                    if (act == "off") return UwpStartupSet(id, false, lines);
                    return "";
                }
                if (act == "on") return StartupOn(id, lines);
                if (act == "del") return StartupDelete(id, lines);
                return StartupOff(id, lines);
            }
            if (mod == "task")
            {
                if (act == "disable") return TaskSet(id, false, lines);
                if (act == "enable") return TaskSet(id, true, lines);
                return TaskDelete(id, lines);
            }
            if (mod == "service") return ServiceAct(id, act, lines);
            return "";
        }

        private static string RegDelete(string id, List<string> lines)
        {
            if (!id.StartsWith("u:", StringComparison.Ordinal)) return "";
            string[] parts = id.Substring(2).Split('|');
            if (parts.Length < 3) return "";
            string hive = parts[0];
            string sub = parts[1];
            string key = parts[2];
            RegistryHive h = hive.StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            RegistryView view = hive.EndsWith("32") ? RegistryView.Registry32 : RegistryView.Default;
            string full = sub + @"\" + key;
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                using (RegistryKey k = root.OpenSubKey(full))
                {
                    if (k == null) return "";
                    foreach (string vn in k.GetValueNames())
                        lines.Add("REG\t" + hive + @"\" + full + @"\" + vn + "\t" + Str(k.GetValue(vn)));
                }
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                {
                    root.DeleteSubKeyTree(full, false);
                }
                return "已删除登记：" + key;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>UWP 启动任务的启用/停用写 State（2 启用 / 1 停用），改的是当前用户，不需要管理员。</summary>
        private static string UwpStartupSet(string id, bool enable, List<string> lines)
        {
            if (!id.StartsWith("w:", StringComparison.Ordinal)) return "";
            string[] parts = id.Substring(2).Split('|');
            if (parts.Length < 2) return "";
            string key = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\"
                       + parts[0] + @"\" + parts[1];
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
                using (RegistryKey k = root.OpenSubKey(key, true))
                {
                    if (k == null) return "";
                    lines.Add("UWPSTART\t" + key + "\t" + Str(k.GetValue("State")));
                    k.SetValue("State", enable ? 2 : 1, RegistryValueKind.DWord);
                }
                return (enable ? "已启用启动应用：" : "已停用启动应用：") + parts[0];
            }
            catch { return ""; }
        }

        /// <summary>停用：值留着，只打禁用标记，随时能恢复。</summary>
        private static string StartupOff(string id, List<string> lines)
        {
            if (!id.StartsWith("s:", StringComparison.Ordinal)) return "";
            string[] parts = id.Substring(2).Split('|');
            if (parts.Length < 3) return "";
            string tag = parts[0];
            string sub = parts[1];
            string vn = parts[2];
            RegistryHive h = tag.StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            RegistryView view = tag.EndsWith("32") ? RegistryView.Registry32 : RegistryView.Default;
            bool runKey = IsRunKeyPath(sub);
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                using (RegistryKey k = root.OpenSubKey(sub, true))
                {
                    if (k == null) return "";
                    object v = k.GetValue(vn);
                    if (v == null) return "";
                    lines.Add("RUN\t" + tag + "\t" + sub + "\t" + vn + "\t" + v.ToString());
                    /* 组策略那类键没有 StartupApproved 可挂，只能摘掉值 */
                    if (!runKey) k.DeleteValue(vn, false);
                }
                if (runKey)
                {
                    string approved = ApprovedPath(sub);
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                        using (RegistryKey k = root.CreateSubKey(approved))
                        {
                            if (k != null)
                            {
                                byte[] off = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                                k.SetValue(vn, off, RegistryValueKind.Binary);
                            }
                        }
                    }
                    catch { }
                }
                return "已停用启动项：" + vn + "（原值已备份，随时可以恢复自启）";
            }
            catch { return ""; }
        }

        /// <summary>删除：把整个登记项摘掉，不再随开机运行。</summary>
        private static string StartupDelete(string id, List<string> lines)
        {
            if (!id.StartsWith("s:", StringComparison.Ordinal)) return "";
            string[] parts = id.Substring(2).Split('|');
            if (parts.Length < 3) return "";
            string tag = parts[0];
            string sub = parts[1];
            string vn = parts[2];
            RegistryHive h = tag.StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            RegistryView view = tag.EndsWith("32") ? RegistryView.Registry32 : RegistryView.Default;
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                using (RegistryKey k = root.OpenSubKey(sub, true))
                {
                    if (k == null) return "";
                    object v = k.GetValue(vn);
                    if (v == null) return "";
                    lines.Add("RUN\t" + tag + "\t" + sub + "\t" + vn + "\t" + v.ToString());
                    k.DeleteValue(vn, false);
                }
                if (IsRunKeyPath(sub))
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                        using (RegistryKey k = root.OpenSubKey(ApprovedPath(sub), true))
                        {
                            if (k != null) k.DeleteValue(vn, false);
                        }
                    }
                    catch { }
                }
                return "已删除启动项：" + vn;
            }
            catch { return ""; }
        }

        private static string StartupOn(string id, List<string> lines)
        {
            if (!id.StartsWith("s:", StringComparison.Ordinal)) return "";
            string[] parts = id.Substring(2).Split('|');
            if (parts.Length < 3) return "";
            string tag = parts[0];
            string sub = parts[1];
            string vn = parts[2];
            RegistryHive h = tag.StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            RegistryView view = tag.EndsWith("32") ? RegistryView.Registry32 : RegistryView.Default;
            if (!IsRunKeyPath(sub)) return "";
            string approved = ApprovedPath(sub);
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(h, view))
                using (RegistryKey k = root.OpenSubKey(approved, true))
                {
                    if (k != null) k.DeleteValue(vn, false);
                }
                return "已恢复启动项：" + vn;
            }
            catch { return ""; }
        }

        /// <summary>把任务定义导出成备份：先试读任务文件，读不到就问任务计划服务要 XML。</summary>
        private static void TaskBackup(string tn, string file, List<string> lines)
        {
            string xml = "";
            try { if (File.Exists(file)) xml = File.ReadAllText(file); } catch { }
            if (xml.Length == 0)
            {
                string sch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
                xml = RunText(sch, "/Query /TN \"\\" + tn + "\" /XML", 30000);
            }
            if (xml.Length > 0) lines.Add("TASK\t" + tn + "\t" + xml.TrimEnd());
        }

        private static string TaskDelete(string id, List<string> lines)
        {
            if (!id.StartsWith("t:", StringComparison.Ordinal)) return "";
            string tn = id.Substring(2);
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", tn.Replace('\\', Path.DirectorySeparatorChar) + ".xml");
            TaskBackup(tn, file, lines);
            string sch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
            int code = Run(sch, "/Delete /TN \"\\" + tn + "\" /F", 30000);
            if (code != 0) return "";
            return "已删除计划任务：" + tn;
        }

        /// <summary>禁用/启用已有计划任务（不删，随时能开回来）。</summary>
        private static string TaskSet(string id, bool enable, List<string> lines)
        {
            if (!id.StartsWith("t:", StringComparison.Ordinal)) return "";
            string tn = id.Substring(2);
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", tn.Replace('\\', Path.DirectorySeparatorChar) + ".xml");
            TaskBackup(tn, file, lines);
            string sch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
            int code = Run(sch, "/Change /TN \"\\" + tn + "\" /" + (enable ? "ENABLE" : "DISABLE"), 30000);
            if (code != 0) return "";
            return (enable ? "已启用计划任务：" : "已禁用计划任务：") + tn;
        }

        private static string ServiceAct(string id, string act, List<string> lines)
        {
            if (!id.StartsWith("v:", StringComparison.Ordinal)) return "";
            string name = id.Substring(2);
            string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe");
            if (act == "auto" || act == "demand" || act == "disabled")
            {
                lines.Add("SVC\t" + name + "\t" + act);
                int c = Run(sc, "config \"" + name + "\" start= " + act, 20000);
                if (c != 0) return "";
                string txt = act == "auto" ? "自动" : (act == "demand" ? "手动" : "禁用");
                return "服务 " + name + " 启动类型改为：" + txt;
            }
            if (act == "stop")
            {
                int c = Run(sc, "stop \"" + name + "\"", 30000);
                return c == 0 ? "已停止服务：" + name : "";
            }
            if (act == "del")
            {
                lines.Add("SVC-TYPE\t" + name);
                int c = Run(sc, "delete \"" + name + "\"", 20000);
                return c == 0 ? "已删除服务：" + name : "";
            }
            return "";
        }

        /* ==================== 工具 ==================== */

        public static int Run(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        /// <summary>跑一条命令并把标准输出整段读回来（输出量大，不能像 Run 那样只等不读）。</summary>
        public static string RunText(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); psi.StandardErrorEncoding = Encoding.GetEncoding(936); }
                catch { }
                using (Process p = Process.Start(psi))
                {
                    string err = "";
                    System.Threading.Thread th = new System.Threading.Thread(delegate ()
                    {
                        try { err = p.StandardError.ReadToEnd(); } catch { }
                    });
                    th.IsBackground = true;
                    th.Start();
                    string outp = "";
                    try { outp = p.StandardOutput.ReadToEnd(); } catch { }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return outp;
                    }
                    if (err.Length > 0) Log.Warn("maint", "命令有错误输出：" + Cut(err, 200));
                    return outp;
                }
            }
            catch (Exception ex) { Log.Warn("maint", "命令执行失败：" + ex.Message); return ""; }
        }

        /// <summary>极简 CSV 解析：支持引号内逗号、引号内换行、双引号转义。</summary>
        public static List<string[]> ParseCsv(string text)
        {
            List<string[]> rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;
            List<string> cur = new List<string>();
            StringBuilder f = new StringBuilder();
            bool q = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; }
                        else q = false;
                    }
                    else f.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { cur.Add(f.ToString()); f.Length = 0; }
                else if (c == '\r') { }
                else if (c == '\n')
                {
                    cur.Add(f.ToString()); f.Length = 0;
                    rows.Add(cur.ToArray()); cur.Clear();
                }
                else f.Append(c);
            }
            if (f.Length > 0 || cur.Count > 0) { cur.Add(f.ToString()); rows.Add(cur.ToArray()); }
            return rows;
        }

        public static string Clean(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return (s == "N/A" || s == "n/a") ? "" : s;
        }

        public static string Str(object o)
        {
            return o == null ? "" : o.ToString();
        }

        public static string HiveName(RegistryHive h)
        {
            return h == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
        }

        public static string Tag(string xml, string tag)
        {
            Match m = Regex.Match(xml, "<" + tag + ">([\\s\\S]*?)</" + tag + ">", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        public static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>从命令行里取出可执行文件路径（支持带引号与带参数两种写法）。</summary>
        public static string FirstExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int e = cmd.IndexOf('"', 1);
                if (e > 1) return Environment.ExpandEnvironmentVariables(cmd.Substring(1, e - 1));
            }
            string low = cmd.ToLowerInvariant();
            int exeIdx = low.IndexOf(".exe", StringComparison.Ordinal);
            if (exeIdx >= 0) return Environment.ExpandEnvironmentVariables(cmd.Substring(0, exeIdx + 4).Trim());
            int comma = cmd.IndexOf(',');
            if (comma > 0) return Environment.ExpandEnvironmentVariables(cmd.Substring(0, comma).Trim());
            return "";
        }
    }
}
