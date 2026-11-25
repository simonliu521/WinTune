// MiniMonitor - 嵌在任务栏右下角（时钟左边）的极简监控条
// 显示：内存占用（渐变圆环，双击清理内存）/ 上下行网速 / CPU 温度+占用 / GPU 温度+占用
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[StructLayout(LayoutKind.Sequential)]
public struct Utilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int L, T, R, B;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

public class MiniMonitor : Form
{
    const int W = 210, H = 40;
    static Color BgColor = Color.FromArgb(16, 16, 16);

    static readonly Color TextWhite = Color.FromArgb(243, 236, 224);
    static readonly Color TextGray = Color.FromArgb(176, 155, 128);
    static readonly Color Green = Color.FromArgb(91, 214, 160);
    static readonly Color UpRed = Color.FromArgb(255, 138, 91);
    static readonly Color Cyan = Color.FromArgb(80, 200, 230);
    static readonly Color SepColor = Color.FromArgb(46, 46, 50);

    Timer tick = new Timer();
    Timer anim = new Timer();
    Timer dockTick = new Timer();

    long lastIdle, lastKernel, lastUser;
    long lastRx, lastTx;
    double cpuPct, memPct, rxRate, txRate;
    ulong memTotal, memAvail;
    double gpuTemp = -1, gpuUtil = -1, cpuTemp = -1;
    double shownPct;
    int ringPhase;                        // 0 空闲 / 1 归零 / 2 回弹
    double phaseFrom, phaseTo;
    long animT0;                          // 本段动画起点（TickCount）
    double animFrom, animTo;
    const double AnimMs = 800;            // 每段动画时长：0.8 秒

    IntPtr nvmlDev = IntPtr.Zero;
    bool nvmlReady;
    IntPtr pdhQuery = IntPtr.Zero, pdhCounter = IntPtr.Zero, pdhBuf = IntPtr.Zero;
    bool pdhReady;

    // 内核级 CPU 温度：由 LhmTemp 后台采集（内嵌 LibreHardwareMonitor + PawnIO 驱动）

    string cfgPath;
    DateTime flashUntil = DateTime.MinValue;
    ToolTip tip = new ToolTip();
    MenuItem miShotCfg;
    MenuItem miHotkeyItem;
    MenuItem miSaveDirItem;
    public static bool Elevated;                     // 是否以管理员权限运行

    string lastKey;
    Bitmap ringBmp; int ringBmpPct = -1; bool firstDock = true;
    int dockCount;

    NetworkInterface[] nics;
    DateTime nicStamp = DateTime.MinValue;

