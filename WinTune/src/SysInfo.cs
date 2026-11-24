using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace WinTune
{
    public static class Log
    {
        private static readonly object Lock = new object();
        private static string _dir;
        private static string _file;

        public static string Dir
        {
            get
            {
                if (_dir == null)
                {
                    _dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WinTune", "logs");
                    try { Directory.CreateDirectory(_dir); } catch { }
                }
                return _dir;
            }
        }

        public static string CurrentFile
        {
            get
            {
                if (_file == null)
                {
                    _file = Path.Combine(Dir, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                }
                return _file;
            }
        }

        public static void Info(string tag, string msg)
        {
            Write("INFO", tag, msg);
        }

        public static void Warn(string tag, string msg)
        {
            Write("WARN", tag, msg);
        }

        public static void Error(string tag, string msg)
        {
            Write("ERROR", tag, msg);
        }

        private static void Write(string level, string tag, string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + tag + " | " + msg;
            lock (Lock)
            {
                try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
    }

    public static class SysInfo
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong GetTickCount64();

        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string OsName()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return "Windows";
                    string name = Str(k.GetValue("ProductName"));
                    string disp = Str(k.GetValue("DisplayVersion"));
                    string build = Str(k.GetValue("CurrentBuild"));
                    string ubr = Str(k.GetValue("UBR"));
                    if (build.Length > 0)
                    {
                        if (build == "22000" || build.IndexOf("22") == 0) { }
                        if (Convert.ToInt32(build) >= 22000 && name.IndexOf("Windows 10") >= 0)
                            name = name.Replace("Windows 10", "Windows 11");
                        return name + (disp.Length > 0 ? " " + disp : "") + " · " + build +
                               (ubr.Length > 0 ? "." + ubr : "");
                    }
                    return name;
                }
            }
            catch { return "Windows"; }
        }

        public static string BuildNumber()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    return Str(k == null ? null : k.GetValue("CurrentBuild"));
                }
            }
            catch { return "0"; }
        }

        public static string Arch()
        {
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        public static bool HasWinget()
        {
            return FindOnPath("winget.exe") || FileExists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe"));
        }

        public static bool HasChoco()
        {
            return FindOnPath("choco.exe") || FileExists(@"C:\ProgramData\chocolatey\bin\choco.exe");
        }

        public static bool HasPwsh7()
        {
            return FileExists(@"C:\Program Files\PowerShell\7\pwsh.exe");
        }

        public static string PolicyHint()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows"))
                {
                    if (k == null) return "无";
                    string[] names = k.GetSubKeyNames();
                    return names.Length > 0 ? "检测到 " + names.Length + " 个策略节点" : "无";
                }
            }
            catch { return "未知"; }
        }

        public static string DiskJson(string drive)
        {
            try
            {
                DriveInfo d = new DriveInfo(drive);
                long total = d.TotalSize / 1073741824L;
                long free = d.TotalFreeSpace / 1073741824L;
                return new JB()
                    .Add("drive", drive)
                    .Add("totalGB", total)
                    .Add("freeGB", free)
                    .Add("usedGB", total - free)
                    .End();
            }
            catch { return "null"; }
        }

        public static string RestorePointHint()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
                {
                    if (k == null) return "未开启";
                    object v = k.GetValue("RPSessionInterval");
                    if (v == null) return "未确认";
                    return Convert.ToInt32(v) == 0 ? "未开启" : "已开启";
                }
            }
            catch { return "未确认"; }
        }

        public static bool FileExists(string p)
        {
            try { return File.Exists(p); } catch { return false; }
        }

        public static bool FindOnPath(string exe)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (path == null) return false;
                foreach (string dir in path.Split(';'))
                {
                    if (dir.Trim().Length == 0) continue;
                    try { if (File.Exists(Path.Combine(dir.Trim(), exe))) return true; }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static string Str(object o)
        {
            return o == null ? "" : o.ToString();
        }

        public static string Uptime()
        {
            ulong ms = GetTickCount64();
            long min = (long)(ms / 60000);
            return (min / 60) + " 小时 " + (min % 60) + " 分";
        }
    }
}
