using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// BCUninstaller 4.16 的卸载引擎桥接。
    /// 只调用 BCU-console 的 uninstall 命令，不启用它的残留/Junk 清理。
    /// BCU 负责处理没有 UninstallString 的应用（内置 UniversalUninstaller）。
    /// </summary>
    public static class Bcu
    {
        public const string Version = "4.16";

        private static string Dir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BCU"); }
        }

        private static string Exe
        {
            get { return Path.Combine(Dir, "BCU-console.exe"); }
        }

        public static bool Available()
        {
            try { return File.Exists(Exe); }
            catch { return false; }
        }

        /// <summary>只做卸载，不启用 BCU 的 /J 残留清理。</summary>
        public static void Uninstall(List<string> ids, bool deep, Action<string> onEvent)
        {
            if (onEvent == null) onEvent = delegate (string s) { };

            if (!Available())
            {
                onEvent(new JB().Add("type", "bcuDone").Add("ok", false)
                    .Add("msg", "缺少 BCU 引擎：请把 BCUninstaller 4.16 便携包放到程序目录的 BCU 文件夹").End());
                return;
            }

            List<Apps.App> all = Apps.List();
            List<Apps.App> targets = new List<Apps.App>();
            int skipped = 0;

            foreach (string id in ids)
            {
                Apps.App a = null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Id == id) { a = all[i]; break; }
                }
                if (a == null) continue;
                /* 只保护 WinTune 自身；系统组件、运行库和驱动也交给 BCU，
                   由界面用「谨慎卸载」提醒用户，不再在引擎层静默跳过。 */
                if (Apps.IsSelf(a)) { skipped++; continue; }
                targets.Add(a);
            }

            if (targets.Count == 0)
            {
                onEvent(new JB().Add("type", "bcuDone").Add("ok", false)
                    .Add("msg", skipped > 0 ? "选中的是 WinTune 自身组件，已跳过" : "没有找到可卸载的程序").End());
                return;
            }

            string listPath = Path.Combine(Path.GetTempPath(), "wintune-bcu-" + Guid.NewGuid().ToString("N") + ".bcul");
            try
            {
                WriteList(targets, listPath);
            }
            catch (Exception ex)
            {
                onEvent(new JB().Add("type", "bcuDone").Add("ok", false)
                    .Add("msg", "生成 BCU 卸载清单失败：" + ex.Message).End());
                return;
            }

            onEvent(new JB().Add("type", "bcuStart").Add("total", targets.Count).Add("skipped", skipped).End());

            int exitCode = -1;
            StringBuilder output = new StringBuilder();
            bool admin = SysInfo.IsAdmin();
            try
            {
                if (!admin)
                {
                    onEvent(new JB().Add("type", "bcuLine").Add("line", "需要管理员权限，正在请求 UAC 授权…").End());
                    exitCode = RunElevated(listPath);
                }
                else
                {
                    ProcessStartInfo si = new ProcessStartInfo(Exe);
                    si.Arguments = "uninstall \"" + listPath + "\" /Q /U /V";
                    si.WorkingDirectory = Dir;
                    si.UseShellExecute = false;
                    si.CreateNoWindow = true;
                    si.RedirectStandardOutput = true;
                    si.RedirectStandardError = true;

                    using (Process p = new Process())
                    {
                        p.StartInfo = si;
                        p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            onEvent(new JB().Add("type", "bcuLine").Add("line", e.Data).End());
                        };
                        p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                        {
                            if (e.Data == null) return;
                            output.AppendLine(e.Data);
                            onEvent(new JB().Add("type", "bcuLine").Add("line", e.Data).End());
                        };

                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();

                        if (!p.WaitForExit(900000))
                        {
                            try { p.Kill(); } catch { }
                            onEvent(new JB().Add("type", "bcuDone").Add("ok", false)
                                .Add("total", targets.Count).Add("msg", "BCU 卸载超时，已停止等待").End());
                            return;
                        }
                        p.WaitForExit();
                        exitCode = p.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                onEvent(new JB().Add("type", "bcuDone").Add("ok", false)
                    .Add("total", targets.Count).Add("msg", "启动 BCU 引擎失败：" + ex.Message).End());
                return;
            }
            finally
            {
                try { File.Delete(listPath); } catch { }
            }

            /* BCU 的 UniversalUninstaller 会删目录，但不一定删除 HKCU/HKLM 里的卸载登记。
               只对本次选中、且安装目录已经不存在的那条登记做精确删除；不扫描其它条目。 */
            List<Apps.App> after = Apps.List();
            List<Apps.App> removed = new List<Apps.App>();
            for (int i = 0; i < targets.Count; i++)
            {
                bool still = false;
                for (int j = 0; j < after.Count; j++)
                {
                    if (after[j].Id == targets[i].Id) { still = true; break; }
                }
                if (!still) removed.Add(targets[i]);
            }

            DeepClean.Result dc = null;
            string deepMsg = "";
            if (deep)
            {
                if (!admin)
                {
                    deepMsg = "深度清理需要管理员权限，本次未执行";
                }
                else if (exitCode != 0)
                {
                    deepMsg = "卸载未成功，本次未执行深度清理";
                }
                else if (removed.Count == 0)
                {
                    deepMsg = "没有确认已卸载的软件，本次未清理个人数据";
                }
                else
                {
                    Action<string> dlog = delegate (string s)
                    {
                        onEvent(new JB().Add("type", "bcuLine").Add("line", s).End());
                    };
                    dc = DeepClean.Run(removed, dlog);
                    deepMsg = "深度清理 " + dc.Apps + " 个软件，删除目录 " + dc.Dirs
                        + "、快捷方式 " + dc.Files + "、注册表项 " + dc.Regs
                        + (dc.Fail > 0 ? "，失败 " + dc.Fail : "");
                }
            }

            int regRemoved = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Apps.App a = targets[i];
                try
                {
                    if (a.Loc.Length == 0 || Directory.Exists(a.Loc)) continue;
                    if (RemoveRegistration(a)) regRemoved++;
                }
                catch { }
            }

            int remaining = 0;
            StringBuilder remainNames = new StringBuilder();
            for (int i = 0; i < targets.Count; i++)
            {
                bool still = false;
                for (int j = 0; j < after.Count; j++)
                {
                    if (after[j].Id == targets[i].Id) { still = true; break; }
                }
                if (still)
                {
                    remaining++;
                    if (remainNames.Length > 0) remainNames.Append("、");
                    remainNames.Append(targets[i].Name);
                }
            }

            onEvent(new JB().Add("type", "bcuDone").Add("ok", exitCode == 0)
                .Add("total", targets.Count).Add("skipped", skipped)
                .Add("regRemoved", regRemoved).Add("remaining", remaining)
                .Add("remainingNames", remainNames.ToString())
                .Add("deep", deep)
                .Add("deepApps", dc == null ? 0 : dc.Apps)
                .Add("deepSkipped", dc == null ? 0 : dc.Skipped)
                .Add("deepDirs", dc == null ? 0 : dc.Dirs)
                .Add("deepFiles", dc == null ? 0 : dc.Files)
                .Add("deepRegs", dc == null ? 0 : dc.Regs)
                .Add("deepFail", dc == null ? 0 : dc.Fail)
                .Add("deepMsg", deepMsg)
                .Add("exit", exitCode)
                .Add("msg", exitCode == 0
                    ? ("BCU 卸载完成" + (remaining > 0 ? "，仍有 " + remaining + " 个条目未消失" : ""))
                    : ("BCU 返回错误码 " + exitCode)).End());
        }

        private static int RunElevated(string listPath)
        {
            ProcessStartInfo si = new ProcessStartInfo(Exe);
            si.Arguments = "uninstall \"" + listPath + "\" /Q /U /V";
            si.WorkingDirectory = Dir;
            si.UseShellExecute = true;
            si.Verb = "runas";
            using (Process p = new Process())
            {
                p.StartInfo = si;
                p.Start();
                if (!p.WaitForExit(900000))
                {
                    try { p.Kill(); } catch { }
                    return -1;
                }
                return p.ExitCode;
            }
        }

        private static void WriteList(List<Apps.App> targets, string path)
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.Encoding = new UTF8Encoding(false);
            settings.OmitXmlDeclaration = false;

            using (XmlWriter w = XmlWriter.Create(path, settings))
            {
                w.WriteStartDocument();
                w.WriteStartElement("UninstallList");

                w.WriteStartElement("Filters");
                for (int i = 0; i < targets.Count; i++)
                {
                    Apps.App a = targets[i];
                    w.WriteStartElement("Filter");
                    w.WriteElementString("Name", "WinTune: " + Clean(a.Name));
                    w.WriteElementString("Exclude", "false");

                    w.WriteStartElement("ComparisonEntries");
                    WriteCondition(w, "DisplayName", Clean(a.Name));
                    string key = KeyOf(a);
                    if (key.Length > 0) WriteCondition(w, "RegistryKeyName", key);
                    w.WriteEndElement(); /* ComparisonEntries */

                    w.WriteElementString("Enabled", "true");
                    w.WriteEndElement(); /* Filter */
                }
                w.WriteEndElement(); /* Filters */

                w.WriteElementString("Enabled", "true");
                w.WriteEndElement(); /* UninstallList */
                w.WriteEndDocument();
            }
        }

        private static void WriteCondition(XmlWriter w, string target, string text)
        {
            w.WriteStartElement("FilterCondition");
            w.WriteElementString("InvertResults", "false");
            w.WriteElementString("ComparisonMethod", "Equals");
            w.WriteElementString("FilterText", text);
            w.WriteElementString("TargetPropertyId", target);
            w.WriteElementString("Enabled", "true");
            w.WriteEndElement();
        }

        private static string Clean(string text)
        {
            if (text == null) return "";
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>Id 格式：HKLM|SOFTWARE\...\Uninstall|KeyName</summary>
        private static string KeyOf(Apps.App a)
        {
            string[] p = a.Id.Split(new char[] { '|' }, 3);
            return p.Length == 3 ? p[2] : "";
        }

        private static bool RemoveRegistration(Apps.App a)
        {
            string[] p = a.Id.Split(new char[] { '|' }, 3);
            if (p.Length != 3) return false;
            RegistryKey root = p[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using (RegistryKey k = root.OpenSubKey(p[1], true))
            {
                if (k == null) return false;
                k.DeleteSubKeyTree(p[2], false);
                return true;
            }
        }
    }
}
