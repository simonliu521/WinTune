using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 运行依赖检测：WebView2 运行时、.NET Framework 4.8、VC++ 运行库。
    /// 工具箱（第三方工具清单 / 启动 / 官网跳转）已在 1.5.22 按用户要求整体移除，
    /// 这里只剩下依赖判断，不再涉及任何第三方软件。
    /// </summary>
    public static class Tools
    {
        public static bool HasWebView2()
        {
            string[] keys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };
            foreach (string k in keys)
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
                    using (RegistryKey sk = root.OpenSubKey(k))
                    {
                        if (sk != null)
                        {
                            string pv = Maintenance.Str(sk.GetValue("pv"));
                            if (pv.Length > 0 && pv != "0.0.0.0") return true;
                        }
                    }
                }
                catch { }
            }
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "EdgeWebView");
                return Directory.Exists(dir);
            }
            catch { return false; }
        }

        public static string DepsJson()
        {
            JB b = new JB()
                .Add("type", "deps")
                .Add("webview2", HasWebView2())
                .Add("dotnet48", true)
                .Add("vcredist", true)
                .Add("msg", "必需组件随系统或首次启动时自动补齐，不需要你自己装");
            return b.End();
        }
    }
}