    [DllImport("nvml.dll")] static extern int nvmlInit();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex(int index, out IntPtr dev);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization u);

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int buf, int len);
    const int PROCESS_QUERY_INFORMATION = 0x0400;
    const int PROCESS_SET_QUOTA = 0x0100;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint fmt, out uint bufSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("kernel32.dll")] static extern uint GetLastError();
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    // 程序自己的版本号：这是唯一的版本来源，启动时写进同目录 MiniMonitor.config 的 appSettings。
    // 外部程序（WinTune 等）读那个配置文件就能拿到这里的真实版本，升级本程序只需改这一行再编译。
    public const string AppVersion = "1.6.12";

    /// <summary>把 AppVersion / 构建日期写进 exe 同目录的 MiniMonitor.config（appSettings 段）。
    /// 写不进去（例如装在 Program Files 无写权限）就静默跳过，配置里保留打包时的值。</summary>
    static void SyncVersionConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string cfg = Path.Combine(dir, "MiniMonitor.config");
            string build = "";
            try { build = File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd"); }
            catch { }

            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.PreserveWhitespace = true;
            if (File.Exists(cfg))
            {
                doc.Load(cfg);
            }
            else
            {
                doc.LoadXml("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n</configuration>\r\n");
            }

            System.Xml.XmlElement root = doc.DocumentElement;
            if (root == null) return;

            System.Xml.XmlNode app = root.SelectSingleNode("appSettings");
            if (app == null)
            {
                app = doc.CreateElement("appSettings");
                root.AppendChild(app);
            }

            bool dirty = false;
            dirty |= SetCfgNode(doc, app, "Version", AppVersion);
            if (build.Length > 0) dirty |= SetCfgNode(doc, app, "Build", build);

            if (dirty) doc.Save(cfg);
        }
        catch { }
    }

    static bool SetCfgNode(System.Xml.XmlDocument doc, System.Xml.XmlNode app, string key, string value)
    {
        System.Xml.XmlNode node = app.SelectSingleNode("add[@key='" + key + "']");
        if (node == null)
        {
            System.Xml.XmlElement el = doc.CreateElement("add");
            System.Xml.XmlAttribute a = doc.CreateAttribute("key"); a.Value = key; el.Attributes.Append(a);
            a = doc.CreateAttribute("value"); a.Value = value; el.Attributes.Append(a);
            app.AppendChild(el);
            return true;
        }
        System.Xml.XmlAttribute v = node.Attributes["value"];
        if (v == null)
        {
            v = doc.CreateAttribute("value"); v.Value = value; node.Attributes.Append(v);
            return true;
        }
        if (v.Value == value) return false;
        v.Value = value;
        return true;
    }

    public MiniMonitor()
    {
        cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        BackColor = BgColor;
        DoubleBuffered = true;
        Text = "MiniMonitor";
        try
        {
            using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null) Icon = (Icon)ic.Clone();
        }
        catch { }
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        Load += OnLoad;
        FormClosing += delegate { try { Log("ui closing"); } catch { } RestoreTaskbar(); Shot.Unregister(Handle); LhmTemp.Stop(); };
        MouseDoubleClick += OnDoubleClick;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseEnter += delegate { RefreshTip(); };
        MouseHover += delegate { RefreshTip(); };
    }

    void RefreshTip()
    {
        try
        {
            tip.SetToolTip(this,
                "内存 " + (int)Math.Round(memPct) + "%（可用 " + (memAvail / 1073741824.0).ToString("0.0") + "/" + (memTotal / 1073741824.0).ToString("0.0") + " GB）\n" +
                "CPU " + (int)Math.Round(cpuPct) + "%　温度 " + (cpuTemp < 0 ? "--" : ((int)cpuTemp).ToString()) + "℃\n" +
                "GPU " + (gpuTemp < 0 ? "--" : ((int)gpuTemp).ToString()) + "℃　负载 " + (gpuUtil < 0 ? "--" : ((int)gpuUtil).ToString()) + "%\n" +
                "双击圆环清理内存 · 右键菜单");
        }
        catch { }
    }

    void OnLoad(object s, EventArgs e)
    {
        try
        {
            nvmlReady = (nvmlInit() == 0) && (nvmlDeviceGetHandleByIndex(0, out nvmlDev) == 0);
        }
        catch { nvmlReady = false; }

        try
        {
            pdhReady = (PdhOpenQuery(null, IntPtr.Zero, out pdhQuery) == 0)
                && (PdhAddCounter(pdhQuery, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out pdhCounter) == 0);
            if (pdhReady)
            {
                pdhBuf = Marshal.AllocHGlobal(256);
                PdhCollectQueryData(pdhQuery);
            }
        }
        catch { pdhReady = false; }

        GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
        NetStats(out lastRx, out lastTx);
        SampleGpu();
        SampleCpuTemp();
        Sample();
        shownPct = memPct;

        DockToTaskbar();

        tick.Interval = 1000;
        tick.Tick += delegate { Sample(); if ((tickN++ % 2) == 0) SampleTaskbarColor(); EnsureAgent(); MarkDirty(); };
        tick.Start();
        DependencyCheck();

        anim.Interval = 125;
        anim.Tick += delegate { Animate(); };
        anim.Start();

        dockTick.Interval = 2000;
        dockTick.Tick += delegate { DockToTaskbar(); if (++dockCount % 5 == 0) TrimSelf(); };
        dockTick.Start();

        // 右键菜单改为每次弹出时重建：老式 ContextMenu 的子菜单宽度会被记下来，
        // 路径改短后菜单还保持原来的宽度，只有重建才会重新量宽度。
        ContextMenu = null;

        Shot.LoadSettings();                         // 读取已保存的快捷键
        Shot.Register(Handle);                       // 全局截图热键
        Log("hotkey " + Shot.HotkeyName + " registered=" + Shot.Registered);
        Log("saveDir=" + Shot.EffectiveSaveDir);
        UpdateShotMenu();

        // 启动后 6 秒记录一次温度来源，便于核对 LHM 是否生效
        Timer tdbg = new Timer();
        tdbg.Interval = 6000;
        tdbg.Tick += delegate
        {
            tdbg.Stop(); tdbg.Dispose();
            Log("temp cpu=" + cpuTemp + " lhm=" + LhmTemp.Last + " admin=" + Elevated);
        };
        tdbg.Start();
    }

    void UpdateShotMenu()
    {
        if (miHotkeyItem != null)
            miHotkeyItem.Text = "快捷键　" + Shot.HotkeyName;
        if (miSaveDirItem != null)
            miSaveDirItem.Text = "存储位置　" + ShortDir(Shot.EffectiveSaveDir);
    }

    // 路径太长时只留末级目录，避免菜单被一个字串撑得很宽
    static string ShortDir(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        string t = p.TrimEnd('\\', '/');
        if (t.Length <= 18) return t;
        int i = t.LastIndexOfAny(new char[] { '\\', '/' });
        if (i <= 0) return t;
        string leaf = t.Substring(i + 1);
        if (leaf.Length == 0) return t;
        return "…\\" + leaf;
    }

    // CPU 内核温度靠 PawnIO 驱动，服务键在就认为装了
    static bool PawnioInstalled()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                return k != null;
        }
        catch { return false; }
    }

    void ShowWidgetMenu(Point clientPt)
    {
        try
        {
            try { tip.Hide(this); } catch { }   // 悬停气泡先收起来，别和菜单叠在一起
            List<PopMenu.MItem> items = BuildItems();
            PopMenu pm = new PopMenu(items);
            pm.ShowAt(PointToScreen(clientPt));
        }
        catch { }
    }

    List<PopMenu.MItem> BuildItems()
    {
        List<PopMenu.MItem> list = new List<PopMenu.MItem>();

        PopMenu.MItem miAuto = new PopMenu.MItem("开机自启", 0, delegate { ToggleAutoStart(); });
        miAuto.Toggle = true;
        miAuto.Checked = AutoStartOn();
        list.Add(miAuto);

        PopMenu.MItem miWinTune = new PopMenu.MItem("打开 WinTune", 5, delegate { OpenWinTune(); });
        list.Add(miWinTune);

        PopMenu.MItem miClean = new PopMenu.MItem("清理内存", 1, delegate { BeginClean(); });

        PopMenu.MItem miShotCfg = new PopMenu.MItem("截图设置", 2, null);
        miShotCfg.Children = new PopMenu.MItem[] {
            new PopMenu.MItem("快捷键　" + Shot.HotkeyName, -1, delegate { EditHotkey(); }),
            new PopMenu.MItem("存储位置　" + ShortDir(Shot.EffectiveSaveDir), -1, delegate { PickSaveDir(); })
        };

        list.Add(miClean);
        list.Add(miShotCfg);
        // 截图设置下面直接给出当前版本号；只是信息行，不带图标、点了就收菜单
        list.Add(new PopMenu.MItem("版本　" + AppVersion, -1, null));

        // 没装 PawnIO 驱动时给个一键入口（包里带了开源安装器）
        try
        {
            string setup = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PawnIO_setup.exe");
            if (!PawnioInstalled() && File.Exists(setup))
            {
                list.Add(new PopMenu.MItem("装温度驱动", 3, delegate
                {
                    bool ok = RunElevated(Application.ExecutablePath, "--bootstrap");
                    MessageBox.Show(ok
                        ? "温度驱动和后台代理都装好了，几秒内 CPU 温度就会出来。"
                        : "安装被取消了，CPU 温度会一直是 --。",
                        "MiniMonitor", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }));
            }
            else if (PawnioInstalled() && InstallerPath().Length > 0)
            {
                list.Add(new PopMenu.MItem("修复温度驱动", 3, delegate { RepairDriverAsync(); }));
            }
        }
        catch { }

        list.Add(new PopMenu.MItem("退出", 4, delegate { ExitAll(); }));
        return list;
    }

    static string FindWinTuneExe()
    {
        try
        {
            string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
            DirectoryInfo d = new DirectoryInfo(baseDir);
            for (int i = 0; i < 3 && d != null; i++)
            {
                string a = Path.Combine(d.FullName, "WinTune.exe");
                if (File.Exists(a)) return a;
                string b = Path.Combine(d.FullName, "WinTune", "WinTune.exe");
                if (File.Exists(b)) return b;
                d = d.Parent;
            }
        }
        catch { }
        return "";
    }

    static bool WinTuneProcessExists()
    {
        try { return Process.GetProcessesByName("WinTune").Length > 0; }
        catch { return false; }
    }

    static void StartWinTuneWithUac(string exe)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = true;
            psi.Verb = "runas";                     // 兜底：代理不在时才弹 UAC
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != 1223)         // 1223 = 用户点了“否”
                MessageBox.Show("启动 WinTune 失败：\n" + ex.Message,
                    "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动 WinTune 失败：\n" + ex.Message,
                "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 管理员代理进程里调用：它本身就是提权令牌，CreateProcess 出来的 WinTune 直接带管理员权限，
    // 不经过 UAC / 安全桌面，远控时不会因为 UAC 弹窗把画面卡住。
    static void LaunchWinTuneElevated()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("WinTune"))
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9);
                    SetForegroundWindow(h);
                    return;
                }
            }
            string exe = FindWinTuneExe();
            if (exe.Length == 0) return;
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi);
        }
        catch { }
    }

    void OpenWinTune()
    {
        // 已经在跑就把它的窗口拉到前面，不再开第二个实例
        try
        {
            foreach (Process p in Process.GetProcessesByName("WinTune"))
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9);              // SW_RESTORE
                    SetForegroundWindow(h);
                    return;
                }
            }
        }
        catch { }

        string exe = FindWinTuneExe();
        if (exe.Length == 0)
        {
            MessageBox.Show("没有在 MiniMonitor 的同级或上级目录里找到 WinTune.exe。\n" +
                "请保持 MiniMonitor 文件夹和 WinTune.exe 一起解压。",
                "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        /* 优先让管理员温度代理代启：它直接 CreateProcess，WinTune 继承管理员令牌，
           不需要 UAC。远控软件抓不到安全桌面时，原来那一发 UAC 会把整个画面卡住。 */
        if (TempBus.RequestOpenWinTune())
        {
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                System.Threading.Thread.Sleep(2500);
                if (!WinTuneProcessExists()) StartWinTuneWithUac(exe);
            });
            th.IsBackground = true;
            th.Start();
            return;
        }

        StartWinTuneWithUac(exe);
    }

    bool AutoStartOn()
    {
        try
        {
            object v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                "MiniMonitor", null);
            return v is string && ((string)v).Trim().Length > 0;
        }
        catch { return false; }
    }

    void ToggleAutoStart()
    {
        bool on = !AutoStartOn();
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (k == null) throw new Exception("打不开当前用户的启动项注册表");
                if (on) k.SetValue("MiniMonitor", "\"" + Application.ExecutablePath + "\"",
                    Microsoft.Win32.RegistryValueKind.String);
                else
                    try { k.DeleteValue("MiniMonitor", false); } catch { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("开机自启设置失败：" + ex.Message, "MiniMonitor",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 退出：界面进程和管理员温度代理必须一起收手。
    // 只 Close() 的话代理会继续跑（它是计划任务拉起的独立进程），还占着 exe，文件夹都删不掉。
    void StopTempAgent()
    {
        try { TempBus.RequestStop(); } catch { }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/End /TN \"" + TaskName + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);                                     // 直接结束任务实例，不等它
        }
        catch { }
    }

    void ExitAll()
    {
        try { Log("exit requested"); } catch { }
        StopTempAgent();
        Application.Exit();
    }

    void EditHotkey()
    {
        using (HotkeyDialog dlg = new HotkeyDialog())
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            bool ok = Shot.Apply(Handle, dlg.Mod, dlg.Vk);
            if (!ok) MessageBox.Show("该组合已被其他程序占用，换一个试试。", "截图快捷键");
            UpdateShotMenu();
        }
    }

    void PickSaveDir()
    {
        using (FolderBrowserDialog dlg = new FolderBrowserDialog())
        {
            dlg.Description = "选择截图保存的文件夹";
            dlg.ShowNewFolderButton = true;
            dlg.SelectedPath = Shot.EffectiveSaveDir;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Shot.SaveDir = dlg.SelectedPath;
            Shot.SaveSettings();
            UpdateShotMenu();                 // 选完就生效，不再弹确认
        }
    }

    // ---------- 贴任务栏 ----------
    bool dockedToTray = false;

    void DockToTaskbar()
    {
        try
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { dockedToTray = false; return; }
            RECT rt;
            if (!GetWindowRect(tray, out rt)) return;
            int barH = rt.B - rt.T;

            int h = barH > 20 ? barH : H;
            int w = Size.Width;
            if (Size.Height != h) Size = new Size(w, h);

            // 右边缘：紧贴通知区域（托盘图标所在）的左边，不遮盖任何图标
            int rightEdge = rt.R - 8;
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                RECT rn;
                if (GetWindowRect(notify, out rn)) rightEdge = rn.L - 6;
            }

            // 真正挂成任务栏的子窗口：任务栏隐藏、移动、改宽度时自动跟随。
            // 注意：管理员权限进程会被跨完整性检查拒绝（SetParent 失败且会破坏窗口），
            // 所以只有在普通权限运行时才尝试，管理员模式一律用覆盖式定位。
            if (!Elevated && (!dockedToTray || GetParent(Handle) != tray))
            {
                // 必须先真正显示，再清掉置顶位、补上 WS_CHILD，否则 SetParent 不生效
                if (!Visible) Show();
                TopMost = false;
                SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

                long st = GetWindowLongPtr(Handle, -16).ToInt64();
                SetWindowLongPtr(Handle, -16, new IntPtr(st | 0x40000000L));   // WS_CHILD

                SetParent(Handle, tray);
                dockedToTray = (GetParent(Handle) == tray);
                firstDock = true;
                if (dockedToTray)
                {
                    Log("dock: attached to Shell_TrayWnd");
                }
                else
                {
                    // 失败：清掉 WS_CHILD、还原尺寸位置，退回覆盖式定位
                    SetWindowLongPtr(Handle, -16, new IntPtr(st & ~0x40000000L));
                    Size = new Size(w, h);
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, 0x0004 | 0x0010);
                    Log("dock: attach failed, fallback overlay err=" + GetLastError());
                }
            }

            if (dockedToTray)
            {
                // 子窗口用父客户区坐标定位
                int cx = rightEdge - w - rt.L;
                int cy = rt.B - h - rt.T;
                RECT cur;
                bool moved = firstDock || !GetWindowRect(Handle, out cur)
                             || cur.L != rightEdge - w || cur.T != rt.B - h;
                firstDock = false;
                if (!moved) return;
                SetWindowPos(Handle, IntPtr.Zero, cx, cy, w, h, 0x0004 | 0x0010);  // NOZORDER|NOACTIVATE
                SampleTaskbarColor();
                return;
            }

            int x = rightEdge - w;
            if (x < rt.L) x = rt.L;
            int y = rt.B - h;

            bool movedOv = firstDock || Location.X != x || Location.Y != y;
            firstDock = false;
            if (!movedOv) return;

            Location = new Point(x, y);
            EnsureTopMost();
            SampleTaskbarColor();
        }
        catch { }
    }

    // 从任务栏空白处取色，让挂件底色和任务栏一致（多点采样取中位，抗图标干扰；用屏幕坐标）
    void SampleTaskbarColor()
    {
        try
        {
            Point p0 = PointToScreen(Point.Empty);
            int w = 16, h = Math.Max(10, Height - 14);
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = p0.X - w - 10;
            if (x < vs.Left) x = p0.X + Width + 10;
            int y = p0.Y + 7;
            if (y + h > vs.Bottom) return;
            using (Bitmap b = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(b))
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                Color med = MedianColor(b, w, h);
                if (med.R < 0) return;
                ApplyBarTheme(med);
            }
        }
        catch { }
    }

    static Color MedianColor(Bitmap b, int w, int h)
    {
        int n = w * h;
        int[] lum = new int[n];
        int[] argb = new int[n];
        int i = 0;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                Color c = b.GetPixel(xx, yy);
                argb[i] = c.ToArgb();
                lum[i] = (int)(c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                i++;
            }
        Array.Sort(lum, argb);
        return Color.FromArgb(argb[n / 2]);
    }

    // 把任务栏实测色套到界面上（深/浅两套字色，双阈值防抖）
    void ApplyBarTheme(Color c)
    {
        float lum = (299f * c.R + 587f * c.G + 114f * c.B) / 255000f;
        if (themeReady)
        {
            if (Math.Abs(c.R - barBase.R) + Math.Abs(c.G - barBase.G) + Math.Abs(c.B - barBase.B) < 6) return;
            if (barLight) { if (lum < 0.47f) barLight = false; }
            else { if (lum > 0.53f) barLight = true; }
        }
        else barLight = lum > 0.52f;

        barBase = c;
        if (barLight)
        {
            fgMain = Color.FromArgb(28, 30, 34);
            fgSub = Color.FromArgb(112, 106, 98);
            upColor = Color.FromArgb(198, 92, 40);
            downColor = Color.FromArgb(20, 136, 98);
            ringTrack = Color.FromArgb(34, 0, 0, 0);
            ringGreen = Color.FromArgb(22, 148, 94);
            ringYellow = Color.FromArgb(182, 128, 10);
            ringRed = Color.FromArgb(204, 50, 34);
        }
        else
        {
            fgMain = TextWhite;
            fgSub = TextGray;
            upColor = UpRed;
            downColor = Green;
            ringTrack = Color.FromArgb(40, 40, 44);
            ringGreen = RingGreen;
            ringYellow = RingYellow;
            ringRed = RingRed;
        }
        BackColor = c;
        ringBmpPct = -1;
        themeReady = true;
        Invalidate();
    }

    void EnsureTopMost()
    {
        try
        {
            // 已被别人盖住时才重排一次 z 序；本来就压在最上就不动，避免无谓刷新
            POINT p; p.X = Location.X + 30; p.Y = Location.Y + 20;
            if (WindowFromPoint(p) == Handle) return;
            SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    void RestoreTaskbar() { }

    // ---------- 交互 ----------
    Point dragStart;
    int dragBaseX, dragBaseY;
    bool dragging, dragPending;

    void OnMouseDown(object s, MouseEventArgs e)
    {
        // 按下先不进入拖动：双击时手会抖一两像素，直接拖会把悬浮条挪走，
        // 圆环跟着移位，第二下就点不中了。移动超过 5px 才真的开始拖。
        if (e.Button == MouseButtons.Left)
        {
            dragPending = true; dragging = false;
            dragStart = e.Location; dragBaseX = Location.X; dragBaseY = Location.Y;
        }
    }

    DateTime lastDown = DateTime.MinValue, lastClean = DateTime.MinValue;
    Point lastDownPt;

    void HandleLeftDown(int x, int y)
    {
        DateTime now = DateTime.Now;
        if ((now - lastDown).TotalMilliseconds < 700 && Math.Abs(x - lastDownPt.X) < 12 && Math.Abs(y - lastDownPt.Y) < 12)
        {
            lastDown = DateTime.MinValue;
            TriggerClean(x, y);
        }
        else { lastDown = now; lastDownPt = new Point(x, y); }
    }

    void TriggerClean(int x, int y)
    {
        if (!InRing(x, y)) { Log("miss " + x + "," + y); return; }
        if ((DateTime.Now - lastClean).TotalMilliseconds < 900) return;
        Log("clean-trigger " + x + "," + y);
        BeginClean();
    }

    // 双击圆环、右键菜单「清理内存」都走这里：先起步圆环动画，再后台清理
    void BeginClean()
    {
        if ((DateTime.Now - lastClean).TotalMilliseconds < 500) return;
        lastClean = DateTime.Now;
        StartRingAnim();                       // 动画立刻起步，画面马上有反馈
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { CleanMemory(); });
    }

    void OnMouseMove(object s, MouseEventArgs e)
    {
        if (dragPending)
        {
            if (e.Button != MouseButtons.Left) dragPending = false;             // 键早松了
            else if (Math.Abs(e.X - dragStart.X) >= 5 || Math.Abs(e.Y - dragStart.Y) >= 5)
            {
                dragPending = false; dragging = true;
            }
        }
        if (dragging) Location = new Point(dragBaseX + (e.X - dragStart.X), dragBaseY + (e.Y - dragStart.Y));
    }

    void OnMouseUp(object s, MouseEventArgs e)
    {
        dragging = false; dragPending = false;
    }

    bool InRing(int x, int y)
    {
        double dx = x - 20, dy = y - ClientSize.Height / 2.0;
        return Math.Sqrt(dx * dx + dy * dy) <= 18;
    }

    void OnDoubleClick(object s, MouseEventArgs e)
    {
        TriggerClean(e.X, e.Y);
    }

    void TrimSelf()
    {
        try { SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
        catch { }
    }

    // ---------- 内存清理 ----------
    void CleanMemory()
    {
        MEMORYSTATUSEX before = new MEMORYSTATUSEX();
        before.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref before);

        int trimmed = 0;
        // 外壳进程和自己的界面不动：压 explorer 的工作集会让任务栏连同挂在上面的悬浮条一起重绘/闪一下，
        // 自己这份压了也会让圆环重画一顿，看起来像“UI 消失又回来”。
        int selfId = 0;
        try { selfId = Process.GetCurrentProcess().Id; } catch { }
        string[] shellSkip = { "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
                               "SearchHost", "TextInputHost", "dwm", "csrss", "winlogon", "MiniMonitor" };
        Process[] ps = null;
        try { ps = Process.GetProcesses(); } catch { }
        if (ps != null)
        {
            foreach (Process pr in ps)
            {
                bool skip = false;
                try { skip = pr.Id == selfId; } catch { }
                if (!skip)
                {
                    try
                    {
                        string pn = pr.ProcessName;
                        for (int i = 0; i < shellSkip.Length; i++)
                            if (string.Equals(pn, shellSkip[i], StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    }
                    catch { }
                }
                if (skip) { try { pr.Dispose(); } catch { } continue; }
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, pr.Id);
                    if (h != IntPtr.Zero && EmptyWorkingSet(h)) trimmed++;
                }
                catch { }
                finally
                {
                    if (h != IntPtr.Zero) CloseHandle(h);
                    try { pr.Dispose(); } catch { }
                }
            }
        }

        try
        {
            int cmd = 4; // MemoryPurgeStandbyList
            NtSetSystemInformation(0x50, ref cmd, 4);
        }
        catch { }

        TrimSelf();

        MEMORYSTATUSEX after = new MEMORYSTATUSEX();
        after.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref after);

        long freed = 0;
        if (after.ullAvailPhys > before.ullAvailPhys) freed = (long)(after.ullAvailPhys - before.ullAvailPhys);

        memPct = after.dwMemoryLoad;
        memAvail = after.ullAvailPhys;

        // 后台线程清理完，交回 UI 线程刷新一次，避免跨线程操作控件
        try { BeginInvoke((MethodInvoker)delegate { Sample(); MarkDirty(); }); }
        catch { }


        try
        {
            Directory.CreateDirectory(cfgPath);
            File.AppendAllText(Path.Combine(cfgPath, "clean.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  进程=" + trimmed + "  释放=" + (freed / 1048576) + "MB  清理后可用=" + (memAvail / 1048576) + "MB\r\n");
        }
        catch { }
    }

    void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(cfgPath, "trace.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); }
        catch { }
    }

    // ---------- 采样 ----------
    void Sample()
    {
        TempBus.Heartbeat();   // 告诉管理员代理“界面还活着”；界面崩了代理 20 秒后自己退出

        long idle, kernel, user;
        if (GetSystemTimes(out idle, out kernel, out user))
        {
            long di = idle - lastIdle;
            long dk = (kernel - lastKernel) - di;
            long du = user - lastUser;
            long total = di + dk + du;
            if (total > 0) cpuPct = Math.Max(0, Math.Min(100, 100.0 * (total - di) / total));
            lastIdle = idle; lastKernel = kernel; lastUser = user;
        }

        long rx, tx;
        NetStats(out rx, out tx);
        rxRate = Math.Max(0, rx - lastRx);
        txRate = Math.Max(0, tx - lastTx);
        lastRx = rx; lastTx = tx;

        SampleGpu();
        SampleCpuTemp();

        MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
        ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref ms))
        {
            memPct = ms.dwMemoryLoad;
            memTotal = ms.ullTotalPhys;
            memAvail = ms.ullAvailPhys;
        }
    }

    void Animate()
    {
        if (ringPhase != 0)
        {
            // 按真实经过时间推进，避免 WinForms 定时器 15.6ms 粒度把时长带偏
            double p = (Environment.TickCount - animT0) / AnimMs;
            if (p > 1) p = 1;
            shownPct = animFrom + (animTo - animFrom) * p;
            if (p >= 1)
            {
                shownPct = animTo;
                if (ringPhase == 1)
                {
                    ringPhase = 2;
                    animFrom = 0;
                    animTo = (int)Math.Round(memPct);
                    animT0 = Environment.TickCount;
                }
                else
                {
                    ringPhase = 0;
                    anim.Interval = 125;
                }
            }
            else if (anim.Interval != 15) anim.Interval = 15;
        }
        else
        {
            int target = (int)Math.Round(memPct);
            int cur = (int)Math.Round(shownPct);
            if (cur != target)
            {
                shownPct = cur + Math.Sign(target - cur);
                if (anim.Interval != 33) anim.Interval = 33;
            }
            else if (anim.Interval != 125) anim.Interval = 125;
        }
        Invalidate();
    }

    // 只有显示内容真的变了才重绘，避免定时器空转刷新
    void MarkDirty()
    {
        string key = (int)Math.Round(shownPct) + "|" + (int)Math.Round(cpuPct) + "|" + (int)Math.Round(gpuUtil) + "|" +
                     (int)cpuTemp + "|" + (int)gpuTemp + "|" + Speed(rxRate) + "|" + Speed(txRate) + "|" + BackColor.ToArgb();
        if (key == lastKey) return;
        lastKey = key;
        Invalidate();
    }

    // 每个整数之间至少留 8ms，整段控制在 0.8 秒
    int StepInterval(double count)
    {
        int n = (int)Math.Max(1, Math.Round(count));
        int iv = (int)Math.Round(800.0 / n);
        if (iv < 8) iv = 8;
        return iv;
    }

    // 双击圆环：0.8 秒倒数到 0，再用 0.8 秒回到真实占用
    void StartRingAnim()
    {
        ringPhase = 1;
        animFrom = shownPct;
        animTo = 0;
        animT0 = Environment.TickCount;
        anim.Interval = 15;
        Invalidate();
    }

    void SampleGpu()
    {
        if (!nvmlReady) return;
        try
        {
            uint t;
            if (nvmlDeviceGetTemperature(nvmlDev, 0, out t) == 0) gpuTemp = t;
            Utilization u;
            if (nvmlDeviceGetUtilizationRates(nvmlDev, out u) == 0) gpuUtil = u.Gpu;
        }
        catch { }
    }

    void SampleCpuTemp()
    {
        // 优先读温度代理（管理员常驻）通过共享内存喂过来的内核温度
        int lt = TempBus.Read();
        if (lt > 0) { cpuTemp = lt; return; }
        // 兜底：本进程恰好是管理员时也能直接采集
        lt = LhmTemp.Last;
        if (lt > 0) { cpuTemp = lt; return; }
        if (!pdhReady) return;
        try
        {
            if (PdhCollectQueryData(pdhQuery) != 0) return;
            uint size = 0, cnt = 0;
            int rc = PdhGetFormattedCounterArray(pdhCounter, 0x200, out size, out cnt, IntPtr.Zero);
            if (rc != unchecked((int)0x800007D2) && rc != 0) return;
            if (size <= 0 || cnt == 0) return;
            if (PdhGetFormattedCounterArray(pdhCounter, 0x200, out size, out cnt, pdhBuf) != 0) return;
            long p = (long)pdhBuf;
            uint status = (uint)Marshal.ReadInt32((IntPtr)(p + 8));
            long bits = Marshal.ReadInt64((IntPtr)(p + 16));
            double kelvin10 = BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
            if (status == 0 && kelvin10 > 2000 && kelvin10 < 4000)
                cpuTemp = Math.Round(kelvin10 / 10.0 - 273.15, 0);
        }
        catch { }
    }

    // 直接双击 exe 启动时只有界面进程，没有管理员温度代理，CPU 温度会退回主板温区（看着像"没读到"）。
    // 这里发现代理数据断了就悄悄把计划任务跑起来——任务已经建好时 /Run 不需要再弹 UAC。
    // 5 秒一轮：任务被结束、代理被杀，最多 5 秒后重新拉起，不出现长时间 --。
    DateTime lastAgentKick = DateTime.MinValue;
    void EnsureAgent()
    {
        if (TempBus.Read() > 0) return;
        if (LhmTemp.Last > 0) return;                                   // 本进程就是管理员，自己在采集
        if ((DateTime.Now - lastAgentKick).TotalSeconds < 5) return;
        lastAgentKick = DateTime.Now;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (RunSchtasks("/Query /TN \"" + TaskName + "\"") != 0)
                {
                    DependencyCheck();                                  // 任务被删了：后台复查并补齐
                    return;
                }
                if (AgentTaskRunning()) return;                          // 已经在跑：跳过，不重复 /Run
                RunSchtasks("/Run /TN \"" + TaskName + "\"");
                Log("agent kick");
            }
            catch { }
        });
    }

    // 启动自检：依赖的文件、PawnIO 驱动、计划任务、温度代理，缺哪个就补哪个。
    // 温度链路已经通的时候什么都不做；只有真读不到温度才会去装驱动/建任务/拉起代理。
    // 运行中每 75 秒允许复查一次：任务被删、驱动被停都能自己回来；UAC 引导 5 分钟内只尝试一次，避免反复弹。
    DateTime lastDepCheck = DateTime.MinValue;
    DateTime lastInstallPrompt = DateTime.MinValue;
    void DependencyCheck()
    {
        if ((DateTime.Now - lastDepCheck).TotalSeconds < 75) return;
        lastDepCheck = DateTime.Now;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                System.Threading.Thread.Sleep(2500);
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                // 温度链路真正需要的只有这 5 个 dll；配置文件（MiniMonitor.config）只管版本号，
                // 缺了不影响读温度，所以不放进这份清单，免得误报"CPU 温度会读不到"。
                string[] need = { "LibreHardwareMonitorLib.dll", "PawnIOLib.dll", "System.Memory.dll",
                                  "System.Buffers.dll", "System.Runtime.CompilerServices.Unsafe.dll" };
                string miss = "";
                foreach (string f in need) if (!File.Exists(Path.Combine(dir, f))) miss += "\n· " + f;
                if (miss.Length > 0)
                {
                    try { Log("missing:" + miss.Replace("\r", " ").Replace("\n", " ")); } catch { }
                    BeginInvoke((Action)delegate
                    {
                        MessageBox.Show("程序目录里缺文件，CPU 温度会读不到：" + miss +
                            "\n\n把压缩包里的文件整份放回同一个文件夹，不要单独拷 exe。",
                            "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }

                // 代理刚启动那几秒可能还没出值，先给 8 秒观察窗口，别误判成"没依赖"
                bool ok = false;
                for (int i = 0; i < 5; i++)
                {
                    // cpuTemp 包含 ACPI/PDH 兜底值；界面已经有温度时，不要再弹“读不到温度”的提示。
                    if (TempBus.Read() > 0 || LhmTemp.Last > 0 || cpuTemp > 0) { ok = true; break; }
                    System.Threading.Thread.Sleep(1600);
                }
                if (ok) return;                                          // 温度已经能读到，不打扰

                // 驱动或计划任务缺任意一个，先明确提示用户运行安装程序；
                // 不再静默 bootstrap，避免用户不知道为什么会弹 UAC。
                if (!PawnioInstalled() || !TaskReady())
                {
                    if ((DateTime.Now - lastInstallPrompt).TotalSeconds < 300) return;   // 5 分钟内不重复提示
                    lastInstallPrompt = DateTime.Now;
                    try { Log("dep: prompt install"); } catch { }

                    string setup = InstallerPath();
                    if (setup.Length == 0)
                    {
                        BeginInvoke((Action)delegate
                        {
                            MessageBox.Show("未检测到 PawnIO 驱动，CPU 温度无法显示。\n\n" +
                                "程序目录里也没有 PawnIO_setup.exe，请把完整包里的安装程序放回 MiniMonitor 目录后重试。",
                                "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                        return;
                    }

                    bool yes = false;
                    var done = new System.Threading.ManualResetEvent(false);
                    string why = !PawnioInstalled()
                        ? "未检测到 PawnIO 驱动，CPU 温度无法显示。"
                        : "温度代理任务缺失，CPU 温度无法显示。";
                    BeginInvoke((Action)delegate
                    {
                        yes = MessageBox.Show(why + "\n\n是否现在运行 PawnIO 安装程序？\n（会弹一次 UAC，装完自动重新检测）",
                            "MiniMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                        done.Set();
                    });
                    done.WaitOne(30000);
                    if (!yes) return;
                    try { Log("dep: run installer"); } catch { }
                    RunElevated(Application.ExecutablePath, "--bootstrap");
                    return;
                }

                // 驱动和任务都在，只是代理没跑（比如刚开机任务延迟中），直接拉一次；拉完再等温度。
                if (!AgentTaskRunning()) RunSchtasks("/Run /TN \"" + TaskName + "\"");
                try { Log("dep: agent started"); } catch { }
                for (int i = 0; i < 4; i++)
                {
                    if (TempBus.Read() > 0 || LhmTemp.Last > 0 || cpuTemp > 0) return;
                    System.Threading.Thread.Sleep(1500);
                }

                // 驱动装了但温度仍读不到：提示重装/修复 PawnIO，不再无声无息。
                // 上次修复失败后 7 天内不再每次开机弹；右键菜单仍可手动重试。
                if ((DateTime.Now - lastInstallPrompt).TotalSeconds < 300) return;
                if (RepairFailedRecently()) return;
                lastInstallPrompt = DateTime.Now;
                string installer = InstallerPath();
                if (installer.Length == 0)
                {
                    BeginInvoke((Action)delegate
                    {
                        MessageBox.Show("PawnIO 已安装，但 CPU 温度仍读不到，且程序目录里没有 PawnIO_setup.exe。\n\n" +
                            "请把完整包里的 PawnIO_setup.exe 放回 MiniMonitor 目录后重试。",
                            "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                    return;
                }
                bool repair = false;
                var wait = new System.Threading.ManualResetEvent(false);
                BeginInvoke((Action)delegate
                {
                    repair = MessageBox.Show("PawnIO 已安装，但 CPU 温度仍读不到。\n\n是否运行 PawnIO 安装程序修复驱动？",
                        "MiniMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                    wait.Set();
                });
                wait.WaitOne(30000);
                if (repair)
                {
                    try { Log("dep: repair install"); } catch { }
                    bool repaired = RunElevated(Application.ExecutablePath, "--bootstrap --repair");
                    string result = "";
                    try
                    {
                        string f = Path.Combine(RepairStateDir(), "repair.result");
                        if (File.Exists(f)) result = File.ReadAllText(f).Trim();
                    }
                    catch { }
                    BeginInvoke((Action)delegate
                    {
                        if (!repaired) MessageBox.Show("驱动修复没有完成：可能取消了管理员授权，或安装程序启动失败。\n\n" +
                            "可以右键任务栏 MiniMonitor →「修复温度驱动」再试一次。",
                            "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else if (result == "ok") MessageBox.Show("PawnIO 驱动已重装，温度代理已重启。\n\n几秒内 CPU 温度应该会恢复；如果仍是旧值，请完全退出 MiniMonitor 再打开。",
                            "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else MessageBox.Show("PawnIO 驱动已重装，但温度代理仍未写出 CPU 温度。\n\n" +
                            "这通常不是缺少 PawnIO_setup.exe，而是当前硬件库读不到这颗 CPU。已暂停 7 天内的重复提示，可右键菜单手动重试。",
                            "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            }
            catch { }
        });
    }

    void NetStats(out long rx, out long tx)
    {
        rx = 0; tx = 0;
        try
        {
            if (nics == null || (DateTime.Now - nicStamp).TotalSeconds > 120)
            {
                List<NetworkInterface> keep = new List<NetworkInterface>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    keep.Add(ni);
                }
                nics = keep.ToArray();
                nicStamp = DateTime.Now;
            }
            for (int i = 0; i < nics.Length; i++)
            {
                IPv4InterfaceStatistics st = nics[i].GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { nics = null; }
    }

    //  占用：≤50 绿 / 50~60 过渡 / 60~80 黄 / 80~90 过渡 / ≥90 红
    Color PctColor(double v)
    {
        if (v <= 49) return ringGreen;
        if (v <= 51) return Lerp(ringGreen, ringYellow, (v - 49) / 2.0);
        if (v <= 79) return ringYellow;
        if (v <= 81) return Lerp(ringYellow, ringRed, (v - 79) / 2.0);
        return ringRed;
    }

    //  温度：≤60 绿 / 60~70 过渡 / 70~80 黄 / 80~90 过渡 / ≥90 红
    Color TempColor(double v)
    {
        if (v <= 59) return ringGreen;
        if (v <= 61) return Lerp(ringGreen, ringYellow, (v - 59) / 2.0);
        if (v <= 79) return ringYellow;
        if (v <= 81) return Lerp(ringYellow, ringRed, (v - 79) / 2.0);
        return ringRed;
    }
    // ---------- 绘制 ----------
    static readonly Color RingGreen = Color.FromArgb(72, 208, 122);
    static readonly Color RingYellow = Color.FromArgb(240, 196, 46);
    static readonly Color RingRed = Color.FromArgb(232, 80, 60);

    static Color Lerp(Color a, Color b, double t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    //  内存：≤55 绿 / 55~65 过渡 / 66~75 黄 / 76~85 过渡 / ≥85 红
    Color MemColor(double v)
    {
        if (v <= 55) return ringGreen;
        if (v <= 65) return Lerp(ringGreen, ringYellow, (v - 55) / 10.0);
        if (v <= 75) return ringYellow;
        if (v <= 85) return Lerp(ringYellow, ringRed, (v - 75) / 10.0);
        return ringRed;
    }

    // 圆环预渲染成小位图：只有百分比变化时才重画那 40 段弧
    void DrawRingCached(Graphics g, int shownInt, float cx, float cy, float r)
    {
        if (ringBmp == null || ringBmpPct != shownInt)
        {
            if (ringBmp != null) ringBmp.Dispose();
            int d = (int)Math.Ceiling(r * 2 + 8);
            ringBmp = new Bitmap(d, d, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics b = Graphics.FromImage(ringBmp))
            {
                b.SmoothingMode = SmoothingMode.AntiAlias;
                float mx = d / 2f, my = d / 2f;

                // 底槽：外沿压一道暗弧、内沿提一道细高光，做出浅浅的凹槽
                using (Pen basePen = new Pen(ringTrack, 3f))
                    b.DrawEllipse(basePen, mx - r, my - r, r * 2, r * 2);
                using (Pen troughShadow = new Pen(Color.FromArgb(barLight ? 40 : 85, 0, 0, 0), 1f))
                    b.DrawArc(troughShadow, mx - r - 1.2f, my - r - 1.2f, r * 2 + 2.4f, r * 2 + 2.4f, 18, 144);
                using (Pen troughLight = new Pen(Color.FromArgb(barLight ? 26 : 40, 255, 255, 255), 0.8f))
                    b.DrawArc(troughLight, mx - r + 1.2f, my - r + 1.2f, r * 2 - 2.4f, r * 2 - 2.4f, 198, 144);

                float sweep = Math.Min(100, Math.Max(0, shownInt)) * 3.6f;
                if (sweep > 0.6f)
                {
                    float step = 2.5f;
                    int segs = (int)Math.Ceiling(sweep / step);
                    float each = sweep / segs;

                    // 投影层：整条弧略向右下偏移、压暗，像浮在槽上
                    for (int i = 0; i < segs; i++)
                    {
                        double posPct = each * (i + 0.5) / 3.6;
                        Color dark = Lerp(MemColor(posPct), Color.Black, 0.55);
                        using (Pen pen = new Pen(Color.FromArgb(barLight ? 120 : 150, dark.R, dark.G, dark.B), 3f))
                            b.DrawArc(pen, mx - r + 0.7f, my - r + 0.9f, r * 2, r * 2, -90 + each * i, each + 0.8f);
                    }
                    // 主弧：略窄、比投影亮一点，撑起圆环的正面
                    for (int i = 0; i < segs; i++)
                    {
                        double posPct = each * (i + 0.5) / 3.6;
                        using (Pen pen = new Pen(Lerp(MemColor(posPct), Color.White, 0.06), 2.6f))
                            b.DrawArc(pen, mx - r - 0.15f, my - r - 0.25f, r * 2, r * 2, -90 + each * i, each + 0.8f);
                    }
                    // 内沿高光：细亮线贴在内侧，给出厚度
                    for (int i = 0; i < segs; i++)
                    {
                        double posPct = each * (i + 0.5) / 3.6;
                        Color hi = Lerp(MemColor(posPct), Color.White, 0.55);
                        float rr = r - 1.15f;
                        using (Pen pen = new Pen(Color.FromArgb(barLight ? 90 : 120, hi.R, hi.G, hi.B), 0.8f))
                            b.DrawArc(pen, mx - rr, my - rr - 0.2f, rr * 2, rr * 2, -90 + each * i, each + 0.8f);
                    }
                }
            }
            ringBmpPct = shownInt;
        }
        g.DrawImageUnscaled(ringBmp, (int)(cx - ringBmp.Width / 2f), (int)(cy - ringBmp.Height / 2f));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0201)   // WM_LBUTTONDOWN：自己判定双击，不依赖 WinForms 双击事件
        {
            long lp = m.LParam.ToInt64();
            HandleLeftDown((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        }
        else if (m.Msg == 0x0203)   // WM_LBUTTONDBLCLK：Windows 认出来的第二次按下，直接当双击起点
        {
            long lp = m.LParam.ToInt64();
            int cx = (short)(lp & 0xFFFF), cy = (short)((lp >> 16) & 0xFFFF);
            lastDown = DateTime.MinValue;
            TriggerClean(cx, cy);
            return;
        }
        else if (m.Msg == 0x0312 && (int)m.WParam == Shot.HKID)   // 全局截图热键
        {
            Log("hotkey fired");
            try { BeginInvoke((MethodInvoker)delegate { Shot.Capture(this); }); } catch { }
        }
        else if (m.Msg == 0x007B)   // WM_CONTEXTMENU：自己弹重建过的菜单
        {
            ShowWidgetMenu(PointToClient(Cursor.Position));
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true; Invalidate(); base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false; Invalidate(); base.OnMouseLeave(e);
    }

    bool hovering;

    // ---- 跟随任务栏配色 ----
    Color barBase = Color.FromArgb(16, 16, 16);
    bool barLight, themeReady;
    int tickN;
    Color fgMain = Color.FromArgb(243, 236, 224);
    Color fgSub = Color.FromArgb(176, 155, 128);
    Color upColor = Color.FromArgb(255, 138, 91);
    Color downColor = Color.FromArgb(91, 214, 160);
    Color ringTrack = Color.FromArgb(40, 40, 44);
    Color ringGreen = Color.FromArgb(72, 208, 122);
    Color ringYellow = Color.FromArgb(240, 196, 46);
    Color ringRed = Color.FromArgb(232, 80, 60);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int hh = ClientSize.Height;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        if (hovering)
            using (SolidBrush hb = new SolidBrush(barLight ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(12, 255, 255, 255)))
                g.FillRectangle(hb, ClientRectangle);

        // ① 内存圆环（渐变流光，缓存位图，值不变不重画）
        float cx = 20, cy = hh / 2f, r = 13.5f;
        int shownInt = (int)Math.Round(shownPct);
        DrawRingCached(g, shownInt, cx, cy, r);
        DrawText(g, shownInt.ToString(), FontBold(8f), fgMain, cx - 16, cy - 8, 32, 16, ContentAlignment.MiddleCenter);

        // ② 网速：上=暖橙，下=青绿（纯三角，无杆）
        float ax = 40;
        DrawArrow(g, ax, 12, true, upColor);
        DrawText(g, Speed(txRate), FontBold(7.5f), fgMain, ax + 12, 4, 52, 16, ContentAlignment.MiddleLeft);
        DrawArrow(g, ax, 27, false, downColor);
        DrawText(g, Speed(rxRate), FontBold(7.5f), fgMain, ax + 12, 19, 52, 16, ContentAlignment.MiddleLeft);

        using (Pen sep = new Pen(barLight ? Color.FromArgb(46, 0, 0, 0) : Color.FromArgb(46, 244, 183, 64), 1f))
        {
            g.DrawLine(sep, 104, 9, 104, hh - 9);
            g.DrawLine(sep, 160, 9, 160, hh - 9);
        }

        // ③ CPU
        StackLetters(g, 108, "CPU", hh);
        DrawText(g, (cpuTemp < 0 ? "--" : ((int)cpuTemp).ToString()) + "℃", FontBold(8.5f), cpuTemp < 0 ? fgSub : TempColor(cpuTemp), 122, 4, 34, 16, ContentAlignment.MiddleLeft);
        DrawText(g, ((int)Math.Round(cpuPct)).ToString() + "%", FontBold(7f), PctColor(cpuPct), 122, 19, 34, 16, ContentAlignment.MiddleLeft);

        // ④ GPU
        StackLetters(g, 164, "GPU", hh);
        DrawText(g, (gpuTemp < 0 ? "--" : ((int)gpuTemp).ToString()) + "℃", FontBold(8.5f), gpuTemp < 0 ? fgSub : TempColor(gpuTemp), 178, 4, 30, 16, ContentAlignment.MiddleLeft);
        DrawText(g, (gpuUtil < 0 ? "--" : ((int)gpuUtil).ToString()) + "%", FontBold(7f), gpuUtil < 0 ? fgSub : PctColor(gpuUtil), 178, 19, 30, 16, ContentAlignment.MiddleLeft);
    }

    void StackLetters(Graphics g, float x, string letters, int hh)
    {
        int n = letters.Length;
        float cell = (hh - 6f) / n;
        for (int i = 0; i < n; i++)
            DrawText(g, letters.Substring(i, 1), FontBold(6.5f), Color.FromArgb(150, fgSub), x - 1, 3 + i * cell, 12, cell, ContentAlignment.MiddleCenter);
    }

    void DrawArrow(Graphics g, float x, float y, bool up, Color c)
    {
        using (SolidBrush b = new SolidBrush(c))
        {
            float m = x + 4.5f, h = 3.8f;
            if (up)
                g.FillPolygon(b, new PointF[] { new PointF(m, y - h), new PointF(m + h, y + h), new PointF(m - h, y + h) });
            else
                g.FillPolygon(b, new PointF[] { new PointF(m - h, y - h), new PointF(m + h, y - h), new PointF(m, y + h) });
        }
    }

    static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
    static readonly StringFormat sfLeft, sfCenter;

    static MiniMonitor()
    {
        sfLeft = new StringFormat();
        sfLeft.LineAlignment = StringAlignment.Center;
        sfLeft.Alignment = StringAlignment.Near;
        sfLeft.FormatFlags = StringFormatFlags.NoWrap;

        sfCenter = new StringFormat();
        sfCenter.LineAlignment = StringAlignment.Center;
        sfCenter.Alignment = StringAlignment.Center;
        sfCenter.FormatFlags = StringFormatFlags.NoWrap;
    }

    Font GetFont(float size, FontStyle style)
    {
        string key = size.ToString() + "_" + (int)style;
        Font f;
        if (!fontCache.TryGetValue(key, out f))
        {
            try { f = new Font("Microsoft YaHei UI", size, style); }
            catch { f = new Font("Segoe UI", size, style); }
            fontCache[key] = f;
        }
        return f;
    }

    Font FontBold(float size) { return GetFont(size, FontStyle.Bold); }
    Font FontNorm(float size) { return GetFont(size, FontStyle.Regular); }

    void DrawText(Graphics g, string s, Font f, Color c, float x, float y, float w, float h, ContentAlignment align)
    {
        using (SolidBrush b = new SolidBrush(c))
            g.DrawString(s, f, b, new RectangleF(x, y, w, h), align == ContentAlignment.MiddleLeft ? sfLeft : sfCenter);
    }

    string Speed(double bps)
    {
        if (bps >= 1048576) return (bps / 1048576.0).ToString("0.0") + " M/s";
        if (bps >= 1024)
        {
            double kb = bps / 1024.0;
            return (kb >= 10 ? ((int)Math.Round(kb)).ToString() : kb.ToString("0.0")) + " K/s";
        }
        return ((int)bps).ToString() + " B/s";
    }

    // ---------- 温度代理（手动启动的计划任务，只有 MiniMonitor 启动时才拉起） ----------
    const string TaskName = "MiniMonitorWidget";

    bool TaskReady()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\" /XML");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                // 任务在、最高权限、动作是 --agent，就算可用；不再要求开机触发
                return p.ExitCode == 0 && xml.Contains("HighestAvailable") && xml.Contains("--agent");
            }
        }
        catch { return false; }
    }

    int RunSchtasks(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(8000); return p.ExitCode; }
        }
        catch { return -1; }
    }

    bool AgentTaskRunning()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\" /FO LIST");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return p.ExitCode == 0 && (o.Contains("Running") || o.Contains("正在运行"));
            }
        }
        catch { return false; }
    }

    // 手动触发的任务：不带登录/开机触发器，只由 MiniMonitor 启动时 /Run，退出时 /End
    static string BuildTaskXml()
    {
        string exe = Application.ExecutablePath;
        string dir = Path.GetDirectoryName(exe);
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            " <RegistrationInfo><Description>MiniMonitor 温度代理（仅由 MiniMonitor 按需启动）</Description></RegistrationInfo>\r\n" +
            " <Triggers />\r\n" +
            " <Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            " <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority><Enabled>true</Enabled></Settings>\r\n" +
            " <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--agent</Arguments><WorkingDirectory>" + System.Security.SecurityElement.Escape(dir) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>";
    }

    // 管理员代理自己补驱动：代理已经是最高权限，静默装 PawnIO 不会再弹 UAC。
    // 装好（或本来就有）后把安装器收进 %LOCALAPPDATA%\WinTune\deps，程序目录里不留“只躺着没跑过”的 exe；
    // 以后驱动真被移除，还能从备份位置静默重装，不要求用户重新解压整包。
    static string InstallerPath()
    {
        string app = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PawnIO_setup.exe");
        if (File.Exists(app)) return app;
        string deps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "deps");
        string bak = Path.Combine(deps, "PawnIO_setup.exe");
        return File.Exists(bak) ? bak : "";
    }

    // 安装器不再从程序目录删除：用户能看到它，修复入口也始终有文件可用。
    static void StashInstaller()
    {
        try
        {
            string app = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PawnIO_setup.exe");
            if (!File.Exists(app)) return;
            string deps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune", "deps");
            Directory.CreateDirectory(deps);
            string bak = Path.Combine(deps, "PawnIO_setup.exe");
            try { File.Copy(app, bak, true); } catch { }
        }
        catch { }
    }

    static bool EnsurePawnio(bool force)
    {
        // force=true 用于“驱动登记还在、但温度读不到”的修复：不能因为注册表键存在就直接返回。
        if (!force && PawnioInstalled()) { StashInstaller(); return true; }
        try
        {
            string setup = InstallerPath();
            if (setup.Length == 0) return false;
            ProcessStartInfo psi = new ProcessStartInfo(setup, "-install -silent");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                bool ok = PawnioInstalled() && p.ExitCode == 0;
                if (ok) StashInstaller();
                return ok;
            }
        }
        catch { return false; }
    }

    // 一次性引导：界面用 runas 调起本模式，只弹一次 UAC，驱动和计划任务一起弄好
    static void Bootstrap(bool repair)
    {
        bool pawn = EnsurePawnio(repair);
        string tmp = Path.Combine(Path.GetTempPath(), "MiniMonitorTask.xml");
        try
        {
            File.WriteAllText(tmp, BuildTaskXml(), Encoding.Unicode);
            RunHidden("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            RunHidden("schtasks.exe", "/End /TN \"" + TaskName + "\"");
            RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
        }
        catch { }

        if (repair)
        {
            // 让新代理有几秒钟写共享内存；把结果留给界面判断，避免“点了是没反应”。
            try
            {
                System.Threading.Thread.Sleep(9000);
                bool temp = TempBus.Read() > 0;
                Directory.CreateDirectory(RepairStateDir());
                File.WriteAllText(Path.Combine(RepairStateDir(), "repair.result"), temp && pawn ? "ok" : "fail");
            }
            catch { }
        }
        Environment.Exit(0);
    }

    static string RepairStateDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor");
    }

    static bool RepairFailedRecently()
    {
        try
        {
            string f = Path.Combine(RepairStateDir(), "repair.result");
            if (!File.Exists(f)) return false;
            if (File.ReadAllText(f).Trim() != "fail") return false;
            return (DateTime.Now - File.GetLastWriteTime(f)).TotalDays < 7;
        }
        catch { return false; }
    }

    void SetAutoStart(bool on)
    {
        try
        {
            // 彻底不再用注册表 Run 自启：顺手把历史值清掉
            ClearRunValue();
            // 清掉旧版启动文件夹里的快捷方式，避免开机跑两个
            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MiniMonitor.lnk");
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch { }

            if (!on)
            {
                if (!RunElevated("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F"))
                    MessageBox.Show("删除自启任务失败，可能取消了管理员确认。",
                        "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(), "MiniMonitorTask.xml");
            File.WriteAllText(tmp, BuildTaskXml(), Encoding.Unicode);
            bool created = RunElevated("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            if (!created)
                MessageBox.Show("创建开机自启任务失败，需要点「是」通过管理员确认。",
                    "MiniMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { }
    }

    // 以管理员身份跑一条命令（弹一次 UAC），用于建/删最高权限任务
    bool RunElevated(string file, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(90000);
                return p.ExitCode == 0;
            }
        }
        catch { return false; }   // 用户取消 UAC
    }

    // 右键菜单的“修复温度驱动”：后台跑，别把菜单线程卡住。
    void RepairDriverAsync()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            bool ok = RunElevated(Application.ExecutablePath, "--bootstrap --repair");
            string result = "";
            try
            {
                string f = Path.Combine(RepairStateDir(), "repair.result");
                if (File.Exists(f)) result = File.ReadAllText(f).Trim();
            }
            catch { }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (!ok) MessageBox.Show("修复没有完成：可能取消了管理员授权。", "MiniMonitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (result == "ok") MessageBox.Show("驱动已重装，温度代理已重启。", "MiniMonitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show("驱动已重装，但温度仍未恢复；已暂停 7 天内的重复提示。", "MiniMonitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            catch { }
        });
    }

    // 旧版注册表自启的清理（现在只用计划任务，不再写 Run）
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "MiniMonitor";

    void ClearRunValue()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                if (k != null) k.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    // 温度代理是静态方法，不能调用界面的实例 Log；这里单独写一份 trace。
    static void Trace(string s)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "trace.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n");
        }
        catch { }
    }

    // 温度代理：一把锁保证只有一个，循环把温度写进共享内存
    static void RunTempAgent()
    {
        System.Threading.Mutex mx = null;
        bool created = false;
        try { mx = new System.Threading.Mutex(true, "Local\\MiniMonitorTempAgent", out created); }
        catch { created = true; }
        if (mx != null && !created) return;      // 已有代理在跑

        // 代理本身是最高权限：驱动没装就静默补上，避免新机器只显示 --
        EnsurePawnio(false);
        LhmTemp.Start();
        TempBus.Create();
        if (TempBus.StopRequested()) { try { LhmTemp.Stop(); } catch { } return; }   // 主程序正在退出，不再拉起界面
        // 等任务栏就绪，再用 explorer 以普通权限身份把界面拉起来（界面才能挂进任务栏）
        System.Threading.Thread.Sleep(4000);
        if (!TempBus.StopRequested()) LaunchUi();

        int miss = 0;
        bool uiSeen = false;
        long lastHb = 0;
        DateTime lastUiLaunch = DateTime.Now;
        while (true)
        {
            try { TempBus.Publish(LhmTemp.Last); } catch { }
            try { if (TempBus.TakeOpenWinTuneRequest()) LaunchWinTuneElevated(); } catch { }
            // 温度一直读不到时，看看 PawnIO 驱动服务是不是被停掉了（代理本身是管理员，能直接拉起来）
            if (LhmTemp.Last > 0) miss = 0;
            else if (++miss >= 10)
            {
                miss = 0;
                RunHidden("sc.exe", "start PawnIO");
            }
            // 界面心跳：界面被任务管理器杀掉、崩了，代理把界面重新拉起来；
            // 只有界面点「退出」写了 stop 标志，代理才真正收手。WinTune 关闭不参与这条链路。
            long hb = 0;
            try { hb = TempBus.HeartbeatTicks(); } catch { }
            if (hb > 0) { uiSeen = true; lastHb = hb; }
            if (uiSeen && DateTime.Now.Ticks - lastHb > TimeSpan.TicksPerSecond * 10)
            {
                try { Trace("ui heartbeat lost"); } catch { }
                uiSeen = false;
                lastHb = 0;
            }
            if (!uiSeen && (DateTime.Now - lastUiLaunch).TotalSeconds > 10)
            {
                try { Trace("ui missing, relaunch"); } catch { }
                LaunchUi();
                lastUiLaunch = DateTime.Now;
            }
            if (TempBus.StopRequested()) break;      // 界面点「退出」→ 代理跟着收手
            System.Threading.Thread.Sleep(1000);
        }
        try { LhmTemp.Stop(); } catch { }
        Environment.Exit(0);
    }

    // 静默跑一条命令（不显示窗口，最多等 5 秒）
    static void RunHidden(string file, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi)) p.WaitForExit(5000);
        }
        catch { }
    }

    // 用 explorer 启动自己：explorer 是普通权限，这样界面进程不会继承管理员令牌
    static void LaunchUi()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + Application.ExecutablePath + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch { }
    }

    static System.Threading.Mutex uiMutex;   // 界面单实例锁，必须一直持有到进程结束

    /// <summary>
    /// 自己加载同目录的托管依赖，代替旧式 &lt;exe&gt;.config 里的 bindingRedirect。
    /// 硬件库引用 System.Memory 4.0.5.0，本地实际是 4.0.1.2；同名程序集按本地这份加载，
    /// 目录里只有一个 MiniMonitor.config。
    /// </summary>
    static Assembly ResolveLocalAssembly(object sender, ResolveEventArgs e)
    {
        try
        {
            string simple = new AssemblyName(e.Name).Name;
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, simple + ".dll");
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        catch { }
        return null;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveLocalAssembly;

        bool isAgent = false;
        bool isBootstrap = false;
        bool isRepair = false;
        if (args != null) foreach (string a in args)
        {
            if (a == "--agent") isAgent = true;
            if (a == "--bootstrap") isBootstrap = true;
            if (a == "--repair") isRepair = true;
        }

        bool admin = false;
        try
        {
            admin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        Elevated = admin;

        // 每次启动把版本号写回同目录的配置文件（界面模式和温度代理模式都写一次）
        SyncVersionConfig();

        // 温度代理模式：管理员常驻、无窗口，只把 CPU 内核温度写进共享内存
        if (isAgent) { RunTempAgent(); return; }

        // 管理员引导模式：装驱动 + 建自启任务 + 拉起代理，完事就退
        if (isBootstrap) { Bootstrap(isRepair); return; }

        // 界面模式：普通权限运行，才能挂成任务栏的子窗口
        bool uiCreated = false;
        try { uiMutex = new System.Threading.Mutex(true, "Local\\MiniMonitorUI", out uiCreated); }
        catch { uiCreated = true; }
        if (!uiCreated) return;                  // 已经有一个界面在跑

        TempBus.ClearStop();                     // 上一轮“随软件退出”留下的停止标志不能带到这一轮

        Application.EnableVisualStyles();
        Application.Run(new MiniMonitor());
    }
}

