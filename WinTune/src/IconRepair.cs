using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WinTune
{
    /// <summary>
    /// 图标自愈：exe、窗口、任务栏、桌面 / 开始菜单 / 任务栏固定项统一到 assets\icon.ico。
    /// Windows 会把快捷方式图标单独缓存，升级后只换 exe 图标并不一定会刷新任务栏按钮，
    /// 所以这里主动重写所有 WinTune 快捷方式并通知 Shell 重新读取图标。
    /// </summary>
    public static class IconRepair
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_FLUSHNOWAIT = 0x3000;

        public static string IconPath
        {
            get { return Path.Combine(Application.StartupPath, @"assets\icon.ico"); }
        }

        /// <summary>读 ICO 里最适合窗口/任务栏的一档，失败时退回 exe 内嵌图标。</summary>
        public static Icon LoadAppIcon()
        {
            try
            {
                if (File.Exists(IconPath)) return new Icon(IconPath, 32, 32);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            return null;
        }

        /// <summary>每个版本执行一次：改写快捷方式、碰一下 ico 时间、通知 Shell 刷新。</summary>
        public static void Ensure()
        {
            try
            {
                string marker = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "icon-repaired-1.5.48");
                bool first = !File.Exists(marker);
                string exe = Application.ExecutablePath;
                string ico = IconPath;
                string iconLocation = ico + ",0";

                if (first && File.Exists(ico))
                {
                    try { File.SetLastWriteTime(ico, DateTime.Now); } catch { }
                }

                string[] roots = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Internet Explorer\Quick Launch"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
                };

                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    try { RewriteTree(root, exe, iconLocation); } catch { }
                }

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSHNOWAIT, null, IntPtr.Zero);

                if (first)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(marker));
                        File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Encoding.UTF8);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void RewriteTree(string root, string exe, string iconLocation)
        {
            string[] files;
            try { files = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { return; }

            foreach (string lnk in files)
            {
                try
                {
                    object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    object sc = shell.GetType().InvokeMember(
                        "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    Type st = sc.GetType();

                    string target = Convert.ToString(st.InvokeMember(
                        "TargetPath", BindingFlags.GetProperty, null, sc, null));
                    string name = Path.GetFileNameWithoutExtension(lnk);

                    bool isWinTune = name.IndexOf("WinTune", StringComparison.OrdinalIgnoreCase) >= 0
                        || (target != null && Path.GetFileName(target).Equals("WinTune.exe", StringComparison.OrdinalIgnoreCase));
                    if (!isWinTune) continue;

                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                        new object[] { Path.GetDirectoryName(exe) });
                    st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconLocation });
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                        new object[] { "WinTune · 系统优化与清理" });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lnk, IntPtr.Zero);
                }
                catch { }
            }
        }
    }
}
