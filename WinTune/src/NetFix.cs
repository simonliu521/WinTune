using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 网络修复。只调 Windows 自带命令（netsh / ipconfig / arp）和真实网络测量，
    /// 不下载第三方组件，不改没被点名的配置。所有判断都当场读系统状态，读不到就如实说读不到。
    /// </summary>
    public static class NetFix
    {
        private static volatile bool cancelReq;

        /// <summary>界面点“结束卡死工具”：把当前网络命令连子进程一起收掉。</summary>
        public static void Cancel()
        {
            /* 只置标志；正在等命令的后台线程会在 500ms 内看到并 taskkill /T 收掉整棵树。 */
            cancelReq = true;
        }

        private static void KillTree(Process p, bool wait)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    "/PID " + p.Id + " /T /F");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                Process k = Process.Start(si);
                if (k == null) return;
                if (wait) { try { k.WaitForExit(3000); } catch { } }
                try { k.Dispose(); } catch { }
            }
            catch { }
        }

        /* 测速：上限 20 MB / 25 秒，谁先到就停，避免慢网一直挂着 */
        private const int SpeedCapBytes = 20 * 1024 * 1024;
        private const int SpeedCapMs = 25000;
        private const int HostsListCap = 200;

        public static string HostsPath
        {
            get
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Path.Combine(win, @"System32\drivers\etc\hosts");
            }
        }

        /* ===================== 诊断信息 ===================== */

        private class Nic
        {
            public string Name = "";
            public string Desc = "";
            public string TypeText = "";
            public string Status = "";
            public bool Up;
            public long Speed;
            public List<string> Ipv4 = new List<string>();
            public List<string> Gw = new List<string>();
            public List<string> Dns = new List<string>();
            public bool Dhcp;
            public string Mac = "";
            public bool Virtual;
            public bool Bluetooth;
        }

        public class HostsEntry
        {
            public int Line;
            public string Ip = "";
            public string Domains = "";
            public string Raw = "";
            public string Why = "";
        }

        public class HostsInfo
        {
            public bool Ok = true;
            public string Error = "";
            public int TotalLines;
            public int Entries;
            public bool Writable;
            public List<HostsEntry> All = new List<HostsEntry>();
            public List<HostsEntry> Suspects = new List<HostsEntry>();
        }

        public static string InfoJson()
        {
            List<Nic> nics = ReadNics();
            HostsInfo hi = ReadHosts();

            StringBuilder nicArr = new StringBuilder();
            nicArr.Append('[');
            int upCount = 0, physUp = 0;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (n.Up) upCount++;
                if (n.Up && !n.Virtual && n.Ipv4.Count > 0) physUp++;
                if (i > 0) nicArr.Append(',');
                nicArr.Append(new JB()
                    .Add("name", n.Name)
                    .Add("desc", n.Desc)
                    .Add("type", n.TypeText)
                    .Add("status", n.Status)
                    .Add("up", n.Up)
                    .Add("speed", n.Speed)
                    .Add("ipv4", Join(n.Ipv4, " / "))
                    .Add("gw", Join(n.Gw, " / "))
                    .Add("dns", Join(n.Dns, " / "))
                    .Add("dhcp", n.Dhcp)
                    .Add("mac", n.Mac)
                    .Add("virtual", n.Virtual)
                    .End());
            }
            nicArr.Append(']');

            /* 默认网关汇总：优先物理网卡 */
            StringBuilder gwArr = new StringBuilder();
            gwArr.Append('[');
            bool gwFirst = true;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up) continue;
                for (int k = 0; k < n.Gw.Count; k++)
                {
                    if (!gwFirst) gwArr.Append(',');
                    gwFirst = false;
                    gwArr.Append(new JB().Add("iface", n.Name).Add("addr", n.Gw[k]).Add("virtual", n.Virtual).End());
                }
            }
            gwArr.Append(']');

            StringBuilder hsArr = new StringBuilder();
            hsArr.Append('[');
            int hsShown = 0;
            for (int i = 0; i < hi.Suspects.Count; i++)
            {
                HostsEntry e = hi.Suspects[i];
                if (hsShown++ >= HostsListCap) break;
                if (hsShown > 1) hsArr.Append(',');
                hsArr.Append(EntryJson(e));
            }
            hsArr.Append(']');

            StringBuilder allArr = new StringBuilder();
            allArr.Append('[');
            int allShown = 0;
            for (int i = 0; i < hi.All.Count; i++)
            {
                if (allShown++ >= HostsListCap) break;
                if (allShown > 1) allArr.Append(',');
                allArr.Append(EntryJson(hi.All[i]));
            }
            allArr.Append(']');

            StringBuilder dnsArr = new StringBuilder();
            dnsArr.Append('[');
            int dnsShown = 0;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up || n.Ipv4.Count == 0) continue;
                if (dnsShown++ > 0) dnsArr.Append(',');
                dnsArr.Append(new JB().Add("iface", n.Name).Add("dns", Join(n.Dns, " / "))
                    .Add("dhcp", n.Dhcp).Add("virtual", n.Virtual).End());
            }
            dnsArr.Append(']');

            ProxyInfo px = ReadProxy();
            Dictionary<string, int> mtus = ReadMtu();
            List<RouteInfo> routes = ReadPersistentRoutes();

            string proxyJson = new JB().Add("ie", px.IeEnable).Add("server", px.Server)
                .Add("auto", px.Auto).Add("winhttp", px.WinHttp)
                .Add("localPort", px.LocalPort).Add("localAlive", px.LocalAlive).End();

            StringBuilder svcArr = new StringBuilder();
            svcArr.Append('[');
            string[] svcKeys = new string[] { "Dnscache", "Dhcp" };
            string[] svcLabels = new string[] { "DNS Client", "DHCP Client" };
            for (int i = 0; i < svcKeys.Length; i++)
            {
                bool found;
                bool run = SvcRunning(svcKeys[i], out found);
                if (i > 0) svcArr.Append(',');
                svcArr.Append(new JB().Add("key", svcKeys[i]).Add("label", svcLabels[i])
                    .Add("found", found).Add("running", run).End());
            }
            svcArr.Append(']');

            StringBuilder mtuArr = new StringBuilder();
            mtuArr.Append('[');
            bool mtuFirst = true;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up) continue;
                if (!mtus.ContainsKey(n.Name)) continue;
                if (!mtuFirst) mtuArr.Append(',');
                mtuFirst = false;
                mtuArr.Append(new JB().Add("iface", n.Name).Add("mtu", mtus[n.Name]).End());
            }
            mtuArr.Append(']');

            StringBuilder rtArr = new StringBuilder();
            rtArr.Append('[');
            for (int i = 0; i < routes.Count; i++)
            {
                if (i > 0) rtArr.Append(',');
                rtArr.Append(new JB().Add("dest", routes[i].Dest).Add("mask", routes[i].Mask)
                    .Add("gw", routes[i].Gw).Add("metric", routes[i].Metric).End());
            }
            rtArr.Append(']');
            return new JB()
                .Add("type", "netInfo")
                .Add("admin", SysInfo.IsAdmin())
                .Add("nics", nics.Count)
                .Add("up", upCount)
                .Add("physUp", physUp)
                .Add("hostsPath", HostsPath)
                .Raw("nicList", nicArr.ToString())
                .Raw("gateways", gwArr.ToString())
                .Raw("dnsList", dnsArr.ToString())
                .Raw("hostsSuspects", hsArr.ToString())
                .Raw("hostsEntries", allArr.ToString())
                .Add("hostsOk", hi.Ok)
                .Add("hostsError", hi.Error)
                .Add("hostsTotalLines", hi.TotalLines)
                .Add("hostsEntriesCount", hi.Entries)
                .Add("hostsSuspectCount", hi.Suspects.Count)
                .Add("hostsWritable", hi.Writable)
                .Add("hostsShown", Math.Min(hi.Entries, HostsListCap))
                .Raw("proxy", proxyJson)
                .Raw("services", svcArr.ToString())
                .Raw("routes", rtArr.ToString())
                .Raw("mtuList", mtuArr.ToString())                .Add("measuredAt", DateTime.Now.ToString("HH:mm:ss"))
                .End();
        }

        private static string EntryJson(HostsEntry e)
        {
            return new JB().Add("line", e.Line).Add("ip", e.Ip).Add("domains", e.Domains)
                .Add("why", e.Why).Add("raw", e.Raw).End();
        }

        private static string Join(List<string> l, string sep)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(l[i]);
            }
            return sb.ToString();
        }

        /* ===================== 代理 / 服务 / 路由 / MTU 读取 ===================== */

        private class ProxyInfo
        {
            public bool IeEnable;
            public string Server = "";
            public string Auto = "";
            public string WinHttp = "";
            public int LocalPort;
            public bool LocalAlive;
        }

        private class RouteInfo
        {
            public string Dest = "";
            public string Mask = "";
            public string Gw = "";
            public int Metric;
        }

        /// <summary>从 "127.0.0.1:7890" / "http=127.0.0.1:7890;https=..." 里抽端口。</summary>
        private static string LocalProxyHostPort(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string low = s.ToLowerInvariant();
            int a = low.IndexOf("127.0.0.1:");
            int b = low.IndexOf("localhost:");
            int at = -1;
            if (a >= 0) at = a;
            if (b >= 0 && (at < 0 || b < at)) at = b;
            if (at < 0) return "";
            int i = at + 10;
            StringBuilder sb = new StringBuilder();
            while (i < low.Length && low[i] >= '0' && low[i] <= '9') { sb.Append(low[i]); i++; }
            return sb.ToString();
        }

        private static bool TcpAlive(string host, int port, int ms)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private static ProxyInfo ReadProxy()
        {
            ProxyInfo p = new ProxyInfo();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    if (k != null)
                    {
                        object en = k.GetValue("ProxyEnable");
                        p.IeEnable = en != null && Convert.ToInt32(en) != 0;
                        object sv = k.GetValue("ProxyServer");
                        p.Server = sv == null ? "" : sv.ToString();
                        object au = k.GetValue("AutoConfigURL");
                        p.Auto = au == null ? "" : au.ToString();
                    }
                }
            }
            catch { }
            string w = ShellCapture("netsh.exe", "winhttp show proxy", 5000);
            string[] ln = w.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < ln.Length; i++)
            {
                string s = ln[i].Trim();
                if (s.Length == 0) continue;
                if (s.IndexOf("代理服务器", StringComparison.Ordinal) == 0 ||
                    s.IndexOf("Proxy Server", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int c = s.IndexOf(':');
                    if (c > 0) p.WinHttp = s.Substring(c + 1).Trim();
                }
            }
            string hp = LocalProxyHostPort(p.Server);
            if (hp.Length == 0) hp = LocalProxyHostPort(p.Auto);
            if (hp.Length > 0) { try { p.LocalPort = int.Parse(hp); } catch { p.LocalPort = 0; } }
            if (p.LocalPort > 0) p.LocalAlive = TcpAlive("127.0.0.1", p.LocalPort, 350);
            return p;
        }

        /// <summary>netsh interface ipv4 show subinterfaces：Idx  Met  MTU  State  Name。</summary>
        private static Dictionary<string, int> ReadMtu()
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string txt = ShellCapture("netsh.exe", "interface ipv4 show subinterfaces", 5000);
            string[] ln = txt.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < ln.Length; i++)
            {
                string s = ln[i].Trim();
                if (s.Length == 0) continue;
                string[] p = s.Split(new char[] { ' ', '\t' }, 5, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5) continue;
                int idx, mtu;
                if (!int.TryParse(p[0], out idx)) continue;
                if (!int.TryParse(p[2], out mtu)) continue;
                if (mtu < 500 || mtu > 9100) continue;
                d[p[4].Trim()] = mtu;
            }
            return d;
        }

        private static bool SvcRunning(string key, out bool found)
        {
            found = false;
            string txt = ShellCapture("sc.exe", "query " + key, 5000);
            if (txt.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) >= 0) found = true;
            string[] ln = txt.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < ln.Length; i++)
            {
                string s = ln[i].Trim();
                if (s.IndexOf("STATE", StringComparison.OrdinalIgnoreCase) != 0) continue;
                if (s.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string[] t = s.Split(new char[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 3 && t[t.Length - 1] == "4") return true;
                return false;
            }
            return false;
        }

        private static bool WaitSvcState(string key, bool wantRun, int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                bool found;
                bool run = SvcRunning(key, out found);
                if (found && run == wantRun) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        /// <summary>持久静态路由存在 Tcpip\Parameters\PersistentRoutes，值名就是 dest,mask,gw,metric。</summary>
        private static List<RouteInfo> ReadPersistentRoutes()
        {
            List<RouteInfo> list = new List<RouteInfo>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes", false))
                {
                    if (k == null) return list;
                    string[] names = k.GetValueNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        string[] p = names[i].Split(',');
                        if (p.Length < 3) continue;
                        RouteInfo r = new RouteInfo();
                        r.Dest = p[0].Trim();
                        r.Mask = p[1].Trim();
                        r.Gw = p[2].Trim();
                        if (p.Length >= 4) { try { r.Metric = int.Parse(p[3].Trim()); } catch { } }
                        list.Add(r);
                    }
                }
            }
            catch { }
            return list;
        }

        private static bool PingQuick(string host, int ms)
        {
            try
            {
                Ping p = new Ping();
                PingReply r = p.Send(host, ms);
                return r != null && r.Status == IPStatus.Success;
            }
            catch { return false; }
        }

        /// <summary>发不分片 ICMP 逐步往下试，返回路径 MTU；探不出来返回 0。</summary>
        private static int ProbeMtu(string target, out string note)
        {
            note = "";
            int[] sizes = new int[] { 1472, 1464, 1452, 1442, 1422, 1402, 1382, 1352, 1322, 1292, 1272 };
            int best = 0, timeouts = 0;
            Ping p = new Ping();
            for (int i = 0; i < sizes.Length; i++)
            {
                try
                {
                    PingOptions o = new PingOptions(64, true);
                    PingReply r = p.Send(target, 800, new byte[sizes[i]], o);
                    if (r == null) { timeouts++; if (timeouts >= 2) break; continue; }
                    if (r.Status == IPStatus.Success) { best = sizes[i]; break; }
                    if (r.Status == IPStatus.PacketTooBig) continue;
                    timeouts++;
                    if (timeouts >= 2) break;
                }
                catch { break; }
            }
            if (best == 0) { note = "探不出来：对端不回 ICMP 或者这条链路全丢包"; return 0; }
            note = "最大不分片包 " + best + " 字节";
            return best + 28;
        }

        private static Nic PickMain(List<Nic> nics)
        {
            Nic up = null;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (n.Up && !n.Virtual && n.Ipv4.Count > 0) { up = n; break; }
            }
            if (up == null)
            {
                for (int i = 0; i < nics.Count; i++)
                    if (nics[i].Up) { up = nics[i]; break; }
            }
            return up;
        }

        private static string BackupReg(string key, string tag, Action<string> onEvent)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string to = Path.Combine(dir, tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".reg");
                ShellCapture("reg.exe", "export \"" + key + "\" \"" + to + "\" /y", 10000);
                Line(onEvent, "已导出注册表备份 → " + to);
                return to;
            }
            catch (Exception ex)
            {
                Line(onEvent, "注册表备份失败：" + ex.Message);
                return "";
            }
        }
        private static List<Nic> ReadNics()
        {
            List<Nic> list = new List<Nic>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return list; }

            foreach (NetworkInterface ni in all)
            {
                NetworkInterfaceType nt;
                try { nt = ni.NetworkInterfaceType; } catch { continue; }
                if (nt == NetworkInterfaceType.Loopback) continue;

                string nm = "";
                string ds = "";
                try { nm = ni.Name; } catch { }
                try { ds = ni.Description; } catch { }

                string low = (nm + " " + ds).ToLowerInvariant();
                bool bt = low.Contains("bluetooth") || low.Contains("蓝牙");
                bool virtualNic = IsVirtualName(low);
                bool wired = (nt == NetworkInterfaceType.Ethernet || nt == NetworkInterfaceType.GigabitEthernet ||
                              nt == NetworkInterfaceType.FastEthernetT || nt == NetworkInterfaceType.FastEthernetFx) && !bt && !virtualNic;
                bool wifi = nt == NetworkInterfaceType.Wireless80211 && !virtualNic && !bt;

                /* 只留真实网卡：有线 / 无线 / 蓝牙（蓝牙个人热点常被识别成以太网，按描述名单独认） */
                if (!(wired || wifi || bt)) continue;
                if (bt && virtualNic) continue;

                Nic n = new Nic();
                n.Name = nm;
                n.Desc = ds;
                n.Bluetooth = bt;
                n.Virtual = false;
                n.TypeText = bt ? "蓝牙" : (wifi ? "无线" : TypeText(nt));
                try
                {
                    n.Status = StatusText(ni.OperationalStatus);
                    n.Up = ni.OperationalStatus == OperationalStatus.Up;
                }
                catch { n.Up = false; }
                try { n.Speed = ni.Speed; } catch { n.Speed = 0; }
                try
                {
                    PhysicalAddress pa = ni.GetPhysicalAddress();
                    n.Mac = pa == null ? "" : pa.ToString();
                }
                catch { }

                try
                {
                    IPInterfaceProperties ip = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation ua in ip.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            n.Ipv4.Add(ua.Address.ToString());
                    }
                    foreach (GatewayIPAddressInformation g in ip.GatewayAddresses)
                    {
                        if (g.Address == null) continue;
                        if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (g.Address.Equals(IPAddress.Any)) continue;
                        n.Gw.Add(g.Address.ToString());
                    }
                    foreach (IPAddress d in ip.DnsAddresses)
                    {
                        if (d.AddressFamily == AddressFamily.InterNetwork) n.Dns.Add(d.ToString());
                    }
                    try
                    {
                        IPv4InterfaceProperties v4 = ip.GetIPv4Properties();
                        if (v4 != null) n.Dhcp = v4.IsDhcpEnabled;
                    }
                    catch { }
                }
                catch { }
                list.Add(n);
            }
            return list;
        }

        private static string TypeText(NetworkInterfaceType t)
        {
            if (t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet ||
                t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx)
                return "有线";
            if (t == NetworkInterfaceType.Wireless80211) return "无线";
            if (t == NetworkInterfaceType.Ppp) return "拨号";
            if (t == NetworkInterfaceType.Tunnel) return "隧道";
            if (t == NetworkInterfaceType.Loopback) return "回环";
            return t.ToString();
        }

        private static string StatusText(OperationalStatus s)
        {
            if (s == OperationalStatus.Up) return "已连接";
            if (s == OperationalStatus.Down) return "已断开";
            if (s == OperationalStatus.Dormant) return "休眠";
            if (s == OperationalStatus.NotPresent) return "设备不在";
            if (s == OperationalStatus.LowerLayerDown) return "下层断开";
            return s.ToString();
        }

        /// <summary>虚拟网卡 / VPN / 回环适配器：可以显示，但默认不拿它当主网卡改 DNS。</summary>
        private static bool IsVirtualName(string s)
        {
            if (s == null) return false;
            string t = s.ToLowerInvariant();
            return t.Contains("virtual") || t.Contains("vmware") || t.Contains("hyper-v") ||
                   t.Contains("virtualbox") || t.Contains("tap-") || t.Contains("wan miniport") ||
                   t.Contains("vpn") || t.Contains("wireguard") || t.Contains("tailscale") ||
                   t.Contains("zerotier") || t.Contains("npcap") || t.Contains("loopback") ||
                   t.Contains("teredo") || t.Contains("isatap");
        }

        /* ===================== hosts 解析 ===================== */

        public static HostsInfo ReadHosts()
        {
            HostsInfo h = new HostsInfo();
            string path = HostsPath;
            if (!File.Exists(path))
            {
                h.Ok = false;
                h.Error = "hosts 文件不存在：" + path;
                return h;
            }

            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.Default); }
            catch (Exception ex)
            {
                h.Ok = false;
                h.Error = "读不到 hosts：" + ex.Message;
                return h;
            }

            h = ParseText(lines);
            /* 能不能写：打开一次读写句柄就关掉，不改内容。非管理员一律按“不能写”说 */
            bool openOk = false;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    openOk = true;
                }
            }
            catch { openOk = false; }
            h.Writable = openOk && SysInfo.IsAdmin();
            return h;
        }

        /// <summary>解析 hosts 文本并按三条可验证的证据标可疑项；界面与自检共用同一套口径。</summary>
        public static HostsInfo ParseText(string[] lines)
        {
            HostsInfo h = new HostsInfo();
            h.TotalLines = lines == null ? 0 : lines.Length;
            List<HostsEntry> parsed = new List<HostsEntry>();
            for (int i = 0; i < h.TotalLines; i++)
            {
                string raw = lines[i];
                string body = raw;
                int hash = body.IndexOf('#');
                if (hash >= 0) body = body.Substring(0, hash);
                body = body.Trim();
                if (body.Length == 0) continue;

                string[] parts = body.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                HostsEntry e = new HostsEntry();
                e.Line = i + 1;
                e.Raw = raw.Trim();
                e.Ip = parts.Length > 0 ? parts[0] : "";
                StringBuilder dom = new StringBuilder();
                for (int q = 1; q < parts.Length; q++)
                {
                    if (q > 1) dom.Append(' ');
                    dom.Append(parts[q]);
                }
                e.Domains = dom.ToString();
                parsed.Add(e);
            }
            h.Entries = parsed.Count;

            /* 判定只看能说清楚的证据：
               1) IP 写法不合法（这一行根本不会生效）
               2) 指向 127.0.0.1 / 0.0.0.0 / ::1 的却不是本机名字（常见于屏蔽网站、劫持）
               3) 同一个域名同时指到两个不同地址（DNS 之外的多源冲突）
               私人内网 IP（192.168 / 10 / 172.16-31）指向内网主机属于正常用法，不当成可疑。 */
            Dictionary<string, string> firstIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parsed.Count; i++)
            {
                HostsEntry e = parsed[i];
                IPAddress ip;
                if (!IPAddress.TryParse(e.Ip, out ip))
                {
                    e.Why = "IP 写法不合法，这一行不会生效";
                }
                else if (IsLoopback(ip) && !IsLocalName(e.Domains))
                {
                    e.Why = "指向本机地址 " + e.Ip + "，对应网站会被屏蔽或被劫持";
                }
                else
                {
                    string[] ds = e.Domains.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int q = 0; q < ds.Length; q++)
                    {
                        string d = ds[q];
                        if (firstIp.ContainsKey(d))
                        {
                            if (!string.Equals(firstIp[d], e.Ip, StringComparison.OrdinalIgnoreCase))
                            {
                                e.Why = "域名 " + d + " 同时指向 " + firstIp[d] + " 和 " + e.Ip + "，两条互相冲突";
                                break;
                            }
                        }
                        else firstIp[d] = e.Ip;
                    }
                }

                if (e.Why.Length > 0) h.Suspects.Add(e);
                if (h.All.Count < HostsListCap) h.All.Add(e);
            }
            return h;
        }
        private static bool IsLoopback(IPAddress ip)
        {
            if (ip == null) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            string s = ip.ToString();
            return s == "0.0.0.0" || s == "::" || s == "::1";
        }

        private static bool IsLocalName(string domains)
        {
            if (domains == null || domains.Trim().Length == 0) return false;
            string machine = "";
            try { machine = Environment.MachineName; } catch { }
            string[] ds = domains.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bool allLocal = ds.Length > 0;
            for (int i = 0; i < ds.Length; i++)
            {
                string d = ds[i].ToLowerInvariant();
                bool local = d == "localhost" || d == "localhost.localdomain" || d == "local" ||
                             d == "broadcasthost" || d == "ip6-localhost" || d == "ip6-loopback" ||
                             (machine.Length > 0 && d == machine.ToLowerInvariant());
                if (!local) { allLocal = false; break; }
            }
            return allLocal;
        }

        private static string DefaultHosts()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Copyright (c) 1993-2009 Microsoft Corp.");
            sb.AppendLine("#");
            sb.AppendLine("# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.");
            sb.AppendLine("#");
            sb.AppendLine("# This file contains the mappings of IP addresses to host names. Each");
            sb.AppendLine("# entry should be kept on an individual line. The IP address should");
            sb.AppendLine("# be placed in the first column followed by the corresponding host name.");
            sb.AppendLine("# The IP address and the host name should be separated by at least one");
            sb.AppendLine("# space.");
            sb.AppendLine("#");
            sb.AppendLine("# Additionally, comments (such as these) may be inserted on individual");
            sb.AppendLine("# lines or following the machine name denoted by a '#' symbol.");
            sb.AppendLine("#");
            sb.AppendLine("# For example:");
            sb.AppendLine("#");
            sb.AppendLine("#      102.54.94.97     rhino.acme.com          # source server");
            sb.AppendLine("#       38.25.63.10     x.acme.com              # x client host");
            sb.AppendLine();
            sb.AppendLine("# localhost name resolution is handled within DNS itself.");
            sb.AppendLine("#\t127.0.0.1       localhost");
            sb.AppendLine("#\t::1             localhost");
            return sb.ToString();
        }

        /* ===================== 执行入口 ===================== */

        public static void Run(string action, string a1, string a2, Action<string> onEvent)
        {
            cancelReq = false;
            try
            {
                switch (action)
                {
                    case "dnsflush": DoDnsFlush(onEvent); break;
                    case "dnsauto": DoDnsAuto(a1, onEvent); break;
                    case "dnsset": DoDnsSet(a1, a2, onEvent); break;
                    case "nicrestart": DoNicRestart(a1, onEvent); break;
                    case "nicreset": DoNicReset(onEvent); break;
                    case "nicreinit": DoNicReinit(a1, onEvent); break;
                    case "gwrenew": DoGwRenew(a1, onEvent); break;
                    case "gwpings": DoGwPings(onEvent); break;
                    case "hostsopen": DoHostsOpen(onEvent); break;
                    case "hostsrestore": DoHostsRestore(onEvent); break;
                    case "hostsclean": DoHostsClean(onEvent); break;
                    case "hostsadd": DoHostsAdd(a1, a2, onEvent); break;
                    case "hostsdel": DoHostsDel(a1, onEvent); break;
                    case "proxyreset": DoProxyReset(onEvent); break;
                    case "svcrestart": DoSvcRestart(onEvent); break;
                    case "mtuprobe": DoMtuProbe(a1, onEvent); break;
                    case "mtuset": DoMtuSet(a1, a2, onEvent); break;
                    case "routesdel": DoRoutesDel(a1, onEvent); break;
                    case "speedtest": DoSpeed(a1, onEvent); break;
                    default: Done(onEvent, action, false, "未知操作：" + action); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("net", action + " 异常：" + ex.Message);
                Done(onEvent, action, false, "执行异常：" + ex.Message);
            }
        }

        /* ===================== DNS ===================== */

        private static void DoDnsFlush(Action<string> onEvent)
        {
            Line(onEvent, "执行 ipconfig /flushdns …");
            int code = Shell("ipconfig.exe", "/flushdns", onEvent);
            Done(onEvent, "dnsflush", code == 0,
                code == 0 ? "DNS 缓存已清空" : ("退出代码 " + code + "，缓存没清掉"));
        }

        private static void DoDnsAuto(string iface, Action<string> onEvent)
        {
            List<Nic> nics = ReadNics();
            string only = iface == null ? "" : iface.Trim();
            int changed = 0, total = 0;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up || n.Virtual || n.Ipv4.Count == 0) continue;
                if (only.Length > 0 && !string.Equals(n.Name, only, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                Line(onEvent, "【" + n.Name + "】改为自动获取 DNS …");
                int code = Shell("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name + "\" source=dhcp", onEvent);
                bool ok = code == 0;
                if (!ok && !n.Dhcp)
                {
                    /* 固定 IP 的网卡不接受 source=dhcp，改为清空自定义地址 */
                    Line(onEvent, "这块网卡是固定 IP，改用“清空自定义 DNS”…");
                    code = Shell("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name + "\" source=static address=none register=none", onEvent);
                    ok = code == 0;
                }
                if (ok) changed++;
            }
            Shell("ipconfig.exe", "/flushdns", onEvent);
            if (total == 0)
                Done(onEvent, "dnsauto", false, "没有已连接的物理网卡，改不了 DNS");
            else if (changed == 0)
                Done(onEvent, "dnsauto", false, "改自动获取失败，看上面每块的返回；这类改动需要管理员权限");
            else
                Done(onEvent, "dnsauto", true, "已把 " + changed + "/" + total + " 块网卡的 DNS 改为自动获取");
        }

        private static void DoDnsSet(string servers, string iface, Action<string> onEvent)
        {
            string[] raw = (servers == null ? "" : servers).Split(new char[] { ',', ';', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> good = new List<string>();
            for (int i = 0; i < raw.Length && good.Count < 3; i++)
            {
                IPAddress ip;
                if (IPAddress.TryParse(raw[i], out ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    good.Add(ip.ToString());
                else
                    Line(onEvent, "跳过不是 IPv4 地址的输入：" + raw[i]);
            }
            if (good.Count == 0)
            {
                Done(onEvent, "dnsset", false, "没有可用的 IPv4 地址，DNS 没改");
                return;
            }

            List<Nic> nics = ReadNics();
            List<Nic> target = new List<Nic>();
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up || n.Ipv4.Count == 0) continue;
                if (iface != null && iface.Trim().Length > 0)
                {
                    if (!string.Equals(n.Name, iface.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (n.Virtual) continue;
                target.Add(n);
            }
            if (target.Count == 0)
            {
                Done(onEvent, "dnsset", false, "没找到符合条件的网卡（要让虚拟网卡也能改，请在卡片上单独指定）");
                return;
            }

            int changed = 0;
            for (int i = 0; i < target.Count; i++)
            {
                Nic n = target[i];
                Line(onEvent, "【" + n.Name + "】设置 DNS 为 " + Join(good, " / ") + " …");
                int code = Shell("netsh.exe", "interface ipv4 set dnsservers name=\"" + n.Name +
                    "\" source=static address=" + good[0] + " register=primary validate=no", onEvent);
                if (code != 0) continue;
                for (int k = 1; k < good.Count; k++)
                {
                    Shell("netsh.exe", "interface ipv4 add dnsservers name=\"" + n.Name + "\" address=" +
                        good[k] + " index=" + (k + 1) + " validate=no", onEvent);
                }
                changed++;
            }
            Shell("ipconfig.exe", "/flushdns", onEvent);
            Done(onEvent, "dnsset", changed > 0,
                changed > 0 ? ("已把 " + changed + " 块网卡的 DNS 设为 " + Join(good, " / "))
                            : "设置失败，看上面的返回；改 DNS 需要管理员权限");
        }

        /* ===================== 网卡 ===================== */

        private static void DoNicRestart(string iface, Action<string> onEvent)
        {
            if (iface == null || iface.Trim().Length == 0)
            {
                Done(onEvent, "nicrestart", false, "没有指定网卡");
                return;
            }
            string name = iface.Trim();
            Line(onEvent, "重启用【" + name + "】：先禁用，等状态变成断开再启用。");
            int c1 = Shell("netsh.exe", "interface set interface name=\"" + name + "\" admin=disable", onEvent);
            if (c1 != 0)
            {
                Done(onEvent, "nicrestart", false, "禁用失败（常见原因：不是管理员，或网卡名对不上）");
                return;
            }

            WaitStatus(name, false, 8, onEvent);
            int c2 = Shell("netsh.exe", "interface set interface name=\"" + name + "\" admin=enable", onEvent);
            if (c2 != 0)
            {
                Done(onEvent, "nicrestart", false, "重新启用失败，请到“网络连接”里手动启用这块网卡");
                return;
            }

            bool up = WaitStatus(name, true, 20, onEvent);
            if (up) Shell("ipconfig.exe", "/renew \"" + name + "\"", onEvent);
            Shell("ipconfig.exe", "/flushdns", onEvent);

            Nic n = FindNic(name);
            string now = n == null ? "（读不到这块网卡）" : ("IPv4 " + (n.Ipv4.Count > 0 ? Join(n.Ipv4, " / ") : "还没拿到") +
                "，网关 " + (n.Gw.Count > 0 ? Join(n.Gw, " / ") : "没有"));
            Done(onEvent, "nicrestart", up, up ? "网卡已重新启用；" + now : "已启用，但 20 秒内没恢复连接；" + now);
        }

        private static bool WaitStatus(string name, bool wantUp, int seconds, Action<string> onEvent)
        {
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                Nic n = FindNic(name);
                if (n == null) continue;
                if (n.Up == wantUp)
                {
                    Line(onEvent, "状态已变为 " + n.Status + "（等了 " + (i + 1) + " 秒）");
                    return true;
                }
            }
            Line(onEvent, "等了 " + seconds + " 秒，状态没变成" + (wantUp ? "已连接" : "已断开"));
            return false;
        }

        private static Nic FindNic(string name)
        {
            List<Nic> nics = ReadNics();
            for (int i = 0; i < nics.Count; i++)
                if (string.Equals(nics[i].Name, name, StringComparison.OrdinalIgnoreCase)) return nics[i];
            return null;
        }

        private static void DoNicReset(Action<string> onEvent)
        {
            Line(onEvent, "重置 Winsock 目录 …");
            int c1 = Shell("netsh.exe", "winsock reset", onEvent);
            Line(onEvent, "重置 TCP/IP 协议栈 …");
            int c2 = Shell("netsh.exe", "int ip reset", onEvent);
            Shell("ipconfig.exe", "/flushdns", onEvent);
            bool ok = c1 == 0 && c2 == 0;
            Done(onEvent, "nicreset", ok,
                ok ? "网络栈已重置，需要重启电脑才生效" : "有一条没重置成功，看上面的返回");
        }

        /* 恢复初始化：把这块网卡改回“自动获取 IP + 自动获取 DNS”，再重启用一次让它生效 */
        private static void DoNicReinit(string iface, Action<string> onEvent)
        {
            string name = iface == null ? "" : iface.Trim();
            if (name.Length == 0)
            {
                Done(onEvent, "nicreinit", false, "没有指定网卡");
                return;
            }
            Nic n = FindNic(name);
            if (n == null) { Done(onEvent, "nicreinit", false, "找不到这块网卡，可能名字对不上"); return; }

            Line(onEvent, "把【" + name + "】改回自动获取 IP 与 DNS …");
            int c1 = Shell("netsh.exe", "interface ip set address name=\"" + name + "\" source=dhcp", onEvent);
            int c2 = Shell("netsh.exe", "interface ip set dns name=\"" + name + "\" source=dhcp", onEvent);
            int c3 = Shell("netsh.exe", "interface ipv4 set dnsservers name=\"" + name + "\" source=dhcp", onEvent);
            Shell("ipconfig.exe", "/flushdns", onEvent);
            Shell("ipconfig.exe", "/renew \"" + name + "\"", onEvent);

            Line(onEvent, "重启用一次，让 DHCP 租约重新协商 …");
            Shell("netsh.exe", "interface set interface name=\"" + name + "\" admin=disable", onEvent);
            WaitStatus(name, false, 6, onEvent);
            int c4 = Shell("netsh.exe", "interface set interface name=\"" + name + "\" admin=enable", onEvent);
            bool up = WaitStatus(name, true, 20, onEvent);
            if (up) Shell("ipconfig.exe", "/renew \"" + name + "\"", onEvent);
            Shell("ipconfig.exe", "/flushdns", onEvent);

            Nic now = FindNic(name);
            string detail = now == null ? "（读不到这块网卡）"
                : ("IPv4 " + (now.Ipv4.Count > 0 ? Join(now.Ipv4, " / ") : "还没拿到") +
                   "，网关 " + (now.Gw.Count > 0 ? Join(now.Gw, " / ") : "没有") +
                   "，DNS " + (now.Dns.Count > 0 ? Join(now.Dns, " / ") : "自动"));
            bool ok = c1 == 0 && c2 == 0 && c4 == 0 && up;
            Done(onEvent, "nicreinit", ok,
                ok ? "已恢复为自动获取；" + detail
                   : "有一步没成功（改地址、改 DNS、重启用任一步失败都会这样），请到网络连接里手动检查；" + detail);
        }

        /* ===================== 网关 ===================== */

        private static void DoGwRenew(string iface, Action<string> onEvent)
        {
            string name = iface == null ? "" : iface.Trim();
            if (name.Length == 0)
            {
                List<Nic> nics = ReadNics();
                for (int i = 0; i < nics.Count; i++)
                {
                    if (nics[i].Up && !nics[i].Virtual && nics[i].Gw.Count > 0)
                    {
                        name = nics[i].Name;
                        break;
                    }
                }
            }
            if (name.Length == 0)
            {
                Done(onEvent, "gwrenew", false, "没有已连接、带网关的网卡，无从续租");
                return;
            }

            Line(onEvent, "清空 ARP 缓存（网关的 MAC 会重新学习）…");
            Shell("netsh.exe", "interface ip delete arpcache", onEvent);

            Line(onEvent, "释放【" + name + "】的 IP 租约 …（这一步会短暂断网）");
            Shell("ipconfig.exe", "/release \"" + name + "\"", onEvent);
            Line(onEvent, "重新申请 IP 与网关 …");
            int code = Shell("ipconfig.exe", "/renew \"" + name + "\"", onEvent);

            Nic n = FindNic(name);
            bool hasGw = n != null && n.Gw.Count > 0;
            string now = n == null ? "（读不到这块网卡）" : ("IPv4 " + (n.Ipv4.Count > 0 ? Join(n.Ipv4, " / ") : "没拿到") +
                "，网关 " + (hasGw ? Join(n.Gw, " / ") : "没拿到"));
            Done(onEvent, "gwrenew", code == 0 && hasGw, (code == 0 ? "续租完成；" : "续租返回代码 " + code + "；") + now);
        }

        private static void DoGwPings(Action<string> onEvent)
        {
            StringBuilder arr = new StringBuilder();
            arr.Append('[');
            int n = 0;

            List<string> gws = new List<string>();
            List<Nic> nics = ReadNics();
            for (int i = 0; i < nics.Count; i++)
            {
                if (!nics[i].Up) continue;
                for (int k = 0; k < nics[i].Gw.Count; k++)
                    if (!gws.Contains(nics[i].Gw[k])) gws.Add(nics[i].Gw[k]);
            }

            for (int i = 0; i < gws.Count; i++)
            {
                if (n++ > 0) arr.Append(',');
                arr.Append(PingOne("网关 " + gws[i], gws[i], true, onEvent));
            }
            /* 公网 DNS：ICMP 被防火墙拦时，至少能说明“这条链路通不通” */
            if (n++ > 0) arr.Append(',');
            arr.Append(PingOne("公网 223.5.5.5", "223.5.5.5", false, onEvent));

            /* 域名解析单独一条：DNS 坏掉时 ping IP 通、域名不通，最容易看出来 */
            if (n++ > 0) arr.Append(',');
            arr.Append(ResolveTest("www.baidu.com"));

            arr.Append(']');
            Emit(onEvent, new JB().Add("type", "netPing").Raw("targets", arr.ToString()).End());
            Done(onEvent, "gwpings", true, "连通性测试完成");
        }

        private static string PingOne(string label, string host, bool isGw, Action<string> onEvent)
        {
            string err = "";
            int sent = 0, recv = 0;
            long min = long.MaxValue, max = 0, sum = 0;
            try
            {
                using (Ping p = new Ping())
                {
                    for (int i = 0; i < 4; i++)
                    {
                        sent++;
                        try
                        {
                            PingReply r = p.Send(host, 1200);
                            if (r != null && r.Status == IPStatus.Success)
                            {
                                recv++;
                                long t = r.RoundtripTime;
                                sum += t;
                                if (t < min) min = t;
                                if (t > max) max = t;
                            }
                            else if (r != null) err = r.Status.ToString();
                        }
                        catch (Exception ex) { err = ex.Message; break; }
                        Thread.Sleep(300);
                    }
                }
            }
            catch (Exception ex) { err = ex.Message; }

            long avg = recv > 0 ? sum / recv : 0;
            int loss = sent > 0 ? (sent - recv) * 100 / sent : 100;
            Line(onEvent, label + "：收到 " + recv + "/" + sent + " 个回应" +
                (recv > 0 ? ("，平均 " + avg + " ms，丢包 " + loss + "%") : "，没通" + (err.Length > 0 ? "（" + err + "）" : "")));
            return new JB()
                .Add("label", label).Add("host", host).Add("gw", isGw)
                .Add("ok", recv > 0).Add("sent", sent).Add("recv", recv).Add("loss", loss)
                .Add("avg", (double)avg)
                .Add("min", recv > 0 ? (double)min : 0)
                .Add("max", (double)max)
                .Add("err", err).End();
        }

        private static string ResolveTest(string domain)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string ip = "";
            string err = "";
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(domain);
                for (int i = 0; i < addrs.Length; i++)
                    if (addrs[i].AddressFamily == AddressFamily.InterNetwork) { ip = addrs[i].ToString(); break; }
                if (ip.Length == 0) err = "只解析到 IPv6 或没有地址";
            }
            catch (Exception ex) { err = ex.Message; }
            sw.Stop();
            return new JB()
                .Add("label", "域名解析 " + domain).Add("host", domain).Add("gw", false)
                .Add("ok", ip.Length > 0).Add("sent", 1).Add("recv", ip.Length > 0 ? 1 : 0)
                .Add("loss", ip.Length > 0 ? 0 : 100)
                .Add("avg", (double)sw.ElapsedMilliseconds)
                .Add("min", (double)sw.ElapsedMilliseconds).Add("max", (double)sw.ElapsedMilliseconds)
                .Add("err", ip.Length > 0 ? ip : err).End();
        }

        /* ===================== hosts 操作 ===================== */

        private static void DoHostsOpen(Action<string> onEvent)
        {
            try
            {
                Process.Start("notepad.exe", "\"" + HostsPath + "\"");
                Done(onEvent, "hostsopen", true, "已在记事本里打开；保存时如果提示权限，需要以管理员身份运行记事本");
            }
            catch (Exception ex)
            {
                Done(onEvent, "hostsopen", false, "打开失败：" + ex.Message);
            }
        }

        private static void DoHostsRestore(Action<string> onEvent)
        {
            if (!SysInfo.IsAdmin())
            {
                Done(onEvent, "hostsrestore", false, "改 hosts 需要管理员权限，请以管理员身份重新打开本软件");
                return;
            }
            string bak = BackupHosts(onEvent);
            try
            {
                File.WriteAllText(HostsPath, DefaultHosts(), Encoding.Default);
                Shell("ipconfig.exe", "/flushdns", onEvent);
                Done(onEvent, "hostsrestore", true, "已恢复成 Windows 默认 hosts（只剩注释）；原文件备份 " + (bak.Length > 0 ? bak : "失败"));
            }
            catch (Exception ex)
            {
                Done(onEvent, "hostsrestore", false, "写入失败：" + ex.Message);
            }
        }

        private static void DoHostsClean(Action<string> onEvent)
        {
            HostsInfo h = ReadHosts();
            if (!h.Ok) { Done(onEvent, "hostsclean", false, h.Error); return; }
            if (h.Suspects.Count == 0) { Done(onEvent, "hostsclean", true, "没有发现可疑条目，hosts 不用改"); return; }
            if (!SysInfo.IsAdmin())
            {
                Done(onEvent, "hostsclean", false, "改 hosts 需要管理员权限，请以管理员身份重新打开本软件");
                return;
            }

            string bak = BackupHosts(onEvent);
            try
            {
                string[] lines = File.ReadAllLines(HostsPath, Encoding.Default);
                HashSet<int> bad = new HashSet<int>();
                for (int i = 0; i < h.Suspects.Count; i++) bad.Add(h.Suspects[i].Line);
                StringBuilder sb = new StringBuilder();
                int removed = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (bad.Contains(i + 1))
                    {
                        Line(onEvent, "删除第 " + (i + 1) + " 行：" + lines[i].Trim());
                        removed++;
                        continue;
                    }
                    sb.AppendLine(lines[i]);
                }
                File.WriteAllText(HostsPath, sb.ToString(), Encoding.Default);
                Shell("ipconfig.exe", "/flushdns", onEvent);
                Done(onEvent, "hostsclean", true, "删掉 " + removed + " 条可疑映射；备份 " + (bak.Length > 0 ? bak : "失败"));
            }
            catch (Exception ex)
            {
                Done(onEvent, "hostsclean", false, "写入失败：" + ex.Message);
            }
        }

        private static void DoHostsAdd(string ip, string domain, Action<string> onEvent)
        {
            IPAddress addr;
            if (!IPAddress.TryParse((ip == null ? "" : ip).Trim(), out addr))
            {
                Done(onEvent, "hostsadd", false, "IP 写法不对：" + ip);
                return;
            }
            string d = (domain == null ? "" : domain).Trim().ToLowerInvariant();
            if (d.Length == 0 || d.IndexOf(' ') >= 0 || d.IndexOf('#') >= 0)
            {
                Done(onEvent, "hostsadd", false, "域名不能为空，也不能带空格或 #");
                return;
            }
            for (int i = 0; i < d.Length; i++)
            {
                char c = d[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                          c == '.' || c == '-' || c == '_';
                if (!ok)
                {
                    Done(onEvent, "hostsadd", false, "域名里有不支持的字符：" + c);
                    return;
                }
            }
            if (!SysInfo.IsAdmin())
            {
                Done(onEvent, "hostsadd", false, "改 hosts 需要管理员权限，请以管理员身份重新打开本软件");
                return;
            }

            HostsInfo h = ReadHosts();
            if (!h.Ok) { Done(onEvent, "hostsadd", false, h.Error); return; }
            for (int i = 0; i < h.All.Count; i++)
            {
                string[] ds = h.All[i].Domains.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < ds.Length; k++)
                {
                    if (string.Equals(ds[k], d, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(h.All[i].Ip, addr.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        Done(onEvent, "hostsadd", true, "这条映射已经存在，没有重复写入");
                        return;
                    }
                }
            }

            string bak = BackupHosts(onEvent);
            try
            {
                string line = addr.ToString() + "\t" + d + "\t# WinTune added " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                File.AppendAllText(HostsPath, "\r\n" + line + "\r\n", Encoding.Default);
                Shell("ipconfig.exe", "/flushdns", onEvent);
                Done(onEvent, "hostsadd", true, "已添加 " + addr + " → " + d + "；备份 " + (bak.Length > 0 ? bak : "失败"));
            }
            catch (Exception ex)
            {
                Done(onEvent, "hostsadd", false, "写入失败：" + ex.Message);
            }
        }

        /// <summary>删一行映射。参数支持"line:行号"精确删某行，或直接给域名删所有该域名的行。</summary>
        private static void DoHostsDel(string arg, Action<string> onEvent)
        {
            string a = (arg == null ? "" : arg).Trim();
            if (a.Length == 0)
            {
                Done(onEvent, "hostsdel", false, "没有指定要删哪一行");
                return;
            }

            int lineNo = 0;
            string d = a;
            if (a.StartsWith("line:", StringComparison.OrdinalIgnoreCase))
            {
                int n;
                if (!int.TryParse(a.Substring(5), out n) || n <= 0)
                {
                    Done(onEvent, "hostsdel", false, "行号写法不对：" + a);
                    return;
                }
                lineNo = n;
                d = "";
            }
            string what = lineNo > 0 ? ("第 " + lineNo + " 行") : ("域名 " + d);

            if (!SysInfo.IsAdmin())
            {
                Done(onEvent, "hostsdel", false, "改 hosts 需要管理员权限，请以管理员身份重新打开本软件");
                return;
            }

            string bak = BackupHosts(onEvent);
            try
            {
                string[] lines = File.ReadAllLines(HostsPath, Encoding.Default);
                if (lineNo > lines.Length)
                {
                    Done(onEvent, "hostsdel", false, "这一行不存在（hosts 可能刚被别的程序改过，点“重新检测”再试）");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                int removed = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    bool hit = false;
                    if (lineNo > 0)
                    {
                        hit = (i + 1 == lineNo);
                    }
                    else
                    {
                        string body = lines[i];
                        int hash = body.IndexOf('#');
                        if (hash >= 0) body = body.Substring(0, hash);
                        string[] parts = body.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int k = 1; k < parts.Length; k++)
                            if (string.Equals(parts[k], d, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                    }
                    if (hit)
                    {
                        Line(onEvent, "删除第 " + (i + 1) + " 行：" + lines[i].Trim());
                        removed++;
                        continue;
                    }
                    sb.AppendLine(lines[i]);
                }

                if (removed == 0)
                {
                    Done(onEvent, "hostsdel", true, "hosts 里已经没有 " + what + " 的映射");
                    return;
                }
                File.WriteAllText(HostsPath, sb.ToString(), Encoding.Default);
                Shell("ipconfig.exe", "/flushdns", onEvent);
                Done(onEvent, "hostsdel", true, "已删除 " + what + "（" + removed + " 行）；备份 " +
                    (bak.Length > 0 ? bak : "失败"));
            }
            catch (Exception ex)
            {
                Done(onEvent, "hostsdel", false, "写入失败：" + ex.Message);
            }
        }
        private static string BackupHosts(Action<string> onEvent)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string to = Path.Combine(dir, "hosts-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.Copy(HostsPath, to, true);
                Line(onEvent, "已备份 hosts → " + to);
                return to;
            }
            catch (Exception ex)
            {
                Line(onEvent, "备份 hosts 失败：" + ex.Message);
                return "";
            }
        }

        /* ===================== 代理 / 服务 / MTU / 路由 动作 ===================== */

        private static void DoProxyReset(Action<string> onEvent)
        {
            Line(onEvent, "复位系统代理：清 WinHTTP 代理，再关掉指向死端口的 IE 代理 …");
            int c = Shell("netsh.exe", "winhttp reset proxy", onEvent);
            string bak = BackupReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "proxy", onEvent);
            bool ieOk = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (k != null)
                    {
                        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        ieOk = true;
                        object au = k.GetValue("AutoConfigURL");
                        string a = au == null ? "" : au.ToString();
                        if (LocalProxyHostPort(a).Length > 0)
                        {
                            k.DeleteValue("AutoConfigURL", false);
                            Line(onEvent, "PAC 地址也是指向本机死端口，已一并清掉");
                        }
                    }
                }
            }
            catch (Exception ex) { Line(onEvent, "改 IE 代理失败：" + ex.Message); }
            bool ok = c == 0 || ieOk;
            Done(onEvent, "proxyreset", ok,
                ok ? "系统代理已复位：WinHTTP 走直连，IE 代理已关闭" : "两条都没改成功，看上面的返回");
        }

        private static void DoSvcRestart(Action<string> onEvent)
        {
            string[] keys = new string[] { "Dnscache", "Dhcp" };
            string[] labels = new string[] { "DNS Client", "DHCP Client" };
            int okCount = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                Line(onEvent, "重启 " + labels[i] + "（" + keys[i] + "）…");
                Shell("sc.exe", "stop " + keys[i], onEvent);
                WaitSvcState(keys[i], false, 6);
                Shell("sc.exe", "start " + keys[i], onEvent);
                if (WaitSvcState(keys[i], true, 10)) okCount++;
                else Line(onEvent, labels[i] + " 没有回到运行状态");
            }
            Done(onEvent, "svcrestart", okCount == keys.Length,
                okCount == keys.Length ? "DNS Client 与 DHCP Client 都已重启"
                    : ("只起来 " + okCount + " / " + keys.Length + " 个服务，看上面的返回"));
        }

        private static void DoMtuProbe(string iface, Action<string> onEvent)
        {
            string want = iface == null ? "" : iface;
            List<Nic> nics = ReadNics();
            Nic n = want.Length > 0 ? FindNic(want) : null;
            if (n == null) n = PickMain(nics);
            if (n == null) { Done(onEvent, "mtuprobe", false, "没有已连接的网卡，探不了 MTU"); return; }

            Dictionary<string, int> mtus = ReadMtu();
            int declared = mtus.ContainsKey(n.Name) ? mtus[n.Name] : 0;
            string target = n.Gw.Count > 0 ? n.Gw[0] : "223.5.5.5";
            Line(onEvent, "网卡 " + n.Name + " 当前 MTU=" + (declared > 0 ? declared.ToString() : "读不到") +
                "，对 " + target + " 发不分片 ICMP 逐步往下试 …");
            string note;
            int mtu = ProbeMtu(target, out note);
            if (mtu <= 0)
            {
                Line(onEvent, note);
                Done(onEvent, "mtuprobe", false, note);
                return;
            }
            Line(onEvent, "路径 MTU = " + mtu + "（" + note + "）");
            Emit(onEvent, new JB().Add("type", "netMtu").Add("iface", n.Name).Add("declared", declared)
                .Add("mtu", mtu).Add("target", target).Add("note", note).Add("ok", true).End());
            Done(onEvent, "mtuprobe", true, "路径 MTU " + mtu +
                (declared > mtu ? ("，比网卡现在的 " + declared + " 小，可以按这个改") : "，与网卡当前值一致"));
        }

        private static void DoMtuSet(string iface, string mtuText, Action<string> onEvent)
        {
            int val = 0;
            try { val = int.Parse(mtuText == null ? "" : mtuText.Trim()); }
            catch { val = 0; }
            if (val < 576 || val > 9000) { Done(onEvent, "mtuset", false, "MTU 取值不对（应在 576～9000）"); return; }

            string name = iface == null ? "" : iface;
            if (name.Length == 0)
            {
                Nic m = PickMain(ReadNics());
                if (m == null) { Done(onEvent, "mtuset", false, "没有已连接的网卡，改不了 MTU"); return; }
                name = m.Name;
            }
            Line(onEvent, "把 " + name + " 的 MTU 设为 " + val + "（store=persistent）…");
            int c = Shell("netsh.exe", "interface ipv4 set subinterface \"" + name + "\" mtu=" + val + " store=persistent", onEvent);
            Dictionary<string, int> now = ReadMtu();
            bool ok = c == 0 && now.ContainsKey(name) && now[name] == val;
            Done(onEvent, "mtuset", ok,
                ok ? ("MTU 已设为 " + val) : "设置没生效，看上面的返回（需要管理员权限）");
        }

        private static void DoRoutesDel(string spec, Action<string> onEvent)
        {
            string[] p = (spec == null ? "" : spec).Split('|');
            if (p.Length < 3) { Done(onEvent, "routesdel", false, "路由参数不完整"); return; }
            string dest = p[0].Trim(), mask = p[1].Trim(), gw = p[2].Trim();
            Line(onEvent, "删除持久静态路由 " + dest + " / " + mask + " → " + gw + " …");
            string bak = BackupReg(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes", "routes", onEvent);
            int c = Shell("route.exe", "delete " + dest + " mask " + mask, onEvent);
            bool del = false;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes", true))
                {
                    if (k != null)
                    {
                        string[] names = k.GetValueNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            if (names[i].StartsWith(dest + "," + mask + "," + gw, StringComparison.OrdinalIgnoreCase))
                            {
                                k.DeleteValue(names[i], false);
                                del = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Line(onEvent, "改注册表失败：" + ex.Message); }
            bool ok = del || c == 0;
            Done(onEvent, "routesdel", ok,
                ok ? ("已删除 " + dest + " → " + gw + "；备份 " + bak)
                    : "没删掉：需要管理员权限，或这条路由已经被人改过");
        }

        /* ===================== 体检 ===================== */

        private static void CheckAdd(List<string> list, string id, string level, string title, string detail,
            string fix, string a1, string a2, string fixName)
        {
            list.Add(new JB().Add("id", id).Add("level", level).Add("title", title)
                .Add("detail", detail).Add("fix", fix).Add("a1", a1)
                .Add("a2", a2).Add("fixName", fixName).End());
        }

        /// <summary>进页面时跑一遍：只列有证据的问题，没问题就返回空数组，界面什么也不显示。</summary>
        public static string CheckJson()
        {
            long t0 = Environment.TickCount;
            List<string> items = new List<string>();
            List<Nic> nics = ReadNics();
            HostsInfo hi = ReadHosts();
            ProxyInfo px = ReadProxy();

            Nic main = null;
            Nic firstPhys = null;
            int physUp = 0;
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up || n.Virtual) continue;
                if (firstPhys == null) firstPhys = n;
                if (n.Ipv4.Count > 0)
                {
                    physUp++;
                    if (main == null) main = n;
                }
            }
            if (main == null)
                for (int i = 0; i < nics.Count; i++)
                    if (nics[i].Up) { main = nics[i]; break; }

            if (physUp == 0)
            {
                CheckAdd(items, "nic", "risk", "没有已连接的物理网卡",
                    "共 " + nics.Count + " 块网卡，没有一块物理网卡拿到地址；网线、无线开关或驱动要看一下",
                    firstPhys != null ? "nicrestart" : "", firstPhys != null ? firstPhys.Name : "", "",
                    firstPhys != null ? "重启这块网卡" : "");
            }

            int ap = 0;
            string apName = "", apIp = "";
            for (int i = 0; i < nics.Count; i++)
            {
                Nic n = nics[i];
                if (!n.Up || n.Virtual) continue;
                for (int k = 0; k < n.Ipv4.Count; k++)
                {
                    if (n.Ipv4[k].StartsWith("169.254."))
                    {
                        ap++;
                        apName = n.Name;
                        apIp = n.Ipv4[k];
                        break;
                    }
                }
            }
            if (ap > 0)
                CheckAdd(items, "apipa", "risk", "IP 是 169.254 自动分配",
                    "网卡 " + apName + " 拿到的是 " + apIp + "，说明 DHCP 没有回应；这种状态下网关和 DNS 都是空的",
                    "gwrenew", apName, "", "重新获取 IP 与网关");

            if (main != null && main.Gw.Count == 0 && ap == 0)
                CheckAdd(items, "gw", "risk", "没有默认网关",
                    "网卡 " + main.Name + " 有 IP 地址，但没有默认网关，只能通局域网、上不了外网",
                    "gwrenew", main.Name, "", "重新获取 IP 与网关");

            if (main != null && main.Dns.Count == 0)
                CheckAdd(items, "dnsnone", "risk", "没有可用的 DNS",
                    "网卡 " + main.Name + " 没有下发 DNS 服务器，域名解析会直接失败（浏览器报找不到网站）",
                    "dnsset", "223.5.5.5,119.29.29.29", main.Name, "设为阿里+腾讯 DNS");
            else if (main != null)
            {
                bool local = false;
                for (int i = 0; i < main.Dns.Count; i++)
                    if (main.Dns[i] == "127.0.0.1" || main.Dns[i] == "::1") local = true;
                if (local && !TcpAlive("127.0.0.1", 53, 300))
                    CheckAdd(items, "dnslocal", "warn", "DNS 指向本机但没人应答",
                        "网卡 " + main.Name + " 的 DNS 是 127.0.0.1，本机 53 端口没有解析服务在跑；多半是代理或 DNS 工具退出后没改回来",
                        "dnsset", "223.5.5.5,119.29.29.29", main.Name, "设为阿里+腾讯 DNS");
            }

            if (hi.Suspects.Count > 0)
            {
                HostsEntry e = hi.Suspects[0];
                string sample = e.Raw.Length > 0 ? e.Raw : (e.Ip + " " + e.Domains);
                CheckAdd(items, "hosts", "warn", "hosts 有 " + hi.Suspects.Count + " 条可疑映射",
                    "第 " + e.Line + " 行 " + sample + " —— " + e.Why,
                    "hostsclean", "", "", "只删可疑条目");
            }

            if (px.WinHttp.Length > 0)
                CheckAdd(items, "winhttp", "warn", "WinHTTP 还挂着代理",
                    "WinHTTP 现在走 " + px.WinHttp + "；系统更新、商店和部分软件下载会连不上（浏览器却正常）",
                    "proxyreset", "", "", "复位系统代理");
            if (px.IeEnable && px.LocalPort > 0 && !px.LocalAlive)
                CheckAdd(items, "ieproxy", "warn", "系统代理指向没程序监听的端口",
                    "系统代理是 127.0.0.1:" + px.LocalPort + "，这个端口现在没有程序在听；浏览器可能还能开网页，其它软件多半连不上",
                    "proxyreset", "", "", "复位系统代理");

            bool found = false;
            bool dnsRun = SvcRunning("Dnscache", out found);
            if (found && !dnsRun)
                CheckAdd(items, "dnscache", "risk", "DNS Client 服务没在运行",
                    "Dnscache 停了以后域名解析会直接失败，ping 域名也不通",
                    "svcrestart", "", "", "重启 DNS/DHCP 服务");
            bool dhcpRun = SvcRunning("Dhcp", out found);
            if (found && !dhcpRun)
                CheckAdd(items, "dhcpsvc", "risk", "DHCP Client 服务没在运行",
                    "Dhcp 停了以后拿不到地址、也续不了租，换网络后会一直停在 169.254",
                    "svcrestart", "", "", "重启 DNS/DHCP 服务");

            if (main != null)
            {
                Dictionary<string, int> mtus = ReadMtu();
                int declared = mtus.ContainsKey(main.Name) ? mtus[main.Name] : 0;
                if (declared >= 1500)
                {
                    string target = main.Gw.Count > 0 ? main.Gw[0] : "223.5.5.5";
                    string note;
                    int path = ProbeMtu(target, out note);
                    if (path > 0 && path < declared - 8)
                        CheckAdd(items, "mtu", "warn", "链路 MTU 比网卡写的小",
                            "网卡 " + main.Name + " 写的是 " + declared + "，实测到 " + target + " 最大只能过 " + path +
                            " 字节（" + note + "）。这种链路能 ping 通、小网页能开，大页面和下载会卡住",
                            "mtuset", main.Name, path.ToString(), "把 MTU 设为 " + path);
                }
            }

            List<RouteInfo> routes = ReadPersistentRoutes();
            int stale = 0;
            for (int i = 0; i < routes.Count && stale < 3; i++)
            {
                RouteInfo r = routes[i];
                if (r.Gw.Length == 0 || r.Gw == "0.0.0.0") continue;
                bool sameAsGw = false;
                if (main != null)
                    for (int k = 0; k < main.Gw.Count; k++)
                        if (main.Gw[k] == r.Gw) sameAsGw = true;
                if (sameAsGw) continue;
                if (PingQuick(r.Gw, 900)) continue;
                stale++;
                CheckAdd(items, "route" + i, "warn", "静态路由 " + r.Dest + " 的网关不通",
                    "这条持久路由指向 " + r.Gw + "（" + r.Dest + " / " + r.Mask + "），现在 ping 不通，多半是换了网络留下的旧路由",
                    "routesdel", r.Dest + "|" + r.Mask + "|" + r.Gw, "", "删除这条路由");
            }

            StringBuilder arr = new StringBuilder();
            arr.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) arr.Append(',');
                arr.Append(items[i]);
            }
            arr.Append(']');

            return new JB().Add("type", "netCheck").Add("count", items.Count)
                .Raw("items", arr.ToString())
                .Add("ms", Environment.TickCount - t0)
                .Add("at", DateTime.Now.ToString("HH:mm:ss")).End();
        }

        /// <summary>跑命令但只收输出不往界面推，给体检用。</summary>
        private static string ShellCapture(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                try
                {
                    Encoding enc = Encoding.GetEncoding(936);
                    si.StandardOutputEncoding = enc;
                    si.StandardErrorEncoding = enc;
                }
                catch { }
                Process p = Process.Start(si);
                StringBuilder sb = new StringBuilder();
                Thread th = new Thread(delegate ()
                {
                    try { sb.Append(p.StandardError.ReadToEnd()); } catch { }
                });
                th.IsBackground = true;
                th.Start();
                string so = "";
                try { so = p.StandardOutput.ReadToEnd(); } catch { }
                bool exited = false;
                try { exited = p.WaitForExit(timeoutMs); } catch { }
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                }
                try { th.Join(3000); } catch { }
                return so + "\n" + sb.ToString();
            }
            catch { return ""; }
        }
        /* ===================== 测速 ===================== */

        private class SpeedServer
        {
            public string Id = "";
            public string Name = "";
            public string PingUrl = "";
            public string DownUrl = "";
            public string UpUrl = "";
        }

        private class SpeedReport
        {
            public bool Ok;
            public bool UploadOk;
            public string Msg = "";
            public string Node = "";
            public string Server = "";
            public long PingMs;
            public long JitterMs;
            public long DownBytes;
            public double DownAvg;
            public double DownPeak;
            public long DownMs;
            public long UpBytes;
            public double UpAvg;
            public double UpPeak;
            public long UpMs;
            public bool ViaProxy;
        }

        /* 可选的测速服务器：都是能实测的公开节点，不做估算 */
        private static List<SpeedServer> Servers()
        {
            List<SpeedServer> l = new List<SpeedServer>();
            SpeedServer cf = new SpeedServer();
            cf.Id = "cloudflare"; cf.Name = "Cloudflare（就近节点）";
            cf.PingUrl = "https://speed.cloudflare.com/__down?bytes=0";
            cf.DownUrl = "https://speed.cloudflare.com/__down?bytes=52428800";
            cf.UpUrl = "https://speed.cloudflare.com/__up";
            l.Add(cf);

            SpeedServer wt = new SpeedServer();
            wt.Id = "wtnet"; wt.Name = "WTnet 德国（LibreSpeed）";
            wt.PingUrl = "https://speedtest.wtnet.de/backend/empty.php";
            wt.DownUrl = "https://speedtest.wtnet.de/backend/garbage.php?ckSize=100";
            wt.UpUrl = "https://speedtest.wtnet.de/backend/empty.php";
            l.Add(wt);
            return l;
        }

        private static void DoSpeed(string serverId, Action<string> onEvent)
        {
            List<SpeedServer> all = Servers();
            List<SpeedServer> order = new List<SpeedServer>();
            string want = (serverId == null ? "" : serverId.Trim().ToLowerInvariant());
            if (want.Length == 0 || want == "auto")
            {
                order.AddRange(all);
            }
            else
            {
                for (int i = 0; i < all.Count; i++) if (all[i].Id == want) order.Add(all[i]);
                if (order.Count == 0) order.AddRange(all);
            }

            Stopwatch total = Stopwatch.StartNew();
            string lastErr = "";
            for (int si = 0; si < order.Count; si++)
            {
                SpeedServer srv = order[si];
                bool viaProxy = false;
                long ping = 0, jitter = 0;
                EmitSpeed(onEvent, "server", 0, 0, 0, 0, 0, 0,
                    "服务器：" + srv.Name + " · 正在建连…");

                bool pingOk = PingPhase(srv, false, onEvent, out ping, out jitter, out lastErr);
                if (!pingOk)
                {
                    Line(onEvent, "直连 " + srv.Name + " 失败（" + Trunc(lastErr) + "），改用系统代理再试…");
                    pingOk = PingPhase(srv, true, onEvent, out ping, out jitter, out lastErr);
                    if (pingOk) viaProxy = true;
                }
                if (!pingOk)
                {
                    Line(onEvent, srv.Name + " 连不上，换下一个服务器…");
                    continue;
                }

                Line(onEvent, "连接 " + srv.Name + "，延迟 " + ping + " ms，波动 " + jitter + " ms");
                Line(onEvent, "开始测下载，持续 " + SpeedSeconds + " 秒…");
                SpeedReport r = new SpeedReport();
                r.Node = srv.Name; r.Server = srv.Id; r.PingMs = ping; r.JitterMs = jitter; r.ViaProxy = viaProxy;

                bool downOk = DownloadPhase(srv, viaProxy, onEvent,
                    out r.DownBytes, out r.DownAvg, out r.DownPeak, out r.DownMs, out lastErr);
                if (!downOk)
                {
                    Line(onEvent, "下载阶段失败：" + Trunc(lastErr));
                    continue;
                }

                Line(onEvent, "下载稳定在 " + r.DownAvg.ToString("0.0") + " Mbps，接着测上传 " + SpeedSeconds + " 秒…");
                r.UpBytes = 0; r.UpAvg = 0; r.UpPeak = 0; r.UpMs = 0;
                r.UpBytes = 0;
                long ub; double ua, up; long um; string uerr;
                r.UploadOk = UploadPhase(srv, viaProxy, onEvent, out ub, out ua, out up, out um, out uerr);
                r.UpBytes = ub; r.UpAvg = ua; r.UpPeak = up; r.UpMs = um;
                if (r.UpBytes > 0 && !r.UploadOk) Line(onEvent, "上传阶段提前结束：" + Trunc(uerr));

                total.Stop();
                r.Ok = true;
                Emit(onEvent, new JB().Add("type", "netSpeedDone")
                    .Add("ok", true)
                    .Add("node", r.Node).Add("server", r.Server)
                    .Add("pingMs", r.PingMs).Add("jitterMs", r.JitterMs)
                    .Add("downAvg", Math.Round(r.DownAvg, 2)).Add("downPeak", Math.Round(r.DownPeak, 2))
                    .Add("downBytes", r.DownBytes).Add("downMs", r.DownMs)
                    .Add("upOk", r.UploadOk)
                    .Add("upAvg", Math.Round(r.UpAvg, 2)).Add("upPeak", Math.Round(r.UpPeak, 2))
                    .Add("upBytes", r.UpBytes).Add("upMs", r.UpMs)
                    .Add("avg", Math.Round(r.DownAvg, 2)).Add("peak", Math.Round(r.DownPeak, 2))
                    .Add("bytes", r.DownBytes).Add("ms", r.DownMs)
                    .Add("totalMs", (long)total.ElapsedMilliseconds)
                    .Add("proxy", r.ViaProxy)
                    .Add("note", r.UploadOk ? "" : "上传没测完：" + Trunc(uerr))
                    .End());
                Done(onEvent, "speedtest", true,
                    r.Node + "：下载 " + Math.Round(r.DownAvg, 1) + " Mbps / 上传 " +
                    (r.UploadOk ? Math.Round(r.UpAvg, 1) + " Mbps" : "未测成") +
                    " / 延迟 " + r.PingMs + " ms / 波动 " + r.JitterMs + " ms");
                return;
            }

            total.Stop();
            Done(onEvent, "speedtest", false, "测速没成功：" + Trunc(lastErr));
        }

        private const int SpeedSeconds = 10;

        /* 延迟与抖动：直连时用 TCP 443 握手计时（不叠 TLS/HTTP，测得的是网络往返）；走代理时退回 HTTP 小请求计时 */
        private static bool PingPhase(SpeedServer srv, bool viaProxy, Action<string> onEvent,
            out long pingMs, out long jitterMs, out string err)
        {
            pingMs = 0; jitterMs = 0; err = "";
            List<double> samples = new List<double>();
            string host = "";
            try { host = new Uri(srv.PingUrl).Host; } catch { }
            if (host.Length == 0) { err = "测速地址不合法"; return false; }
            try
            {
                /* 先连一次把 DNS 与路由热起来，不计入样本 */
                for (int i = -1; i < 5; i++)
                {
                    if (i >= 0)
                        EmitSpeed(onEvent, "ping", i * 15, 0, 0, 0, 0, 0, "测延迟 " + (i + 1) + "/5…");
                    Stopwatch sw = Stopwatch.StartNew();
                    if (viaProxy)
                    {
                        HttpWebRequest req = NewSpeedReq(srv.PingUrl, true, 10000);
                        req.Method = "GET";
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        {
                            Stream st = resp.GetResponseStream();
                            byte[] b = new byte[1024];
                            try { while (st.Read(b, 0, b.Length) > 0) { } } catch { }
                        }
                    }
                    else
                    {
                        Socket so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        so.NoDelay = true;
                        try
                        {
                            bool done = so.ConnectAsync(host, 443).Wait(5000);
                            if (!done) throw new Exception("连接超时");
                        }
                        finally { try { so.Close(); } catch { } }
                    }
                    sw.Stop();
                    if (i >= 0) samples.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
            if (samples.Count == 0) { err = "没取到延迟样本"; return false; }
            double sum = 0;
            for (int i = 0; i < samples.Count; i++) sum += samples[i];
            double avg = sum / samples.Count;
            double variance = 0;
            for (int i = 0; i < samples.Count; i++) variance += (samples[i] - avg) * (samples[i] - avg);
            variance = variance / samples.Count;
            double jitter = Math.Sqrt(variance);
            pingMs = (long)Math.Round(samples[0]);
            for (int i = 1; i < samples.Count; i++) if (samples[i] < pingMs) pingMs = (long)Math.Round(samples[i]);
            jitterMs = (long)Math.Round(jitter);
            EmitSpeed(onEvent, "ping", 100, 0, 0, 0, 0, 0,
                "延迟 " + pingMs + " ms，抖动 " + jitterMs + " ms");
            return true;
        }

        /* 下载：持续 SpeedSeconds 秒，边读边报，最后按“总字节 / 实测时长”算平均 */
        private static bool DownloadPhase(SpeedServer srv, bool viaProxy, Action<string> onEvent,
            out long bytes, out double avg, out double peak, out long ms, out string err)
        {
            bytes = 0; avg = 0; peak = 0; ms = 0; err = "";
            Stopwatch sw = Stopwatch.StartNew();
            long lastTick = -1, lastBytes = 0;
            double peakVal = 0;
            long capMs = SpeedSeconds * 1000L;
            try
            {
                while (sw.ElapsedMilliseconds < capMs)
                {
                    HttpWebRequest req = NewSpeedReq(srv.DownUrl, viaProxy, 12000);
                    req.Method = "GET";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        int code = (int)resp.StatusCode;
                        if (code < 200 || code >= 300) { err = "HTTP " + code; return false; }
                        Stream st = resp.GetResponseStream();
                        byte[] buf = new byte[131072];
                        while (true)
                        {
                            int n = st.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            bytes += n;
                            long el = sw.ElapsedMilliseconds;
                            if (lastTick < 0 || el - lastTick >= 200)
                            {
                                double inst = 0;
                                if (lastTick > 0 && el > lastTick)
                                    inst = (bytes - lastBytes) * 8.0 / 1000000.0 / ((el - lastTick) / 1000.0);
                                if (inst > peakVal) peakVal = inst;
                                lastTick = el; lastBytes = bytes;
                                double a = bytes * 8.0 / 1000000.0 / (Math.Max(1, el) / 1000.0);
                                long pct = el * 100 / capMs; if (pct > 100) pct = 100;
                                EmitSpeed(onEvent, "download", pct, inst, a, bytes, 0, el,
                                    "下载 " + (el / 1000.0).ToString("0.0") + "/" + SpeedSeconds + " 秒 · " +
                                    fmtBytes(bytes) + " · 当前 " + inst.ToString("0.0") + " Mbps · 平均 " + a.ToString("0.0") + " Mbps");
                            }
                            if (sw.ElapsedMilliseconds >= capMs) break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (bytes == 0) { err = ex.Message; return false; }
                Line(onEvent, "下载阶段提前结束（" + Trunc(ex.Message) + "），用已测到的数据算平均。");
            }
            ms = sw.ElapsedMilliseconds;
            if (ms <= 0) ms = 1;
            avg = bytes * 8.0 / 1000000.0 / (ms / 1000.0);
            peak = peakVal;
            if (bytes == 0) { err = "没读到数据"; return false; }
            EmitSpeed(onEvent, "download", 100, peak, avg, bytes, 0, ms,
                "下载完成：平均 " + avg.ToString("0.0") + " Mbps，峰值 " + peak.ToString("0.0") + " Mbps");
            return true;
        }

        /* 上传：持续 SpeedSeconds 秒，按上一块的实际耗时自动放大/缩小分块，快线不被小包拖慢，慢线不被大包拖长 */
        private static bool UploadPhase(SpeedServer srv, bool viaProxy, Action<string> onEvent,
            out long bytes, out double avg, out double peak, out long ms, out string err)
        {
            bytes = 0; avg = 0; peak = 0; ms = 0; err = "";
            byte[] payload = new byte[262144];
            int chunk = 65536;
            Stopwatch sw = Stopwatch.StartNew();
            long capMs = SpeedSeconds * 1000L;
            double peakVal = 0;
            try
            {
                while (sw.ElapsedMilliseconds < capMs - 200)
                {
                    Stopwatch one = Stopwatch.StartNew();
                    HttpWebRequest req = NewSpeedReq(srv.UpUrl, viaProxy, 20000);
                    req.Method = "POST";
                    req.ContentType = "application/octet-stream";
                    req.ContentLength = chunk;
                    req.AllowWriteStreamBuffering = false;
                    using (Stream rs = req.GetRequestStream())
                    {
                        rs.Write(payload, 0, chunk);
                    }
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        int code = (int)resp.StatusCode;
                        if (code < 200 || code >= 300) { err = "HTTP " + code; break; }
                        try
                        {
                            Stream st = resp.GetResponseStream();
                            byte[] b = new byte[4096];
                            while (st.Read(b, 0, b.Length) > 0) { }
                        }
                        catch { }
                    }
                    one.Stop();
                    bytes += chunk;
                    long el = sw.ElapsedMilliseconds;
                    double inst = one.ElapsedMilliseconds > 0
                        ? chunk * 8.0 / 1000000.0 / (one.ElapsedMilliseconds / 1000.0) : 0;
                    if (inst > peakVal) peakVal = inst;

                    /* 下一块大小：一块超过 3.5 秒就减半，半秒内写完就翻倍（上限 256 KB） */
                    if (one.ElapsedMilliseconds > 3500 && chunk > 8192) chunk /= 2;
                    else if (one.ElapsedMilliseconds < 500 && chunk < 262144) chunk *= 2;

                    long pct = el * 100 / capMs; if (pct > 100) pct = 100;
                    double a = bytes * 8.0 / 1000000.0 / (Math.Max(1, el) / 1000.0);
                    EmitSpeed(onEvent, "upload", pct, inst, a, bytes, 0, el,
                        "上传 " + (el / 1000.0).ToString("0.0") + "/" + SpeedSeconds + " 秒 · " +
                        fmtBytes(bytes) + " · 当前 " + inst.ToString("0.0") + " Mbps · 平均 " + a.ToString("0.0") + " Mbps");
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                if (bytes == 0) return false;
                Line(onEvent, "上传阶段提前结束（" + Trunc(ex.Message) + "），用已测到的数据算平均。");
            }
            ms = sw.ElapsedMilliseconds;
            if (ms <= 0) ms = 1;
            avg = bytes * 8.0 / 1000000.0 / (ms / 1000.0);
            peak = peakVal;
            EmitSpeed(onEvent, "upload", 100, peak, avg, bytes, 0, ms,
                "上传完成：平均 " + avg.ToString("0.0") + " Mbps，峰值 " + peak.ToString("0.0") + " Mbps");
            return bytes > 0 && err.Length == 0;
        }

        private static HttpWebRequest NewSpeedReq(string url, bool viaProxy, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 |
                SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "WinTune/1.5.34";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = Math.Max(3000, timeoutMs);
            req.AllowAutoRedirect = true;
            req.Accept = "*/*";
            req.KeepAlive = true;
            if (!viaProxy) req.Proxy = null;
            else
            {
                IWebProxy p = WebRequest.DefaultWebProxy;
                req.Proxy = p;
                if (p != null) { try { p.Credentials = CredentialCache.DefaultCredentials; } catch { } }
            }
            return req;
        }

        private static string Trunc(string s)
        {
            if (s == null) return "";
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        private static string fmtBytes(long b)
        {
            if (b >= 1048576) return (b / 1048576.0).ToString("0.0") + " MB";
            if (b >= 1024) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        /* ===================== 消息 ===================== */

        private static void Done(Action<string> onEvent, string action, bool ok, string msg)
        {
            if (cancelReq)
            {
                ok = false;
                msg = "已强制结束卡住的命令。已经生效的改动会保留；可以回到页面重新体检。";
            }
            Log.Info("net", action + " → " + (ok ? "OK " : "FAIL ") + msg);
            Emit(onEvent, new JB().Add("type", "netDone").Add("action", action)
                .Add("ok", ok).Add("msg", msg).End());
        }

        private static void EmitSpeed(Action<string> onEvent, string stage, long pct, double inst, double avg,
            long bytes, long totalBytes, long elapsed, string text)
        {
            Emit(onEvent, new JB().Add("type", "netSpeed")
                .Add("stage", stage).Add("pct", pct)
                .Add("inst", Math.Round(inst, 1)).Add("avg", Math.Round(avg, 1))
                .Add("bytes", bytes).Add("total", totalBytes).Add("elapsed", elapsed)
                .Add("text", text).End());
        }

        private static void Line(Action<string> onEvent, string s)
        {
            if (onEvent == null) return;
            onEvent(new JB().Add("type", "netOut").Add("line", s).End());
        }

        private static void Emit(Action<string> onEvent, string json)
        {
            if (onEvent != null) onEvent(json);
        }

        /* ===================== 跑命令 ===================== */

        /// <summary>跑一条系统命令，逐行回吐真实输出，返回退出码。命令不存在/被拒也照实报。
        /// 输出必须边跑边吐：以前是 ReadToEnd 完才显示，命令一慢界面就像卡死。
        /// 超过 120 秒或用户点“强制结束”会杀掉，不留挂起进程。</summary>
        private static int Shell(string exe, string args, Action<string> onEvent)
        {
            if (cancelReq) { Line(onEvent, "（已收到强制结束，跳过后续命令）"); return 122; }
            Line(onEvent, "> " + Path.GetFileName(exe) + (args.Length > 0 ? " " + args : ""));
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                try
                {
                    Encoding enc = Encoding.GetEncoding(936);
                    si.StandardOutputEncoding = enc;
                    si.StandardErrorEncoding = enc;
                }
                catch { }

                using (Process p = Process.Start(si))
                {
                    int[] shown = new int[] { 0 };
                    bool[] truncated = new bool[] { false };
                    Action<string> emit = delegate (string raw)
                    {
                        if (raw == null) return;
                        string s = raw.TrimEnd();
                        if (s.Length == 0) return;
                        if (shown[0] >= 200)
                        {
                            if (!truncated[0]) { truncated[0] = true; Line(onEvent, "  …（输出太长，后面省略）"); }
                            return;
                        }
                        shown[0]++;
                        Line(onEvent, "  " + s);
                    };

                    Thread to = new Thread(delegate ()
                    {
                        try
                        {
                            string ln;
                            while ((ln = p.StandardOutput.ReadLine()) != null) emit(ln);
                        }
                        catch { }
                    });
                    to.IsBackground = true;
                    to.Start();
                    Thread te = new Thread(delegate ()
                    {
                        try
                        {
                            string ln;
                            while ((ln = p.StandardError.ReadLine()) != null) emit(ln);
                        }
                        catch { }
                    });
                    te.IsBackground = true;
                    te.Start();

                    int t0 = Environment.TickCount;
                    while (true)
                    {
                        int elapsed = Environment.TickCount - t0;
                        /* 先判取消：Cancel 已经杀了进程，先 WaitForExit 会把强制结束当成普通非零退出。 */
                        if (cancelReq || elapsed > 120000)
                        {
                            KillTree(p, true);
                            try { if (!p.HasExited) p.Kill(); } catch { }
                            try { p.WaitForExit(2000); } catch { }
                            Line(onEvent, cancelReq ? "（已强制结束该命令）" : "（命令超过 2 分钟未结束，已自动结束）");
                            return cancelReq ? 122 : 123;
                        }
                        if (p.WaitForExit(500))
                        {
                            try { to.Join(1200); te.Join(1200); } catch { }
                            if (p.ExitCode != 0) Line(onEvent, "（退出代码 " + p.ExitCode + "）");
                            return p.ExitCode;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Line(onEvent, "命令没跑起来：" + ex.Message);
                return -1;
            }
        }

        private static void PushLines(string text, Action<string> onEvent)
        {
            if (text == null || text.Length == 0) return;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int shown = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].TrimEnd();
                if (s.Length == 0) continue;
                Line(onEvent, "  " + s);
                if (++shown >= 80)
                {
                    Line(onEvent, "  …（输出太长，后面省略）");
                    break;
                }
            }
        }
    }
}
