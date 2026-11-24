using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 运行依赖自检。整个文件夹拷到全新电脑，最可能缺的就是 WebView2 运行时
    /// （Win10 不预装，Win11 通常有）。缺了先自己装，装不上才提示。
    /// </summary>
    public static class Dependency
    {
        private const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        public static bool HasWebView2()
        {
            string[] keys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientId,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientId
            };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(keys[i]))
                    {
                        if (rk != null)
                        {
                            string pv = Convert.ToString(rk.GetValue("pv"));
                            if (pv != null && pv.Trim().Length > 0) return true;
                        }
                    }
                }
                catch { }
            }
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\EdgeUpdate\Clients\" + ClientId))
                {
                    if (rk != null)
                    {
                        string pv = Convert.ToString(rk.GetValue("pv"));
                        if (pv != null && pv.Trim().Length > 0) return true;
                    }
                }
            }
            catch { }
            /* 包里自带固定版本（WebView2\EBWebView）也算就绪 */
            try
            {
                if (Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2", "EBWebView"))) return true;
            }
            catch { }
            return false;
        }

        /// <summary>缺依赖就自己装。返回空串表示可以继续启动，否则是失败原因。</summary>
        public static string EnsureWithUi()
        {
            if (HasWebView2()) return "";
            try { return InstallWithUi(); }
            catch (Exception ex) { return ex.Message; }
        }

        private static string InstallWithUi()
        {
            Form f = new Form();
            f.Text = "WinTune";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(448, 128);
            f.BackColor = Color.FromArgb(18, 22, 28);
            f.ForeColor = Color.FromArgb(230, 236, 243);
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.ControlBox = false;
            f.ShowInTaskbar = true;

            Label l = new Label();
            l.AutoSize = false;
            l.SetBounds(20, 18, 408, 42);
            l.Text = "第一次在这台电脑上运行，正在准备界面组件（WebView2）…";
            l.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            Label l2 = new Label();
            l2.AutoSize = false;
            l2.SetBounds(20, 56, 408, 20);
            l2.Text = "大约 1～2 分钟，会联网下载，中途别关。";
            l2.ForeColor = Color.FromArgb(140, 154, 172);
            l2.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);

            ProgressBar pb = new ProgressBar();
            pb.SetBounds(20, 84, 408, 6);
            pb.Style = ProgressBarStyle.Marquee;
            pb.MarqueeAnimationSpeed = 26;

            f.Controls.Add(l);
            f.Controls.Add(l2);
            f.Controls.Add(pb);

            string[] holder = new string[] { "" };
            Thread th = new Thread(delegate ()
            {
                try
                {
                    holder[0] = Install(delegate (string note)
                    {
                        try { f.BeginInvoke(new Action(delegate () { l.Text = note; })); }
                        catch { }
                    });
                }
                catch (Exception ex) { holder[0] = ex.Message; }
                try { f.BeginInvoke(new Action(delegate () { f.Close(); })); }
                catch { }
            });
            th.IsBackground = true;
            f.Shown += delegate (object s, EventArgs e) { th.Start(); };
            f.ShowDialog();
            return holder[0];
        }

        private static string Install(Action<string> note)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "deps");
            try { Directory.CreateDirectory(local); }
            catch { }

            /* 包里放了离线安装包就直接用，没网的电脑也能装 */
            string exe = "";
            string[] bundled = new string[]
            {
                Path.Combine(baseDir, "WebView2", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"),
                Path.Combine(baseDir, "WebView2", "MicrosoftEdgeWebview2Setup.exe"),
                Path.Combine(baseDir, "vendor", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe")
            };
            for (int i = 0; i < bundled.Length; i++)
            {
                try { if (File.Exists(bundled[i])) { exe = bundled[i]; break; } }
                catch { }
            }

            if (exe.Length == 0)
            {
                note("正在下载界面组件…");
                string dl = Path.Combine(local, "MicrosoftEdgeWebview2Setup.exe");
                if (!Download("https://go.microsoft.com/fwlink/p/?LinkId=2124703", dl))
                    return "没有联网，包里也没有 WebView2 离线安装包（放进 WebView2\\MicrosoftEdgeWebView2RuntimeInstallerX64.exe 即可离线安装）。";
                exe = dl;
            }

            note("正在安装界面组件，装完会自动继续…");
            int code = Run(exe, "/silent /install", 300000);
            if (HasWebView2()) return "";
            return "安装程序退出码 " + code + "，但系统里仍然没有检测到 WebView2 Runtime。";
        }

        private static bool Download(string url, string path)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   /* TLS 1.2 */
            }
            catch { }
            try
            {
                WebClient wc = new WebClient();
                wc.Headers.Add("User-Agent", "WinTune");
                wc.DownloadFile(url, path);
                return new FileInfo(path).Length > 100 * 1024;
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return false;
            }
        }

        private static int Run(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                if (p == null) return -1;
                int waited = 0;
                while (waited < timeoutMs)
                {
                    if (p.HasExited) return p.ExitCode;
                    Thread.Sleep(500);
                    waited += 500;
                }
                try { p.Kill(); } catch { }
                return -2;
            }
            catch (Exception ex)
            {
                Log.Warn("dep", "安装失败：" + ex.Message);
                return -3;
            }
        }
    }
}