// ---------------- 自绘深色右键菜单 ----------------
class PopMenu : Form
{
    public class MItem
    {
        public string Text;
        public int Icon;                        // 0 电源 1 闪电 2 相机 3 芯片 4 关闭 -1 无
        public bool Checked;
        public bool Toggle;                     // 右侧画开关（不再写“已开启/未开启”文字）
        public MItem[] Children;
        public Action Click;
        public MItem(string text, int icon, Action click) { Text = text; Icon = icon; Click = click; }
    }

    const int RowH = 36, PadY = 8, W = 216, SubW = 176, Gap = 6, Rad = 10;
    const float TxtX = 40f;

    static readonly Color CBg = Color.FromArgb(255, 22, 23, 28);
    static readonly Color CLine = Color.FromArgb(255, 42, 43, 51);
    static readonly Color CHi = Color.FromArgb(255, 42, 36, 24);
    static readonly Color CGold = Color.FromArgb(255, 244, 183, 64);
    static readonly Color CTxt = Color.FromArgb(255, 243, 236, 224);
    static readonly Color CTxt2 = Color.FromArgb(255, 176, 155, 128);
    static readonly Color CSwitchOn = Color.FromArgb(255, 35, 92, 73);
    static readonly Color CSwitchKnob = Color.FromArgb(255, 55, 224, 160);
    static readonly Color CSwitchOff = Color.FromArgb(255, 58, 61, 70);
    static readonly Color CSwitchKnobOff = Color.FromArgb(255, 138, 143, 153);

