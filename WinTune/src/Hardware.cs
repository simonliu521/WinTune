using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using LibreHardwareMonitor.Hardware;

namespace WinTune
{
    /// <summary>
    /// 概览页的实时硬件信息（CPU / GPU / 内存 / 硬盘温度与占用），
    /// 以及 MiniMonitor 的截图设置读写（%APPDATA%\MiniMonitor\settings.ini + 计划任务）。
    /// </summary>
    public static class Hardware
    {
        private static Computer pc;
        private static string err = "";
        private static readonly object sync = new object();

        /* ---------- 兜底用的系统接口 ---------- */
        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private static bool pawnioBootTried;

        private static void Ensure()
        {
            if (pc != null) return;
            /* 注销/重启后 PawnIO 是手动启动的内核驱动，常常处于停止状态。
               传感器对象一旦在驱动启动前 Open，CPU 温度就会一直空白；
               所以在第一次初始化前先把驱动拉起来，再打开 LibreHardwareMonitor。 */
            if (!pawnioBootTried)
            {
                pawnioBootTried = true;
                try
                {
                    if (PawnioInstalled() && !PawnioRunning()) StartPawnio();
                }
                catch { }
            }
            if (err.Length > 0) return;
            try
            {
                Computer c = new Computer();
                c.IsCpuEnabled = true;
                c.IsGpuEnabled = true;
                c.IsMemoryEnabled = true;
                c.IsMotherboardEnabled = true;
                c.IsStorageEnabled = true;
                c.IsControllerEnabled = false;
                c.Open();
                pc = c;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                Log.Warn("hw", "硬件传感器初始化失败：" + ex.Message);
            }
        }

        public static void Close()
        {
            lock (sync)
            {
                try { if (pc != null) pc.Close(); } catch { }
                pc = null;
                err = "";
            }
        }

        /// <summary>驱动启动/重启后必须重新 Open 传感器，否则温度读数不会自己回来。</summary>
        public static void ResetSensors()
        {
            Close();
        }

