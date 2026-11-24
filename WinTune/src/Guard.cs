using System;
using System.IO;
using System.Collections.Generic;

namespace WinTune
{
    /// <summary>
    /// 删除前的最后一道闸：路径必须在已知白名单内，且不得落在系统关键目录上。
    /// 任何绕过这里的删除调用都视为缺陷。
    /// </summary>
    public static class Guard
    {
        private static readonly List<string> Forbidden = new List<string>();

        static Guard()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Forbidden.Add(Norm(win));
            Forbidden.Add(Norm(Path.Combine(win, "System32")));
            Forbidden.Add(Norm(Path.Combine(win, "SysWOW64")));
            Forbidden.Add(Norm(Path.Combine(win, "WinSxS")));
            Forbidden.Add(Norm(Path.Combine(win, "Fonts")));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
            Forbidden.Add(Norm(up));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)));
            Forbidden.Add(Norm(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)));
        }

        /// <summary>统一成不带尾部反斜杠的大写全路径，便于前缀比较。</summary>
        public static string Norm(string path)
        {
            if (path == null) return "";
            string p = path.Trim().Trim('"');
            try { p = Path.GetFullPath(p); } catch { }
            p = p.TrimEnd('\\');
            return p.ToUpperInvariant();
        }

        /// <summary>path 是否等于 root 或其子路径。</summary>
        public static bool IsUnder(string path, string root)
        {
            string p = Norm(path);
            string r = Norm(root);
            if (p.Length == 0 || r.Length == 0) return false;
            if (p == r) return true;
            return p.StartsWith(r + "\\", StringComparison.Ordinal);
        }

        /// <summary>path 是否恰好是某个禁区本身（不允许把整棵系统目录当清理目标）。</summary>
        public static bool IsForbiddenItself(string path)
        {
            string p = Norm(path);
            foreach (string f in Forbidden)
            {
                if (p == f) return true;
            }
            return false;
        }

        /// <summary>path 是否落在禁区内部（这类路径一律不允许删除）。</summary>
        public static bool IsInsideForbidden(string path)
        {
            string p = Norm(path);
            foreach (string f in Forbidden)
            {
                if (p != f && p.StartsWith(f + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        /// <summary>
        /// 最终判定：必须落在某个允许根之下、不得是该根本身（根本身不删，只清内容）、
        /// 不得是禁区、不得是重解析点。
        /// </summary>
        public static bool CanDelete(string path, IEnumerable<string> allowRoots, out string reason)
        {
            return CanDelete(path, allowRoots, false, out reason);
        }

        /// <summary>
        /// allowSelf=true 时允许删除"恰好等于某个允许根"的目标（用于 Windows.old、
        /// 已确认卸载的软件残留目录这类必须整目录删的项）；默认不允许，只清根本身的内容。
        /// </summary>
        public static bool CanDelete(string path, IEnumerable<string> allowRoots, bool allowSelf, out string reason)
        {
            reason = "";
            string p = Norm(path);
            if (p.Length == 0) { reason = "路径为空"; return false; }
            if (p.Length <= 3) { reason = "拒绝操作盘符根目录"; return false; }
            if (IsForbiddenItself(p)) { reason = "目标是系统关键目录"; return false; }

            // 授权完全由白名单决定；白名单根本身若过宽（盘符、Windows、用户根）则不作为授权依据
            bool allowed = false;
            foreach (string root in allowRoots)
            {
                string nr = Norm(root);
                if (nr.Length == 0 || nr.Length <= 3) continue;
                if (IsForbiddenItself(nr)) continue;
                if (Depth(nr) < 2) continue;

                if (p == nr)
                {
                    if (!allowSelf) { reason = "目标是清理根目录本身，拒绝整目录删除"; return false; }
                    allowed = true;
                    break;
                }
                if (IsUnder(p, nr)) { allowed = true; break; }
            }
            if (!allowed) { reason = "路径不在允许的清理范围内"; return false; }

            if (IsReparsePoint(p)) { reason = "该路径是符号链接/联接点，跳过以避免误删扩散"; return false; }
            return true;
        }

        /// <summary>路径深度：C:\a\b = 3。</summary>
        private static int Depth(string normalized)
        {
            if (normalized.Length == 0) return 0;
            int n = 1;
            for (int i = 0; i < normalized.Length; i++) if (normalized[i] == '\\') n++;
            return n;
        }

        /// <summary>目录是否为空（判断外壳目录能不能整个删）。</summary>
        public static bool IsEmptyDir(string path)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(path);
                return d.GetFileSystemInfos().Length == 0;
            }
            catch { return false; }
        }

        public static bool Exists(string path)
        {
            try { return Directory.Exists(path) || File.Exists(path); } catch { return false; }
        }

        /// <summary>把路径规整成可展示形式（保留原始大小写）。</summary>
        public static string Display(string path)
        {
            return path;
        }
    }
}