    readonly List<MItem> items;
    readonly int mainH;
    int hover = -1, openSub = -1, subHover = -1;
    bool closing;

    public PopMenu(List<MItem> items)
    {
        this.items = items;
        mainH = PadY * 2 + RowH * items.Count;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = CBg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Bounds = new Rectangle(0, 0, W, mainH);
        Region = new Region(Round(0, 0, W, mainH, Rad * 2));
    }

    static GraphicsPath Round(int x, int y, int w, int h, int d)
    {
        d = Math.Max(2, Math.Min(d, Math.Min(w, h)));
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    bool HasSub(int i) { return items[i].Children != null && items[i].Children.Length > 0; }
    int SubTop { get { return PadY + openSub * RowH; } }
    int SubH { get { return openSub >= 0 ? PadY * 2 + RowH * items[openSub].Children.Length : 0; } }
    Rectangle SubRect { get { return new Rectangle(W + Gap, SubTop, SubW, SubH); } }

    public void ShowAt(Point pt)
    {
        PopMenu old = cur;
        if (old != null && !old.closing) { try { old.HideAndDispose(); } catch { } }

        Rectangle wa = Screen.FromPoint(pt).WorkingArea;
        int x = Math.Max(wa.Left, Math.Min(pt.X, wa.Right - W));
        int y = Math.Max(wa.Top, Math.Min(pt.Y, wa.Bottom - mainH));
        Location = new Point(x, y);
        Show();
        Activate();
        cur = this;
        InstallHook();
    }

    // ── 全局鼠标钩子：点菜单外面自动收起（窗口没被激活时 OnDeactivate 靠不住）
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    delegate int LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc cb, IntPtr hmod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hk, int code, IntPtr wp, IntPtr lp);

