using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// WinTune 启动时只做一次 MiniMonitor 检查：
    /// 进程在、PawnIO 服务在跑、温度代理任务也在跑，就什么都不做；
    /// 缺哪一样就补哪一样。WinTune 退出时完全不碰 MiniMonitor。
    /// </summary>
    public static class MiniBoot
    {
        private const string PawnioService = "PawnIO";
        private const string AgentTask = "MiniMonitorWidget";

        public static void Ensure()
        {
            try
            {
                string exe = Hardware.FindMiniMonitorExe();
                if (exe.Length == 0) return;

                bool proc = UiRunning();
                bool driver = ServiceExists();
                bool driverRun = driver && ServiceRunning();
                bool task = TaskExists();
                bool agentRun = task && TaskRunning();

                if (proc && driverRun && task && agentRun) return;   // 都已经在，免打扰

                /* 驱动或任务缺失：交给 MiniMonitor 自己的 --bootstrap 一次补齐。
                   WinTune 正常是管理员，子进程直接继承权限，不额外弹 UAC。 */
                if (!driver || !task)
                {
                    Bootstrap(exe);
                    if (!proc) StartUi(exe);
                    return;
                }

                if (!driverRun) ScStart();
                if (!proc) { StartUi(exe); return; }
                if (task && !agentRun) Schtasks("/Run /TN \"" + AgentTask + "\"");
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
            return Run(Sys("schtasks.exe"), "/Query /TN \"" + AgentTask + "\"", 8000) == 0;
        }

        private static bool TaskRunning()
        {
            string o = Capture(Sys("schtasks.exe"), "/Query /TN \"" + AgentTask + "\" /FO LIST", 8000);
            return o.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0
                || o.IndexOf("正在运行", StringComparison.Ordinal) >= 0;
        }

        private static void ScStart()
        {
            Run(Sys("sc.exe"), "start " + PawnioService, 8000);
        }

        private static void Schtasks(string args)
        {
            Run(Sys("schtasks.exe"), args, 8000);
        }

        private static void Bootstrap(string exe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--bootstrap");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch { }
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
