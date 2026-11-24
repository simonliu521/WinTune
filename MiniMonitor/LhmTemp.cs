using System;
using System.Linq;
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

    public static int Last = -1;          // 摄氏度整数，-1 表示暂时读不到
    public static int Cores = -1;         // 核心数，仅用于界面提示

    public static void Start()
    {
        if (timer != null) return;
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
                if (pc == null)
                {
                    if (DateTime.Now < retryAt) return;
                    Computer c = new Computer { IsCpuEnabled = true };
                    c.Open();
                    IHardware found = null;
                    foreach (IHardware hw in c.Hardware)
                        if (hw.HardwareType == HardwareType.Cpu) { found = hw; break; }
                    if (found == null)
                    {
                        c.Close();
                        retryAt = DateTime.Now.AddSeconds(60);
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
                if (cpu.Sensors != null) Cores = cpu.Sensors.Count(x => x.SensorType == SensorType.Temperature && (x.Name ?? "").StartsWith("CPU Core"));
                retryAt = DateTime.MinValue;
            }
            catch
            {
                Last = -1;
                try { if (pc != null) { pc.Close(); pc = null; } } catch { }
                try { cpu = null; } catch { }
                retryAt = DateTime.Now.AddSeconds(30);
            }
        }
    }
}