    static PopMenu cur;
    static IntPtr hook = IntPtr.Zero;
    static LowLevelMouseProc hookProc;

    static int HookCb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = (int)wParam;
            if (nCode >= 0 && (msg == 0x0201 || msg == 0x0204 || msg == 0x0207))
            {
                PopMenu p = cur;
                if (p != null && !p.closing)
                {
                    MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Point q = new Point(ms.pt.X, ms.pt.Y);
                    if (!p.Bounds.Contains(q))
                    {
                        try { p.BeginInvoke(new Action(() => { if (cur == p) p.HideAndDispose(); })); }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return (int)CallNextHookEx(hook, nCode, wParam, lParam);
    }

    void InstallHook()
    {
        try
        {
            if (hookProc == null) hookProc = new LowLevelMouseProc(HookCb);
            if (hook == IntPtr.Zero) hook = SetWindowsHookEx(14, hookProc, IntPtr.Zero, 0);
        }
        catch { }
    }

    static void UninstallHook()
    {
        try { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
        catch { }
    }

    void HideAndDispose()
    {
        if (closing) return;
        closing = true;
        UninstallHook();
        try { Hide(); } catch { }
        if (cur == this) cur = null;
        try { Dispose(); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UninstallHook();
        if (cur == this) cur = null;
        base.OnFormClosed(e);
    }

    void Relayout()
    {
        int h = mainH, w = W;
        if (openSub >= 0)
        {
            h = Math.Max(h, SubTop + SubH);
            w = W + Gap + SubW;
        }
        Rectangle wa = Screen.FromPoint(new Point(Left + 10, Top + 10)).WorkingArea;
        int nx = Left, ny = Top;
        if (nx + w > wa.Right) nx = wa.Right - w;
        if (nx < wa.Left) nx = wa.Left;
        if (ny + h > wa.Bottom) ny = wa.Bottom - h;
        if (ny < wa.Top) ny = wa.Top;
        Bounds = new Rectangle(nx, ny, w, h);

        GraphicsPath rp = Round(0, 0, W, mainH, Rad * 2);
        if (openSub >= 0) rp.AddPath(Round(W + Gap, SubTop, SubW, SubH, Rad * 2), false);
        Region old = Region;
        Region = new Region(rp);
        if (old != null) old.Dispose();
    }

    void UpdateHover()
    {
        Point cp = PointToClient(Cursor.Position);
        int oldH = hover, oldS = openSub, oldSH = subHover;
        hover = -1; subHover = -1;

        bool inSubCard = false;
        if (openSub >= 0)
        {
            Rectangle sr = SubRect;
            if (sr.Contains(cp))
            {
                inSubCard = true;
                int idx = (cp.Y - SubTop - PadY) / RowH;
                if (idx >= 0 && idx < items[openSub].Children.Length) subHover = idx;
            }
        }
        if (cp.X < W && cp.Y >= PadY && cp.Y < PadY + RowH * items.Count)
        {
            int idx = (cp.Y - PadY) / RowH;
            if (idx >= 0 && idx < items.Count) hover = idx;
        }

        if (hover >= 0 && HasSub(hover))
        {
            if (openSub != hover) { openSub = hover; Relayout(); }
        }
        else if (!inSubCard && openSub >= 0)
        {
            openSub = -1; Relayout();
        }

        if (oldH != hover || oldS != openSub || oldSH != subHover) Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) { Close(); return; }

        Point cp = e.Location;
        if (openSub >= 0 && SubRect.Contains(cp))
        {
            int idx = (cp.Y - SubTop - PadY) / RowH;
            if (idx >= 0 && idx < items[openSub].Children.Length)
            {
                Run(items[openSub].Children[idx].Click);
                return;
            }
        }
        if (cp.X < W && cp.Y >= PadY && cp.Y < PadY + RowH * items.Count)
        {
            int idx = (cp.Y - PadY) / RowH;
            if (idx >= 0 && idx < items.Count)
            {
                if (HasSub(idx)) { openSub = idx; Relayout(); Invalidate(); return; }
                Run(items[idx].Click);
            }
        }
    }

    void Run(Action a)
    {
        if (closing) return;
        closing = true;
        UninstallHook();
        if (cur == this) cur = null;
        try { Hide(); } catch { }
        try { if (a != null) a(); }
        catch { }
        finally { try { Dispose(); } catch { } }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (closing) return;
        HideAndDispose();
    }

    void DrawCard(Graphics g, int x, int y, int w, int h)
    {
        using (GraphicsPath p = Round(x, y, w, h, Rad * 2))
        {
            using (SolidBrush b = new SolidBrush(CBg)) g.FillPath(b, p);
            using (Pen pen = new Pen(CLine, 1f)) g.DrawPath(pen, p);
        }
    }

    void DrawHi(Graphics g, int x, int top, int w)
    {
        using (GraphicsPath p = Round(x + 5, top + 2, w - 10, RowH - 4, 14))
        using (SolidBrush b = new SolidBrush(CHi))
            g.FillPath(b, p);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        DrawCard(g, 0, 0, W, mainH);

        using (Font f = new Font("Microsoft YaHei", 10.5f, FontStyle.Regular))
        using (StringFormat sf = new StringFormat())
        {
            sf.LineAlignment = StringAlignment.Center;
            sf.Alignment = StringAlignment.Near;
            sf.FormatFlags = StringFormatFlags.NoWrap;

            for (int i = 0; i < items.Count; i++)
            {
                int top = PadY + i * RowH;
                if (i == items.Count - 1 && items.Count > 2)
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                        g.FillRectangle(lb, 14, top - 1, W - 28, 1);

                bool act = (items[i].Click != null || HasSub(i));
                bool hov = act && (hover == i || openSub == i);
                if (hov) DrawHi(g, 0, top, W);
                DrawItemIcon(g, 22, top + RowH / 2, items[i].Icon);

                Color tc = hov ? CGold : ((!act || items[i].Icon == 4) ? CTxt2 : CTxt);
                float rightPad = items[i].Toggle ? 64f : 26f;
                using (SolidBrush tb = new SolidBrush(tc))
                    g.DrawString(items[i].Text, f, tb, new RectangleF(TxtX, top, W - TxtX - rightPad, RowH), sf);

                if (items[i].Toggle)
                {
                    int sw = 36, sh = 18;
                    int sx = W - sw - 16, sy = top + (RowH - sh) / 2;
                    using (GraphicsPath sp = Round(sx, sy, sw, sh, sh))
                    using (SolidBrush sb = new SolidBrush(items[i].Checked ? CSwitchOn : CSwitchOff))
                        g.FillPath(sb, sp);
                    int kd = 12;
                    int kx = items[i].Checked ? sx + sw - kd - 3 : sx + 3;
                    using (SolidBrush kb = new SolidBrush(items[i].Checked ? CSwitchKnob : CSwitchKnobOff))
                        g.FillEllipse(kb, kx, sy + 3, kd, kd);
                }
                else if (items[i].Checked)
                    using (SolidBrush db = new SolidBrush(CGold))
                        g.FillEllipse(db, W - 26, top + RowH / 2f - 3.5f, 7, 7);

                if (HasSub(i))
                    using (Pen ap = new Pen(CTxt2, 1.2f))
                    {
                        float ax = W - 24, ay = top + RowH / 2f;
                        g.DrawLine(ap, ax, ay - 3.5f, ax + 4, ay);
                        g.DrawLine(ap, ax + 4, ay, ax, ay + 3.5f);
                    }
            }

            if (openSub >= 0)
            {
                DrawCard(g, W + Gap, SubTop, SubW, SubH);
                MItem[] ch = items[openSub].Children;
                for (int i = 0; i < ch.Length; i++)
                {
                    int top = SubTop + PadY + i * RowH;
                    if (subHover == i) DrawHi(g, W + Gap, top, SubW);
                }
                for (int i = 0; i < ch.Length; i++)
                {
                    int top = SubTop + PadY + i * RowH;
                    using (SolidBrush tb = new SolidBrush(subHover == i ? CGold : CTxt))
                        g.DrawString(ch[i].Text, f, tb, new RectangleF(W + Gap + 14, top, SubW - 20, RowH), sf);
                }
            }
        }
    }

    void DrawItemIcon(Graphics g, float cx, float cy, int kind)
    {
        Color c = (kind == 4) ? CTxt2 : CGold;
        using (Pen p = new Pen(c, 1.4f))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            switch (kind)
            {
                case 0:   // 电源
                    g.DrawArc(p, cx - 6, cy - 6, 12, 12, -60, 300);
                    g.DrawLine(p, cx, cy - 9, cx, cy - 1);
                    break;
                case 1:   // 闪电
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx + 1, cy - 8), new PointF(cx - 5, cy + 1),
                        new PointF(cx - 0.5f, cy + 1), new PointF(cx - 2, cy + 8),
                        new PointF(cx + 6, cy - 2), new PointF(cx + 1, cy - 2) });
                    break;
                case 2:   // 相机
                    g.DrawRectangle(p, cx - 8, cy - 4, 16, 11);
                    g.DrawRectangle(p, cx - 3, cy - 7, 6, 4);
                    g.DrawEllipse(p, cx - 4, cy - 1.5f, 8, 8);
                    break;
                case 3:   // 芯片
                    g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
                    g.DrawRectangle(p, cx - 8, cy - 8, 16, 16);
                    break;
                case 4:   // ×
                    g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
                case 5:   // WinTune 的 W
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx - 7, cy - 6), new PointF(cx - 3.6f, cy + 6),
                        new PointF(cx, cy - 2.5f), new PointF(cx + 3.6f, cy + 6),
                        new PointF(cx + 7, cy - 6) });
                    break;
            }
        }
    }
}





