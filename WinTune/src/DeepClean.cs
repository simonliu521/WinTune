using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 卸载后的深度清理：只处理本次已从应用清单消失的个人软件，
    /// 删除它的安装目录、自己的 AppData 配置/缓存、快捷方式和注册表项。
    /// 不动系统组件、运行库、驱动，也不整棵删除发布者公共目录/公共注册表根。
    /// </summary>
    public static class DeepClean
    {
        public class Result
        {
            public int Apps = 0;
            public int Skipped = 0;
            public int Dirs = 0;
            public int Files = 0;
            public int Regs = 0;
            public int Fail = 0;
        }

        public static Result Run(List<Apps.App> apps, Action<string> log)
        {
            Result r = new Result();
            if (log == null) log = delegate (string s) { };

            foreach (Apps.App a in apps)
            {
                if (a == null) continue;
                if (a.Kind == "system")
                {
                    r.Skipped++;
                    log("深度清理跳过系统组件：" + a.Name);
                    continue;
                }

                r.Apps++;
                int dirs = 0, files = 0, regs = 0, fail = 0;
                foreach (string d in DataDirs(a))
                {
                    if (!Directory.Exists(d)) continue;
                    if (DeleteDir(d, log)) dirs++;
                    else fail++;
                }
                foreach (string f in ShortcutFiles(a))
                {
                    if (!File.Exists(f)) continue;
                    if (DeleteFile(f)) files++;
                    else fail++;
                }
                foreach (string key in RegistryKeys(a))
                {
                    bool existed = false;
                    bool ok = DeleteKey(key, out existed);
                    if (existed)
                    {
                        if (ok) regs++;
                        else fail++;
                    }
                }

                r.Dirs += dirs;
                r.Files += files;
                r.Regs += regs;
                r.Fail += fail;
                log("深度清理 " + a.Name + "：目录 " + dirs + "，快捷方式 " + files + "，注册表 " + regs
                    + (fail > 0 ? "，失败 " + fail : ""));
            }
            return r;
        }

        /* ===================== 名字归一化 ===================== */

        private static readonly Regex VerTail = new Regex(
            @"\s+v?\d+(\.\d+)*(\s*\([^)]*\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LegalTail = new Regex(
            @"\s*(Inc\.?|LLC|Ltd\.?|Corporation|Corp\.?|Co\.?,?\s*Ltd\.?|GmbH)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] BadTokens =
        {
            "microsoft", "windows", "software", "program", "programs", "system",
            "documents", "downloads", "desktop", "appdata", "users",
            "inc", "llc", "ltd", "corp", "corporation", "gmbh", "company"
        };

        private static List<string> Tokens(Apps.App a)
        {
            List<string> list = new List<string>();
            AddToken(list, a.Name);
            AddToken(list, VerTail.Replace(a.Name ?? "", "").Trim());
            return list;
        }

        private static void AddToken(List<string> list, string s)
        {
            if (s == null) return;
            s = CleanName(s);
            if (s.Length < 2 || s.Length > 80) return;
            if (Array.IndexOf(BadTokens, s.ToLowerInvariant()) >= 0) return;
            foreach (string e in list)
                if (string.Equals(e, s, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(s);
        }

        private static string CleanName(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string Publisher(Apps.App a)
        {
            string p = CleanName(LegalTail.Replace(a.Pub ?? "", "").Trim());
            if (p.Length < 2 || p.Length > 60) return "";
            if (Array.IndexOf(BadTokens, p.ToLowerInvariant()) >= 0) return "";
            return p;
        }

        /* ===================== 数据目录 ===================== */

        private static List<string> DataDirs(Apps.App a)
        {
            List<string> c = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string pub = Publisher(a);

            foreach (string t in Tokens(a))
            {
                AddDir(c, Path.Combine(local, t));
                AddDir(c, Path.Combine(roaming, t));
                AddDir(c, Path.Combine(common, t));
                AddDir(c, Path.Combine(local, "Programs", t));
                AddDir(c, Path.Combine(docs, t));
                AddDir(c, Path.Combine(user, "Documents", t));
                AddDir(c, Path.Combine(user, "Downloads", t));
                AddDir(c, Path.Combine(user, "Desktop", t));
                AddDir(c, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), t));
                AddDir(c, Path.Combine(user, "." + t));
                if (pub.Length > 0)
                {
                    AddDir(c, Path.Combine(local, pub, t));
                    AddDir(c, Path.Combine(roaming, pub, t));
                    AddDir(c, Path.Combine(common, pub, t));
                }
            }
            if (a.Loc != null && a.Loc.Trim().Length > 0) AddDir(c, a.Loc.Trim().Trim('"'));
            return c;
        }

        private static void AddDir(List<string> list, string p)
        {
            if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p)) return;
            try
            {
                p = Path.GetFullPath(p).TrimEnd('\\', '/');
                foreach (string e in list)
                    if (string.Equals(e, p, StringComparison.OrdinalIgnoreCase)) return;
                list.Add(p);
            }
            catch { }
        }

        private static bool SafeDir(string p)
        {
            string full = Path.GetFullPath(p).TrimEnd('\\', '/');
            string low = full.ToLowerInvariant();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\').ToLowerInvariant();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\').ToLowerInvariant();
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\').ToLowerInvariant();
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\').ToLowerInvariant();

            if (full.Length < 12) return false;
            string[] exact =
            {
                @"c:\", @"c:\windows", @"c:\program files", @"c:\program files (x86)",
                @"c:\users", local, roaming, common, user,
                Path.Combine(user, "documents"), Path.Combine(user, "downloads"), Path.Combine(user, "desktop")
            };
            foreach (string e in exact)
                if (string.Equals(low, e.TrimEnd('\\').ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)) return false;

            if (low.StartsWith(@"c:\windows\")) return false;
            if (low.Contains(@"\wintune") || low.Contains(@"\minimonitor") || low.Contains(@"\bcu")) return false;
            if (low.EndsWith(@"\appdata\local") || low.EndsWith(@"\appdata\roaming") || low.EndsWith(@"\appdata")) return false;
            return true;
        }

        private static bool DeleteDir(string path, Action<string> log)
        {
            if (!SafeDir(path)) return false;
            try { if (!Directory.Exists(path)) return false; }
            catch { return false; }
            log("删除目录：" + path);
            try
            {
                Directory.Delete(path, true);
                return true;
            }
            catch
            {
                try
                {
                    Run("attrib.exe", "-R -S -H \"" + path + "\" /S /D");
                    Directory.Delete(path, true);
                    return true;
                }
                catch { return false; }
            }
        }

        private static bool DeleteFile(string path)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /* ===================== 快捷方式 ===================== */

        private static List<string> ShortcutFiles(Apps.App a)
        {
            List<string> c = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            string[] roots =
            {
                Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(common, @"Microsoft\Windows\Start Menu\Programs"),
                desktop, commonDesktop
            };

            List<string> tokens = Tokens(a);
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(root, "*.lnk"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (MatchShortcut(n, tokens)) c.Add(f);
                    }
                    foreach (string d in Directory.GetDirectories(root))
                    {
                        string n = Path.GetFileName(d);
                        if (MatchExact(n, tokens)) c.Add(d);
                    }
                }
                catch { }
            }
            return c;
        }

        private static bool MatchExact(string name, List<string> tokens)
        {
            foreach (string t in tokens)
                if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool MatchShortcut(string name, List<string> tokens)
        {
            foreach (string t in tokens)
            {
                if (t.Length < 3) continue;
                if (name.StartsWith(t + " ", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(t + "-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, t, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /* ===================== 注册表 ===================== */

        private static List<string> RegistryKeys(Apps.App a)
        {
            List<string> c = new List<string>();
            string pub = Publisher(a);
            foreach (string t in Tokens(a))
            {
                AddKey(c, @"Software\" + t);
                AddKey(c, @"Software\WOW6432Node\" + t);
                if (pub.Length > 0)
                {
                    AddKey(c, @"Software\" + pub + @"\" + t);
                    AddKey(c, @"Software\WOW6432Node\" + pub + @"\" + t);
                }
            }
            string[] p = a.Id.Split(new char[] { '|' }, 3);
            if (p.Length == 3) AddKey(c, p[1] + @"\" + p[2]);
            return c;
        }

        private static void AddKey(List<string> list, string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            foreach (string e in list)
                if (string.Equals(e, p, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(p);
        }

        private static bool DeleteKey(string path, out bool existed)
        {
            existed = false;
            try
            {
                using (RegistryKey p = Registry.CurrentUser.OpenSubKey(path, false))
                    if (p != null) existed = true;
            }
            catch { }
            try
            {
                using (RegistryKey p = Registry.LocalMachine.OpenSubKey(path, false))
                    if (p != null) existed = true;
            }
            catch { }
            if (!existed) return false;
            bool a = DeleteKeyFrom(Registry.CurrentUser, path);
            bool b = DeleteKeyFrom(Registry.LocalMachine, path);
            return a || b;
        }

        private static bool DeleteKeyFrom(RegistryKey root, string path)
        {
            try
            {
                using (RegistryKey probe = root.OpenSubKey(path, false))
                {
                    if (probe == null) return false;
                }
                root.DeleteSubKeyTree(path, false);
                return true;
            }
            catch { return false; }
        }

        private static void Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    p.StartInfo = si;
                    p.Start();
                    p.WaitForExit(60000);
                }
            }
            catch { }
        }
    }
}
