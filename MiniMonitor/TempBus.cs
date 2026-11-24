using System;
using System.Runtime.InteropServices;

// 温度共享内存：管理员代理进程写，普通权限的界面进程读。
// 用 Local\ 命名（同会话可见），并显式给 Everyone 读权限，避免跨完整性打开被拒。
public static class TempBus
{
    const string MapName = "Local\\MiniMonitorTempShared";
    const string Sddl = "D:(A;;FRFW;;;WD)";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES sa, uint prot, uint hi, uint lo, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, uint bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    static IntPtr wMap = IntPtr.Zero, wView = IntPtr.Zero;   // 写端
    static IntPtr rMap = IntPtr.Zero, rView = IntPtr.Zero;   // 读端
    static IntPtr stopMap = IntPtr.Zero, stopView = IntPtr.Zero;  // 退出信号
    static IntPtr hbMap = IntPtr.Zero, hbView = IntPtr.Zero;      // 界面心跳

    // 写端：代理进程调用一次
    public static bool Create()
    {
        if (wView != IntPtr.Zero) return true;

        // 代理重启时共享内存可能还被界面进程读着；CreateFileMapping 按 SECTION_ALL_ACCESS 打开，
        // SDDL 只给了读写会被拒。先用 OpenFileMapping(FILE_MAP_READ|WRITE) 接管已有映射。
        IntPtr existing = OpenFileMappingW(0x0002 | 0x0004, false, MapName);
        if (existing != IntPtr.Zero)
        {
            IntPtr view = MapViewOfFile(existing, 0x0002 | 0x0004, 0, 0, 0);
            if (view != IntPtr.Zero)
            {
                wMap = existing;
                wView = view;
                return true;
            }
            CloseHandle(existing);
        }

        IntPtr sd = IntPtr.Zero; uint sz = 0;
        SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out sd, out sz))
        {
            sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
            sa.lpSecurityDescriptor = sd;
            sa.bInheritHandle = false;
        }
        wMap = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04, 0, 64, MapName);
        if (sd != IntPtr.Zero) LocalFree(sd);
        if (wMap == IntPtr.Zero) return false;
        wView = MapViewOfFile(wMap, 0x0002 | 0x0004, 0, 0, 0);   // READ|WRITE
        return wView != IntPtr.Zero;
    }

    // 写端：每次采集后调用
    public static void Publish(int temp)
    {
        if (!Create()) return;
        Marshal.WriteInt64(wView, 8, DateTime.Now.Ticks);
        Marshal.WriteInt32(wView, 0, temp);
    }

    // 读端：界面每秒调用；8 秒没更新视为失效
    public static int Read()
    {
        if (rView == IntPtr.Zero)
        {
            IntPtr h = OpenFileMappingW(0x0004, false, MapName);   // FILE_MAP_READ
            if (h == IntPtr.Zero) return -1;
            IntPtr v = MapViewOfFile(h, 0x0004, 0, 0, 0);
            if (v == IntPtr.Zero) { CloseHandle(h); return -1; }
            rMap = h; rView = v;
        }
        try
        {
            long ticks = Marshal.ReadInt64(rView, 8);
            if (ticks == 0) return -1;
            if (DateTime.Now.Ticks - ticks > TimeSpan.TicksPerSecond * 8) return -1;
            return Marshal.ReadInt32(rView, 0);
        }
        catch { return -1; }
    }

    // 读端：代理重启后时间戳变了也不用重开，句柄一直有效
    public static void ResetReader()
    {
        try { if (rView != IntPtr.Zero) UnmapViewOfFile(rView); } catch { }
        try { if (rMap != IntPtr.Zero) CloseHandle(rMap); } catch { }
        rView = IntPtr.Zero; rMap = IntPtr.Zero;
    }

    // ---- 退出信号：界面点「退出」时往 offset16 写 1，管理员代理每秒检查后自行结束 ----
    public static bool RequestStop()
    {
        try
        {
            if (stopView == IntPtr.Zero)
            {
                IntPtr h = OpenFileMappingW(0x0002 | 0x0004, false, MapName);
                if (h == IntPtr.Zero) return false;
                IntPtr v = MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, 0);
                if (v == IntPtr.Zero) { CloseHandle(h); return false; }
                stopMap = h; stopView = v;
            }
            Marshal.WriteInt32(stopView, 16, 1);
            return true;
        }
        catch { return false; }
    }

    public static bool StopRequested()
    {
        try
        {
            if (wView == IntPtr.Zero) return false;
            return Marshal.ReadInt32(wView, 16) != 0;
        }
        catch { return false; }
    }

    // ---- 打开 WinTune：界面写 offset 28，管理员代理读到后用自己的提权令牌启动，不再弹 UAC ----
    public static bool RequestOpenWinTune()
    {
        IntPtr h = IntPtr.Zero, v = IntPtr.Zero;
        try
        {
            h = OpenFileMappingW(0x0002 | 0x0004, false, MapName);
            if (h == IntPtr.Zero) return false;
            v = MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, 0);
            if (v == IntPtr.Zero) return false;
            Marshal.WriteInt32(v, 28, 1);
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (v != IntPtr.Zero) UnmapViewOfFile(v); } catch { }
            try { if (h != IntPtr.Zero) CloseHandle(h); } catch { }
        }
    }

    /// <summary>代理端取一次请求并清零，避免每秒重复拉起。</summary>
    public static bool TakeOpenWinTuneRequest()
    {
        try
        {
            if (wView == IntPtr.Zero) return false;
            if (Marshal.ReadInt32(wView, 28) == 0) return false;
            Marshal.WriteInt32(wView, 28, 0);
            return true;
        }
        catch { return false; }
    }

    /// <summary>界面每次启动先把退出标志清零：上一轮关闭时留下的 1 不能带到这一轮。</summary>
    public static void ClearStop()
    {
        IntPtr h = IntPtr.Zero, v = IntPtr.Zero;
        try
        {
            h = OpenFileMappingW(0x0002 | 0x0004, false, MapName);
            if (h == IntPtr.Zero) return;
            v = MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, 0);
            if (v == IntPtr.Zero) return;
            Marshal.WriteInt32(v, 16, 0);
        }
        catch { }
        finally
        {
            try { if (v != IntPtr.Zero) UnmapViewOfFile(v); } catch { }
            try { if (h != IntPtr.Zero) CloseHandle(h); } catch { }
        }
    }

    // ---- 界面心跳：offset 20，界面每秒写一次；代理发现界面没了就自行退出，避免孤儿 ----
    public static void Heartbeat()
    {
        try
        {
            if (hbView == IntPtr.Zero)
            {
                IntPtr h = OpenFileMappingW(0x0002 | 0x0004, false, MapName);
                if (h == IntPtr.Zero) return;
                IntPtr v = MapViewOfFile(h, 0x0002 | 0x0004, 0, 0, 0);
                if (v == IntPtr.Zero) { CloseHandle(h); return; }
                hbMap = h; hbView = v;
            }
            Marshal.WriteInt64(hbView, 20, DateTime.Now.Ticks);
        }
        catch { }
    }

    public static long HeartbeatTicks()
    {
        try
        {
            if (wView == IntPtr.Zero) return 0;
            return Marshal.ReadInt64(wView, 20);
        }
        catch { return 0; }
    }
}

