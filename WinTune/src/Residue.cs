using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinTune
{
    public class Occ
    {
        public string Kind = "";    // service / process / holder / task
        public string Name = "";
        public string Detail = "";
        public string Svc = "";     // holder 场景下：持有句柄的服务短名
        public int Pid = 0;
    }

    public class ResidueHit
    {
        public string Id = "";
        public string Brand = "";
        public string Path = "";
        public long Bytes = 0;
        public int Files = 0;
        public bool InUse = false;
        public string Evidence = "";
        public string Note = "";
        public string Created = "";
        public string Level = "strong";     // strong = 安装目录残留；weak = 共享数据目录
        public string Label = "";           // 列表里显示的名称（浏览器插件用插件名，不用目录 ID）
        public List<Occ> Occs = new List<Occ>();
    }

    /// <summary>
    /// 软件残留 = 安装位置里存在、但"已装软件清单"里找不到的目录。
    /// 判定顺序：
    ///   ① 先读注册表卸载项，得到"这台机器现在装了哪些软件、装在哪"
    ///   ② 再扫常见安装位置（Program Files / Program Files (x86) / ProgramData / 用户 Programs）
    ///   ③ 任何与已装清单对不上的目录 → 疑似已卸载软件残留，列出来给用户选
    /// 删除前会停掉指向该目录的服务 / 进程 / 计划任务，再删文件。
    /// </summary>
    public static class Residue
    {
        /* ==================== 公共工具 ==================== */

        public static string NormPath(string p)
        {
            if (p == null) return "";
            string s = p.Replace('/', '\\').Trim().Trim('"').TrimEnd('\\');
            return s.ToUpperInvariant();
        }

        public static string NormName(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string PathName(string p)
        {
            string t = (p == null ? "" : p.TrimEnd('\\'));
            int i = t.LastIndexOf('\\');
            return i >= 0 ? t.Substring(i + 1) : t;
        }

        /* 目录里常见的数据 / 缓存 / 子模块名：遇到这些词不把它当成"软件名"，
           品牌归到上一层，保证同一个软件的多个目录合成一条。 */
        private static readonly string[] GenericSegs = new string[]
        {
            "application","applications","userdata","data","cache","codecache","gpucache","shadercache",
            "grshadercache","partitions","toolchain","builds","depots","node_modules","webview2","hotupdate",
            "pwa","locales","resources","resource","language","settings","logs","log","config","temp",
            "plugins","documents","desktopeditors","installer","update","updater","bin","lib","libs",
            "assets","defaults","extensions","storage","profile","profiles","session","sessions",
            "crashpad","crashdumps","report","reports","scripts","themes","skins","fonts","media",
            "cef","amt","required","plug-ins","sample_files","icons","legal","ruby","www","setup",
            "common","programs","program","appdata"
        };

        /* 厂商容器目录：这层下面往往并排放着好几个软件，要多取一级才分得清 */
        private static readonly string[] VendorSegs = new string[]
        {
            "tencent","google","microsoft","adobe","apple","intel","nvidia","amd","realtek","logi",
            "logitech","msi","asus","gigabyte","dell","lenovo","samsung","oray","baidu","alibaba",
            "huawei","xiaomi","sony","dts","conexant","broadcom","qualcomm","autodesk","corel",
            "jetbrains","mozilla","cherrystudio","thunder network","iqiyi video"
        };

        private static bool InList(string[] list, string s)
        {
            string n = NormName(s);
            if (n.Length == 0) return false;
            for (int i = 0; i < list.Length; i++) if (list[i] == n) return true;
            return false;
        }

        private static string VendorCn(string s)
        {
            string n = NormName(s);
            if (n == "tencent") return "腾讯";
            if (n == "microsoft") return "微软";
            if (n == "google") return "谷歌";
            if (n == "baidu") return "百度";
            if (n == "alibaba") return "阿里";
            if (n == "huawei") return "华为";
            if (n == "xiaomi") return "小米";
            if (n == "mozilla") return "Mozilla";
            return "";
        }

        /// <summary>品牌名 = 软件名（不是整条路径）。同一个软件的多个目录归到同一个品牌。</summary>
        private static string BrandName(string path)
        {
            string p = path.TrimEnd('\\');
            string rel = "";
            int best = -1;
            foreach (string[] r in ScanRoots())
            {
                string root = r[0].TrimEnd('\\');
                if (p.Length > root.Length + 1 && p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.Length > best) { best = root.Length; rel = p.Substring(root.Length + 1); }
                }
            }
            if (rel.Length == 0) return PathName(p);

            string[] segs = rel.Split('\\');
            string s1 = segs.Length > 0 ? segs[0] : "";
            string s2 = segs.Length > 1 ? segs[1] : "";
            if (s1.Length == 0) return PathName(p);

            string key = s1;
            if (s2.Length > 0 && !InList(GenericSegs, s2) && InList(VendorSegs, s1)) key = s1 + " " + s2;

            /* 同一个产品常用几套目录名（点号包名、驼峰名、子模块名），先按整条相对路径归一，
               避免同一个软件在列表里拆成好几组 */
            string nrel = NormName(rel.Replace('\\', ' '));
            if (nrel.IndexOf("awesun", StringComparison.Ordinal) >= 0) return "向日葵";
            if (nrel.IndexOf("apex", StringComparison.Ordinal) >= 0) return "Apex 加速器";
            if (nrel.IndexOf("qmlauncher", StringComparison.Ordinal) >= 0) return "QQ 音乐";
            if (nrel == "uv" || nrel.StartsWith("uvcache", StringComparison.Ordinal) ||
                nrel.StartsWith("uvwebview2", StringComparison.Ordinal) || nrel == "uvtool") return "uv 包管理器";
            if (nrel.IndexOf("driverutility", StringComparison.Ordinal) >= 0) return "MSI 驱动工具";
            if (nrel.IndexOf("mayfly", StringComparison.Ordinal) >= 0) return "Mayfly 模组管理器";
            if (nrel.IndexOf("aulahub", StringComparison.Ordinal) >= 0) return "AULA 狼蛛";
            if (nrel.IndexOf("androws", StringComparison.Ordinal) >= 0) return "腾讯应用宝";

            /* 第一层是 Programs / Common 这类通用目录时，往下取第一个真正的软件目录名 */
            if (InList(GenericSegs, s1))
            {
                for (int i = 1; i < segs.Length; i++)
                {
                    if (segs[i].Length == 0 || InList(GenericSegs, segs[i])) continue;
                    key = segs[i];
                    break;
                }
            }

            /* 别名表反查：磁盘上的英文目录名换成中文软件名 */
            string k = NormName(key);
            foreach (string[] a in AliasPairs)
            {
                string cn = a[0];
                string ncn = NormName(cn);
                if (ncn.Length >= 3 && k.IndexOf(ncn, StringComparison.Ordinal) >= 0) return cn;
                for (int i = 1; i < a.Length; i++)
                {
                    string al = NormName(a[i]);
                    if (al.Length == 0) continue;
                    if (al.Length >= 4)
                    {
                        if (k.IndexOf(al, StringComparison.Ordinal) >= 0) return cn;
                    }
                    else if (k == al || k.StartsWith(al + " ", StringComparison.Ordinal)) return cn;
                }
            }

            string vendor = VendorCn(s1);
            if (vendor.Length > 0) return key.Length > s1.Length ? vendor + key.Substring(s1.Length) : vendor;
            return key;
        }

        private static string ShortId(string path)
        {
            try
            {
                using (MD5 md5 = MD5.Create())
                {
                    byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(NormPath(path)));
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "x" + Math.Abs(NormPath(path).GetHashCode()).ToString(); }
        }

        private static string CreatedText(string path)
        {
            try { return Directory.GetCreationTime(path).ToString("yyyy-MM-dd"); }
            catch { return ""; }
        }

        /* ==================== 已装软件清单 ==================== */

        /* 中文软件名 → 磁盘上的目录名 / 进程名。很多软件清单里只有中文名，
           目录却是英文，靠这张表才认得出"装着的软件的数据目录"。
           每条：{ 清单里的关键字, 目录别名1[, 目录别名2] } */
        private static readonly string[][] AliasPairs = new string[][]
        {
            new string[] { "豆包", "doubao" },
            new string[] { "夸克", "quark" },
            new string[] { "微信输入法", "wetype", "wechatinput" },
            new string[] { "微信", "wechat", "weixin" },
            new string[] { "企业微信", "wxwork", "wecom" },
            new string[] { "抖音", "douyin" },
            new string[] { "爱奇艺", "iqiyi" },
            new string[] { "百度网盘", "baidunetdisk" },
            new string[] { "同花顺", "hexin", "ths" },
            new string[] { "雷电模拟器", "ldplayer", "leidian" },
            new string[] { "迅雷", "thunder", "xunlei" },
            new string[] { "腾讯会议", "wemeet" },
            new string[] { "钉钉", "dingtalk" },
            new string[] { "网易云音乐", "cloudmusic", "netease" },
            new string[] { "QQ音乐", "qqmusic" },
            new string[] { "酷狗", "kugou" },
            new string[] { "优酷", "youku" },
            new string[] { "腾讯视频", "qqlive" },
            new string[] { "哔哩哔哩", "bilibili" },
            new string[] { "WPS", "kingsoft", "wps" },
            new string[] { "搜狗输入法", "sogouinput", "sogou" },
            new string[] { "向日葵", "sunlogin", "oray" },
            new string[] { "火绒", "huorong" },
            new string[] { "驱动精灵", "drivergenius" },
            new string[] { "鲁大师", "ludashi" },
            new string[] { "360安全卫士", "360safe" },
            new string[] { "360极速浏览器", "360chrome" },
            new string[] { "2345", "2345" },
            new string[] { "飞书", "feishu", "lark" },
            new string[] { "剪映", "jianying" },
            new string[] { "美图秀秀", "xiuxiu" },
            new string[] { "有道", "youdao" },
            new string[] { "腾讯文档", "tencentdocs" },
            new string[] { "罗技", "logitech", "lghub", "logi" },
            new string[] { "火狐", "firefox" },
            new string[] { "QQ电脑管家", "qqpcmgr" },
            new string[] { "腾讯游戏助手", "gameassist" },
            new string[] { "雷蛇", "razer" },
            new string[] { "MuMu模拟器", "mumu" },
            new string[] { "夜神模拟器", "nox" },
            new string[] { "Node.js", "nodejs", "npm" },
            new string[] { "光速虚拟机", "vmware" },
        };

        private static string[] AliasOf(string name)
        {
            List<string> r = new List<string>();
            string n = NormName(name);
            if (n.Length == 0) return r.ToArray();
            foreach (string[] p in AliasPairs)
            {
                string key = NormName(p[0]);
                if (key.Length == 0) continue;
                if (n.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                for (int i = 1; i < p.Length; i++)
                {
                    string v = NormName(p[i]);
                    if (v.Length > 0 && !r.Contains(v)) r.Add(v);
                }
            }
            return r.ToArray();
        }

        private class InstalledSet
        {
            public HashSet<string> Dirs = new HashSet<string>();
            public List<string> Names = new List<string>();
            public List<string> NamesRaw = new List<string>();
            public List<string> Pubs = new List<string>();          // 发布者（归一化）
            public List<string> PubsRaw = new List<string>();       // 发布者（原样，展示用）

            public int DirCount { get { return this.Dirs.Count; } }
            public int NameCount { get { return this.Names.Count; } }

            public void AddDir(string p)
            {
                string n = NormPath(p);
                if (n.Length < 6) return;
                // 至少形如 C:\xxx，避免把 C:\ 之类当地址
                if (n.IndexOf(":\\", StringComparison.Ordinal) != 1) return;
                if (!this.Dirs.Contains(n)) this.Dirs.Add(n);
            }

            public void AddName(string s)
            {
                string n = NormName(s);
                if (n.Length < 3) return;
                if (!this.Names.Contains(n)) { this.Names.Add(n); this.NamesRaw.Add(s); }
            }

            public void AddPub(string s)
            {
                string n = NormName(s);
                if (n.Length < 3) return;
                if (!this.Pubs.Contains(n)) { this.Pubs.Add(n); this.PubsRaw.Add(s); }
            }

            public static InstalledSet Build()
            {
                InstalledSet s = new InstalledSet();
                try
                {
                    List<Apps.App> apps = Apps.List();
                    foreach (Apps.App a in apps)
                    {
                        if (a.Loc != null && a.Loc.Length > 0) s.AddDir(a.Loc);
                        if (a.Uninstall != null && a.Uninstall.Length > 0)
                        {
                            string exe = RegistryScan.FirstToken(a.Uninstall);
                            if (exe.Length > 0)
                            {
                                string d = "";
                                try { d = Path.GetDirectoryName(exe); } catch { }
                                if (d != null && d.Length > 0) s.AddDir(d);
                            }
                        }
                        if (a.Name != null && a.Name.Length > 0) s.AddName(a.Name);
                        if (a.Pub != null && a.Pub.Length > 0) s.AddPub(a.Pub);
                        /* 中文名软件补上英文目录别名，避免把它在用的数据目录当成残留 */
                        string[] al = AliasOf(a.Name == null ? "" : a.Name);
                        for (int k = 0; k < al.Length; k++) s.AddName(al[k]);
                    }
                    Log.Info("residue", "已装清单：目录 " + s.DirCount + " 个 / 名称 " + s.NameCount + " 个 / 发布者 " + s.Pubs.Count + " 个");

                    /* 正在运行的进程：它们的目录一定在用，不能被当成残留 */
                    try
                    {
                        foreach (Process p in Process.GetProcesses())
                        {
                            try
                            {
                                string f = p.MainModule.FileName;
                                if (f == null || f.Length == 0) continue;
                                string d = Path.GetDirectoryName(f);
                                if (d != null && d.Length > 0) s.AddDir(d);
                            }
                            catch { }
                        }
                    }
                    catch { }

                    /* 服务与 App Paths 指向的目录，也算在用 */
                    try
                    {
                        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                        {
                            if (k != null)
                            {
                                foreach (string svn in k.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (RegistryKey sk = k.OpenSubKey(svn))
                                        {
                                            if (sk == null) continue;
                                            object img = sk.GetValue("ImagePath");
                                            if (img == null) continue;
                                            string exe = ExeFromImage(img.ToString());
                                            if (exe.Length == 0) continue;
                                            string d = Path.GetDirectoryName(exe);
                                            if (d != null) s.AddDir(d);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        string[] apRoots = new string[]
                        {
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
                        };
                        foreach (string ap in apRoots)
                        {
                            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ap))
                            {
                                if (k == null) continue;
                                foreach (string nm in k.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (RegistryKey ek = k.OpenSubKey(nm))
                                        {
                                            if (ek == null) continue;
                                            object v = ek.GetValue("");
                                            if (v == null) continue;
                                            string exe = RegistryScan.FirstToken(v.ToString());
                                            if (exe.Length == 0) continue;
                                            string d = Path.GetDirectoryName(exe);
                                            if (d != null) s.AddDir(d);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }

                    /* 浏览器：http/https 处理程序所在目录 + 程序名，也进已装清单 */
                    try
                    {
                        string[] uaKeys = new string[]
                        {
                            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice",
                            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"
                        };
                        foreach (string keyPath in uaKeys)
                        {
                            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(keyPath))
                            {
                                if (k == null) continue;
                                object prog = k.GetValue("ProgId");
                                if (prog == null) continue;
                                string progId = prog.ToString();
                                if (progId.Length == 0) continue;
                                string cmd = "";
                                using (RegistryKey ck = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                                {
                                    if (ck != null)
                                    {
                                        object v = ck.GetValue("");
                                        if (v != null) cmd = v.ToString();
                                    }
                                }
                                if (cmd.Length == 0) continue;
                                string exe = RegistryScan.FirstToken(cmd);
                                if (exe.Length == 0) continue;
                                string d = Path.GetDirectoryName(exe);
                                if (d != null) s.AddDir(d);
                            }
                        }
                    }
                    catch { }
                    Log.Info("residue", "扩展后已装清单：目录 " + s.DirCount + " 个 / 名称 " + s.NameCount + " 个");
                }
                catch (Exception ex) { Log.Warn("residue", "读取已装清单失败：" + ex.Message); }
                return s;
            }

            /// <summary>目录名与已装软件名/发布者宽松匹配（含包含关系），用于判断"同厂商目录"。</summary>
            public string LooseMatch(string name)
            {
                string n = NormName(name);
                if (n.Length < 3) return "";
                for (int i = 0; i < this.Names.Count; i++)
                {
                    string s = this.Names[i];
                    if (s == n) return this.NamesRaw[i];
                    if (InstalledSet.PrefixRelated(s, n)) return this.NamesRaw[i];
                }
                for (int i = 0; i < this.Pubs.Count; i++)
                {
                    string p = this.Pubs[i];
                    if (p == n) return this.PubsRaw[i];
                    if (InstalledSet.PrefixRelated(p, n)) return this.PubsRaw[i];
                }
                return "";
            }

            /// <summary>目录里 exe 的公司名是否属于某个已装软件的发布者。返回命中的发布者。</summary>
            public string VendorMatch(string company)
            {
                string c = NormName(company);
                if (c.Length < 3) return "";
                for (int i = 0; i < this.Pubs.Count; i++)
                {
                    string p = this.Pubs[i];
                    if (c == p) return this.PubsRaw[i];
                    if (p.Length >= 4 && c.Length >= 4 &&
                        (p.IndexOf(c, StringComparison.Ordinal) >= 0 || c.IndexOf(p, StringComparison.Ordinal) >= 0))
                        return this.PubsRaw[i];
                }
                return "";
            }

            /// <summary>路径本身就是某个已装软件的安装目录。</summary>
            public bool Exact(string path)
            {
                string d = NormPath(path);
                if (d.Length == 0) return false;
                return this.Dirs.Contains(d);
            }

            /// <summary>路径是某个已装软件安装目录的子目录（父链命中）。</summary>
            public bool Under(string path)
            {
                string d = NormPath(path);
                if (d.Length == 0) return false;
                foreach (string t in this.Dirs)
                {
                    if (t.Length > d.Length && t.StartsWith(d + "\\", StringComparison.Ordinal)) return true;
                }
                return false;
            }

            /// <summary>目录名 / 目录里的 exe 名，与已装软件名对得上。</summary>
            public bool NameMatch(string path, bool allowPartial)
            {
                string leaf = NormName(PathName(path));
                if (leaf.Length >= 4)
                {
                    foreach (string n in this.Names)
                    {
                        if (n == leaf) return true;
                        if (!allowPartial) continue;
                        if (Related(n, leaf)) return true;
                    }
                }
                if (!allowPartial) return false;

                foreach (string exe in TopExes(path))
                {
                    string e = NormName(exe);
                    if (e.Length < 4) continue;
                    if (e == "unins000" || e == "uninst" || e == "setup" || e == "update" || e == "install") continue;
                    foreach (string n in this.Names)
                    {
                        if (n == e) return true;
                        if (Related(n, e)) return true;
                    }
                }
                return false;
            }

            /// <summary>名称相关：相等、互为前缀，或一方包含另一方（长度都≥4）。</summary>
            private static bool Related(string a, string b)
            {
                if (a == b) return true;
                if (a.Length < 4 || b.Length < 4) return false;
                if (PrefixRelated(a, b)) return true;
                if (a.IndexOf(b, StringComparison.Ordinal) >= 0) return true;
                if (b.IndexOf(a, StringComparison.Ordinal) >= 0) return true;
                return false;
            }

            /// <summary>两个归一化名称是否是"前缀关系"（短的那个从长的那头开始），避免 "msi" 误命中 "systemsia"。</summary>
            private static bool PrefixRelated(string a, string b)
            {
                if (a.Length == b.Length) return a == b;
                string s = a.Length < b.Length ? a : b;
                string l = a.Length < b.Length ? b : a;
                if (s.Length < 4) return false;
                return l.StartsWith(s, StringComparison.Ordinal);
            }
        }

        private static List<string> TopExes(string dir)
        {
            List<string> list = new List<string>();
            try
            {
                string[] files = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                int n = 0;
                foreach (string f in files)
                {
                    if (n++ >= 24) break;
                    try { list.Add(Path.GetFileNameWithoutExtension(f)); } catch { }
                }
            }
            catch { }
            return list;
        }

        private static List<string> TopExeFiles(string dir)
        {
            List<string> exes = new List<string>();
            CollectByExt(dir, 0, ".exe", exes, 24);
            if (exes.Count == 0) CollectByExt(dir, 0, ".dll", exes, 8);
            return exes;
        }

        private static void CollectByExt(string dir, int depth, string ext, List<string> list, int max)
        {
            if (list.Count >= max || depth > 3) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (list.Count >= max) return;
                    if (f.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)) list.Add(f);
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (list.Count >= max) return;
                    CollectByExt(d, depth + 1, ext, list, max);
                }
            }
            catch { }
        }

        /// <summary>目录里有没有独立卸载程序，有的话更像"装过又删掉的软件"。</summary>
        private static bool HasUninstaller(string dir)
        {
            foreach (string f in TopExeFiles(dir))
            {
                string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.StartsWith("uninst", StringComparison.Ordinal) ||
                    n.StartsWith("unins0", StringComparison.Ordinal) ||
                    n.StartsWith("uninstall", StringComparison.Ordinal) ||
                    n == "uninst" || n == "卸载")
                    return true;
            }
            return false;
        }

        private static List<string> VendorFiles(string dir)
        {
            List<string> list = new List<string>();
            CollectByExt(dir, 0, ".exe", list, 12);
            CollectByExt(dir, 0, ".dll", list, 24);
            return list;
        }

        private static bool IsGenericCompany(string c)
        {
            string n = NormName(c);
            if (n.Length < 4) return true;
            if (n.StartsWith("microsoft", StringComparison.Ordinal)) return true;
            if (n.StartsWith("windows", StringComparison.Ordinal)) return true;
            if (n.StartsWith("todo", StringComparison.Ordinal)) return true;
            if (n == "unknown" || n == "admin" || n == "administrator" || n == "company") return true;
            return false;
        }

        /// <summary>取目录里出现最多的公司名（忽略微软这类通用系统组件），作为"厂商"证据。</summary>
        private static string VendorOf(string dir)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string f in VendorFiles(dir))
            {
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(f);
                    string c = (vi.CompanyName == null ? "" : vi.CompanyName.Trim());
                    if (c.Length < 3) continue;
                    if (IsGenericCompany(c)) continue;
                    if (counts.ContainsKey(c)) counts[c] = counts[c] + 1;
                    else counts[c] = 1;
                }
                catch { }
            }
            string best = ""; int bestN = 0;
            foreach (KeyValuePair<string, int> kv in counts)
                if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
            return best;
        }

        /* ==================== 系统目录黑名单 ==================== */

        private static readonly string[] SystemNames = new string[]
        {
            "windows","windowsapps","microsoft","commonfiles","commonfilesx86","modifiablewindowsapps",
            "msbuild","dotnet","net","internetexplorer","windowsdefender",
            "windowsdefenderadvancedthreatprotection","windowsmail","windowsmediaplayer",
            "windowsmediaplayerlegacy","windowsnt","windowsphotoviewer","windowsportabledevices",
            "windowsjournal","windowssidebar","windowssecurity","windowsjournalreader",
            "referenceassemblies","uninstall","installshieldinstallationinformation",
            "system","system32","syswow64","winsxs","panther","prefetch","softwaredistribution",
            "servicing","temp","packages","packagecache","driverstore","drivers","driverdata",
            "nvidia","nvidia corporation".Replace(" ", ""),"amd","intel","realtek","microsoftnet",
            "microsoftsqlserver","msxml","windowsapps","windowskits","windowsperformancekit",
            "winevt","windowspowershell","windowsapps","appcompat","assembly","policydefinitions",
            "usoshared","usoprivate","windowsmultimediaplatform","windowsappsplatform",
            /* AppData 里的平台与缓存目录：不是软件残留 */
            "programs","crashdumps","d3dscache","connecteddevicesplatform","comms","gdk",
            "iconcache","elevateddiagnostics","virtualstore","temporaryinternetfiles",
            "microsoftedge","microsoftedgestore","microsoftonedrive","onedrive",
            "boostinterprocess","usoshared",
        };

        private static bool IsSystemName(string name)
        {
            string n = NormName(name);
            if (n.Length == 0) return true;
            foreach (string s in SystemNames) if (s == n) return true;
            return false;
        }

        /* ==================== 安装位置 ==================== */

        /* 0 = 程序目录（不在清单即可删）；1 = 共享数据（保守）；2 = 用户数据（在用也列出） */
        private static List<string[]> ScanRoots()
        {
            List<string[]> l = new List<string[]>();
            l.Add(new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "0" });
            l.Add(new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "0" });
            l.Add(new string[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1" });
            l.Add(new string[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "0" });
            /* 用户级安装与数据目录：浏览器、聊天工具、开发工具的残留大量在这里 */
            l.Add(new string[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "2" });
            l.Add(new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "2" });
            l.Add(new string[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"), "2" });

            /* 其它固定磁盘上的常见安装目录：软件装在 D:/E: 的，残留也要能扫到 */
            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (di.DriveType != DriveType.Fixed) continue;
                        string rt = di.Name;
                        if (rt.Length < 3) continue;
                        if (rt.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] cand = new string[]
                        {
                            Path.Combine(rt, "Program Files"),
                            Path.Combine(rt, "Program Files (x86)"),
                            Path.Combine(rt, "Programs"),
                            Path.Combine(rt, "Apps"),
                            Path.Combine(rt, "软件"),
                            Path.Combine(rt, "应用"),
                        };
                        foreach (string c in cand)
                            if (Directory.Exists(c)) l.Add(new string[] { c, "3" });
                    }
                    catch { }
                }
            }
            catch { }
            return l;
        }

        private static List<string> Dirs(string root)
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(root)) list.Add(d);
            }
            catch { }
            return list;
        }

        /* ==================== 占用索引 ==================== */

        private class OccIndex
        {
            public List<string[]> Services = new List<string[]>();  // name, exe
            public List<Occ> Procs = new List<Occ>();
        }

        private static OccIndex BuildIndex()
        {
            OccIndex idx = new OccIndex();

            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (root != null)
                    {
                        foreach (string name in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey k = root.OpenSubKey(name))
                                {
                                    if (k == null) continue;
                                    object img = k.GetValue("ImagePath");
                                    if (img == null) continue;
                                    string exe = ExeFromImage(img.ToString());
                                    if (exe.Length == 0) continue;
                                    idx.Services.Add(new string[] { name, exe });
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string f = p.MainModule.FileName;
                        if (f == null || f.Length == 0) continue;
                        if (f.IndexOf(":\\", StringComparison.Ordinal) != 1) continue;
                        Occ o = new Occ();
                        o.Kind = "process";
                        o.Pid = p.Id;
                        try { o.Name = p.ProcessName + ".exe (PID " + p.Id + ")"; }
                        catch { o.Name = "PID " + p.Id; }
                        o.Detail = f;
                        idx.Procs.Add(o);
                    }
                    catch { }
                }
            }
            catch { }

            return idx;
        }

        private static string ExeFromImage(string img)
        {
            string s = (img == null ? "" : img).Trim().Trim('"');
            if (s.Length == 0) return "";
            int idx = s.ToLowerInvariant().IndexOf(".exe");
            if (idx > 0) s = s.Substring(0, idx + 4).Trim('"');
            if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    s.Substring(@"\SystemRoot\".Length));
            else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), s);
            if (s.IndexOf(":\\", StringComparison.Ordinal) != 1) return "";
            return s;
        }

        private static bool UnderDir(string file, string dirNorm)
        {
            string f = NormPath(file);
            if (f.Length == 0 || dirNorm.Length == 0) return false;
            if (f == dirNorm) return true;
            return f.StartsWith(dirNorm + "\\", StringComparison.Ordinal);
        }

        /// <summary>找出指向该目录的服务、进程（可选计划任务）。</summary>
        public static List<Occ> FindOccupants(string dir) { return FindOccupants(dir, true); }

        public static List<Occ> FindOccupants(string dir, bool includeTasks)
        {
            List<Occ> list = new List<Occ>();
            string d = NormPath(dir);
            if (d.Length == 0) return list;

            OccIndex idx = BuildIndex();
            foreach (string[] s in idx.Services)
            {
                if (UnderDir(s[1], d))
                {
                    Occ o = new Occ();
                    o.Kind = "service";
                    o.Name = s[0];
                    o.Detail = s[1];
                    list.Add(o);
                }
            }
            foreach (Occ p in idx.Procs)
            {
                if (UnderDir(p.Detail, d))
                {
                    Occ o = new Occ();
                    o.Kind = "process";
                    o.Name = p.Name;
                    o.Detail = p.Detail;
                    o.Pid = p.Pid;
                    list.Add(o);
                }
            }

            if (includeTasks) list.AddRange(FindTasks(d));
            return list;
        }

        private static List<Occ> FindTasks(string dirNorm)
        {
            List<Occ> list = new List<Occ>();
            try
            {
                string tasksDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\Tasks");
                if (!Directory.Exists(tasksDir)) return list;
                string[] files = Directory.GetFiles(tasksDir, "*", SearchOption.AllDirectories);
                foreach (string f in files)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.Length > 512 * 1024) continue;
                        string text = ReadTextAny(f);
                        if (text == null || text.Length == 0) continue;
                        if (!ContainsPath(text, dirNorm)) continue;

                        string rel = f.Substring(tasksDir.Length).Replace('/', '\\');
                        if (!rel.StartsWith("\\")) rel = "\\" + rel;
                        if (!TaskExists(rel)) continue;   // 计划任务库里没有它，就是孤立定义文件，不算占用

                        Occ o = new Occ();
                        o.Kind = "task";
                        o.Name = rel;
                        o.Detail = "残留计划任务指向该目录，执行时一并删除";
                        list.Add(o);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>路径必须落在词边界，避免 "C:\Program" 命中 "C:\Program Files\..."。</summary>
        private static bool ContainsPath(string text, string dirNorm)
        {
            if (text == null || dirNorm == null) return false;
            if (dirNorm.Length < 10) return false;
            int i = 0;
            while ((i = text.IndexOf(dirNorm, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int e = i + dirNorm.Length;
                if (e >= text.Length) return true;
                char c = text[e];
                if (c == '\\' || c == '"' || c == '\'' || c == ' ' || c == '<' || c == '>' ||
                    c == '\r' || c == '\n' || c == '\t' || c == ',' || c == ';') return true;
                i = e;
            }
            return false;
        }

        /// <summary>任务是否真的在计划任务库里（孤立定义文件不算）。</summary>
        private static bool TaskExists(string rel)
        {
            int code = RunExit("schtasks.exe", "/query /tn \"" + rel + "\"");
            if (code < 0) return true;      // schtasks 不可用时保守放行，交给 /end 兜底
            return code == 0;
        }

        private static string ReadTextAny(string file)
        {
            try
            {
                byte[] b = File.ReadAllBytes(file);
                if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
                return Encoding.UTF8.GetString(b);
            }
            catch { return null; }
        }

        /* ==================== 文件占用（Restart Manager） ==================== */

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        /// <summary>
        /// 用系统 Restart Manager 找出真正持有该目录文件的进程 —— 比"扫进程主模块路径"更准，
        /// 能发现那种主程序已经没了、但还有句柄没释放的情形（典型就是删不掉的原因）。
        /// </summary>
        public static List<Occ> FindHolders(string dir)
        {
            List<Occ> list = new List<Occ>();
            string[] files = PickFiles(dir, 48);
            if (files.Length == 0) return list;

            uint handle = 0;
            string key = Guid.NewGuid().ToString("N");
            if (RmStartSession(out handle, 0, key) != 0) return list;
            try
            {
                if (RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null) != 0) return list;

                uint needed = 0, count = 0, reasons = 0;
                RM_PROCESS_INFO[] empty = new RM_PROCESS_INFO[0];
                RmGetList(handle, out needed, ref count, empty, ref reasons);
                if (needed == 0) return list;

                RM_PROCESS_INFO[] info = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(handle, out needed, ref count, info, ref reasons) != 0) return list;

                for (int i = 0; i < (int)count; i++)
                {
                    Occ o = new Occ();
                    o.Kind = "holder";
                    o.Pid = info[i].Process.dwProcessId;
                    string nm = info[i].strAppName;
                    if (nm == null || nm.Length == 0) nm = "PID " + o.Pid;
                    o.Name = nm + " (PID " + o.Pid + ")";
                    if (info[i].strServiceShortName != null && info[i].strServiceShortName.Length > 0)
                    {
                        o.Svc = info[i].strServiceShortName;
                        o.Detail = "服务 " + o.Svc + " 正在使用";
                    }
                    else o.Detail = "持有文件句柄";
                    list.Add(o);
                }
            }
            catch { }
            finally { RmEndSession(handle); }
            return list;
        }

        private static string[] PickFiles(string dir, int max)
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(dir)) { if (list.Count >= max) break; list.Add(f); }
                if (list.Count < max)
                    foreach (string d in Directory.GetDirectories(dir))
                    {
                        foreach (string f in Directory.GetFiles(d)) { if (list.Count >= max) break; list.Add(f); }
                        if (list.Count >= max) break;
                    }
            }
            catch { }
            return list.ToArray();
        }

        /// <summary>服务 + 进程 + 计划任务 + 文件句柄，四类占用一起查。</summary>
        public static List<Occ> FindAll(string dir)
        {
            List<Occ> list = FindOccupants(dir, true);
            HashSet<int> pids = new HashSet<int>();
            foreach (Occ o in list) if (o.Pid > 0) pids.Add(o.Pid);
            foreach (Occ o in FindHolders(dir))
            {
                if (o.Pid > 0 && pids.Contains(o.Pid)) continue;
                if (o.Pid > 0) pids.Add(o.Pid);
                list.Add(o);
            }
            return list;
        }

        /// <summary>系统关键进程不杀，只提示用户自己关。</summary>
        private static bool IsProtectedProcess(int pid)
        {
            if (pid <= 4) return true;
            try { if (pid == Process.GetCurrentProcess().Id) return true; } catch { }
            try
            {
                Process p = Process.GetProcessById(pid);
                string n = p.ProcessName.ToLowerInvariant();
                if (n == "explorer" || n == "system" || n == "smss" || n == "csrss" || n == "wininit" ||
                    n == "services" || n == "lsass" || n == "svchost" || n == "dwm" || n == "sihost" ||
                    n == "taskhostw" || n == "ctfmon" || n == "searchindexer" || n == "msmpeng" ||
                    n == "runtimebroker" || n == "winlogon" || n == "fontdrvhost" || n == "msdtc" ||
                    n == "ntoskrnl" || n == "registry") return true;
            }
            catch { }
            return false;
        }

        /* ==================== 浏览器：只列缓存、临时文件与插件 ==================== */

        private static readonly string[][] BrowserList = new string[][]
        {
            new string[] { "谷歌 Chrome", @"%LOCALAPPDATA%\google\Chrome\User Data" },
            new string[] { "Microsoft Edge", @"%LOCALAPPDATA%\Microsoft\Edge\User Data" },
            new string[] { "夸克", @"%LOCALAPPDATA%\Quark\User Data" },
            new string[] { "360 极速浏览器", @"%LOCALAPPDATA%\360Chrome\Chrome\User Data" },
            new string[] { "360 安全浏览器", @"%LOCALAPPDATA%\360se6\User Data" },
            new string[] { "Brave", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data" },
            new string[] { "Vivaldi", @"%LOCALAPPDATA%\Vivaldi\User Data" },
            new string[] { "搜狗浏览器", @"%LOCALAPPDATA%\SogouExplorer\WebKit" },
            new string[] { "Yandex", @"%LOCALAPPDATA%\Yandex\YandexBrowser\User Data" },
            new string[] { "Opera", @"%APPDATA%\Opera Software\Opera Stable" }
        };

        /* 只碰这些名字：缓存、临时、着色器缓存、站点缓存 */
        private static readonly string[] BrowserCacheNames = new string[]
        {
            "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "Media Cache",
            "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Temp", "Crashpad"
        };

        /* 这些目录一律不列：书签、密码、历史、Cookie、登录态、扩展的本地数据 */
        private static readonly string[] BrowserKeepNames = new string[]
        {
            "Bookmarks", "Login Data", "Login Data For Account", "Web Data", "History", "Cookies",
            "Network", "Local Storage", "IndexedDB", "Session Storage", "Preferences", "Secure Preferences",
            "Local State", "Sessions", "Current Session", "Current Tabs", "Last Session", "Last Tabs",
            "Top Sites", "Favicons", "Shortcuts", "Web Applications", "Affiliation Database",
            "Sync Data", "Sync Extension Settings", "Managed Extension Settings", "Extension State",
            "Local Extension Settings"
        };

        private static bool IsKeepName(string name)
        {
            for (int i = 0; i < BrowserKeepNames.Length; i++)
                if (string.Equals(BrowserKeepNames[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsBrowserArea(string path)
        {
            string p = path.ToLowerInvariant();
            return p.IndexOf(@"\google\chrome", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\microsoft\edge", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\quark\user data", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\360chrome", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\360se6", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\bravesoftware", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\vivaldi", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\sogouexplorer", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\yandex", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\opera software", StringComparison.Ordinal) >= 0
                || p.IndexOf(@"\mozilla\firefox", StringComparison.Ordinal) >= 0;
        }

        private static void ScanBrowsers(List<ResidueHit> hits, HashSet<string> seen, ref int total)
        {
            foreach (string[] b in BrowserList)
            {
                if (total >= 240) return;
                string root = Environment.ExpandEnvironmentVariables(b[1]);
                if (!Directory.Exists(root)) continue;
                ScanChromium(b[0], root, hits, seen, ref total, 0);
            }
            ScanFirefox(hits, seen, ref total);
        }

        private static void ScanChromium(string brand, string userData, List<ResidueHit> hits, HashSet<string> seen, ref int total, int depth)
        {
            foreach (string name in new string[] { "ShaderCache", "GrShaderCache", "GPUCache", "Code Cache", "Media Cache", "Crashpad" })
            {
                string d = Path.Combine(userData, name);
                if (Directory.Exists(d)) AddBrowserHit(brand, d, "缓存目录", "", hits, seen, ref total, 0);
            }

            List<string> profiles = new List<string>();
            foreach (string d in Dirs(userData))
            {
                string n = PathName(d);
                if (n.StartsWith("Default", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Guest Profile", StringComparison.OrdinalIgnoreCase))
                    profiles.Add(d);
            }
            if (profiles.Count == 0) profiles.Add(userData);

            foreach (string prof in profiles)
            {
                if (total >= 240) return;
                bool multi = profiles.Count > 1;
                string pn = multi ? "（" + PathName(prof) + "）" : "";
                foreach (string name in BrowserCacheNames)
                {
                    string d = Path.Combine(prof, name);
                    if (Directory.Exists(d)) AddBrowserHit(brand, d, name == "Crashpad" ? "崩溃转储" : "缓存目录", pn, hits, seen, ref total, 0);
                }
                string sw = Path.Combine(prof, "Service Worker", "CacheStorage");
                if (Directory.Exists(sw)) AddBrowserHit(brand, sw, "站点缓存", pn, hits, seen, ref total, 0);
                ScanChromiumExtensions(brand, prof, pn, hits, seen, ref total);
            }
        }

        private static void ScanChromiumExtensions(string brand, string prof, string pn, List<ResidueHit> hits, HashSet<string> seen, ref int total)
        {
            string extRoot = Path.Combine(prof, "Extensions");
            if (!Directory.Exists(extRoot)) return;
            foreach (string id in Dirs(extRoot))
            {
                if (total >= 240) return;
                string verDir = "";
                foreach (string v in Dirs(id)) { verDir = v; break; }
                string name = "";
                string ver = "";
                if (verDir.Length > 0)
                {
                    string mn = Path.Combine(verDir, "manifest.json");
                    if (File.Exists(mn)) ReadChromeManifest(mn, out name, out ver, verDir);
                }
                if (name.Length == 0) name = PathName(id);
                if (ver.Length == 0) ver = PathName(verDir);
                AddBrowserHit(brand, id, "插件", pn, hits, seen, ref total, 1, name + (ver.Length > 0 ? " " + ver : ""));
            }
        }

        private static void ReadChromeManifest(string manifest, out string name, out string ver, string verDir)
        {
            name = "";
            ver = "";
            try
            {
                string txt = File.ReadAllText(manifest, Encoding.UTF8);
                Match m = Regex.Match(txt, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success) name = m.Groups[1].Value;
                Match mv = Regex.Match(txt, "\"version\"\\s*:\\s*\"([^\"]*)\"");
                if (mv.Success) ver = mv.Groups[1].Value;
                if (name.StartsWith("__MSG_", StringComparison.Ordinal))
                {
                    string key = name.Replace("__MSG_", "").Replace("__", "");
                    string loc = Path.Combine(verDir, "_locales");
                    string zh = Path.Combine(loc, "zh_CN", "messages.json");
                    string en = Path.Combine(loc, "en", "messages.json");
                    string f = File.Exists(zh) ? zh : (File.Exists(en) ? en : "");
                    if (f.Length > 0)
                    {
                        string lt = File.ReadAllText(f, Encoding.UTF8);
                        Match lm = Regex.Match(lt, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{\\s*\"message\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                        if (lm.Success) name = lm.Groups[1].Value;
                        else name = "";
                    }
                    else name = "";
                }
            }
            catch { }
        }

        private static void ScanFirefox(List<ResidueHit> hits, HashSet<string> seen, ref int total)
        {
            string localProfiles = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles");
            if (Directory.Exists(localProfiles))
            {
                foreach (string prof in Dirs(localProfiles))
                {
                    if (total >= 240) return;
                    string pn = "（" + PathName(prof) + "）";
                    foreach (string name in new string[] { "cache2", "startupCache", "cache", "thumbnails", "OfflineCache" })
                    {
                        string d = Path.Combine(prof, name);
                        if (Directory.Exists(d)) AddBrowserHit("火狐", d, "缓存目录", pn, hits, seen, ref total, 0);
                    }
                }
            }

            string roamingProfiles = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Mozilla\Firefox\Profiles");
            if (Directory.Exists(roamingProfiles))
            {
                foreach (string prof in Dirs(roamingProfiles))
                {
                    if (total >= 240) return;
                    string pn = "（" + PathName(prof) + "）";
                    string ext = Path.Combine(prof, "extensions");
                    if (!Directory.Exists(ext)) continue;
                    foreach (string d in Dirs(ext))
                    {
                        if (total >= 240) return;
                        AddBrowserHit("火狐", d, "插件", pn, hits, seen, ref total, 1, PathName(d));
                    }
                    foreach (string f in Directory.GetFiles(ext, "*.xpi"))
                    {
                        if (total >= 240) return;
                        AddBrowserHit("火狐", f, "插件", pn, hits, seen, ref total, 1, Path.GetFileNameWithoutExtension(f));
                    }
                }
            }
        }

        private static void AddBrowserHit(string brand, string path, string kindText, string pn,
            List<ResidueHit> hits, HashSet<string> seen, ref int total, int type)
        {
            AddBrowserHit(brand, path, kindText, pn, hits, seen, ref total, type, "");
        }

        private static void AddBrowserHit(string brand, string path, string kindText, string pn,
            List<ResidueHit> hits, HashSet<string> seen, ref int total, int type, string label)
        {
            string np = NormPath(path);
            if (np.Length == 0) return;
            if (IsKeepName(PathName(path))) return;
            if (!seen.Add(np)) return;

            long bytes = 0;
            int files = 0;
            string skip = "";
            if (File.Exists(path))
            {
                try { bytes = new FileInfo(path).Length; } catch { bytes = 0; }
                files = 1;
            }
            else Scanner.Measure(path, 600, out bytes, out files, out skip);
            if (bytes <= 0 && files == 0) return;

            total++;
            ResidueHit h = new ResidueHit();
            h.Id = ShortId(path);
            h.Brand = brand;
            h.Path = path;
            h.Bytes = bytes;
            h.Files = files;
            h.InUse = false;
            h.Created = CreatedText(path);
            h.Level = "strong";
            h.Label = label;
            if (type == 1)
            {
                h.Evidence = "浏览器插件" + pn + "；删掉后这个扩展会从浏览器里消失";
                h.Note = "只删插件本体。书签、密码、历史、Cookie 都在别的文件里，不受影响；删错了可以回浏览器商店重装";
            }
            else
            {
                h.Evidence = "浏览器" + kindText + pn + "；只含缓存与临时文件";
                h.Note = "不含书签、密码、历史、Cookie。删掉后浏览器会自己重建，第一次打开网页略慢";
            }
            h.Occs = new List<Occ>();
            hits.Add(h);
        }

        public static List<ResidueHit> Detect(Action<string> onEvent)
        {
            List<ResidueHit> hits = new List<ResidueHit>();
            InstalledSet inst = InstalledSet.Build();
            OccIndex idx = BuildIndex();
            int total = 0;
            const int MaxHits = 260;
            HashSet<string> seen = new HashSet<string>();
            _ownDir = NormPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune"));

            /* 浏览器单独一层：只列缓存、临时文件与插件，书签密码历史一律不列 */
            try { ScanBrowsers(hits, seen, ref total); }
            catch (Exception ex) { Log.Warn("residue", "浏览器扫描异常：" + ex.Message); }
            Log.Info("residue", "浏览器条目 " + total + " 个");

            foreach (string[] r in ScanRoots())
            {
                string root = r[0];
                int kind = 0;
                if (r[1] == "1") kind = 1;
                else if (r[1] == "2") kind = 2;
                else if (r[1] == "3") kind = 3;
                if (!Directory.Exists(root)) continue;

                foreach (string d1 in Dirs(root))
                {
                    if (total >= MaxHits) break;
                    string n1 = PathName(d1);
                    if (IsSystemName(n1)) continue;
                    if (IsBrowserArea(d1)) continue;          /* 浏览器数据已由 ScanBrowsers 精细处理 */

                    /* 程序目录：装的整棵跳过 */
                    if (kind == 0 && inst.Exact(d1)) continue;
                    if (kind == 0 && inst.NameMatch(d1, false)) continue;

                    string vp = inst.LooseMatch(n1);
                    bool inUse1 = vp.Length > 0 || inst.NameMatch(d1, true);

                    /* 其它盘的程序目录：多半是绿色/便携软件，有 exe 就是软件本体，一律不动 */
                    if (kind == 3)
                    {
                        if (TopExeFiles(d1).Count > 0) continue;
                        AddHit(d1, 3, inst, vp, inUse1, idx, hits, onEvent, ref total, seen);
                        continue;
                    }

                    List<string> subs = Dirs(d1);

                    if (subs.Count == 0)
                    {
                        AddHit(d1, kind, inst, vp, inUse1, idx, hits, onEvent, ref total, seen);
                        continue;
                    }

                    List<string> unmatched = new List<string>();
                    List<string> used = new List<string>();
                    int usable = 0;
                    foreach (string d2 in subs)
                    {
                        string n2 = PathName(d2);
                        if (IsSystemName(n2)) continue;
                        if (IsBrowserArea(d2)) continue;     /* 浏览器数据已由 ScanBrowsers 精细处理 */
                        usable++;
                        if (kind == 0)
                        {
                            if (inst.Exact(d2)) continue;
                            if (inst.NameMatch(d2, true)) continue;
                            unmatched.Add(d2);
                        }
                        else
                        {
                            if (vp.Length > 0 || inst.NameMatch(d2, true) || inst.Exact(d2)) used.Add(d2);
                            else unmatched.Add(d2);
                        }
                    }

                    if (kind == 0)
                    {
                        if (unmatched.Count == 0) continue;
                        /* 整个厂商目录里没有一个与已装软件对得上，且碎片很多 → 合并成一条 */
                        if (unmatched.Count == usable && unmatched.Count > 5)
                        {
                            AddHit(d1, kind, inst, vp, inUse1, idx, hits, onEvent, ref total, seen);
                            continue;
                        }
                        foreach (string d2 in unmatched)
                        {
                            if (total >= MaxHits) break;
                            AddHit(d2, kind, inst, vp, false, idx, hits, onEvent, ref total, seen);
                        }
                    }
                    else
                    {
                        foreach (string d2 in unmatched)
                        {
                            if (total >= MaxHits) break;
                            AddHit(d2, kind, inst, vp, false, idx, hits, onEvent, ref total, seen);
                        }
                        /* 在用软件的数据目录也列出来，标「不建议删除」，让你看清全貌 */
                        foreach (string d2 in used)
                        {
                            if (total >= MaxHits) break;
                            AddHit(d2, kind, inst, vp, true, idx, hits, onEvent, ref total, seen);
                        }
                    }
                }
            }
            Log.Info("residue", "候选目录 " + hits.Count + " 个");
            return hits;
        }

        private static string _ownDir = "";

        private static void AddHit(string path, int kind, InstalledSet inst, string vendorParent, bool inUseData,
            OccIndex idx, List<ResidueHit> hits, Action<string> onEvent, ref int total, HashSet<string> seen)
        {
            string np = NormPath(path);
            if (np.Length == 0) return;
            if (!seen.Add(np)) return;                             // 同一路径只算一次
            /* 本软件自己的目录（WebView2 缓存等）永远不列 */
            if (_ownDir.Length > 0 && (np == _ownDir || np.StartsWith(_ownDir + "\\", StringComparison.Ordinal))) return;

            long bytes = 0; int files = 0; string skip = "";
            Scanner.Measure(path, 700, out bytes, out files, out skip);
            if (files == 0 && bytes == 0) return;                 // 空壳目录
            if (kind == 2)
            {
                if (bytes < 10485760) return;                     // 用户数据只看 10MB 以上
            }
            else if (kind == 1)
            {
                if (bytes < 262144 && files < 40) return;
            }
            else
            {
                if (bytes < 65536 && files < 30) return;          // 碎片噪音，删了也腾不出空间
            }

            string level;
            string evidence;
            string note;

            if (inUseData)
            {
                level = "vendor";
                evidence = "在用软件" + (vendorParent.Length > 0 ? "（" + vendorParent + "）" : "") + "的数据目录，里面是账号、书签或配置";
                note = "这个软件的安装目录还在，数据目录不能整目录删，会丢账号和书签";
            }
            else if (HasUninstaller(path))
            {
                level = "strong";
                evidence = "目录里有独立卸载程序，像是装过又被删掉的软件";
                note = "该软件已不在已装清单；确有卸载程序，可按整目录清掉";
            }
            else
            {
                bool hasExe = TopExeFiles(path).Count > 0;
                if (vendorParent.Length > 0 && hasExe)
                {
                    level = "vendor";
                    evidence = "同厂商其它软件还在用（" + vendorParent + "），这里像是共享组件或旧版本";
                    note = "同一厂商还有软件在装，这里多半是组件或旧版本目录；不确认就别删";
                }
                else if (vendorParent.Length > 0 && !hasExe)
                {
                    level = "strong";
                    evidence = "已装软件（" + vendorParent + "）的目录下，但只剩临时文件和日志";
                    note = "没有可执行文件，是卸载后遗留的碎片，可清";
                }
                else if (kind == 3)
                {
                    level = "strong";
                    evidence = "非系统盘的程序目录里没有可执行文件，只剩残留数据";
                    note = "程序本体已经不在了，这个目录可以整个清掉";
                }
                else if (kind == 1)
                {
                    level = "weak";
                    evidence = "ProgramData 里的共享数据，已装清单里没有对应软件";
                    note = "共享数据，可能还有别的软件在读，确认再删";
                }
                else if (kind == 2)
                {
                    level = "strong";
                    evidence = "用户数据目录，对应的程序已不在已装清单";
                    note = "程序已经不在清单里，这个数据目录可以整个清掉";
                }
                else
                {
                    level = "strong";
                    evidence = "已装清单里没有这个安装目录";
                    note = "该软件已不在已装清单";
                }
            }

            List<Occ> occs = new List<Occ>();
            HashSet<int> pids = new HashSet<int>();
            string d = NormPath(path);
            foreach (string[] s in idx.Services)
                if (UnderDir(s[1], d))
                {
                    Occ o = new Occ(); o.Kind = "service"; o.Name = s[0]; o.Detail = s[1];
                    occs.Add(o);
                }
            foreach (Occ p in idx.Procs)
                if (UnderDir(p.Detail, d))
                {
                    Occ o = new Occ(); o.Kind = "process"; o.Name = p.Name; o.Detail = p.Detail; o.Pid = p.Pid;
                    occs.Add(o);
                    if (p.Pid > 0) pids.Add(p.Pid);
                }
            foreach (Occ o in FindHolders(path))
            {
                if (o.Pid > 0 && pids.Contains(o.Pid)) continue;
                if (o.Pid > 0) pids.Add(o.Pid);
                occs.Add(o);
            }

            bool hardOcc = false;
            foreach (Occ o in occs) { if (o.Kind != "task") { hardOcc = true; break; } }
            string status;
            if (level == "vendor") status = "不建议删除";
            else if (hardOcc) status = "有残留";
            else status = "建议删除";

            ResidueHit h = new ResidueHit();
            h.Id = ShortId(path);
            h.Brand = BrandName(path);
            h.Path = path;
            h.Bytes = bytes;
            h.Files = files;
            h.InUse = occs.Count > 0;
            h.Occs = occs;
            h.Created = CreatedText(path);
            h.Level = level;
            if (occs.Count > 0) h.Evidence = evidence + "；有 " + occs.Count + " 项占用，删除时会先停掉再删";
            else h.Evidence = evidence + "；没有进程或服务占用";
            h.Note = note;

            hits.Add(h);
            total++;

            if (onEvent != null)
            {
                JB arr = new JB();
                StringBuilder sb = new StringBuilder("[");
                int i = 0;
                foreach (Occ o in occs)
                {
                    if (i++ > 0) sb.Append(',');
                    sb.Append(new JB().Add("kind", o.Kind).Add("name", o.Name).Add("detail", o.Detail).End());
                }
                sb.Append(']');
                onEvent(new JB()
                    .Add("type", "residueItem")
                    .Add("id", h.Id).Add("brand", h.Brand).Add("path", h.Path)
                    .Add("bytes", h.Bytes).Add("files", h.Files)
                    .Add("inUse", h.InUse).Add("evidence", h.Evidence).Add("note", h.Note)
                    .Add("created", h.Created).Add("level", h.Level).Add("status", status)
                    .Add("label", h.Label)
                    .Raw("occs", sb.ToString())
                    .End());
            }
        }

        /* ==================== 删除前停止占用 ==================== */

        /// <summary>
        /// 停止指向该目录的服务、结束进程、结束计划任务，再返回复查结果。
        /// 只对本目录内的可执行文件动手，不会碰系统进程。
        /// </summary>
        public static string PrepareDelete(string dir, Action<string> onEvent)
        {
            List<Occ> before = FindAll(dir);
            if (before.Count == 0) return "无占用";

            int svc = 0, proc = 0, task = 0, hold = 0;
            List<string> keep = new List<string>();
            List<string> rebootNote = new List<string>();
            foreach (Occ o in before)
            {
                if (o.Kind == "service") { StopService(o.Name); svc++; }
                else if (o.Kind == "process" || o.Kind == "holder")
                {
                    if (IsProtectedProcess(o.Pid))
                    {
                        // 系统进程（含资源管理器）不杀，提示用户自己关掉
                        keep.Add(o.Name);
                        continue;
                    }

                    // 句柄挂在服务上：先停服务；服务不可停（驱动守护类）就标记删除，等重启释放
                    if (o.Svc != null && o.Svc.Length > 0)
                    {
                        if (ServiceNotStoppable(o.Svc))
                        {
                            DeleteService(o.Svc);
                            rebootNote.Add("服务 " + o.Svc + " 不可停止，已标记删除，重启后自动释放");
                            continue;
                        }
                        StopService(o.Svc);
                        System.Threading.Thread.Sleep(500);
                    }

                    KillProcess(o.Pid);
                    if (o.Kind == "process") proc++; else hold++;
                }
                else if (o.Kind == "task") { DeleteTask(o.Name); task++; }
            }

            System.Threading.Thread.Sleep(900);
            List<Occ> after = FindAll(dir);

            string msg = "已停止 服务 " + svc + " / 进程 " + proc + " / 句柄占用 " + hold + " / 残留任务已删 " + task;
            if (rebootNote.Count > 0)
            {
                StringBuilder rb = new StringBuilder(msg);
                for (int i = 0; i < rebootNote.Count; i++) rb.Append(i == 0 ? "；" : "、").Append(rebootNote[i]);
                msg = rb.ToString();
            }
            if (keep.Count > 0)
            {
                StringBuilder kb = new StringBuilder(msg + "；这些需要你手动关闭：");
                for (int i = 0; i < keep.Count; i++) { if (i > 0) kb.Append("、"); kb.Append(keep[i]); }
                msg = kb.ToString();
            }
            if (after.Count > 0)
            {
                StringBuilder sb = new StringBuilder(msg + "；仍占用 " + after.Count + " 项：");
                foreach (Occ o in after) sb.Append(o.Name + "（" + o.Detail + "）；");
                msg = sb.ToString();
            }
            Log.Info("residue", "准备删除 " + dir + " → " + msg);
            if (onEvent != null)
                onEvent(new JB().Add("type", "cleanStop").Add("path", dir).Add("msg", msg).End());
            return msg;
        }

        private static void StopService(string name)
        {
            RunTool("sc.exe", "stop \"" + name + "\"");
            for (int i = 0; i < 9; i++)
            {
                System.Threading.Thread.Sleep(400);
                string q = RunCapture("sc.exe", "query \"" + name + "\"");
                if (q.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0) return;
                if (q.IndexOf("STOP_PENDING", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (q.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) < 0) return;
            }
        }

        private static void KillProcess(int pid)
        {
            if (pid <= 0) return;
            RunTool("taskkill.exe", "/PID " + pid + " /F");
        }

        private static void EndTask(string name)
        {
            if (name == null || name.Length == 0) return;
            RunTool("schtasks.exe", "/end /tn \"" + name + "\"");
        }

        /// <summary>服务是否被驱动/守护设为不可停止（sc stop 会直接失败）。</summary>
        private static bool ServiceNotStoppable(string name)
        {
            string q = RunCapture("sc.exe", "query \"" + name + "\"");
            return q.IndexOf("NOT_STOPPABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>标记删除残留服务（配置先备份），进程在重启后消失，文件随之可删。</summary>
        private static void DeleteService(string name)
        {
            if (name == null || name.Length == 0) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string bak = Path.Combine(dir, "svc-" + name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                string txt = "sc qc:\r\n" + RunCapture("sc.exe", "qc \"" + name + "\"") +
                             "\r\nsc query:\r\n" + RunCapture("sc.exe", "query \"" + name + "\"");
                File.WriteAllText(bak, txt, Encoding.UTF8);
            }
            catch { }
            RunTool("sc.exe", "delete \"" + name + "\"");
            Log.Info("residue", "已标记删除残留服务 " + name);
        }

        /// <summary>残留计划任务：先备份定义，再结束并删除，避免重启后又把目录抢回去。</summary>
        private static void DeleteTask(string name)
        {
            if (name == null || name.Length == 0) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string bak = Path.Combine(dir, "task-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml");
                string xml = RunCapture("schtasks.exe", "/query /tn \"" + name + "\" /xml");
                if (xml != null && xml.Length > 0) File.WriteAllText(bak, xml, Encoding.UTF8);
            }
            catch { }
            RunTool("schtasks.exe", "/end /tn \"" + name + "\"");
            RunTool("schtasks.exe", "/delete /tn \"" + name + "\" /f");
            Log.Info("residue", "已删除残留计划任务 " + name);
        }

        private static int RunExit(string exe, string args)
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
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(4000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        private static void RunTool(string exe, string args)
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
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                }
            }
            catch (Exception ex) { Log.Warn("residue", exe + " " + args + " 失败：" + ex.Message); }
        }

        private static string RunCapture(string exe, string args)
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
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);
                    return o + e;
                }
            }
            catch { return ""; }
        }
    }

    /// <summary>注册表残留：无效卸载项与失效启动项。清理前必然先导出备份。</summary>
    public static class RegistryScan
    {
        public class Hit
        {
            public string Hive;      // 展示用
            public string KeyPath;   // 实际删除路径
            public string RegFile;   // reg.exe 用的路径
            public string Display;   // 展示名
            public string Why;       // 判定原因
            public string Kind;      // uninstall / run
            public bool Safe;        // 是否建议清理
        }

        public static List<Hit> DetectUninstall()
        {
            List<Hit> hits = new List<Hit>();
            string[] roots = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (string rootPath in roots)
            {
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(rootPath))
                    {
                        if (root == null) continue;
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey k = root.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string name = S(k.GetValue("DisplayName"));
                                    if (name.Length == 0) continue;
                                    string pub = S(k.GetValue("Publisher"));
                                    bool ms = pub.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
                                    if (ms) continue;   // 微软组件一律不碰

                                    string loc = S(k.GetValue("InstallLocation")).Trim('"');
                                    string unins = S(k.GetValue("UninstallString")).Trim();
                                    string why = "";
                                    if (loc.Length > 0 && !Directory.Exists(loc)) why = "安装目录已不存在";
                                    else if (unins.Length > 0 && !UninstallTargetExists(unins)) why = "卸载程序已不存在";

                                    if (why.Length == 0) continue;

                                    Hit h = new Hit();
                                    h.Hive = "HKLM";
                                    h.KeyPath = rootPath + "\\" + sub;
                                    h.RegFile = "HKLM\\" + rootPath + "\\" + sub;
                                    h.Display = name;
                                    h.Why = why;
                                    h.Kind = "uninstall";
                                    h.Safe = true;
                                    hits.Add(h);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // HKCU 卸载项
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (root != null)
                    {
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey k = root.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string name = S(k.GetValue("DisplayName"));
                                    if (name.Length == 0) continue;
                                    if (name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                    string loc = S(k.GetValue("InstallLocation")).Trim('"');
                                    if (loc.Length == 0 || Directory.Exists(loc)) continue;

                                    Hit h = new Hit();
                                    h.Hive = "HKCU";
                                    h.KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + sub;
                                    h.RegFile = "HKCU\\" + h.KeyPath;
                                    h.Display = name;
                                    h.Why = "安装目录已不存在";
                                    h.Kind = "uninstall";
                                    h.Safe = true;
                                    hits.Add(h);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return hits;
        }

        public static List<Hit> DetectRun()
        {
            List<Hit> hits = new List<Hit>();
            string[][] targets = new string[][]
            {
                new string[] { "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
                new string[] { "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" },
                new string[] { "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" }
            };

            foreach (string[] t in targets)
            {
                RegistryKey root = t[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(t[1]))
                    {
                        if (k == null) continue;
                        foreach (string valueName in k.GetValueNames())
                        {
                            try
                            {
                                string cmd = S(k.GetValue(valueName));
                                if (cmd.Length == 0) continue;
                                if (cmd.IndexOf("rundll32", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                string exe = FirstToken(cmd);
                                if (exe.Length == 0) continue;
                                if (File.Exists(exe)) continue;

                                Hit h = new Hit();
                                h.Hive = t[0];
                                h.KeyPath = t[1];
                                h.RegFile = t[0] + "\\" + t[1];
                                h.Display = valueName;
                                h.Why = "指向的程序不存在：" + exe;
                                h.Kind = "run";
                                h.Safe = true;
                                hits.Add(h);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return hits;
        }

        public static string SummaryJson(List<Hit> uninst, List<Hit> run)
        {
            int shown = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append('[');
            foreach (Hit h in uninst)
            {
                if (shown >= 8) break;
                if (shown > 0) sb.Append(',');
                sb.Append(new JB()
                    .Add("display", h.Display).Add("why", h.Why).Add("kind", "uninstall").Add("hive", h.Hive)
                    .End());
                shown++;
            }
            foreach (Hit h in run)
            {
                if (shown >= 8) break;
                if (shown > 0) sb.Append(',');
                sb.Append(new JB()
                    .Add("display", h.Display).Add("why", h.Why).Add("kind", "run").Add("hive", h.Hive)
                    .End());
                shown++;
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string S(object o) { return o == null ? "" : o.ToString(); }

        private static bool UninstallTargetExists(string cmd)
        {
            string exe = FirstToken(cmd);
            if (exe.Length == 0) return true;
            if (File.Exists(exe)) return true;
            // msiexec 卸载走的是产品码，无法用文件判断，视为存在
            if (exe.ToLowerInvariant().EndsWith("msiexec.exe")) return true;
            return false;
        }

        public static string FirstToken(string cmd)
        {
            string c = cmd.Trim();
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                if (end > 0) return c.Substring(1, end - 1);
            }
            // 无引号且路径含空格：截到 .exe 为止
            int exeIdx = c.ToLowerInvariant().IndexOf(".exe");
            if (exeIdx > 0 && c.IndexOf(':') >= 0)
                return c.Substring(0, exeIdx + 4).Trim('"');
            string[] parts = c.Split(' ');
            string first = parts.Length > 0 ? parts[0].Trim('"') : "";
            if (first.Length == 0) return "";
            if (first.IndexOf(':') < 0) return "";   // 非绝对路径，不判定
            return first;
        }
    }
}