        public static string CpuName()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        string s = Convert.ToString(k.GetValue("ProcessorNameString"));
                        if (!string.IsNullOrEmpty(s)) return s.Trim();
                    }
                }
            }
            catch { }
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        }

        /* ---------- 内存条：容量 / 类型 / 频率 ----------
           走系统自带的 WMI（Win32_PhysicalMemory），不装任何第三方组件，
           也不需要管理员权限。内存条插拔不频繁，结果缓存 5 分钟，
           每秒采样时不会反复查 WMI。 */

        private static string memMods = "";
        private static DateTime memModsAt = DateTime.MinValue;

        public static string MemModulesJson()
        {
            if (memMods.Length > 0 && (DateTime.Now - memModsAt).TotalSeconds < 300) return memMods;
            StringBuilder arr = new StringBuilder("[");
            int cnt = 0;
            try
            {
                using (System.Management.ManagementObjectSearcher se =
                       new System.Management.ManagementObjectSearcher(
                       "SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,DeviceLocator,Manufacturer,PartNumber FROM Win32_PhysicalMemory"))
                {
                    foreach (System.Management.ManagementObject mo in se.Get())
                    {
                        try
                        {
                            ulong cap = Num(mo["Capacity"]);
                            if (cap == 0) continue;
                            long gb = (long)Math.Round(cap / 1073741824.0, MidpointRounding.AwayFromZero);
                            int speed = (int)Num(mo["Speed"]);
                            int cfg = (int)Num(mo["ConfiguredClockSpeed"]);
                            int typ = (int)Num(mo["SMBIOSMemoryType"]);
                            if (cnt++ > 0) arr.Append(',');
                            arr.Append(new JB()
                                .Add("gb", gb)
                                .Add("speed", speed)
                                .Add("cfg", cfg > 0 ? cfg : speed)
                                .Add("type", MemTypeName(typ))
                                .Add("slot", Text(mo["DeviceLocator"]))
                                .Add("vendor", Text(mo["Manufacturer"]))
                                .Add("part", Text(mo["PartNumber"]))
                                .End());
                        }
                        catch { }
                        finally { try { mo.Dispose(); } catch { } }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("hw", "读内存条信息失败：" + ex.Message); }
            arr.Append(']');
            memMods = arr.ToString();
            memModsAt = DateTime.Now;
            return memMods;
        }

        private static ulong Num(object o)
        {
            try
            {
                if (o == null) return 0;
                if (o is ulong) return (ulong)o;
                if (o is uint) return (uint)o;
                if (o is int) return (ulong)(int)o;
                if (o is ushort) return (ushort)o;
                return ulong.Parse(Convert.ToString(o).Trim());
            }
            catch { return 0; }
        }

        private static string Text(object o)
        {
            string s = Convert.ToString(o);
            return s == null ? "" : s.Trim();
        }

        /// <summary>SMBIOS 的内存类型编号换成名字；没读到的返回空串，界面就不显示。</summary>
        private static string MemTypeName(int t)
        {
            switch (t)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return "";
            }
        }

        /* ---------- 采样 ---------- */

        /// <summary>CPU 温度的真实区间；0 与负数代表驱动没给值，一律不算读到。</summary>
        private static bool ValidCpuTemp(float v)
        {
            return v > 0 && v < 125;
        }

        /// <summary>硬盘温度的真实区间。SSD 满载也就 70 度上下，90 度以上是错值，丢掉。</summary>
        private static bool ValidDiskTemp(float v)
        {
            return v > 0 && v < 90;
        }

        public static string SampleJson() { return SampleJson(false); }

        /* fast=true：只读 CPU / 内存 / 显卡 / 逻辑盘这些便宜的量，
           主板与硬盘传感器、风扇、温度表留到慢帧再扫，给 1 秒 3 次的刷新省开销 */
        public static string SampleJson(bool fast)
        {
            Ensure();

            string cpuName = "";
            double cpuLoad = -1, cpuTemp = -1, cpuPower = -1, cpuClock = -1;
            int cores = Environment.ProcessorCount;
            string memName = "";
            double memLoad = -1, memUsedMb = -1, memTotalMb = -1, memTemp = -1;
            List<string> gpus = new List<string>();
            List<string> temps = new List<string>();
            List<string> fans = new List<string>();
            List<string> disks = new List<string>();

            if (pc != null)
            {
                lock (sync)
                {
                    try
                    {
                        foreach (IHardware hw in pc.Hardware)
                        {
                            try
                            {
                                HardwareType ht = hw.HardwareType;
                                /* 快帧跳过主板与硬盘传感器遍历：这两类最花时间，2 秒一次的慢帧再扫 */
                                if (fast && (ht == HardwareType.Motherboard || ht == HardwareType.Storage)) continue;
                                hw.Update();
                                if (ht == HardwareType.Cpu)
                                {
                                    if (cpuName.Length == 0) cpuName = CpuName();
                                    if (cpuName.Length == 0) cpuName = hw.Name;
                                    foreach (ISensor s in hw.Sensors)
                                    {
                                        if (!s.Value.HasValue) continue;
                                        float v = s.Value.Value;
                                        if (s.SensorType == SensorType.Load && (s.Name == "CPU Total" || cpuLoad < 0)) cpuLoad = v;
                                        /* 只收真实读数：0 与 -1 是驱动没给值，超出物理范围的一律丢弃 */
                                        if (s.SensorType == SensorType.Temperature && ValidCpuTemp(v)
                                            && s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0) cpuTemp = v;
                                        if (s.SensorType == SensorType.Temperature && cpuTemp < 0 && ValidCpuTemp(v)) cpuTemp = v;
                                        if (s.SensorType == SensorType.Power && v > 0 && v < 400
                                            && s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0) cpuPower = v;
                                        if (s.SensorType == SensorType.Clock && cpuClock < 0 && v > 100 && v < 6000) cpuClock = v;
                                    }
                                }
                                else if (ht == HardwareType.GpuNvidia || ht == HardwareType.GpuAmd || ht == HardwareType.GpuIntel)
                                {
                                    double gl = -1, gt = -1, gm = -1, gmt = -1, gpow = -1, gfreq = -1;
                                    string gname = hw.Name;
                                    foreach (ISensor s in hw.Sensors)
                                    {
                                        if (!s.Value.HasValue) continue;
                                        float v = s.Value.Value;
                                        if (s.SensorType == SensorType.Load && s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0) gl = v;
                                        else if (s.SensorType == SensorType.Load && gl < 0) gl = v;
                                        if (s.SensorType == SensorType.Temperature && gt < 0 && v > 0 && v < 120) gt = v;
                                        if (s.SensorType == SensorType.SmallData && s.Name.IndexOf("Used", StringComparison.OrdinalIgnoreCase) >= 0) gm = v;
                                        if (s.SensorType == SensorType.SmallData && s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0) gmt = v;
                                        if (s.SensorType == SensorType.Power && gpow < 0 && v > 0 && v < 700) gpow = v;
                                        if (s.SensorType == SensorType.Clock && gfreq < 0 && v > 0) gfreq = v;
                                    }
                                    JB g = new JB().Add("name", gname)
                                        .Add("load", gl)
                                        .Add("temp", gt)
                                        .Add("memUsed", gm)
                                        .Add("memTotal", gmt)
                                        .Add("power", gpow)
                                        .Add("clock", gfreq);
                                    gpus.Add(g.End());
                                }
                                else if (ht == HardwareType.Memory)
                                {
                                    if (memName.Length == 0 && hw.Name.IndexOf("DDR", StringComparison.OrdinalIgnoreCase) >= 0) memName = hw.Name;
                                    foreach (ISensor s in hw.Sensors)
                                    {
                                        if (!s.Value.HasValue) continue;
                                        float v = s.Value.Value;
                                        if (s.SensorType == SensorType.Load && memLoad < 0) memLoad = v;
                                        if (s.SensorType == SensorType.Data && s.Name.IndexOf("Used", StringComparison.OrdinalIgnoreCase) >= 0) memUsedMb = v * 1024;
                                        if (s.SensorType == SensorType.Data && s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0) memTotalMb = v * 1024;
                                        if (s.SensorType == SensorType.Temperature && memTemp < 0 && v > 0 && v < 100) memTemp = v;
                                    }
                                }
                                else if (ht == HardwareType.Motherboard)
                                {
                                    foreach (ISensor s in hw.Sensors)
                                    {
                                        if (!s.Value.HasValue) continue;
                                        float v = s.Value.Value;
                                        /* 传感器名 + 主板型号，0 与超范围值当没读到 */
                                        if (s.SensorType == SensorType.Temperature && v > 0 && v < 150)
                                            temps.Add(new JB().Add("name", s.Name).Add("src", hw.Name).Add("v", v).End());
                                        if (s.SensorType == SensorType.Fan && v > 0 && v < 8000)
                                            fans.Add(new JB().Add("name", s.Name).Add("src", hw.Name).Add("rpm", v).End());
                                    }
                                }
                                else if (ht == HardwareType.Storage)
                                {
                                    /* 一块盘只出一条：同一块盘常被报出好几条温度（部分来自 SMART 里
                                       不是温度的属性，读数会是 80 度以上的错值），取其中最低的那条 */
                                    float t = -1;
                                    foreach (ISensor s in hw.Sensors)
                                    {
                                        if (!s.Value.HasValue) continue;
                                        if (s.SensorType != SensorType.Temperature) continue;
                                        float v = s.Value.Value;
                                        if (!ValidDiskTemp(v)) continue;
                                        if (t < 0 || v < t) t = v;
                                    }
                                    if (t > 0)
                                    {
                                        /* 同型号的盘会有多条，按型号编号区分，方便对上具体那块盘 */
                                        string nm = hw.Name;
                                        int dup = 0;
                                        for (int k = 0; k < temps.Count; k++)
                                        {
                                            if (temps[k].IndexOf("\"src\":\"" + nm + "\"", StringComparison.Ordinal) >= 0) dup++;
                                        }
                                        if (dup > 0) nm = nm + " #" + (dup + 1);
                                        temps.Add(new JB().Add("name", nm).Add("src", hw.Name).Add("v", t).End());
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                        Log.Warn("hw", "采样异常：" + ex.Message);
                    }
                }
            }

            /* 内存兜底：不管传感器有没有，都用系统接口算一遍 */
            double totalGb = 0, availGb = 0, pct = 0;
            try
            {
                MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ms))
                {
                    totalGb = ms.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
                    availGb = ms.ullAvailPhys / 1024.0 / 1024.0 / 1024.0;
                    pct = ms.dwMemoryLoad;
                }
            }
            catch { }

            if (totalGb > 0)
            {
                memTotalMb = totalGb * 1024;
                memUsedMb = (totalGb - availGb) * 1024;
                memLoad = pct;
            }

            /* 逻辑盘容量 */
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (!d.IsReady) continue;
                    disks.Add(new JB()
                        .Add("name", d.Name.TrimEnd('\\'))
                        .Add("total", d.TotalSize)
                        .Add("free", d.TotalFreeSpace)
                        .Add("fs", d.DriveFormat)
                        .End());
                }
            }
            catch { }

            /* CPU 名称兜底：直接读注册表里的型号字符串 */
            if (cpuName.Length == 0) cpuName = CpuName();

            ulong tick = GetTickCount64();
            long upMin = (long)(tick / 60000UL);

            JB b = new JB()
                .Add("type", "hw")
                .Add("ok", err.Length == 0)
                .Add("err", err)
                .Add("fast", fast)
                .Add("cpuName", cpuName)
                .Add("cpuLoad", cpuLoad)
                .Add("cpuTemp", cpuTemp)
                .Add("cpuPower", cpuPower)
                .Add("cpuClock", cpuClock)
                .Add("pawnio", PawnioInstalled())
                .Add("pawnioRun", PawnioRunning())
                .Add("pawnioSetup", FindPawnioSetup().Length > 0)
                .Add("cores", cores)
                .Add("memName", memName)
                .Add("memLoad", memLoad)
                .Add("memUsedMb", memUsedMb)
                .Add("memTotalMb", memTotalMb)
                .Add("memTemp", memTemp)
                .Raw("memModules", MemModulesJson())
                .Raw("gpus", "[" + string.Join(",", gpus.ToArray()) + "]")
                .Raw("disks", "[" + string.Join(",", disks.ToArray()) + "]")
                .Add("uptimeMin", upMin)
                .Add("time", DateTime.Now.ToString("HH:mm:ss"));
            /* 快帧不带这几项：界面沿用上一帧的值，卡片不会被抹掉 */
            if (!fast)
            {
                b.Raw("temps", "[" + string.Join(",", temps.ToArray()) + "]");
                b.Raw("fans", "[" + string.Join(",", fans.ToArray()) + "]");
                b.Add("procs", Process.GetProcesses().Length);
            }
            return b.End();
        }

        /* ==================== MiniMonitor 设置 ==================== */

        public static string SettingsFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor", "settings.ini"); }
        }

        private static readonly string MmTaskName = "MiniMonitorWidget";

        public static string MmJson()
        {
            string hotkey = "", dir = "";
            bool exists = false;
            if (File.Exists(SettingsFile))
            {
                exists = true;
                try
                {
                    foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                    {
                        if (line.StartsWith("Hotkey=")) hotkey = line.Substring(7).Trim();
                        else if (line.StartsWith("SaveDir=")) dir = line.Substring(8).Trim();
                    }
                }
                catch { }
            }

            bool running = false;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    if (string.Equals(p.ProcessName, "MiniMonitor", StringComparison.OrdinalIgnoreCase)) { running = true; break; }
                }
            }
            catch { }

            bool auto = MmAutoStartGet();
            string exe = FindMiniMonitorExe();
            string effDir = dir.Length > 0 ? dir : DefaultShotDir();
            string ver = MmVersion(exe);
            bool verFile = ver.Length > 0 && MmVersionFromConfig(exe);

            JB b = new JB()
                .Add("type", "mm")
                .Add("installed", exists || exe.Length > 0)
                .Add("running", running)
                .Add("hotkey", hotkey.Length > 0 ? hotkey : "Alt+A")
                .Add("saveDir", effDir)
                .Add("autoStart", auto)
                .Add("exe", exe)
                .Add("version", ver)
                .Add("verSrc", verFile ? "config" : (ver.Length > 0 ? "exe" : ""))
                .Add("build", MmBuild(exe))      /* 没有版本资源时用构建日期顶上 */
                .Add("bundled", MmBundled(exe));
            return b.End();
        }

        /// <summary>读 MiniMonitor 自己的开机自启（HKCU 的 Run 项，不依赖 WinTune 启动）。</summary>
        public static bool MmAutoStartGet()
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

        /// <summary>开启/关闭 MiniMonitor 开机自启。返回空串表示成功，否则是失败原因。</summary>
        public static string MmAutoStartSet(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (k == null) return "打不开当前用户的启动项注册表";
                    if (on)
                    {
                        string exe = FindMiniMonitorExe();
                        if (exe.Length == 0) return "找不到 MiniMonitor.exe，先解压完整包";
                        k.SetValue("MiniMonitor", "\"" + exe + "\"", Microsoft.Win32.RegistryValueKind.String);
                    }
                    else
                    {
                        try { k.DeleteValue("MiniMonitor", false); } catch { }
                    }
                }
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>读 MiniMonitor 版本号：优先读 exe 同目录 MiniMonitor.config 的 appSettings/Version
        /// （MiniMonitor 每次启动都会把自己的版本写进这个配置文件），读不到再退回 exe 的文件版本资源；
        /// 0.0.0.0 这类空版本当作没有。</summary>
        public static string MmVersion(string exe)
        {
            if (exe.Length == 0 || !File.Exists(exe)) return "";
            string fromCfg = MmConfigValue(exe, "Version");
            if (fromCfg.Length > 0) return fromCfg;
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
                if (vi == null) return "";
                string pv = (vi.ProductVersion ?? "").Trim();
                if (pv.Length == 0 || pv == "0.0.0.0" || pv == "0.0.0") pv = (vi.FileVersion ?? "").Trim();
                if (pv == "0.0.0.0" || pv == "0.0.0") return "";
                return pv;
            }
            catch { return ""; }
        }

        /// <summary>版本号是不是读自 MiniMonitor.config（给前端标明来源）。</summary>
        public static bool MmVersionFromConfig(string exe)
        {
            return exe.Length > 0 && MmConfigValue(exe, "Version").Length > 0;
        }

        /// <summary>读 MiniMonitor.config 的 appSettings 里某个键；没有文件或没这个键就返回空。
        /// 只认与软件同名的配置，不再回退到带 .exe 的旧命名。</summary>
        private static string MmConfigValue(string exe, string key)
        {
            try
            {
                string dir = Path.GetDirectoryName(exe);
                string[] cands = { Path.Combine(dir, "MiniMonitor.config") };
                for (int i = 0; i < cands.Length; i++)
                {
                    string v = ReadCfg(cands[i], key);
                    if (v.Length > 0) return v;
                }
                return "";
            }
            catch { return ""; }
        }

        private static string ReadCfg(string cfg, string key)
        {
            try
            {
                if (!File.Exists(cfg)) return "";
                XmlDocument doc = new XmlDocument();
                doc.Load(cfg);
                XmlNode node = doc.SelectSingleNode("/configuration/appSettings/add[@key='" + key + "']");
                if (node == null) return "";
                XmlAttribute a = node.Attributes["value"];
                return a == null ? "" : a.Value.Trim();
            }
            catch { return ""; }
        }


        /// <summary>MiniMonitor 的构建日期：优先读配置文件里 MiniMonitor 自己写的 Build，没有就用 exe 的写入时间。</summary>
        private static string MmBuild(string exe)
        {
            if (exe.Length == 0 || !File.Exists(exe)) return "";
            string fromCfg = MmConfigValue(exe, "Build");
            if (fromCfg.Length > 0) return fromCfg;
            try { return File.GetLastWriteTime(exe).ToString("yyyy-MM-dd"); }
            catch { return ""; }
        }

        public static string MmSet(string key, string value)
        {
            try
            {
                if (key == "hotkey" || key == "savedir")
                {
                    string hotkey = "Alt+A", dir = "", se = "";
                    if (File.Exists(SettingsFile))
                        foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                        {
                            if (line.StartsWith("Hotkey=")) hotkey = line.Substring(7).Trim();
                            else if (line.StartsWith("SaveDir=")) dir = line.Substring(8).Trim();
                            else if (line.StartsWith("ShotEnabled=")) se = line.Substring(12).Trim();
                        }
                    if (key == "hotkey") { hotkey = value.Trim(); if (hotkey.Length == 0) hotkey = "Alt+A"; }
                    else dir = value.Trim();
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                    string body = "Hotkey=" + hotkey + "\r\nSaveDir=" + dir + "\r\n";
                    if (se.Length > 0) body += "ShotEnabled=" + se + "\r\n";
                    File.WriteAllText(SettingsFile, body, Encoding.UTF8);
                    return Ok("已保存：" + (key == "hotkey" ? "快捷键 " + hotkey : "存储位置 " + dir) + "（重启 MiniMonitor 后生效）");
                }
                if (key == "shot")
                {
                    string hotkey = "Alt+A", dir = "";
                    if (File.Exists(SettingsFile))
                        foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                        {
                            if (line.StartsWith("Hotkey=")) hotkey = line.Substring(7).Trim();
                            else if (line.StartsWith("SaveDir=")) dir = line.Substring(8).Trim();
                        }
                    bool on = value == "1" || value == "true";
                    if (!on) hotkey = "none";
                    else if (hotkey == "none" || hotkey.Length == 0) hotkey = "Alt+A";
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                    File.WriteAllText(SettingsFile,
                        "Hotkey=" + hotkey + "\r\nSaveDir=" + dir + "\r\nShotEnabled=" + (on ? "1" : "0") + "\r\n", Encoding.UTF8);
                    return Ok(on ? "截图已开启，快捷键 " + hotkey + "（重启 MiniMonitor 后生效）"
                                 : "截图已关闭，热键不再注册（重启 MiniMonitor 后生效）");
                }
                if (key == "opendir")
                {
                    string dir = DefaultShotDir();
                    if (File.Exists(SettingsFile))
                        foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                            if (line.StartsWith("SaveDir=")) { string v = line.Substring(8).Trim(); if (v.Length > 0) dir = v; }
                    try { Directory.CreateDirectory(dir); }
                    catch { }
                    try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
                    catch { return Fail("打不开目录：" + dir); }
                    return Ok("已打开：" + dir);
                }
                if (key == "pawnio") return InstallPawnio();          /* 兼容旧版前端 */
                if (key == "pawnioInstall") return InstallPawnio();
                if (key == "pawnioStart") return StartPawnioAction();
                if (key == "start") return StartMiniMonitor();
                if (key == "restart")
                {
                    try
                    {
                        foreach (Process p in Process.GetProcesses())
                            if (string.Equals(p.ProcessName, "MiniMonitor", StringComparison.OrdinalIgnoreCase))
                                try { p.Kill(); } catch { }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(400);
                    return StartMiniMonitor();
                }
                if (key == "autostart")
                {
                    bool on = value == "1" || value == "true" || value == "on";
                    string r = MmAutoStartSet(on);
                    return r.Length == 0 ? Ok(on
                        ? "已开启开机自启：登录后 MiniMonitor 自己启动，与 WinTune 无关"
                        : "已关闭 MiniMonitor 开机自启") : Fail(r);
                }
                return Fail("不认识的设置：" + key);
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private static string Ok(string msg) { return new JB().Add("type", "mmSet").Add("ok", true).Add("msg", msg).End(); }
        private static string Fail(string msg) { return new JB().Add("type", "mmSet").Add("ok", false).Add("msg", msg).End(); }

        public static string DefaultShotDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "截图");
        }

        public static string FindMiniMonitorExe()
        {
            List<string> cands = new List<string>();

            /* 1) 随本软件一起分发的（MiniMonitor 子文件夹优先，然后才是根目录），
               全部用相对路径定位，换机器换盘符、整个文件夹搬家都不影响 */
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            cands.Add(Path.Combine(baseDir, "MiniMonitor", "MiniMonitor.exe"));
            cands.Add(Path.Combine(baseDir, "MiniMonitor.exe"));
            try
            {
                string up = Path.GetFullPath(Path.Combine(baseDir, ".."));
                cands.Add(Path.Combine(up, "MiniMonitor", "MiniMonitor.exe"));
                cands.Add(Path.Combine(up, "MiniMonitor.exe"));
            }
            catch { }

            /* 2) 已经装在系统里的位置 */
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            cands.Add(Path.Combine(local, "Programs", "MiniMonitor", "MiniMonitor.exe"));
            cands.Add(Path.Combine(local, "MiniMonitor", "MiniMonitor.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MiniMonitor", "MiniMonitor.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MiniMonitor", "MiniMonitor.exe"));
            /* 装在其它盘（D/E/F）的 Program Files 也扫一遍，只探固定盘根目录下一层 */
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    cands.Add(Path.Combine(d.RootDirectory.FullName, "Program Files", "MiniMonitor", "MiniMonitor.exe"));
                    cands.Add(Path.Combine(d.RootDirectory.FullName, "Program Files (x86)", "MiniMonitor", "MiniMonitor.exe"));
                }
            }
            catch { }

            /* 3) 计划任务里登记过的路径 */
            try
            {
                string xml = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string task = Path.Combine(xml, "System32", "Tasks", MmTaskName);
                if (File.Exists(task))
                {
                    string txt = File.ReadAllText(task);
                    Match(txt, cands);
                }
            }
            catch { }

            foreach (string c in cands)
            {
                try { if (c.Length > 0 && File.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return "";
        }

        /// <summary>MiniMonitor.exe 是否就在本软件目录里（随包分发）。</summary>
        public static bool MmBundled(string exe)
        {
            if (exe.Length == 0) return false;
            try
            {
                string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                return Path.GetFullPath(exe).StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>PawnIO 驱动是否已注册（只认服务键；服务暂时停止时也算已安装）。</summary>
        static bool PawnioInstalled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO"))
                    return k != null;
            }
            catch { return false; }
        }

        private static DateTime pawnioCheckAt = DateTime.MinValue;
        private static bool pawnioLastRun;

        /// <summary>
        /// PawnIO 是内核驱动，不是普通用户态进程；这里以服务状态为准。
        /// 若极少数环境另有 PawnIO 用户态进程，也一并认作已运行。
        /// 硬件面板每秒刷新，查询结果缓存 8 秒，避免反复拉起 sc.exe。
        /// </summary>
        static bool PawnioRunning()
        {
            if ((DateTime.Now - pawnioCheckAt).TotalSeconds < 8) return pawnioLastRun;
            pawnioCheckAt = DateTime.Now;
            pawnioLastRun = false;
            try
            {
                foreach (Process p in Process.GetProcessesByName("PawnIO"))
                {
                    pawnioLastRun = true;
                    break;
                }
            }
            catch { }
            if (pawnioLastRun) return true;
            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
                ProcessStartInfo psi = new ProcessStartInfo(sc, "query PawnIO");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(6000);
                    pawnioLastRun = o.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { pawnioLastRun = false; }
            return pawnioLastRun;
        }

        /// <summary>已安装但服务停了：尝试 sc start；WinTune 自身是管理员，通常直接成功。</summary>
        static bool StartPawnio()
        {
            if (PawnioRunning()) return true;
            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
                ProcessStartInfo psi = new ProcessStartInfo(sc, "start PawnIO");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi)) p.WaitForExit(8000);
            }
            catch { }
            pawnioCheckAt = DateTime.MinValue;
            for (int i = 0; i < 10; i++)
            {
                if (PawnioRunning()) return true;
                System.Threading.Thread.Sleep(300);
            }
            return PawnioRunning();
        }

        /// <summary>找 PawnIO 安装器：优先 MiniMonitor 目录，其次 WinTune 目录和原安装缓存。</summary>
        static string FindPawnioSetup()
        {
            List<string> cands = new List<string>();
            string mm = FindMiniMonitorExe();
            if (mm.Length > 0) cands.Add(Path.Combine(Path.GetDirectoryName(mm), "PawnIO_setup.exe"));
            cands.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PawnIO_setup.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinTune", "deps", "PawnIO_setup.exe"));
            foreach (string f in cands) if (File.Exists(f)) return f;
            return "";
        }

        /// <summary>
        /// 提权子进程入口：真正执行一次启动/安装，把结果写到 resultFile。
        /// 主界面不是管理员时，由主进程用 runas 拉起本进程；这样按钮不会“点了没反应”。
        /// </summary>
        public static int PawnioCli(string action, string resultFile)
        {
            string msg;
            bool ok;
            if (action == "start") ok = PawnioStartCore(out msg);
            else ok = PawnioInstallCore(out msg);
            try
            {
                if (!string.IsNullOrEmpty(resultFile))
                {
                    string dir = Path.GetDirectoryName(resultFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(resultFile, (ok ? "OK" : "FAIL") + "\r\n" + msg, Encoding.UTF8);
                }
            }
            catch { }
            return ok ? 0 : 1;
        }

        /// <summary>启动已安装的 PawnIO，并把 sc 的真实退出码和输出带回来。</summary>
        static bool PawnioStartCore(out string msg)
        {
            msg = "";
            if (!PawnioInstalled())
            {
                msg = "PawnIO 还没有安装，请先安装依赖。";
                return false;
            }
            if (PawnioRunning())
            {
                msg = "PawnIO 驱动已经在运行。";
                return true;
            }

            int code = -1;
            string output = "";
            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
                ProcessStartInfo psi = new ProcessStartInfo(sc, "start PawnIO");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    output = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
                    p.WaitForExit(10000);
                    code = p.ExitCode;
                }
            }
            catch (Exception ex) { msg = "调用 sc start 失败：" + ex.Message; }

            pawnioCheckAt = DateTime.MinValue;
            for (int i = 0; i < 20; i++)
            {
                if (PawnioRunning())
                {
                    msg = "PawnIO 驱动已启动。";
                    return true;
                }
                System.Threading.Thread.Sleep(300);
            }
            msg = "sc start 退出码 " + code + (output.Length > 0 ? "：" + output : "") + "；服务仍未运行。";
            return false;
        }

        /// <summary>安装/修复 PawnIO：已装先启动，真缺驱动或起不来再跑安装器。</summary>
        static bool PawnioInstallCore(out string msg)
        {
            msg = "";
            string s = "";
            if (PawnioInstalled())
            {
                if (PawnioStartCore(out s))
                {
                    msg = "PawnIO 已安装，" + s;
                    return true;
                }
                msg = "PawnIO 已安装，但启动失败：" + s + "；继续运行安装程序尝试修复。";
            }

            string setup = FindPawnioSetup();
            if (setup.Length == 0)
            {
                msg += " 找不到 PawnIO_setup.exe，请把完整包里的安装程序放回 MiniMonitor 文件夹。";
                return false;
            }

            int code = -1;
            bool exited = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(setup, "-install -silent");
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(setup);
                psi.UseShellExecute = false;   /* 本进程已经是管理员；不要在这里再弹一次 UAC */
                using (Process p = Process.Start(psi))
                {
                    exited = p.WaitForExit(90000);
                    if (exited) code = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                msg += " 运行 PawnIO 安装程序失败：" + ex.Message;
                return false;
            }

            string codeText = exited ? ("退出码 " + code) : "等待 90 秒仍未退出";
            if (code == 3010 || code == 1641)
                msg += "；安装器 " + codeText + "，要求重启。请重启后再打开 WinTune。";

            pawnioCheckAt = DateTime.MinValue;
            string started = "";
            if (PawnioInstalled() && PawnioStartCore(out started))
            {
                msg = "PawnIO 安装程序 " + codeText + "；" + started;
                return true;
            }

            if (code == 183)
                msg += "；安装器报告“已存在”（退出码 183），但没有把服务拉起来。";
            else if (!exited)
                msg += "；安装器 " + codeText + "。";
            msg += " 没有检测到正在运行的 PawnIO 服务；请查看系统事件或重启后再试。";
            return false;
        }

        /// <summary>主界面不是管理员时，用一次 UAC 拉起提权子进程执行安装/启动。</summary>
        static string RunElevatedPawnio(string action)
        {
            string dir = Path.Combine(Path.GetTempPath(), "WinTune");
            string result = Path.Combine(dir, "pawnio-" + action + "-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Directory.CreateDirectory(dir);
                try { if (File.Exists(result)) File.Delete(result); } catch { }

                ProcessStartInfo psi = new ProcessStartInfo(
                    System.Windows.Forms.Application.ExecutablePath,
                    "--pawnio-" + action + " \"" + result + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    bool exited = p.WaitForExit(120000);
                    int code = exited ? p.ExitCode : -999;
                    if (File.Exists(result))
                    {
                        string[] lines = File.ReadAllLines(result, Encoding.UTF8);
                        bool ok = lines.Length > 0 && lines[0].Trim() == "OK";
                        string msg = lines.Length > 1
                            ? string.Join("\r\n", lines, 1, lines.Length - 1).Trim()
                            : (ok ? "完成。" : "失败，但提权进程没有留下原因。");
                        try { File.Delete(result); } catch { }
                        return ok ? Ok(msg) : Fail(msg + (exited ? "" : "（管理员进程超时）"));
                    }
                    return Fail("管理员进程没有返回结果" +
                        (exited ? "，退出码 " + code : "，等待 120 秒仍未结束"));
                }
            }
            catch (Exception ex)
            {
                return Fail("需要管理员权限，但 UAC 提权没有完成：" + ex.Message);
            }
        }

        /// <summary>只启动已安装的驱动；启动后重开传感器。按钮显「启动依赖」时走这里。</summary>
        static string StartPawnioAction()
        {
            if (!PawnioInstalled())
                return Fail("PawnIO 还没有安装，请先点「安装依赖」");
            if (PawnioRunning())
            {
                ResetSensors();
                return Ok("PawnIO 驱动已经在运行，温度传感器已重新加载");
            }
            if (!SysInfo.IsAdmin())
                return RunElevatedPawnio("start");

            string msg = "";
            bool ok = PawnioStartCore(out msg);
            ResetSensors();
            return ok ? Ok(msg) : Fail(msg);
        }

        /// <summary>启动或安装 PawnIO：已安装但服务停了先 sc start；真缺驱动才运行安装程序。</summary>
        static string InstallPawnio()
        {
            if (PawnioInstalled() && PawnioRunning())
            {
                ResetSensors();
                return Ok("PawnIO 驱动已经在运行，温度传感器已重新加载");
            }
            if (!SysInfo.IsAdmin())
                return RunElevatedPawnio("install");

            string msg = "";
            bool ok = PawnioInstallCore(out msg);
            ResetSensors();
            return ok ? Ok(msg) : Fail(msg);
        }

        /// <summary>单独拉起 MiniMonitor。它是独立程序，WinTune 只负责双击启动，不接管进程、不挂 Job、关窗也不结束它。</summary>
        public static string StartMiniMonitor()
        {
            string exe = FindMiniMonitorExe();
            if (exe.Length == 0) return Fail("本软件目录里没有 MiniMonitor\\MiniMonitor.exe，重装完整包即可");
            try
            {
                foreach (Process p in Process.GetProcesses())
                    if (string.Equals(p.ProcessName, "MiniMonitor", StringComparison.OrdinalIgnoreCase))
                        return Ok("MiniMonitor 已在运行");
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
                return Ok("正在启动 MiniMonitor（它会自己检查驱动和温度代理）");
            }
            catch (Exception ex) { return Fail("启动失败：" + ex.Message); }
        }

        private static void Match(string txt, List<string> cands)
        {
            try
            {
                int i = txt.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return;
                int j = txt.IndexOf("</Command>", i, StringComparison.OrdinalIgnoreCase);
                if (j < 0) return;
                string cmd = txt.Substring(i + 9, j - i - 9).Trim();
                if (cmd.Length > 0) cands.Add(cmd);
            }
            catch { }
        }

        private static bool MakeShortcut(string lnk, string exe, string workDir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe });
                if (workDir != null) sc.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { workDir });
                sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                return true;
            }
            catch { return false; }
        }
    }
}
