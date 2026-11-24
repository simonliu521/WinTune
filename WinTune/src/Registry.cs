using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WinTune
{
    public static class RegCleaner
    {
        public class RegResult
        {
            public int Deleted;
            public int Failed;
            public List<string> Errors = new List<string>();
            public string Backup = "";
        }

        public static RegResult Clean(List<RegistryScan.Hit> uninst, List<RegistryScan.Hit> runs, Action<string> onEvent)
        {
            RegResult res = new RegResult();

            // 1) 先整棵导出，出问题能双击还原
            List<string> backups = new List<string>();
            string b1 = Backup.ExportReg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "uninst-hklm");
            string b2 = Backup.ExportReg(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "uninst-hklm32");
            string b3 = Backup.ExportReg(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "uninst-hkcu");
            string b4 = Backup.ExportReg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "run-hklm");
            string b5 = Backup.ExportReg(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "run-hkcu");
            foreach (string b in new string[] { b1, b2, b3, b4, b5 })
                if (b.Length > 0) backups.Add(b);
            res.Backup = backups.Count > 0 ? backups[0] : "";

            // 2) 删除无效卸载项
            foreach (RegistryScan.Hit h in uninst)
            {
                try
                {
                    RegistryKey root = h.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    string path = h.KeyPath;
                    root.DeleteSubKeyTree(path, false);
                    res.Deleted++;
                    Log.Info("reg", "删除卸载项 " + h.Hive + "\\" + path + " (" + h.Display + ")");
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Errors.Add(h.Display + " → " + ex.Message);
                    Log.Warn("reg", "删除失败 " + h.KeyPath + "：" + ex.Message);
                }
            }

            // 3) 删除失效启动项（只删值，不删键）
            foreach (RegistryScan.Hit h in runs)
            {
                try
                {
                    RegistryKey root = h.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    using (RegistryKey k = root.OpenSubKey(h.KeyPath, true))
                    {
                        if (k == null) continue;
                        k.DeleteValue(h.Display, false);
                        res.Deleted++;
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Errors.Add(h.Display + " → " + ex.Message);
                }
            }

            if (onEvent != null)
            {
                onEvent(new JB()
                    .Add("type", "regDone")
                    .Add("deleted", res.Deleted)
                    .Add("failed", res.Failed)
                    .Add("backup", res.Backup)
                    .End());
            }
            Log.Info("reg", "注册表清理完成 删除=" + res.Deleted + " 失败=" + res.Failed + " 备份=" + res.Backup);
            return res;
        }
    }
}
