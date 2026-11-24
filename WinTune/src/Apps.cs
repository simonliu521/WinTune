using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 应用卸载：读注册表已装程序、调用官方卸载程序、通过 winget 安装常用软件。
    /// 卸载一律交给软件自己的卸载器（可见窗口），不做静默强制删除，也不删除安装目录。
    /// </summary>
    public static class Apps
    {
        public class App
        {
            public string Id = "";
            public string Name = "";
            public string Ver = "";
            public string Pub = "";
            public string Loc = "";
            public string Uninstall = "";
            public string QuietUninstall = "";
            public string Date = "";
            public long SizeKb = 0;
            public string Hive = "";
            public string DisplayIcon = "";
            public string Kind = "";       // system 系统应用 / personal 个人安装
            public string IconData = "";   // data:image/png;base64,...
        }

        private static readonly string[] SubKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        public static List<App> List()
        {
            List<App> list = new List<App>();
            Scan(Registry.LocalMachine, "HKLM", list);
            Scan(Registry.CurrentUser, "HKCU", list);
            list.Sort(delegate (App a, App b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        private static void Scan(RegistryKey root, string hive, List<App> list)
        {
            foreach (string sub in SubKeys)
            {
                RegistryKey k = null;
                try { k = root.OpenSubKey(sub, false); } catch { }
                if (k == null) continue;
                string[] names = null;
                try { names = k.GetSubKeyNames(); } catch { }
                if (names != null)
                {
                    foreach (string n in names)
                    {
                        RegistryKey sk = null;
                        try { sk = k.OpenSubKey(n, false); } catch { }
                        if (sk == null) continue;
                        try
                        {
                            string disp = Str(sk, "DisplayName");
                            if (disp.Length == 0) continue;
                            if (Int(sk, "SystemComponent", 0) == 1) continue;
                            if (Str(sk, "ParentKeyName").Length > 0) continue;
                            string rt = Str(sk, "ReleaseType");
                            if (rt == "Update" || rt == "Hotfix" || rt == "Security Update") continue;

                            App a = new App();
                            a.Id = hive + "|" + sub + "|" + n;
                            a.Name = disp;
                            a.Ver = Str(sk, "DisplayVersion");
                            a.Pub = Str(sk, "Publisher");
                            a.Loc = Str(sk, "InstallLocation");
                            a.Uninstall = Str(sk, "UninstallString");
                            a.QuietUninstall = Str(sk, "QuietUninstallString");
                            a.Date = Str(sk, "InstallDate");
                            a.DisplayIcon = Str(sk, "DisplayIcon");
                            string sz = Str(sk, "EstimatedSize");
                            if (sz.Length > 0) { try { a.SizeKb = Convert.ToInt64(sz); } catch { } }
                            a.Hive = hive;
                            a.Kind = KindOf(a);
                            list.Add(a);
                        }
                        finally { sk.Close(); }
                    }
                }
                k.Close();
            }
        }

        private static string Str(RegistryKey k, string name)
        {
            try
            {
                object v = k.GetValue(name, null);
                return v == null ? "" : Convert.ToString(v).Trim();
            }
            catch { return ""; }
        }

        private static int Int(RegistryKey k, string name, int def)
        {
            try
            {
                object v = k.GetValue(name, null);
                if (v == null) return def;
                return Convert.ToInt32(v);
            }
            catch { return def; }
        }

        /* ============ 系统应用判定 / 图标提取 ============ */

        private static Dictionary<string, string> iconCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>系统自带（装在 Windows 目录、WindowsApps，或微软运行库/外壳组件），其余算个人安装。</summary>
        private static string KindOf(App a)
        {
            string loc = a.Loc == null ? "" : a.Loc.ToLowerInvariant();
            if (loc.Length > 0)
            {
                if (loc.StartsWith(@"c:\windows")) return "system";
                if (loc.IndexOf(@"\windowsapps") >= 0) return "system";
            }
            string n = a.Name.ToLowerInvariant();
            if (n.StartsWith("microsoft visual c++") || n.StartsWith("microsoft .net")
                || n.StartsWith("microsoft edge") || n.StartsWith("microsoft windows")
                || n.StartsWith("windows ") || n.StartsWith("microsoft update")
                || n.StartsWith("microsoft silverlight") || n.StartsWith("microsoft xna"))
                return "system";

            // 驱动与芯片组组件（Intel / Realtek / AMD 芯片组），不算个人安装
            if (n.IndexOf("intel") >= 0 || n.IndexOf("英特尔") >= 0 || n.IndexOf("realtek") >= 0
                || n.IndexOf("amd chipset") >= 0 || n.IndexOf("qualcomm") >= 0)
            {
                if (n.IndexOf("bluetooth") >= 0 || n.IndexOf("蓝牙") >= 0 || n.IndexOf("wireless") >= 0
                    || n.IndexOf("无线") >= 0 || n.IndexOf("audio") >= 0 || n.IndexOf("音频") >= 0
                    || n.IndexOf("graphics") >= 0 || n.IndexOf("驱动") >= 0 || n.IndexOf("chipset") >= 0
                    || n.IndexOf("serial io") >= 0 || n.IndexOf("thunderbolt") >= 0 || n.IndexOf("雷电") >= 0)
                    return "system";
            }
            return "personal";
        }

        /// <summary>取这个程序的图标来源 exe：优先 DisplayIcon，其次安装目录里最像主程序的 exe。</summary>
        private static string ExeOf(App a)
        {
            string di = a.DisplayIcon;
            if (di.Length > 0)
            {
                string p = di;
                if (p.StartsWith("\""))
                {
                    int q = p.IndexOf('"', 1);
                    if (q > 0) p = p.Substring(1, q - 1);
                }
                else
                {
                    int c = p.LastIndexOf(',');
                    if (c > 0) p = p.Substring(0, c);
                }
                p = p.Trim().Trim('"');
                try { if (p.Length > 3 && File.Exists(p)) return p; } catch { }
            }
            string loc = a.Loc;
            if (loc.Length > 0 && Directory.Exists(loc)) return BestExeIn(loc, a.Name);

            // 没有 InstallLocation 的老程序：用卸载命令所在目录推
            try
            {
                string exe = FirstExe(a.Uninstall);
                if (exe.Length > 0)
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (dir != null && Directory.Exists(dir)) return BestExeIn(dir, a.Name);
                }
            }
            catch { }
            return null;
        }

        /// <summary>目录里最像主程序的 exe：同名优先，否则取最大的。</summary>
        private static string BestExeIn(string dir, string name)
        {
            try
            {
                string[] exes = Directory.GetFiles(dir, "*.exe");
                string best = null;
                long bestLen = -1;
                string want = name.ToLowerInvariant();
                foreach (string e in exes)
                {
                    string fn = Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
                    if (fn == want) return e;
                    long len = 0;
                    try { len = new FileInfo(e).Length; } catch { }
                    if (len > bestLen) { bestLen = len; best = e; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>把 exe 的图标缩成 24×24 PNG，转成 data URI 给界面用。取不到就返回空。</summary>
        private static string IconDataOf(string exePath)
        {
            if (exePath == null || exePath.Length == 0) return "";
            string cached;
            if (iconCache.TryGetValue(exePath, out cached)) return cached;
            string data = "";
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(exePath))
                {
                    if (ic != null)
                    {
                        using (Bitmap src = ic.ToBitmap())
                        {
                            using (Bitmap dst = new Bitmap(24, 24))
                            {
                                using (Graphics g = Graphics.FromImage(dst))
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(src, 0, 0, 24, 24);
                                }
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    dst.Save(ms, ImageFormat.Png);
                                    data = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                                }
                            }
                        }
                    }
                }
            }
            catch { data = ""; }
            iconCache[exePath] = data;
            return data;
        }

        public static string ListJson()
        {
            List<App> list = List();
            StringBuilder sb = new StringBuilder(20000);
            sb.Append("{\"type\":\"appsList\",\"total\":").Append(list.Count).Append(",\"items\":[");
            for (int i = 0; i < list.Count; i++)
            {
                App a = list[i];
                if (i > 0) sb.Append(',');
                string iconPath = ExeOf(a);
                bool keep = IsKeep(a.Name);
                string ust = UninstallerState(a);
                bool ufail = HasFailed(a.Id);
                sb.Append(new JB()
                    .Add("id", a.Id).Add("name", a.Name).Add("ver", a.Ver)
                    .Add("pub", a.Pub).Add("loc", a.Loc).Add("date", a.Date)
                    .Add("size", a.SizeKb).Add("hive", a.Hive)
                    .Add("kind", a.Kind)
                    .Add("icon", IconDataOf(iconPath))
                    .Add("exe", iconPath == null ? "" : iconPath)
                    .Add("runtime", keep)
                    .Add("un", ust)
                    .Add("canUninstall", ust == "ok" && !keep && !ufail)
                    .Add("canRemove", !keep && (ust != "ok" || ufail))
                    .End());
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>运行库 / 驱动 / 框架：即使带卸载命令，删了也会让别的软件或硬件失效</summary>
        private static bool IsKeep(string name)
        {
            if (name == null) return false;
            string n = name.ToLower();
            if (n.Contains("redistributable")) return true;
            if (n.Contains("runtime")) return true;
            if (n.Contains(".net") || n.Contains("dotnet")) return true;
            if (n.Contains("visual c++") || n.Contains("c++ 20")) return true;
            if (n.Contains("webview2")) return true;
            if (n.Contains("windows sdk") || n.Contains("windows software development kit")) return true;
            if (n.Contains("directx")) return true;
            if (n.Contains("java") && n.Contains("runtime")) return true;
            /* 硬件驱动：蓝牙 / 无线 / 显卡 / 声卡，删了设备直接不可用 */
            if (n.Contains("bluetooth")) return true;
            if (n.Contains("wireless")) return true;
            if (n.Contains("realtek")) return true;
            if (n.Contains("nvidia")) return true;
            if (n.Contains("amd chipset") || n.Contains("intel(r) chipset")) return true;
            return false;
        }

        /// <summary>本次运行中点「卸载」但卸载器起不来的项（命令指向的 exe 在，运行却失败）。</summary>
        private static readonly List<string> failIds = new List<string>();

        public static void MarkFail(string id)
        {
            lock (failIds) { if (!failIds.Contains(id)) failIds.Add(id); }
        }

        private static bool HasFailed(string id)
        {
            lock (failIds) { return failIds.Contains(id); }
        }

        private static App Find(string id)
        {
            // 注册表路径不区分大小写，前端回传的 id 大小写可能和扫描时不同，比较时忽略大小写
            foreach (App a in List()) if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /* ============ 卸载程序可用性 / 残留清除 ============ */

        /// <summary>卸载命令里的可执行文件是否真的还在；相对名（MsiExec.exe / rundll32.exe）到系统目录找。</summary>
        private static string UninstallerExe(string cmd)
        {
            if (cmd == null || cmd.Length == 0) return "";
            string exe = FirstExe(cmd);
            if (exe.Length == 0) return "";
            if (exe.IndexOf('\\') < 0 && exe.IndexOf('/') < 0)
            {
                string sys = Path.Combine(Environment.SystemDirectory, exe);
                if (File.Exists(sys)) return sys;
                string win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
                if (File.Exists(win)) return win;
                return "";
            }
            try { return File.Exists(exe) ? exe : ""; } catch { return ""; }
        }

        /// <summary>ok = 卸载程序可用；missing = 写了命令但文件已经不在了；none = 根本没写卸载命令。</summary>
        public static string UninstallerState(App a)
        {
            if (a.Uninstall.Length > 0)
                return UninstallerExe(a.Uninstall).Length > 0 ? "ok" : "missing";
            if (a.QuietUninstall.Length > 0)
                return UninstallerExe(a.QuietUninstall).Length > 0 ? "ok" : "missing";
            return "none";
        }

        /// <summary>安装目录：优先注册表 InstallLocation，其次卸载命令所在目录；盘根、系统关键目录一律不认。</summary>
        private static string AppDir(App a)
        {
            string dir = a.Loc == null ? "" : a.Loc.Trim().Trim('"').TrimEnd('\\');
            if (dir.Length > 0 && Directory.Exists(dir) && GuardOk(dir)) return dir;
            string exe = FirstExe(a.Uninstall);
            if (exe.Length == 0) exe = FirstExe(a.QuietUninstall);
            if (exe.IndexOf('\\') >= 0)
            {
                try
                {
                    string d = Path.GetDirectoryName(exe);
                    if (d != null && d.Length > 0 && Directory.Exists(d) && GuardOk(d)) return d.TrimEnd('\\');
                }
                catch { }
            }
            return "";
        }

        private static bool GuardOk(string dir)
        {
            string reason;
            return Guard.CanDelete(dir, new string[] { dir }, true, out reason);
        }

        /// <summary>目录名里带这些词的，通常是邮件 / 聊天记录 / 用户数据，删之前必须提醒。</summary>
        private static readonly string[] SensitiveNames =
        {
            "storage", "mail", "mailbox", "maildata", "messages", "chat", "chats",
            "records", "userdata", "profile", "profiles", "database", "backup"
        };

        private static bool IsSensitive(string name)
        {
            string n = (name == null ? "" : name).ToLowerInvariant();
            foreach (string s in SensitiveNames) if (n == s) return true;
            return false;
        }

        private static void MeasureDir(string dir, out long bytes, out int files, List<string> sensitive)
        {
            bytes = 0; files = 0;
            Stack<string> st = new Stack<string>();
            st.Push(dir);
            int seen = 0;
            while (st.Count > 0)
            {
                string cur = st.Pop();
                if (++seen > 300000) break;
                try
                {
                    foreach (string d in Directory.GetDirectories(cur))
                    {
                        if (Guard.IsReparsePoint(d)) continue;
                        if (sensitive != null)
                        {
                            string nm = Path.GetFileName(d);
                            if (IsSensitive(nm) && sensitive.Count < 8 && !sensitive.Contains(nm)) sensitive.Add(nm);
                        }
                        st.Push(d);
                    }
                    foreach (string f in Directory.GetFiles(cur))
                    {
                        files++;
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>开始菜单 / 桌面里指向这个目录的快捷方式（目标是目录或其内部文件都算）。</summary>
        private static List<string> ShortcutList(string dir)
        {
            List<string> hit = new List<string>();
            if (dir == null || dir.Length == 0) return hit;
            List<string> roots = new List<string>();
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null) shell = Activator.CreateInstance(shellType);
            }
            catch { }

            foreach (string root in roots)
            {
                if (root == null || root.Length == 0 || !Directory.Exists(root)) continue;
                Stack<string> st = new Stack<string>();
                st.Push(root);
                int seen = 0;
                while (st.Count > 0 && seen++ < 20000)
                {
                    string cur = st.Pop();
                    try
                    {
                        foreach (string d in Directory.GetDirectories(cur)) st.Push(d);
                        foreach (string f in Directory.GetFiles(cur, "*.lnk"))
                        {
                            string target = "";
                            try
                            {
                                if (shell != null)
                                {
                                    object lnk = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { f });
                                    target = (string)lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null);
                                }
                            }
                            catch { }
                            if (target != null && target.Length > 0 && Guard.IsUnder(target, dir)) hit.Add(f);
                        }
                    }
                    catch { }
                }
            }
            return hit;
        }

        /// <summary>「清除残留」弹窗前先查清楚：目录在哪、多大、有没有敏感数据目录、几个快捷方式。</summary>
        public static string Leftover(string id)
        {
            App a = Find(id);
            if (a == null)
                return new JB().Add("type", "appLeftover").Add("ok", false)
                    .Add("msg", "找不到这个程序，列表可能已经刷新，回到页面点「刷新」再看").End();

            string st = UninstallerState(a);
            string dir = AppDir(a);
            long bytes = 0; int files = 0;
            List<string> sensitive = new List<string>();
            if (dir.Length > 0) MeasureDir(dir, out bytes, out files, sensitive);
            List<string> lnk = ShortcutList(dir);

            string hive = "HKLM", sub = "", key = "";
            string[] parts = a.Id.Split('|');
            if (parts.Length == 3) { hive = parts[0]; sub = parts[1]; key = parts[2]; }

            StringBuilder sent = new StringBuilder("[");
            for (int i = 0; i < sensitive.Count; i++)
            {
                if (i > 0) sent.Append(',');
                sent.Append(Json.Quote(sensitive[i]));
            }
            sent.Append(']');

            return new JB().Add("type", "appLeftover").Add("ok", true)
                .Add("id", a.Id).Add("name", a.Name)
                .Add("state", st).Add("un", a.Uninstall)
                .Add("dir", dir).Add("bytes", bytes).Add("files", files)
                .Add("shortcuts", lnk.Count)
                .Add("admin", SysInfo.IsAdmin())
                .Raw("sensitive", sent.ToString())
                .Add("regKey", (hive == "HKLM" ? @"HKEY_LOCAL_MACHINE\" : @"HKEY_CURRENT_USER\") + sub + "\\" + key)
                .End();
        }

        /// <summary>
        /// 收尾「没有卸载程序」的应用：先备份注册表、再移除卸载登记；mode=full 时连安装目录与快捷方式一起删。
        /// 进度用 appRemoveOut 逐条发，结束发 appRemoveDone。
        /// </summary>
        public static void Remove(string id, string mode, Action<string> onEvent)
        {
            bool full = mode == "full";
            App a = Find(id);
            if (a == null)
            {
                Emit(onEvent, new JB().Add("type", "appRemoveDone").Add("ok", false)
                    .Add("msg", "找不到这个程序，可能已经被卸载").End());
                return;
            }

            string hive = "HKLM", sub = "", key = "";
            string[] parts = a.Id.Split('|');
            if (parts.Length == 3) { hive = parts[0]; sub = parts[1]; key = parts[2]; }

            string backup = "";
            bool regOk = false;
            string regErr = "";
            try
            {
                backup = Backup.ExportReg((hive == "HKLM" ? @"HKLM\" : @"HKCU\") + sub + "\\" + key, "app-" + SafeTag(key));
                RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                root.DeleteSubKeyTree(sub + "\\" + key, false);
                regOk = true;
                Log.Info("apps", "移除卸载登记 " + hive + "\\" + sub + "\\" + key + " (" + a.Name + ")");
            }
            catch (Exception ex)
            {
                regErr = ex.Message;
                Log.Warn("apps", "移除卸载登记失败：" + ex.Message);
            }

            long bytes = 0; int files = 0; int failed = 0; int pending = 0; int lnkDel = 0;
            string dir = full ? AppDir(a) : "";
            string note = "";
            List<string> lnk = (full && dir.Length > 0) ? ShortcutList(dir) : new List<string>();

            if (dir.Length > 0 && Directory.Exists(dir))
            {
                string reason;
                if (!Guard.CanDelete(dir, new string[] { dir }, true, out reason))
                {
                    note = "安装目录没有动：" + reason;
                    Log.Warn("apps", note);
                }
                else
                {
                    Emit(onEvent, new JB().Add("type", "appRemoveOut").Add("line", "开始删除 " + dir).End());
                    DeleteTree(dir, onEvent, ref bytes, ref files, ref failed, ref pending);
                    if (failed > 0 && SysInfo.IsAdmin())
                    {
                        Emit(onEvent, new JB().Add("type", "appRemoveOut")
                            .Add("line", "有 " + failed + " 个文件删不掉，接管目录权限后再试一次…").End());
                        TakeOwn(dir);
                        DeleteTree(dir, onEvent, ref bytes, ref files, ref failed, ref pending);
                    }
                    if (Directory.Exists(dir)) note = "目录还有残余，删不掉的文件已排入重启后删除";
                    else if (note.Length == 0) note = "安装目录已删除";
                }
            }
            else if (full) note = "注册表里没有可用的安装目录，只移除了登记";

            foreach (string f in lnk)
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); lnkDel++; } catch { }
            }

            Log.Info("apps", "清除残留完成 " + a.Name + " 登记=" + regOk + " 文件=" + files +
                              " 释放=" + bytes + " 待重启=" + pending + " 失败=" + failed + " 快捷方式=" + lnkDel);
            Emit(onEvent, new JB().Add("type", "appRemoveDone")
                .Add("ok", regOk || files > 0 || lnkDel > 0)
                .Add("id", a.Id).Add("name", a.Name).Add("mode", mode)
                .Add("reg", regOk).Add("regErr", regErr).Add("backup", backup)
                .Add("dir", dir).Add("bytes", bytes).Add("files", files)
                .Add("pending", pending).Add("failed", failed).Add("lnk", lnkDel)
                .Add("note", note)
                .End());
        }

        private static void DeleteTree(string dir, Action<string> onEvent,
            ref long bytes, ref int files, ref int failed, ref int pending)
        {
            List<string> fs = new List<string>();
            List<string> dirs = new List<string>();
            Stack<string> st = new Stack<string>();
            st.Push(dir);
            while (st.Count > 0)
            {
                string cur = st.Pop();
                dirs.Add(cur);
                try
                {
                    foreach (string d in Directory.GetDirectories(cur))
                    {
                        if (Guard.IsReparsePoint(d)) continue;
                        st.Push(d);
                    }
                    foreach (string f in Directory.GetFiles(cur)) fs.Add(f);
                }
                catch { }
            }

            int total = fs.Count;
            int done = 0;
            foreach (string f in fs)
            {
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                bool gone = false;
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    gone = true;
                    bytes += len; files++;
                }
                catch (UnauthorizedAccessException) { if (MoveFileEx(f, null, 4)) { pending++; gone = true; } }
                catch (IOException) { if (MoveFileEx(f, null, 4)) { pending++; gone = true; } }
                catch { }
                if (!gone) failed++;

                if (onEvent != null && (++done % 400) == 0)
                    Emit(onEvent, new JB().Add("type", "appRemoveOut")
                        .Add("line", "已处理 " + done + " / " + total + " 个文件 · 释放 " +
                             (bytes / 1048576.0).ToString("0.0") + " MB").End());
            }

            dirs.Sort(delegate (string x, string y) { return y.Length.CompareTo(x.Length); });
            foreach (string d in dirs)
            {
                try { Directory.Delete(d, false); } catch { }
            }
        }

        private static void TakeOwn(string dir)
        {
            try
            {
                RunQuiet(Path.Combine(Environment.SystemDirectory, "takeown.exe"), "/F \"" + dir + "\" /R /D Y", 240000);
                RunQuiet(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                    "\"" + dir + "\" /grant *S-1-5-32-544:(OI)(CI)F /T /Q", 240000);
            }
            catch { }
        }

        private static void RunQuiet(string exe, string args, int ms)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                using (Process p = Process.Start(si)) { p.WaitForExit(ms); }
            }
            catch { }
        }

        private static string SafeTag(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s) b.Append(char.IsLetterOrDigit(c) ? c : '_');
            string t = b.ToString();
            return t.Length > 40 ? t.Substring(0, 40) : t;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newName, int flags);
        /// <summary>调用软件自带卸载器。可见窗口，由用户自己确认。</summary>
        public static string Uninstall(string id)
        {
            App a = Find(id);
            if (a == null) return new JB().Add("type", "appResult").Add("ok", false).Add("msg", "找不到这个程序，可能已被卸载").End();
            string ust = UninstallerState(a);
            if (ust != "ok")
                return new JB().Add("type", "appResult").Add("ok", false)
                    .Add("msg", ust == "missing"
                        ? ("这个程序的卸载入口已经失效（" + (a.Uninstall.Length > 0 ? a.Uninstall : a.QuietUninstall) + "）")
                        : "这个程序没有提供卸载命令，无法调用官方卸载器").End();
            string command = a.Uninstall.Length > 0 ? a.Uninstall : a.QuietUninstall;
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(command);
                si.UseShellExecute = true;
                si.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                Process p = Process.Start(si);
                Log.Info("apps", "调用卸载器：" + a.Name);
                return new JB().Add("type", "appResult").Add("ok", true)
                    .Add("msg", "已打开 " + a.Name + " 的卸载程序，按它自己的提示完成即可").End();
            }
            catch (Exception ex)
            {
                Log.Warn("apps", "卸载器启动失败：" + ex.Message);
                MarkFail(a.Id);
                return new JB().Add("type", "appResult").Add("ok", false)
                    .Add("msg", "启动卸载程序失败：" + ex.Message + "。请从「设置 → 应用」或软件自己的目录处理").End();
            }
        }

        public static string OpenLocation(string id)
        {
            App a = Find(id);
            if (a == null) return new JB().Add("type", "appResult").Add("ok", false).Add("msg", "找不到这个程序").End();
            string dir = a.Loc;
            if (dir.Length == 0 || !Directory.Exists(dir))
            {
                try
                {
                    string exe = FirstExe(a.Uninstall);
                    if (exe.Length > 0) dir = Path.GetDirectoryName(exe);
                }
                catch { }
            }
            if (dir == null || dir.Length == 0 || !Directory.Exists(dir))
                return new JB().Add("type", "appResult").Add("ok", false).Add("msg", "这个程序没有记录安装位置").End();
            Process.Start("explorer.exe", "\"" + dir + "\"");
            return new JB().Add("type", "appResult").Add("ok", true).Add("msg", "已打开安装目录").End();
        }

        private static string FirstExe(string cmd)
        {
            if (cmd.Length == 0) return "";
            string c = cmd.Trim();
            if (c.StartsWith("\""))
            {
                int e = c.IndexOf('"', 1);
                if (e > 1) return c.Substring(1, e - 1);
            }
            // 没写引号的命令：路径本身可能带空格（E:\Program Files\Foxmail 7.2\uninst.exe），先按 .exe 截断
            int x = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (x > 0) return c.Substring(0, x + 4);
            int sp = c.IndexOf(' ');
            if (sp > 0) return c.Substring(0, sp);
            return c;
        }

        /* ================= winget ================= */

        public class Rec
        {
            public string Key = "", Name = "", Pkg = "", Desc = "";
            public Rec(string k, string n, string p, string d) { Key = k; Name = n; Pkg = p; Desc = d; }
        }

        public static List<Rec> Recommend()
        {
            List<Rec> l = new List<Rec>();
            l.Add(new Rec("chrome", "Google Chrome", "Google.Chrome", "浏览器，兼容性最好"));
            l.Add(new Rec("7zip", "7-Zip", "7zip.7zip", "压缩解压，无广告"));
            l.Add(new Rec("vlc", "VLC 播放器", "VideoLAN.VLC", "几乎能播所有格式"));
            l.Add(new Rec("vscode", "VS Code", "Microsoft.VisualStudioCode", "代码编辑器"));
            l.Add(new Rec("npp", "Notepad++", "Notepad++.Notepad++", "轻量文本编辑"));
            l.Add(new Rec("powertoys", "PowerToys", "Microsoft.PowerToys", "微软官方效率工具集"));
            l.Add(new Rec("everything", "Everything", "voidtools.Everything", "秒级文件名搜索"));
            l.Add(new Rec("wechat", "微信", "Tencent.WeChat", "聊天工具"));
            l.Add(new Rec("obs", "OBS Studio", "OBSProject.OBSStudio", "录屏与直播"));
            l.Add(new Rec("git", "Git", "Git.Git", "版本控制"));
            l.Add(new Rec("python", "Python 3.12", "Python.Python.3.12", "运行环境"));
            l.Add(new Rec("firefox", "Firefox", "Mozilla.Firefox", "浏览器"));
            l.Add(new Rec("qq", "QQ", "Tencent.QQ.NT", "聊天工具"));
            l.Add(new Rec("wps", "WPS Office", "Kingsoft.WPSOffice", "办公套件"));
            return l;
        }

        public static string RecommendJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":\"appsRec\",\"winget\":").Append(SysInfo.HasWinget() ? "true" : "false");
            sb.Append(",\"items\":[");
            List<Rec> l = Recommend();
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(new JB().Add("id", l[i].Key).Add("name", l[i].Name)
                    .Add("pkg", l[i].Pkg).Add("desc", l[i].Desc).End());
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static Rec FindRec(string key)
        {
            foreach (Rec r in Recommend()) if (r.Key == key) return r;
            return null;
        }

        /// <summary>winget 安装。流式把每行输出发给界面。</summary>
        public static void Install(string key, Action<string> onEvent)
        {
            Rec r = FindRec(key);
            if (r == null)
            {
                Emit(onEvent, new JB().Add("type", "appDone").Add("ok", false).Add("id", key).Add("msg", "未知的软件").End());
                return;
            }
            if (!SysInfo.HasWinget())
            {
                Emit(onEvent, new JB().Add("type", "appDone").Add("ok", false).Add("id", key)
                    .Add("msg", "这台机器没有 winget，先在「安装 winget」里装一次").End());
                return;
            }
            Emit(onEvent, new JB().Add("type", "appOut").Add("line", "winget install " + r.Pkg).End());
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("winget",
                    "install -e --id " + r.Pkg + " --accept-package-agreements --accept-source-agreements --disable-interactivity");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                si.StandardOutputEncoding = Encoding.UTF8;
                si.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(si))
                {
                    ReadLines(p, onEvent);
                    p.WaitForExit();
                    Emit(onEvent, new JB().Add("type", "appDone")
                        .Add("ok", p.ExitCode == 0).Add("id", key)
                        .Add("msg", p.ExitCode == 0 ? ("已安装 " + r.Name) : ("winget 返回代码 " + p.ExitCode)).End());
                }
            }
            catch (Exception ex)
            {
                Emit(onEvent, new JB().Add("type", "appDone").Add("ok", false).Add("id", key)
                    .Add("msg", "启动 winget 失败：" + ex.Message).End());
            }
        }

        private static void ReadLines(Process p, Action<string> onEvent)
        {
            // 两个流都要读，否则可能死锁
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                try
                {
                    string s;
                    while ((s = p.StandardError.ReadLine()) != null)
                        Emit(onEvent, new JB().Add("type", "appOut").Add("line", s).End());
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
            try
            {
                string s;
                while ((s = p.StandardOutput.ReadLine()) != null)
                    Emit(onEvent, new JB().Add("type", "appOut").Add("line", s).End());
            }
            catch { }
        }

        private static void Emit(Action<string> cb, string json)
        {
            if (cb != null) cb(json);
        }

        public static string OpenWingetPage()
        {
            try
            {
                Process.Start("https://aka.ms/getwinget");
                return new JB().Add("type", "appResult").Add("ok", true)
                    .Add("msg", "已打开 winget 官方安装页（Microsoft App Installer），装完重开本软件").End();
            }
            catch (Exception ex) { return new JB().Add("type", "appResult").Add("ok", false).Add("msg", ex.Message).End(); }
        }
    }
}
