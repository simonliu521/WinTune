using System;
using System.Linq;
using System.Text;
using System.Threading;
using LibreHardwareMonitor.Hardware;

// 内嵌 LibreHardwareMonitor：后台线程每 2 秒读一次 CPU 内核温度（需要 PawnIO 驱动 + 管理员权限）
public static class LhmTemp
{
    static readonly object gate = new object();
    static Computer pc;
    static IHardware cpu;
    static Timer timer;
    static DateTime retryAt = DateTime.MinValue;
    static bool dumped;
    static bool sensorDumped;
    static bool tickLogged;

    public static int Last = -1;          // 摄氏度整数，-1 表示暂时读不到
    public static int Cores = -1;         // 核心数，仅用于界面提示

    public static void Start()
    {
        if (timer != null) return;
        MiniMonitor.Trace("lhm start");
        timer = new Timer(Tick, null, 400, 2000);
    }

    public static void Stop()
    {
        lock (gate)
        {
            try { if (timer != null) timer.Dispose(); } catch { }
            timer = null;
            try { if (pc != null) pc.Close(); } catch { }
            pc = null; cpu = null;
            Last = -1;
        }
    }

    static void Tick(object state)
    {
        lock (gate)
        {
            try
            {
                if (!tickLogged)
                {
                    tickLogged = true;
                    MiniMonitor.Trace("lhm tick enter");
                }
                if (pc == null)
                {
                    if (DateTime.Now < retryAt) return;
                    Computer c = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMemoryEnabled = true,
                        IsMotherboardEnabled = true,
                        IsStorageEnabled = true,
                        IsControllerEnabled = false
                    };
                    c.Open();
                    IHardware found = null;
                    foreach (IHardware hw in c.Hardware)
                        if (hw.HardwareType == HardwareType.Cpu) { found = hw; break; }
                    if (found == null)
                    {
                        c.Close();
                        retryAt = DateTime.Now.AddSeconds(60);
                        MiniMonitor.Trace("lhm: no cpu hardware");
                        return;
                    }
                    pc = c;
                    cpu = found;
                }

                cpu.Update();

                double pkg = -1, max = -1, sum = 0;
                int n = 0;
                foreach (ISensor s in cpu.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                    double v = s.Value.Value;
                    if (v <= 0 || v > 120) continue;
                    string nm = s.Name ?? "";
                    if (nm == "CPU Package") pkg = v;
                    else if (nm == "Core Max") max = v;
                    else if (nm.StartsWith("CPU Core")) { sum += v; n++; }
                }

                double val = pkg > 0 ? pkg : (max > 0 ? max : (n > 0 ? sum / n : -1));
                Last = val > 0 ? (int)Math.Round(val) : -1;
                if (!dumped)
                {
                    dumped = true;
                    MiniMonitor.Trace("lhm first pkg=" + pkg + " max=" + max + " n=" + n + " val=" + val);
                }
                if (Last < 0 && !sensorDumped)
                {
                    sensorDumped = true;
                    StringBuilder sb = new StringBuilder();
                    foreach (ISensor s in cpu.Sensors)
                        sb.Append(s.SensorType + ":" + s.Name + "=" +
                            (s.Value.HasValue ? s.Value.Value.ToString("0.##") : "null") + "; ");
                    MiniMonitor.Trace("lhm sensors: " + sb.ToString());
                }
                if (cpu.Sensors != null) Cores = cpu.Sensors.Count(x => x.SensorType == SensorType.Temperature && (x.Name ?? "").StartsWith("CPU Core"));
                retryAt = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                Last = -1;
                MiniMonitor.Trace("lhm error: " + ex.GetType().Name + " " + ex.Message);
                try { if (pc != null) { pc.Close(); pc = null; } } catch { }
                try { cpu = null; } catch { }
                retryAt = DateTime.Now.AddSeconds(30);
            }
        }
    }
}
