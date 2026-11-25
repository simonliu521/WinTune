using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// WinTune 启动时的依赖启动顺序：
    /// 1) 先看 PawnIO 驱动服务；已安装但停了就 sc start；
    /// 2) 再拉起 MiniMonitor。MiniMonitor 自己会再检查一次依赖，并负责缺驱动时的安装提示。
    /// WinTune 退出时完全不碰 MiniMonitor。
    /// PawnIO 是内核驱动，任务管理器里通常没有同名进程，所以这里检测的是服务状态。
    /// </summary>
    public static class MiniBoot
    {
        private const string PawnioService = "PawnIO";

        public static void Ensure()
        {
            try
            {
                string exe = Hardware.FindMiniMonitorExe();

                /* 已安装但未运行：先启动驱动，再拉起 MiniMonitor。
                   未安装则不去静默安装，交给 MiniMonitor 的“安装 / 取消”提示。 */
                bool installed = ServiceExists();
                bool running = installed && ServiceRunning();
                if (installed && !running)
                {
                    ScStart();
                    running = ServiceRunning();
                }

                if (exe.Length == 0) return;

                /* 驱动已经在跑、但温度代理任务被删了：由 WinTune 这个管理员进程静默补建，
                   不额外弹 UAC，也不碰 PawnIO 安装。 */
                if (running && !TaskExists()) BootstrapTask(exe);

                if (!UiRunning()) StartUi(exe);
            }
            catch { }
        }

        private static bool UiRunning()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                    if (string.Equals(p.ProcessName, "MiniMonitor", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        private static bool ServiceExists()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + PawnioService))
                    return k != null;
            }
            catch { return false; }
        }

        private static bool ServiceRunning()
        {
            string o = Capture(Sys("sc.exe"), "query " + PawnioService, 6000);
            return o.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TaskExists()
        {
            return Run(Sys("schtasks.exe"), "/Query /TN \"MiniMonitorWidget\"", 8000) == 0;
        }

        private static void BootstrapTask(string exe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--bootstrap-task");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                using (Process p = Process.Start(psi)) p.WaitForExit(15000);
            }
            catch { }
        }

        private static void ScStart()
        {
            Run(Sys("sc.exe"), "start " + PawnioService, 8000);
        }

        private static void StartUi(string exe)
        {
            try
            {
                /* 用 explorer 代启，界面进程和 WinTune 彻底脱钩：
                   WinTune 退出、崩溃都不会把它带走，也不会继承管理员令牌。 */
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + exe + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch { }
        }

        private static string Sys(string file)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file);
        }

        private static int Run(string exe, string args, int ms)
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
                    p.WaitForExit(ms);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch { return -1; }
        }

        private static string Capture(string exe, string args, int ms)
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
                    StringBuilder sb = new StringBuilder();
                    sb.Append(p.StandardOutput.ReadToEnd());
                    p.WaitForExit(ms);
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }
    }
}
