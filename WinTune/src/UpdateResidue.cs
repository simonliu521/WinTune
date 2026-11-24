using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 更新残留：软件升级后留下的旧版本目录、更新临时文件、安装包缓存。
    /// 已经装着的软件也逐个检查自己的更新痕迹，并把残留归到具体软件名下。
    /// </summary>
    public static class UpdateResidue
    {
        public class Hit
        {
            public string Id = "";
            public string App = "";
            public string Kind = "browser";   // browser / app / temp / cache / pkg / update / installer
            public string Name = "";
            public string Path = "";
            public long Bytes = 0;
            public int Files = 0;
            public string Ver = "";
            public string Latest = "";
            public string Status = "建议删除";   // 只有两档：建议删除 / 不建议删除（这里不做"修改"）
            public string Detail = "";
            public string Ev = "";               // 判定依据编号（见 Evidence）
            public bool NeedService = false;     // 需要先停 wuauserv
            public bool Force = false;           // 不建议删除但允许强制删除
        }

        /* ==================== 已装软件清单（用来判断残留属于谁） ==================== */

        private class Owner
        {
            public string Name = "";
            public string Loc = "";
            public string Ver = "";
        }

        private class Owners
        {
            public List<Owner> List = new List<Owner>();

            public static Owners Build()
            {
                Owners o = new Owners();
                Add(o, RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                Add(o, RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
                Add(o, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                return o;
            }

            private static void Add(Owners o, RegistryHive hive, string sub)
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                    using (RegistryKey k = root.OpenSubKey(sub))
                    {
                        if (k == null) return;
                        foreach (string key in k.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey sk = k.OpenSubKey(key))
                                {
                                    if (sk == null) continue;
                                    string name = Maintenance.Str(sk.GetValue("DisplayName")).Trim();
                                    string loc = Maintenance.Str(sk.GetValue("InstallLocation")).Trim().Trim('"');
                                    if (name.Length == 0) continue;
                                    if (Maintenance.Str(sk.GetValue("ParentKeyName")).Length > 0) continue;
                                    if (Convert.ToInt32(sk.GetValue("SystemComponent", 0)) == 1) continue;
                                    Owner ow = new Owner();
                                    ow.Name = name;
                                    ow.Loc = loc;
                                    ow.Ver = Maintenance.Str(sk.GetValue("DisplayVersion"));
                                    o.List.Add(ow);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            /// <summary>按安装目录或名字找出这堆残留属于哪个已装软件。</summary>
            public string Who(string path)
            {
                string p = path.ToLowerInvariant().TrimEnd('\\');
                foreach (Owner ow in List)
                {
                    string loc = ow.Loc.ToLowerInvariant().TrimEnd('\\');
                    if (loc.Length < 8) continue;
                    if (p.StartsWith(loc, StringComparison.Ordinal) || loc.StartsWith(p, StringComparison.Ordinal))
                        return ow.Name;
                    /* 安装目录只有两层时，用上级目录对一次（例如 Application 之于 Chrome） */
                    string pd = Parent(p);
                    if (pd.Length >= 8 && (pd == loc || pd == Parent(loc))) return ow.Name;
                }
                /* 名字对不上目录就按关键字兜：把目录名去空格和符号后看是否出现在软件全名里 */
                string leaf = Path.GetFileName(p).ToLowerInvariant();
                string a = Clean(leaf);
                string b = Clean(Path.GetFileName(Parent(p)));
                foreach (Owner ow in List)
                {
                    string nm = Clean(ow.Name);
                    if (a.Length >= 4 && nm.IndexOf(a, StringComparison.Ordinal) >= 0) return ow.Name;
                    if (b.Length >= 4 && nm.IndexOf(b, StringComparison.Ordinal) >= 0) return ow.Name;
                }
                return "";
            }
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (Char.IsLetterOrDigit(c)) b.Append(Char.ToLowerInvariant(c));
            }
            return b.ToString();
        }

        private static string Parent(string p)
        {
            try
            {
                DirectoryInfo d = Directory.GetParent(p);
                return d == null ? "" : d.FullName.ToLowerInvariant();
            }
            catch { return ""; }
        }

        /* ==================== 扫描 ==================== */

        public static string ScanJson(Action<string> log)
        {
            List<Hit> hits = new List<Hit>();
            Owners owners = Owners.Build();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try { ScanBrowserVersions(hits, owners, seen); }
            catch (Exception ex) { Log.Warn("upd", "浏览器版本目录扫描失败：" + ex.Message); }
            if (log != null) log("浏览器旧版本目录已检查");
            try { ScanElectronVersions(hits, owners, seen); }
            catch (Exception ex) { Log.Warn("upd", "Electron 旧版本扫描失败：" + ex.Message); }
            if (log != null) log("应用旧版本目录已检查");
            try { ScanInstalledUpdates(hits, owners, seen); }
            catch (Exception ex) { Log.Warn("upd", "已装软件更新痕迹扫描失败：" + ex.Message); }
            if (log != null) log("已装软件的更新痕迹已检查");
            try { ScanCaches(hits, owners, seen); }
            catch (Exception ex) { Log.Warn("upd", "安装与更新缓存扫描失败：" + ex.Message); }

            long bytes = 0;
            int clean = 0;
            foreach (Hit h in hits)
            {
                bytes += h.Bytes;
                if (h.Status == "建议删除") clean++;
            }

            StringBuilder arr = new StringBuilder();
            arr.Append('[');
            for (int i = 0; i < hits.Count; i++)
            {
                Hit h = hits[i];
                if (i > 0) arr.Append(',');
                arr.Append(new JB()
                    .Add("id", h.Id)
                    .Add("app", h.App)
                    .Add("kind", h.Kind)
                    .Add("name", h.Name)
                    .Add("path", h.Path)
                    .Add("bytes", h.Bytes)
                    .Add("files", h.Files)
                    .Add("ver", h.Ver)
                    .Add("latest", h.Latest)
                    .Add("status", h.Status)
                    .Add("force", h.Force)
                    .Add("detail", h.Detail)
                    .Add("ev", h.Ev)
                    .End());
            }
            arr.Append(']');
            return new JB()
                .Add("type", "updList")
                .Raw("list", arr.ToString())
                .Add("total", hits.Count)
                .Add("clean", clean)
                .Add("bytes", bytes)
                .Add("apps", owners.List.Count)
                .End();
        }

        /* ---------- 1. 浏览器/Chromium 系的旧版本目录 ---------- */

        private static readonly string[][] AppRoots = new string[][]
        {
            new string[] { "谷歌 Chrome", @"%LOCALAPPDATA%\Google\Chrome\Application" },
            new string[] { "谷歌 Chrome", @"%ProgramFiles%\Google\Chrome\Application" },
            new string[] { "谷歌 Chrome", @"%ProgramFiles(x86)%\Google\Chrome\Application" },
            new string[] { "Microsoft Edge", @"%LOCALAPPDATA%\Microsoft\Edge\Application" },
            new string[] { "Microsoft Edge", @"%ProgramFiles(x86)%\Microsoft\Edge\Application" },
            new string[] { "Microsoft Edge", @"%ProgramFiles%\Microsoft\Edge\Application" },
            new string[] { "夸克浏览器", @"%LOCALAPPDATA%\Quark\Application" },
            new string[] { "360 极速浏览器", @"%LOCALAPPDATA%\360Chrome\Chrome\Application" },
            new string[] { "360 安全浏览器", @"%LOCALAPPDATA%\360se6\Application" },
            new string[] { "Brave", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application" },
            new string[] { "Vivaldi", @"%LOCALAPPDATA%\Vivaldi\Application" },
            new string[] { "搜狗浏览器", @"%LOCALAPPDATA%\SogouExplorer\Application" },
            new string[] { "Yandex", @"%LOCALAPPDATA%\Yandex\YandexBrowser\Application" },
            new string[] { "Opera", @"%LOCALAPPDATA%\Programs\Opera" }
        };

        private static readonly Regex VerRe = new Regex(@"^(\d+\.){1,3}\d+$", RegexOptions.Compiled);

        private static void ScanBrowserVersions(List<Hit> hits, Owners owners, HashSet<string> seen)
        {
            List<string[]> roots = new List<string[]>();
            roots.AddRange(AppRoots);
            /* 不认识的浏览器/Chromium 应用也扫一遍：LAB 下任意 Application 目录 */
            foreach (string found in FindApplicationDirs())
            {
                bool dup = false;
                for (int i = 0; i < roots.Count; i++)
                    if (string.Equals(Environment.ExpandEnvironmentVariables(roots[i][1]), found, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) roots.Add(new string[] { "", found });
            }

            foreach (string[] r in roots)
            {
                if (hits.Count > 160) return;
                string dir = Environment.ExpandEnvironmentVariables(r[1]);
                if (!Directory.Exists(dir)) continue;

                List<string> vers = new List<string>();
                try
                {
                    foreach (string d in Directory.GetDirectories(dir))
                        if (VerRe.IsMatch(Path.GetFileName(d))) vers.Add(d);
                }
                catch { continue; }
                if (vers.Count < 2) continue;

                string newest = vers[0];
                for (int i = 1; i < vers.Count; i++)
                    if (VerCmp(Path.GetFileName(vers[i]), Path.GetFileName(newest)) > 0) newest = vers[i];

                string appName = r[0];
                if (appName.Length == 0) appName = Path.GetFileName(Directory.GetParent(dir).FullName);
                string who = owners.Who(dir);
                if (who.Length > 0) appName = who;

                foreach (string v in vers)
                {
                    if (string.Equals(v, newest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Contains(v)) continue;
                    seen.Add(v);

                    long size = 0; int files = 0;
                    Size(v, ref size, ref files, 0);

                    Hit h = new Hit();
                    h.Id = "d:" + v;
                    h.App = appName;
                    h.Kind = "browser";
                    h.Name = Path.GetFileName(v);
                    h.Path = v;
                    h.Bytes = size;
                    h.Files = files;
                    h.Ver = Path.GetFileName(v);
                    h.Latest = Path.GetFileName(newest);
                    h.Detail = "建议删除：这是 " + appName + " 升级前的旧版本目录，当前在用的是 " + Path.GetFileName(newest) + "；删掉不影响浏览器，书签密码都在数据目录里";
                    h.Status = "建议删除";
                    hits.Add(h);
                }
            }
        }

        /// <summary>在 %LOCALAPPDATA%、Programs、Program Files 下两三层内找名叫 Application 的目录。</summary>
        private static List<string> FindApplicationDirs()
        {
            List<string> res = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            WalkForApp(local, res, 0);
            WalkForApp(Path.Combine(local, "Programs"), res, 0);
            WalkForApp(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), res, 0);
            WalkForApp(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), res, 0);
            return res;
        }

        private static void WalkForApp(string dir, List<string> res, int depth)
        {
            if (depth > 2 || res.Count > 40) return;
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string leaf = Path.GetFileName(d);
                    if (string.Equals(leaf, "Application", StringComparison.OrdinalIgnoreCase)) { res.Add(d); continue; }
                    if (leaf.StartsWith("Cache", StringComparison.OrdinalIgnoreCase) || leaf == "Temp"
                        || leaf == "Crashpad" || leaf == "Packages" || leaf == "Windows") continue;
                    WalkForApp(d, res, depth + 1);
                }
            }
            catch { }
        }

        /* ---------- 2. Electron / Squirrel 的 app-<版本> 目录 ---------- */

        private static void ScanElectronVersions(List<Hit> hits, Owners owners, HashSet<string> seen)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> parents = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(local)) parents.Add(d);
                foreach (string d in Directory.GetDirectories(Path.Combine(local, "Programs"))) parents.Add(d);
            }
            catch { }

            foreach (string p in parents)
            {
                if (hits.Count > 180) return;
                List<string> apps = new List<string>();
                try
                {
                    foreach (string d in Directory.GetDirectories(p))
                    {
                        string leaf = Path.GetFileName(d);
                        if (leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) apps.Add(d);
                    }
                    /* 也看 resources\app-<版本> 这种布局 */
                    string res = Path.Combine(p, "resources");
                    if (Directory.Exists(res))
                        foreach (string d in Directory.GetDirectories(res))
                        {
                            string leaf = Path.GetFileName(d);
                            if (leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) apps.Add(d);
                        }
                }
                catch { continue; }
                if (apps.Count < 2) continue;

                string newest = apps[0];
                for (int i = 1; i < apps.Count; i++)
                    if (VerCmp(Path.GetFileName(apps[i]).Substring(4), Path.GetFileName(newest).Substring(4)) > 0) newest = apps[i];

                string appName = Path.GetFileName(p);
                string who = owners.Who(p);
                if (who.Length > 0) appName = who;

                foreach (string a in apps)
                {
                    if (string.Equals(a, newest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Contains(a)) continue;
                    seen.Add(a);
                    long size = 0; int files = 0;
                    Size(a, ref size, ref files, 0);

                    Hit h = new Hit();
                    h.Id = "d:" + a;
                    h.App = appName;
                    h.Kind = "app";
                    h.Name = Path.GetFileName(a);
                    h.Path = a;
                    h.Bytes = size;
                    h.Files = files;
                    h.Ver = Path.GetFileName(a).Substring(4);
                    h.Latest = Path.GetFileName(newest).Substring(4);
                    h.Status = "建议删除";
                    h.Detail = "建议删除：" + appName + " 更新后留下的旧程序目录，现在跑的是 " + h.Latest + " 版";
                    hits.Add(h);
                }
            }
        }

        /* ---------- 3. 已装软件目录里的更新痕迹 ---------- */

        private static readonly string[] UpdateDirs = new string[]
        {
            "update", "updates", "updater", "Updater", "Update", "Updates",
            "old", "oldversion", "backup", "backups", "prev", "previous", ".old", ".bak"
        };

        private static void ScanInstalledUpdates(List<Hit> hits, Owners owners, HashSet<string> seen)
        {
            List<string> locs = new List<string>();
            foreach (Owner ow in owners.List)
            {
                string loc = ow.Loc;
                if (loc.Length < 8) continue;
                loc = Environment.ExpandEnvironmentVariables(loc.Trim('"'));
                try { if (Directory.Exists(loc)) locs.Add(loc); }
                catch { }
                if (locs.Count > 60) break;
            }

            foreach (string loc in locs)
            {
                if (hits.Count > 200) return;
                List<string> kids;
                try { kids = new List<string>(Directory.GetDirectories(loc)); }
                catch { continue; }
                foreach (string d in kids)
                {
                    string leaf = Path.GetFileName(d).ToLowerInvariant();
                    bool hit = false;
                    for (int i = 0; i < UpdateDirs.Length; i++)
                        if (leaf == UpdateDirs[i].ToLowerInvariant()) { hit = true; break; }
                    if (!hit) continue;
                    if (seen.Contains(d)) continue;

                    long size = 0; int files = 0;
                    Size(d, ref size, ref files, 0);
                    if (size < 1024 * 512) continue;     /* 半兆以下不值得列 */

                    seen.Add(d);
                    Hit h = new Hit();
                    h.Id = "d:" + d;
                    h.App = owners.Who(loc);
                    if (h.App.Length == 0) h.App = Path.GetFileName(loc);
                    h.Kind = "update";
                    h.Name = Path.GetFileName(d);
                    h.Path = d;
                    h.Bytes = size;
                    h.Files = files;
                    /* 备份 / 回滚用的目录是软件自己的数据，删了可能丢退回版本；只列不删 */
                    bool keep = (leaf == "backup" || leaf == "backups" || leaf == "prev"
                                 || leaf == "previous" || leaf == ".bak");
                    if (keep)
                    {
                        h.Status = "不建议删除";
                        h.Force = true;
                        h.Detail = "不建议删除：" + h.App + " 自己的备份目录，删掉可能丢掉可回滚的快照；空间够就留着（真要删可勾选强制删除）";
                        h.Ev = "E22";
                    }
                    else
                    {
                        h.Status = "建议删除";
                        h.Detail = "建议删除：" + h.App + " 升级时留下的旧版本 / 更新临时目录，当前版本用不到它，删掉不影响软件运行";
                        h.Ev = "E36";
                    }
                    hits.Add(h);
                }
            }
        }

        /* ---------- 4. 安装包与更新缓存 ---------- */

        private static void ScanCaches(List<Hit> hits, Owners owners, HashSet<string> seen)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            AddCache(hits, seen, owners, "更新临时目录", Path.Combine(local, "SquirrelTemp"), "temp",
                "建议删除：Electron 应用更新时解包的临时目录，装完就没用了", "E25");

            AddCache(hits, seen, owners, "下载的安装包", Path.Combine(local, "Downloaded Installations"), "installer",
                "建议删除：以前下载的安装包缓存，重新装软件时会再从官网下", "E24");

            AddCache(hits, seen, owners, "安装包缓存", Path.Combine(local, "Package Cache"), "installer",
                "建议删除：当前用户的安装器缓存，删掉后修复安装需重新联网下载，已装软件照常运行", "E24");

            /* 全机的 Package Cache 按微软口径不能当垃圾清：删了会影响修复 / 卸载 / 重装 */
            AddCache(hits, seen, owners, "安装包缓存（全机）", Path.Combine(common, "Package Cache"), "installer",
                "不建议删除：微软说明包缓存是修复、修改、卸载时的安装包来源，删了可能无法修复或卸载相关程序；要省空间请用安装器的 --nocache", "E23");

            AddCache(hits, seen, owners, "错误报告", Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"), "cache",
                "建议删除：程序崩溃后攒下的报告存档，只占地方", "E26");

            AddCache(hits, seen, owners, "错误报告（待发送）", Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"), "cache",
                "建议删除：还没发出去的崩溃报告队列", "E26");

            AddCache(hits, seen, owners, "Windows 更新缓存", Path.Combine(win, @"SoftwareDistribution\Download"), "cache",
                "建议删除：Windows 更新下载的安装包，删前会先停更新服务，删完自动恢复", "E02", true);

            /* Temp 里下了很久的安装包 */
            string temp = Path.GetTempPath();
            int listed = 0;
            try
            {
                foreach (string f in Directory.GetFiles(temp))
                {
                    if (listed >= 12) break;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".msi" && ext != ".exe" && ext != ".zip" && ext != ".msix" && ext != ".cab") continue;
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length < 20L * 1024 * 1024) continue;
                    if ((DateTime.Now - fi.LastWriteTime).TotalDays < 7) continue;
                    if (seen.Contains(f)) continue;
                    seen.Add(f);
                    Hit h = new Hit();
                    h.Id = "d:" + f;
                    h.App = "";
                    h.Kind = "installer";
                    h.Name = fi.Name;
                    h.Path = f;
                    h.Bytes = fi.Length;
                    h.Files = 1;
                    h.Status = "建议删除";
                    h.Detail = "建议删除：临时目录里躺了 " + ((int)((DateTime.Now - fi.LastWriteTime).TotalDays)) + " 天的安装包";
                    hits.Add(h);
                    listed++;
                }
            }
            catch { }
        }

        private static void AddCache(List<Hit> hits, HashSet<string> seen, Owners owners, string label, string dir,
            string kind, string detail, string ev)
        {
            AddCache(hits, seen, owners, label, dir, kind, detail, ev, false);
        }

        private static void AddCache(List<Hit> hits, HashSet<string> seen, Owners owners, string label, string dir,
            string kind, string detail, string ev, bool needService)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if (seen.Contains(dir)) return;
                long size = 0; int files = 0;
                Size(dir, ref size, ref files, 0);
                if (size < 1024 * 1024) return;      /* 不到 1MB 不列，免得刷屏 */
                seen.Add(dir);

                Hit h = new Hit();
                h.Id = "d:" + dir;
                h.App = label;
                h.Kind = kind;
                h.Name = label;
                h.Path = dir;
                h.Bytes = size;
                h.Files = files;
                h.Ver = "";
                h.Latest = "";
                h.Status = detail.StartsWith("建议删除") ? "建议删除" : "不建议删除";
                h.Force = h.Status == "不建议删除";
                h.Detail = detail;
                h.Ev = ev;
                h.NeedService = needService;
                hits.Add(h);
            }
            catch { }
        }

        /* ==================== 执行 ==================== */

        public static string Apply(List<string> paths, Action<string> log)
        {
            int ok = 0, fail = 0;
            long freed = 0;
            StringBuilder detail = new StringBuilder();
            List<string> lines = new List<string>();

            foreach (string raw in paths)
            {
                string id = raw;
                if (id.StartsWith("d:", StringComparison.Ordinal)) id = id.Substring(2);
                bool needService = false;
                /* 从 id 里拆不出需求标记，靠路径判断 */
                string low = id.ToLowerInvariant();
                if (low.IndexOf(@"softwaredistribution\download", StringComparison.Ordinal) >= 0) needService = true;

                int sc = -1;
                if (needService)
                {
                    sc = Maintenance.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
                        "stop wuauserv", 30000);
                }
                try
                {
                    long size = 0; int files = 0;
                    Size(id, ref size, ref files, 0);
                    lines.Add("DIR\t" + id + "\t" + size + "\t" + files);

                    bool deleted = DeletePath(id);
                    if (deleted)
                    {
                        ok++;
                        freed += size;
                        string msg = "已删除：" + id + "（" + Fmt(size) + "）";
                        if (log != null) log(msg);
                        detail.Append("✓ ").Append(msg).Append('\n');
                    }
                    else
                    {
                        fail++;
                        detail.Append("× ").Append(id).Append("：被占用或没有权限").Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    detail.Append("× ").Append(id).Append("：").Append(ex.Message).Append('\n');
                }
                finally
                {
                    if (needService)
                        Maintenance.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe"),
                            "start wuauserv", 30000);
                }
            }

            string bk = "";
            if (lines.Count > 0)
            {
                bk = Maintenance.BackupFile("upd");
                try { File.WriteAllLines(bk, lines, Encoding.UTF8); } catch { bk = ""; }
            }

            return new JB()
                .Add("type", "updResult")
                .Add("ok", ok)
                .Add("fail", fail)
                .Add("bytes", freed)
                .Add("backup", bk)
                .Add("detail", detail.ToString())
                .End();
        }

        private static bool DeletePath(string path)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return true;
                if (File.Exists(path))
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                    File.Delete(path);
                    return true;
                }
                ClearReadOnly(path, 0);
                Directory.Delete(path, true);
                return true;
            }
            catch
            {
                /* 一次清不掉时，把里面的只读挨个去掉再来一遍 */
                try
                {
                    ClearReadOnly(path, 0);
                    Directory.Delete(path, true);
                    return true;
                }
                catch { return false; }
            }
        }

        private static void ClearReadOnly(string dir, int depth)
        {
            if (depth > 6) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    try
                    {
                        FileAttributes a = File.GetAttributes(f);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    ClearReadOnly(d, depth + 1);
                    try
                    {
                        FileAttributes a = File.GetAttributes(d);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(d, a & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /* ==================== 工具 ==================== */

        public static void Size(string dir, ref long bytes, ref int files, int depth)
        {
            if (depth > 6 || files > 40000) return;
            try
            {
                if (File.Exists(dir))
                {
                    bytes += new FileInfo(dir).Length;
                    files++;
                    return;
                }
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (files > 40000) return;
                    try { bytes += new FileInfo(f).Length; files++; }
                    catch { }
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (files > 40000) return;
                    FileAttributes a;
                    try { a = File.GetAttributes(d); } catch { continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    Size(d, ref bytes, ref files, depth + 1);
                }
            }
            catch { }
        }

        private static long[] Vers(string s)
        {
            string[] p = s.Split('.');
            long[] v = new long[4];
            for (int i = 0; i < 4 && i < p.Length; i++)
            {
                long x = 0;
                long.TryParse(p[i], out x);
                v[i] = x;
            }
            return v;
        }

        public static int VerCmp(string a, string b)
        {
            long[] x = Vers(a), y = Vers(b);
            for (int i = 0; i < 4; i++)
            {
                if (x[i] > y[i]) return 1;
                if (x[i] < y[i]) return -1;
            }
            return 0;
        }

        private static string Fmt(long b)
        {
            if (b >= 1073741824L) return (b / 1073741824.0).ToString("0.00") + " GB";
            if (b >= 1048576L) return (b / 1048576.0).ToString("0.0") + " MB";
            if (b >= 1024L) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }
    }
}
