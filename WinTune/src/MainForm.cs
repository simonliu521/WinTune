using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace WinTune
{
    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_REMOTESESSION = 0x1000;

        private WebView2 wv;
        private SplashOverlay splash;
        private System.Windows.Forms.Timer readyTimer;
        private bool uiRevealed;
        private string appDir;
        private volatile bool scanning;
        private volatile bool cancelScan;
        private List<CleanItem> lastScan = new List<CleanItem>();

        public MainForm()
        {
            Text = "WinTune · 系统优化与清理";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 860);
            MinimumSize = new Size(1080, 720);
            BackColor = Color.FromArgb(14, 17, 22);

            try
            {
                string ico = Path.Combine(Application.StartupPath, @"assets\icon.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            appDir = Path.Combine(Application.StartupPath, "app");

            wv = new WebView2();
            wv.Dock = DockStyle.Fill;
            try { wv.DefaultBackgroundColor = Color.FromArgb(14, 17, 22); } catch { }
            Controls.Add(wv);

            /* 盖住 WebView2 首次创建前的空白：深色过渡层先出现，
               等前端把 CPU 温度、内存等首屏数据真正画出来后再淡出 */
            splash = new SplashOverlay();
            Controls.Add(splash);
            splash.BringToFront();

            HandleCreated += OnHandleCreated2;
            Shown += OnShown2;
        }

        private void OnHandleCreated2(object sender, EventArgs e)
        {
            try { int v = 1; DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }
            try { int v2 = 1; DwmSetWindowAttribute(Handle, 19, ref v2, 4); } catch { }
        }

        private static bool RemoteAgentRunning()
        {
            /* 向日葵/ToDesk/AnyDesk/RustDesk/CrossDesk 这类工具走的是控制台镜像，
               SM_REMOTESESSION 仍是 0，只能靠进程名判断。 */
            string[] names = new string[] {
                "crossdesk", "crossdesk_service", "crossdesk_session_helper",
                "rustdesk", "todesk", "sunlogin", "sunloginclient", "anydesk",
                "teamviewer", "parsec", "vncserver", "winvnc", "mstsc",
                "gotohttp", "oray", "awesun", "splashtop", "anyviewer",
                "nomachine", "rutserv", "remoteutilities"
            };
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    string n = (p.ProcessName ?? "").ToLowerInvariant();
                    for (int i = 0; i < names.Length; i++)
                        if (n == names[i]) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsRemoteSession()
        {
            try
            {
                if (GetSystemMetrics(SM_REMOTESESSION) != 0) return true;
                string sn = Environment.GetEnvironmentVariable("SESSIONNAME") ?? "";
                if (sn.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)) return true;
                if (RemoteAgentRunning()) return true;
            }
            catch { }
            return false;
        }

        private static string BrowserArgsFor(bool remote)
        {
            /* 远程桌面下 WebView2 的 GPU/DirectComposition 设备会在 RDP 显示通道重建时丢失，
               表现为整个远程桌面卡死、只能断开重连。强制软件合成并关掉窗口遮挡节流。 */
            return remote ? "--disable-gpu --disable-features=CalculateNativeWinOcclusion" : "";
        }

        private void OnShown2(object sender, EventArgs e)
        {
            if (!Directory.Exists(appDir))
            {
                MessageBox.Show("缺少界面目录：\n" + appDir + "\n\n请把 app 文件夹与 WinTune.exe 放在同一目录。",
                    "WinTune", MessageBoxButtons.OK, MessageBoxIcon.Error);
                try { if (splash != null) splash.Fail(); } catch { }
                return;
            }
            try
            {
                CoreWebView2CreationProperties props = new CoreWebView2CreationProperties();
                props.UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "webview2");
                bool remote = IsRemoteSession();
                string browserArgs = BrowserArgsFor(remote);
                if (browserArgs.Length > 0) props.AdditionalBrowserArguments = browserArgs;
                Log.Info("ui", remote ? "远程桌面/远控会话：WebView2 已切换软件渲染" : "本地会话：WebView2 使用硬件加速");
                wv.CreationProperties = props;
                Task init = wv.EnsureCoreWebView2Async(null);
                init.ContinueWith(AfterInit, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                MessageBox.Show("界面组件启动失败：" + ex.Message, "WinTune", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 前端首屏数据就绪（CPU 温度、内存等已经画出来）后才撤掉启动层。
        /// 传感器读不到时不能让遮罩永远盖着，所以 9 秒兜底强制进入。
        /// </summary>
        private void ArmReadyFallback()
        {
            if (readyTimer != null)
            {
                try { readyTimer.Stop(); readyTimer.Dispose(); } catch { }
            }
            readyTimer = new System.Windows.Forms.Timer();
            readyTimer.Interval = 9000;
            readyTimer.Tick += delegate
            {
                readyTimer.Stop();
                RevealUi("界面已就绪");
            };
            readyTimer.Start();
        }

        private void RevealUi(string status)
        {
            if (uiRevealed) return;
            uiRevealed = true;
            if (readyTimer != null)
            {
                try { readyTimer.Stop(); readyTimer.Dispose(); } catch { }
                readyTimer = null;
            }
            if (splash == null) return;
            try { splash.SetStatus(status); } catch { }
            try { splash.Finish(); } catch { }
        }

        private void AfterInit(Task t)
        {
            if (t.IsFaulted)
            {
                Exception b = t.Exception.GetBaseException();
                MessageBox.Show("界面组件没能启动（WebView2 Runtime 缺失或被策略禁用）。\n\n" + b.Message +
                    "\n\n手动装一次 WebView2 运行时后重开即可：\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                    "WinTune", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            try
            {
                CoreWebView2 core = wv.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping("winapp.local", appDir,
                    CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += OnMessage;
                core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs ev)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (splash == null) return;
                            if (splash.IsDisposed) return;
                            if (ev.IsSuccess)
                            {
                                /* 页面加载完只是“壳”到位，温度、内存还没采回来；
                                   这里只换状态文字，不撤遮罩，等前端发 uiReady。 */
                                splash.SetStatus("正在读取硬件数据");
                            }
                            else
                            {
                                if (readyTimer != null)
                                {
                                    try { readyTimer.Stop(); readyTimer.Dispose(); } catch { }
                                    readyTimer = null;
                                }
                                splash.Fail();
                            }
                        }));
                    }
                    catch { }
                };
                ArmReadyFallback();
                core.Navigate("https://winapp.local/index.html");
                Log.Info("ui", "界面已加载");
            }
            catch (Exception ex)
            {
                Log.Error("ui", ex.Message);
                if (readyTimer != null)
                {
                    try { readyTimer.Stop(); readyTimer.Dispose(); } catch { }
                    readyTimer = null;
                }
                try { if (splash != null) splash.Fail(); } catch { }
            }
        }

        public void Send(string json)
        {
            if (wv == null) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Send), json); } catch { }
                return;
            }
            try
            {
                if (wv.CoreWebView2 != null) wv.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        /// <summary>重活放后台：repairList / netInfo / appsList 会跑 WMI、进程、schtasks，
        /// 以前在 WebMessageReceived 里同步执行，等于把 WebView 的 UI 线程按住，表现就是点一下卡几秒。</summary>
        private void SendAsync(Func<string> work, string scope)
        {
            Thread th = new Thread(delegate ()
            {
                try
                {
                    string js = work();
                    if (!string.IsNullOrEmpty(js)) Send(js);
                }
                catch (Exception ex)
                {
                    Log.Error("ui", scope + " 失败：" + ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", scope).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string cmd = "";
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                d = Json.Parse(e.WebMessageAsJson);
                cmd = Json.Str(d, "cmd");
            }
            catch (Exception ex)
            {
                Log.Warn("ui", "消息解析失败：" + ex.Message);
                return;
            }

            switch (cmd)
            {
                case "init":
                case "refresh":
                    SendInit();
                    break;
                case "scan":
                    StartScan();
                    break;
                case "cancel":
                    cancelScan = true;
                    break;
                case "prepare":
                    SendPrepare(Json.StrList(d, "ids"));
                    break;
                case "execute":
                    StartExecute(Json.StrList(d, "ids"));
                    break;
                case "openBackup":
                    OpenFolder(BackupDir());
                    break;
                case "openLog":
                    OpenFolder(Log.Dir);
                    break;
                case "openPath":
                    OpenFolder(Json.Str(d, "path"));
                    break;
                case "exportReport":
                    ExportReport();
                    break;
                case "openUrl":
                    OpenUrl(Json.Str(d, "url"));
                    break;
                case "tweakList":
                    Send(Tweaks.ListJson());
                    break;
                case "optPreset":
                    Send(Tweaks.PresetJson());
                    break;
                case "tweakApply":
                    StartTweak(Json.StrList(d, "ids"), false);
                    break;
                case "tweakRevert":
                    StartTweak(Json.StrList(d, "ids"), true);
                    break;
                case "appsList":
                    SendAsync(new Func<string>(Apps.ListJson), "appsList");
                    break;
                case "appsRec":
                    SendAsync(new Func<string>(Apps.RecommendJson), "appsRec");
                    break;
                case "appUninstall":
                    Send(Apps.Uninstall(Json.Str(d, "id")));
                    break;
                case "appLeftover":
                    StartAppLeftover(Json.Str(d, "id"));
                    break;
                case "appRemove":
                    StartAppRemove(Json.Str(d, "id"), Json.Str(d, "mode"));
                    break;
                case "appLocation":
                    Send(Apps.OpenLocation(Json.Str(d, "id")));
                    break;
                case "appInstall":
                    StartAppInstall(Json.Str(d, "id"));
                    break;
                case "wingetPage":
                    Send(Apps.OpenWingetPage());
                    break;
                case "repairList":
                    SendAsync(new Func<string>(Repair.ListJson), "repairList");
                    break;
                case "repairRun":
                    StartRepair(Json.Str(d, "id"));
                    break;
                case "repairCancel":
                    Repair.Cancel();
                    break;
                case "netInfo":
                    SendAsync(new Func<string>(NetFix.InfoJson), "netInfo");
                    break;
                case "netRun":
                    StartNet(Json.Str(d, "action"), Json.Str(d, "a1"), Json.Str(d, "a2"));
                    break;
                case "netCancel":
                    NetFix.Cancel();
                    break;
                case "netCheck":
                    StartNetCheck();
                    break;
                case "maintScan":
                    StartMaintScan(Json.Str(d, "mod"));
                    break;
                case "maintApply":
                    StartMaintApply(Json.Str(d, "mod"), Json.StrList(d, "actions"));
                    break;
                case "updScan":
                    StartUpdScan();
                    break;
                case "updApply":
                    StartUpdApply(Json.StrList(d, "actions"));
                    break;
                case "uiReady":
                    RevealUi("硬件数据已就绪");
                    break;
                case "hwFast":
                    StartHw(true);
                    break;
                case "hwSample":
                    StartHw(false);
                    break;
                case "mmGet":
                    SendAsync(new Func<string>(Hardware.MmJson), "mmGet");
                    break;
                case "mmSet":
                    {
                        string k = Json.Str(d, "key"), v = Json.Str(d, "value");
                        SendAsync(delegate { return Hardware.MmSet(k, v); }, "mmSet");
                    }
                    break;
                case "deps":
                    SendAsync(new Func<string>(Tools.DepsJson), "deps");
                    break;
                case "evidence":
                    SendAsync(new Func<string>(Evidence.Json), "evidence");
                    break;
                default:
                    Log.Warn("ui", "未知命令：" + cmd);
                    break;
            }
        }

        private string BackupDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinTune", "backup");
        }

        /* 硬件采样放后台线程：3Hz 刷新不能占着 UI 线程；上一次没回来就丢掉这次，
           但慢帧请求（含温度/风扇）丢不得，记个标记等它回来再补一发 */
        private bool hwBusy;
        private bool hwPendingFull;

        private void StartHw(bool fast)
        {
            if (hwBusy)
            {
                if (!fast) hwPendingFull = true;
                return;
            }
            hwBusy = true;
            Thread th = new Thread(delegate ()
            {
                string js = "";
                try { js = Hardware.SampleJson(fast); }
                catch (Exception ex) { Log.Warn("hw", "采样失败：" + ex.Message); }
                if (js.Length > 0) Send(js);
                try
                {
                    BeginInvoke(new Action(delegate ()
                    {
                        hwBusy = false;
                        if (hwPendingFull)
                        {
                            hwPendingFull = false;
                            StartHw(false);
                        }
                    }));
                }
                catch { hwBusy = false; }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartMaintScan(string mod)
        {
            Thread th = new Thread(delegate ()
            {
                try { Send(Maintenance.ScanJson(mod)); }
                catch (Exception ex)
                {
                    Log.Error("maint", "扫描失败：" + ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "maint").Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartMaintApply(string mod, List<string> actions)
        {
            Thread th = new Thread(delegate ()
            {
                try
                {
                    Send(Maintenance.Apply(mod, actions, null));
                    Send(Maintenance.ScanJson(mod));
                }
                catch (Exception ex)
                {
                    Log.Error("maint", "执行失败：" + ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "maint").Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartUpdScan()
        {
            Thread th = new Thread(delegate ()
            {
                try { Send(UpdateResidue.ScanJson(null)); }
                catch (Exception ex)
                {
                    Log.Error("upd", "扫描失败：" + ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "upd").Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartUpdApply(List<string> paths)
        {
            Thread th = new Thread(delegate ()
            {
                try
                {
                    Send(UpdateResidue.Apply(paths, null));
                    Send(UpdateResidue.ScanJson(null));
                }
                catch (Exception ex)
                {
                    Log.Error("upd", "执行失败：" + ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "upd").Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void SendInit()
        {
            string dir = BackupDir();
            Send(new JB()
                .Add("type", "init")
                .Add("version", "1.5.38")
                .Add("admin", SysInfo.IsAdmin())
                .Add("osName", SysInfo.OsName())
                .Add("build", SysInfo.BuildNumber())
                .Add("arch", SysInfo.Arch())
                .Add("winget", SysInfo.HasWinget())
                .Add("choco", SysInfo.HasChoco())
                .Add("pwsh7", SysInfo.HasPwsh7())
                .Add("policy", SysInfo.PolicyHint())
                .Add("restore", SysInfo.RestorePointHint())
                .Add("uptime", SysInfo.Uptime())
                .Raw("disk", SysInfo.DiskJson("C:"))
                .Add("logPath", Log.CurrentFile)
                .Add("backupDir", dir)
                .End());
        }

        private void StartScan()
        {
            if (scanning) return;
            scanning = true;
            cancelScan = false;
            Send(new JB().Add("type", "scanStart").End());

            Thread th = new Thread(delegate ()
            {
                try
                {
                    List<CleanItem> items;
                    string done = Scanner.ScanAsJson(Send, delegate () { return cancelScan; }, out items);
                    lastScan = items;
                    Send(done);
                }
                catch (Exception ex)
                {
                    Log.Error("scan", ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "scan").Add("msg", ex.Message).End());
                }
                finally
                {
                    scanning = false;
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void SendPrepare(List<string> ids)
        {
            List<Cleaner.Plan> plans = Cleaner.BuildPlan(lastScan, ids);
            StringBuilder arr = new StringBuilder();
            arr.Append('[');
            long runnableBytes = 0;
            int ok = 0, skip = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                Cleaner.Plan p = plans[i];
                if (i > 0) arr.Append(',');
                arr.Append(new JB()
                    .Add("id", p.Item.Id)
                    .Add("name", p.Item.Name)
                    .Add("tier", p.Item.Tier)
                    .Add("bytes", p.Bytes)
                    .Add("ok", p.Runnable)
                    .Add("why", p.Why)
                    .Add("note", p.Item.Note)
                    .End());
                if (p.Runnable) { ok++; runnableBytes += p.Bytes; } else skip++;
            }
            arr.Append(']');
            Send(new JB()
                .Add("type", "prepareDone")
                .Add("okCount", ok)
                .Add("skipCount", skip)
                .Add("bytes", runnableBytes)
                .Raw("plans", arr.ToString())
                .End());
        }

        private void StartExecute(List<string> ids)
        {
            List<Cleaner.Plan> plans = Cleaner.BuildPlan(lastScan, ids);

            Send(new JB().Add("type", "cleanStart").Add("total", plans.Count).End());

            Thread th = new Thread(delegate ()
            {
                CleanResult r = new CleanResult();
                try
                {
                    r = Cleaner.Execute(plans, Send);
                }
                catch (Exception ex)
                {
                    Log.Error("clean", "执行异常：" + ex.Message);
                }

                Send(new JB()
                    .Add("type", "cleanDone")
                    .Add("freed", r.Freed)
                    .Add("files", r.DeletedFiles)
                    .Add("pending", r.PendingFiles)
                    .Add("skipped", r.Skipped)
                    .Add("failed", r.Failed)
                    .Add("elapsed", r.ElapsedMs)
                    .Add("manifest", r.Manifest)
                    .Add("restore", r.RestoreNote)
                    .Raw("details", StringsToJson(r.FailDetail))
                    .End());
            });
            th.IsBackground = true;
            th.Start();
        }

        private static string StringsToJson(List<string> list)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < list.Count && i < 40; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json.Quote(list[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void ExportReport()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("WinTune 环境报告");
                sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine();
                sb.AppendLine("系统：" + SysInfo.OsName());
                sb.AppendLine("架构：" + SysInfo.Arch());
                sb.AppendLine("管理员：" + SysInfo.IsAdmin());
                sb.AppendLine("包管理器：winget=" + SysInfo.HasWinget() + "  choco=" + SysInfo.HasChoco());
                sb.AppendLine("PowerShell 7：" + SysInfo.HasPwsh7());
                sb.AppendLine("组策略：" + SysInfo.PolicyHint());
                sb.AppendLine("系统还原：" + SysInfo.RestorePointHint());
                sb.AppendLine("运行时长：" + SysInfo.Uptime());
                sb.AppendLine();
                sb.AppendLine("--- 上次扫描结果 ---");
                foreach (CleanItem it in lastScan)
                {
                    sb.AppendLine("[" + it.Tier + "] " + it.Name + "  " + it.Bytes + " 字节  " + it.Files + " 文件  " +
                                  (it.Available ? "" : "不可用:" + it.SkipReason));
                    if (it.Path.Length > 0) sb.AppendLine("    " + it.Path);
                }
                sb.AppendLine();
                sb.AppendLine("日志：" + Log.CurrentFile);
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                Send(new JB().Add("type", "report").Add("ok", true).Add("path", file).End());
            }
            catch (Exception ex)
            {
                Send(new JB().Add("type", "report").Add("ok", false).Add("msg", ex.Message).End());
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (File.Exists(path))
                {
                    string d = Path.GetDirectoryName(path);
                    if (d != null) path = d;
                }

                /* Directory.Exists 在“存在但没权限”时也返回 false（Test-Path 会抛拒绝访问），
                   所以这里用 GetAttributes 分三态：可访问 / 存在但被拒 / 真的没了 */
                int st = ProbePath(path);
                if (st == 0)
                {
                    Log.Warn("shell", "目录不存在：" + path);
                    Send(new JB().Add("type", "openPathResult").Add("ok", false)
                        .Add("reason", "gone").Add("path", path).End());
                    return;
                }

                /* explorer 用发起会话的普通权限打开；系统保留目录（NetworkService / LocalService 的
                 ServiceProfiles）和 ACL 拒绝的目录会弹“位置不可用”，先拦下来并说明清楚 */
                if (st == -1 || IsExplorerLocked(path) || !CanList(path))
                {
                    string opened = NearestReadable(path);
                    if (opened.Length > 0)
                    {
                        try { Process.Start("explorer.exe", "\"" + opened + "\""); }
                        catch (Exception ex) { Log.Warn("shell", "打开上级目录失败：" + ex.Message); }
                    }
                    Log.Info("shell", "目录不可由资源管理器打开：" + path + " → 上级 " + opened);
                    Send(new JB().Add("type", "openPathResult").Add("ok", false)
                        .Add("reason", "denied").Add("path", path).Add("opened", opened).End());
                    return;
                }

                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("shell", "打开目录失败：" + ex.Message);
                Send(new JB().Add("type", "openPathResult").Add("ok", false)
                    .Add("reason", "error").Add("path", path).Add("msg", ex.Message).End());
            }
        }

        /// <summary>1=可访问；0=真的不存在；-1=存在但当前账户无权访问。</summary>
        private static int ProbePath(string p)
        {
            try
            {
                File.GetAttributes(p);
                return 1;
            }
            catch (UnauthorizedAccessException) { return -1; }
            catch (System.Security.SecurityException) { return -1; }
            catch (Exception) { return 0; }
        }

        /// <summary>当前进程能否列出该目录（列不出来，资源管理器也打不开）。</summary>
        private static bool CanList(string dir)
        {
            try { Directory.GetFileSystemEntries(dir); return true; }
            catch { return false; }
        }

        /// <summary>资源管理器（普通权限）必然打不开的系统保留目录。</summary>
        private static bool IsExplorerLocked(string path)
        {
            try
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string sp = Path.Combine(win, "ServiceProfiles");
                /* ServiceProfiles 目录本身能打开，只有它下面 NetworkService / LocalService 的
                   配置子目录才是系统独占的 */
                return path.Length > sp.Length &&
                       path.StartsWith(sp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>从目标往上找最近一个能列出内容的目录，作为打开失败时的落点。</summary>
        private static string NearestReadable(string path)
        {
            string up = path;
            for (int i = 0; i < 12; i++)
            {
                string parent = Path.GetDirectoryName(up);
                if (string.IsNullOrEmpty(parent) || parent == up) break;
                up = parent;
                if (ProbePath(up) != 1) continue;
                try { Directory.GetFileSystemEntries(up); return up; }
                catch { }
            }
            return "";
        }

        private void StartTweak(List<string> ids, bool revert)
        {
            if (ids.Count == 0) return;
            Thread th = new Thread(delegate ()
            {
                try
                {
                    if (revert) Send(Tweaks.Revert(ids, Send));
                    else Send(Tweaks.Apply(ids, Send));
                }
                catch (Exception ex)
                {
                    Log.Error("tweak", ex.Message);
                    Send(new JB().Add("type", "error").Add("scope", "tweak").Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartAppInstall(string id)
        {
            if (id.Length == 0) return;
            Thread th = new Thread(delegate ()
            {
                try { Apps.Install(id, Send); }
                catch (Exception ex)
                {
                    Log.Error("apps", ex.Message);
                    Send(new JB().Add("type", "appDone").Add("ok", false).Add("id", id).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartAppLeftover(string id)
        {
            if (id.Length == 0) return;
            Thread th = new Thread(delegate ()
            {
                try { Send(Apps.Leftover(id)); }
                catch (Exception ex)
                {
                    Log.Error("apps", ex.Message);
                    Send(new JB().Add("type", "appLeftover").Add("ok", false).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartAppRemove(string id, string mode)
        {
            if (id.Length == 0) return;
            Thread th = new Thread(delegate ()
            {
                try { Apps.Remove(id, mode, Send); }
                catch (Exception ex)
                {
                    Log.Error("apps", ex.Message);
                    Send(new JB().Add("type", "appRemoveDone").Add("ok", false).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartRepair(string id)
        {
            if (id.Length == 0) return;
            Thread th = new Thread(delegate ()
            {
                try { Repair.Run(id, Send); }
                catch (Exception ex)
                {
                    Log.Error("repair", ex.Message);
                    Send(new JB().Add("type", "repairDone").Add("id", id).Add("ok", false).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        /// <summary>网络修复动作统一放后台线程：netsh / ipconfig 会阻塞，测速更要几十秒。</summary>
        private void StartNetCheck()
        {
            Thread th = new Thread(delegate ()
            {
                try { Send(NetFix.CheckJson()); }
                catch (Exception ex)
                {
                    Log.Error("net", "体检失败：" + ex.Message);
                    Send(new JB().Add("type", "netCheck").Add("count", 0).Add("items", "[]")
                        .Add("err", ex.Message).Add("ms", 0).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }
        private void StartNet(string action, string a1, string a2)
        {
            if (action.Length == 0) return;
            Thread th = new Thread(delegate ()
            {
                try { NetFix.Run(action, a1, a2, Send); }
                catch (Exception ex)
                {
                    Log.Error("net", ex.Message);
                    Send(new JB().Add("type", "netDone").Add("action", action)
                        .Add("ok", false).Add("msg", ex.Message).End());
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void OpenUrl(string url)
        {
            try
            {
                string u = (url == null ? "" : url.Trim()).ToLowerInvariant();
                if (!u.StartsWith("https://") && !u.StartsWith("http://")) return;
                Process.Start(url);
            }
            catch { }
        }
    }
}





