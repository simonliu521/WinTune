using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace WinTune
{
    public static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        /// <summary>
        /// 运行期 DLL 全部放在 runtime 子目录：先把原生库搜索路径指过去。
        /// 托管程序集由下面的 AssemblyResolve 负责，不再依赖 WinTune.exe.config。
        /// 目录不存在时保持原样，方便只拷 exe 的调试场景。
        /// </summary>
        private static void InitRuntimeDir()
        {
            try
            {
                string rt = Path.Combine(Application.StartupPath, "runtime");
                if (Directory.Exists(rt)) SetDllDirectory(rt);
            }
            catch { }
        }

        /// <summary>
        /// 自己解析 runtime 里的托管程序集。这样配置文件可以只叫 WinTune.config，
        /// 不必再靠 &lt;exe&gt;.config 里的 probing privatePath 和 bindingRedirect。
        /// 同名程序集即使版本号对不上（例如要求 System.Memory 4.0.5.0、实际是 4.0.1.2），
        /// 也直接返回本地这一份；.NET Framework 允许 AssemblyResolve 这样重定向。
        /// </summary>
        private static Assembly ResolveRuntimeAssembly(object sender, ResolveEventArgs e)
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] dirs = { Path.Combine(baseDir, "runtime"), baseDir };
                foreach (string dir in dirs)
                {
                    string file = Path.Combine(dir, simple + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
            }
            catch { }
            return null;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveRuntimeAssembly;
            InitRuntimeDir();

            bool flag = false;
            bool selftest = false;
            bool repairjson = false;
            bool speedtest = false;
            bool depsjson = false;
            string speedServer = "auto";
            string diag = "";
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--elevated") flag = true;
                if (a == "--selftest") selftest = true;
                if (a == "--repairjson") repairjson = true;
                if (a == "--depsjson") depsjson = true;
                if (a == "--speedtest")
                {
                    speedtest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) speedServer = args[i + 1];
                }
                if (a == "--diag" && i + 1 < args.Length) diag = args[i + 1];
            }

            /* 测速的无人值守自检：不开窗口，把完整过程与结果写进 speedtest.log，便于核对是否真测满 10 秒 */
            if (speedtest)
            {
                StringBuilder sp = new StringBuilder();
                NetFix.Run("speedtest", speedServer, "", delegate (string json) { sp.AppendLine(json); });
                string dirS = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune");
                Directory.CreateDirectory(dirS);
                File.WriteAllText(Path.Combine(dirS, "speedtest.log"), sp.ToString(), Encoding.UTF8);
                return;
            }

            /* 维护修复的体检结果导出，用于排查“执行过但状态不对”的情况 */
            if (repairjson)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "repair.json"), Repair.ListJson(), Encoding.UTF8);
                return;
            }

            if (selftest)
            {
                SelfTest.Run();
                return;
            }

            /* 只导出 WinTune 自己的运行依赖（WebView2 / .NET / VC++），不碰 MiniMonitor */
            if (depsjson)
            {
                string dirD = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune");
                Directory.CreateDirectory(dirD);
                File.WriteAllText(Path.Combine(dirD, "deps.json"), Tools.DepsJson(), Encoding.UTF8);
                return;
            }

            if (diag.Length > 0)
            {
                if (!SysInfo.IsAdmin())
                {
                    try
                    {
                        ProcessStartInfo psi2 = new ProcessStartInfo(Application.ExecutablePath, "--diag \"" + diag + "\"");
                        psi2.Verb = "runas";
                        psi2.UseShellExecute = true;
                        Process.Start(psi2);
                    }
                    catch { }
                    return;
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("path=" + diag);
                sb.AppendLine("exists=" + Directory.Exists(diag));
                try
                {
                    string[] fs = Directory.GetFiles(diag, "*", SearchOption.AllDirectories);
                    sb.AppendLine("files=" + fs.Length);
                    foreach (string f in fs) sb.AppendLine("  " + f);
                }
                catch (Exception ex) { sb.AppendLine("  !! " + ex.Message); }
                List<Occ> occ = Residue.FindAll(diag);
                sb.AppendLine("occ=" + occ.Count);
                foreach (Occ o in occ)
                    sb.AppendLine("  " + o.Kind + " | " + o.Name + " | pid=" + o.Pid + " | " + o.Detail);
                string dlog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "diag.log");
                Directory.CreateDirectory(Path.GetDirectoryName(dlog));
                File.WriteAllText(dlog, sb.ToString(), Encoding.UTF8);
                return;
            }

            // 单实例：已有一个在跑就退出，避免两个界面同时删文件
            bool created = true;
            Mutex mutex = new Mutex(true, @"Local\WinTuneSingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("WinTune 已经在运行。", "WinTune", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 需要管理员：清理系统目录、写 HKLM、建还原点都要
            if (!SysInfo.IsAdmin() && !flag)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated");
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // 用户拒绝了 UAC，仍以普通权限启动，界面会明确标出受限功能
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { IconRepair.Ensure(); } catch { }
            });

            /* 整包拷到全新电脑时最可能缺的就是 WebView2：缺了自己装，装不上才提示 */
            string dep = Dependency.EnsureWithUi();
            if (dep.Length > 0)
            {
                MessageBox.Show(
                    "这台电脑缺少界面组件（WebView2 Runtime），自动安装没有成功。\n\n" +
                    dep + "\n\n" +
                    "手动装一次就能用：https://developer.microsoft.com/microsoft-edge/webview2/\n" +
                    "或者把离线安装包 MicrosoftEdgeWebView2RuntimeInstallerX64.exe 放进本软件目录的 WebView2 文件夹，再双击 WinTune.exe。",
                    "WinTune", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            /* 启动时先确保 PawnIO 驱动在跑，再拉起 MiniMonitor；
               缺驱动时的安装提示由 MiniMonitor 自己负责。退出时不管它。 */
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { MiniBoot.Ensure(); } catch { }
            });

            Application.Run(new MainForm());

            GC.KeepAlive(mutex);
        }
    }
}
