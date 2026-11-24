using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Management;

namespace WinTune
{
    public class CleanResult
    {
        public long Freed;
        public int DeletedFiles;
        public int PendingFiles;
        public int Skipped;
        public int Failed;
        public List<string> FailDetail = new List<string>();
        public string Manifest;
        public string RestoreNote = "";
        public string RegBackup = "";
        public long ElapsedMs;
    }

    /// <summary>
    /// 执行删除。所有目标路径在删除前都要过 Guard；任何一个失败不阻断其它项。
    /// </summary>
    public static class Cleaner
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string existing, string target, int flags);

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string path, uint flags);

        private const int DELAY_UNTIL_REBOOT = 0x4;

        public class Plan
        {
            public CleanItem Item;
            public long Bytes;
            public bool Runnable;
            public string Why;
        }

        public static List<Plan> BuildPlan(List<CleanItem> all, List<string> selectedIds)
        {
            List<Plan> plans = new List<Plan>();
            foreach (CleanItem it in all)
            {
                if (!Contains(selectedIds, it.Id)) continue;
                Plan p = new Plan();
                p.Item = it;
                p.Bytes = it.Bytes;
                string why = "";
                p.Runnable = CheckRunnable(it, out why);
                p.Why = why;
                plans.Add(p);
            }
            return plans;
        }

        private static bool Contains(List<string> list, string v)
        {
            foreach (string s in list) if (s == v) return true;
            return false;
        }

        private static bool CheckRunnable(CleanItem it, out string why)
        {
            why = "";
            if (!it.Available) { why = it.SkipReason; return false; }
            if (it.Tier == "keep") { why = "属于不建议删除的项目"; return false; }
            if (it.Action != "delete") return true;

            if (it.SubDirs != null && it.SubDirs.Length > 0)
            {
                foreach (string p in it.SubDirs)
                {
                    List<string> one = new List<string>();
                    one.Add(p);
                    string r;
                    if (!Guard.CanDelete(p, one, true, out r)) { why = r; return false; }
                }
                return true;
            }

            if (it.AllowSelf)
            {
                List<string> self = new List<string>();
                self.Add(it.Path);
                string r2;
                if (!Guard.CanDelete(it.Path, self, true, out r2)) { why = r2; return false; }
                return true;
            }

            string reason;
            if (!Guard.CanDelete(it.Path, Scanner.AllowedRoots(), true, out reason)) { why = reason; return false; }
            return true;
        }

        public static CleanResult Execute(List<Plan> plans, Action<string> onEvent)
        {
            CleanResult res = new CleanResult();
            DateTime t0 = DateTime.UtcNow;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            // 1) 清单（先写，删之前就有记录）
            res.Manifest = WriteManifest(plans, stamp);

            // 2) 还原点（失败不阻断，但要如实报告）
            res.RestoreNote = Backup.TryRestorePoint("WinTune 清理前自动备份");

            // 3) 逐项执行
            int index = 0;
            int total = plans.Count;
            foreach (Plan p in plans)
            {
                index++;
                if (!p.Runnable)
                {
                    res.Skipped++;
                    Emit(onEvent, new JB().Add("type", "cleanItem")
                        .Add("id", p.Item.Id).Add("name", p.Item.Name)
                        .Add("ok", false).Add("why", p.Why).Add("freed", 0).End());
                    continue;
                }

                long before = res.Freed;
                int filesBefore = res.DeletedFiles;

                try
                {
                    if (p.Item.Action == "delete")
                    {
                        /* 传递优化缓存归 NetworkService 所有，普通进程（含管理员）直接删文件一律被拒，
                           必须交给 DO 服务自己的清理接口；失败时给出明确原因，不去逐文件硬删 */
                        if (p.Item.Id == "do_cache")
                        {
                            if (!PurgeDeliveryOptimizationCache(p.Bytes, res))
                                throw new InvalidOperationException(
                                    "传递优化缓存要由 Delivery Optimization（DoSvc）服务清除，本次接口调用未成功；请确认该服务在运行后重试");
                        }
                        else if (p.Item.SubDirs != null && p.Item.SubDirs.Length > 0)
                        {
                            foreach (string sub in p.Item.SubDirs)
                            {
                                List<string> subRoots = new List<string>();
                                subRoots.Add(sub);
                                DeleteOne(sub, subRoots, true, true, res);
                            }
                        }
                        else if (p.Item.AllowSelf)
                        {
                            List<string> selfRoot = new List<string>();
                            selfRoot.Add(p.Item.Path);
                            DeleteOne(p.Item.Path, selfRoot, true, false, res);
                        }
                        else
                        {
                            DeleteOne(p.Item.Path, Scanner.AllowedRoots(), true, true, res);
                        }
                    }
                    else if (p.Item.Action == "empty_recycle")
                    {
                        res.Freed += EmptyRecycle();
                    }
                    else if (p.Item.Action == "dism")
                    {
                        RunDism();
                    }
                    else if (p.Item.Action == "hibernate")
                    {
                        RunHidden("powercfg.exe", "/h off");
                    }

                    Emit(onEvent, new JB().Add("type", "cleanItem")
                        .Add("id", p.Item.Id).Add("name", p.Item.Name)
                        .Add("ok", true).Add("why", "")
                        .Add("freed", res.Freed - before)
                        .Add("files", res.DeletedFiles - filesBefore)
                        .End());
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.FailDetail.Add(p.Item.Name + " → " + ex.Message);
                    Log.Error("clean", p.Item.Id + " 失败：" + ex.Message);
                    Emit(onEvent, new JB().Add("type", "cleanItem")
                        .Add("id", p.Item.Id).Add("name", p.Item.Name)
                        .Add("ok", false).Add("why", ex.Message).Add("freed", 0).End());
                }
            }

            res.ElapsedMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
            Log.Info("clean", "完成 释放=" + res.Freed + " 文件=" + res.DeletedFiles +
                              " 待重启=" + res.PendingFiles + " 跳过=" + res.Skipped + " 失败=" + res.Failed);
            return res;
        }

        /// <summary>删除单个路径（文件或目录），所有失败逐条记录，不抛出。</summary>
        private static void DeleteOne(string path, List<string> roots, bool allowSelf, bool keepRoot, CleanResult res)
        {
            string reason;
            if (!Guard.CanDelete(path, roots, allowSelf, out reason))
            {
                res.Skipped++;
                res.FailDetail.Add(path + " → " + reason);
                Log.Warn("clean", "跳过 " + path + "：" + reason);
                return;
            }

            if (File.Exists(path))
            {
                DeleteFile(path, res);
                return;
            }
            if (!Directory.Exists(path)) return;

            // 先收集全部文件，自底向上删目录
            List<string> files = new List<string>();
            List<string> dirs = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                dirs.Add(cur);
                try
                {
                    foreach (string d in Directory.GetDirectories(cur))
                    {
                        if (Guard.IsReparsePoint(d)) continue;
                        stack.Push(d);
                    }
                    foreach (string f in Directory.GetFiles(cur)) files.Add(f);
                }
                catch (Exception ex) { res.FailDetail.Add(cur + " → " + ex.Message); }
            }

            foreach (string f in files) DeleteFile(f, res);

            dirs.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string d in dirs)
            {
                if (keepRoot && PathEquals(d, path)) continue;   // 只清空内容，保留根目录本身
                try { Directory.Delete(d, false); }
                catch (IOException) { /* 目录非空，说明有文件没删掉 */ }
                catch (Exception ex) { res.FailDetail.Add(d + " → " + ex.Message); }
            }
        }

        private static bool PathEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            string x = a.TrimEnd('\\');
            string y = b.TrimEnd('\\');
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteFile(string f, CleanResult res)
        {
            try
            {
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
                res.Freed += len;
                res.DeletedFiles++;
            }
            catch (UnauthorizedAccessException)
            {
                if (ScheduleDelete(f)) { res.PendingFiles++; }
                else { res.Failed++; res.FailDetail.Add(f + " → 拒绝访问，且无法排入重启删除"); }
            }
            catch (IOException)
            {
                if (ScheduleDelete(f)) { res.PendingFiles++; }
                else { res.Failed++; res.FailDetail.Add(f + " → 被占用，且无法排入重启删除"); }
            }
            catch (Exception ex)
            {
                res.FailDetail.Add(f + " → " + ex.Message);
            }
        }

        private static bool ScheduleDelete(string f)
        {
            try
            {
                bool ok = MoveFileEx(f, null, DELAY_UNTIL_REBOOT);
                if (ok) Log.Info("clean", "排入重启删除：" + f);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>
        /// 清空回收站：以前 flags=0 会弹系统确认/进度框，一键清理时反而像卡住；
        /// 现在静默执行（用户点「彻底删除」本身就是确认），并用 SHQueryRecycleBin 前后对比算真实释放量。
        /// </summary>
        private static long EmptyRecycle()
        {
            long beforeB = 0; int beforeN = 0;
            if (!RecycleBin.Query(out beforeB, out beforeN))
                Log.Warn("clean", "清空回收站前读不到体积，释放量可能显示为 0");
            try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); }   /* 无确认框 / 无进度条 / 无声 */
            catch (Exception ex)
            {
                Log.Warn("clean", "清空回收站失败：" + ex.Message);
                throw;
            }

            long afterB = 0; int afterN = 0;
            if (RecycleBin.Query(out afterB, out afterN) && afterN > 0 && afterB > 0)
                throw new InvalidOperationException("回收站还有 " + afterN + " 项没清掉（可能属于其他用户或被占用）");
            long freed = beforeB - afterB;
            return freed > 0 ? freed : 0;
        }

        /// <summary>
        /// 用 Windows 自带的清理接口清空传递优化缓存（Delete-DeliveryOptimizationCache）。
        /// 目录 ACL 只给 SYSTEM / NetworkService，直接删文件必然拒绝访问；该 cmdlet 由 DO 服务执行，
        /// 非管理员也能成功。成功时把扫描快照体积计入释放量（体积取自扫描阶段，不是事后重测）。
        /// </summary>
        private static bool PurgeDeliveryOptimizationCache(long scannedBytes, CleanResult res)
        {
            string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(pwsh))
            {
                Log.Warn("clean", "传递优化缓存：找不到 Windows PowerShell，无法调用清理接口");
                return false;
            }

            string script = "try { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; exit 0 } catch { exit 2 }";
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(pwsh,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(240000))
                    {
                        try { p.Kill(); } catch { }
                        Log.Warn("clean", "传递优化缓存：清理接口超时（4 分钟）");
                        return false;
                    }
                    if (p.ExitCode == 0)
                    {
                        res.Freed += scannedBytes;
                        Log.Info("clean", "传递优化缓存：已由 DO 服务清空，释放约 " + scannedBytes + " 字节");
                        return true;
                    }
                    Log.Warn("clean", "传递优化缓存：清理接口返回 " + p.ExitCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("clean", "传递优化缓存：调用清理接口失败：" + ex.Message);
                return false;
            }
        }

        private static void RunDism()
        {
            RunHidden("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase");
        }

        public static void RunHidden(string exe, string args)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                }
            }
            catch (Exception ex) { Log.Warn("exec", exe + " 执行失败：" + ex.Message); }
        }

        private static string WriteManifest(List<Plan> plans, string stamp)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "manifest-" + stamp + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("WinTune 清理清单  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("说明：本文件记录本次删除的全部目标，用于事后核对；文件本身不进回收站。");
                sb.AppendLine();
                foreach (Plan p in plans)
                {
                    sb.AppendLine("[" + (p.Runnable ? "删除" : "跳过") + "] " + p.Item.Name);
                    sb.AppendLine("  动作：" + p.Item.Action + "  分区：" + p.Item.Tier + "  大小：" + p.Bytes + " 字节");
                    if (p.Item.Path.Length > 0) sb.AppendLine("  路径：" + p.Item.Path);
                    if (p.Item.SubDirs != null)
                        foreach (string s in p.Item.SubDirs) sb.AppendLine("  子路径：" + s);
                    if (!p.Runnable) sb.AppendLine("  原因：" + p.Why);
                    sb.AppendLine();
                }
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                return file;
            }
            catch (Exception ex)
            {
                Log.Error("manifest", ex.Message);
                return "";
            }
        }

        public static void Emit(Action<string> cb, string json)
        {
            if (cb != null) cb(json);
        }
    }

    public static class Backup
    {
        /// <summary>创建系统还原点。系统还原未开启时返回说明文字，不抛异常。</summary>
        public static string TryRestorePoint(string desc)
        {
            try
            {
                using (ManagementClass cls = new ManagementClass("root\\default", "SystemRestore", null))
                using (ManagementBaseObject inParams = cls.GetMethodParameters("CreateRestorePoint"))
                {
                    inParams["Description"] = desc;
                    inParams["RestorePointType"] = 0;     // APPLICATION
                    inParams["EventType"] = 100;          // BEGIN_SYSTEM_CHANGE
                    inParams["SequenceNumber"] = 0;
                    cls.InvokeMethod("CreateRestorePoint", inParams, null);
                    Log.Info("backup", "还原点已创建：" + desc);
                    return "已创建";
                }
            }
            catch (Exception ex)
            {
                string msg = (ex.Message == null ? "" : ex.Message).Replace("\r", " ").Replace("\n", " ").Trim();
                if (msg.IndexOf("0x80070422", StringComparison.Ordinal) >= 0 ||
                    msg.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "系统还原未开启，已跳过";
                if (!SysInfo.IsAdmin())
                    return "未创建（需要管理员权限，已跳过）";
                if (msg.Length > 40) msg = msg.Substring(0, 40) + "…";
                Log.Warn("backup", "还原点失败：" + msg);
                return "创建失败：" + msg;
            }
        }

        /// <summary>导出注册表键为 .reg。返回文件路径，失败返回空。</summary>
        public static string ExportReg(string regPath, string tag)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTune", "backup");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "reg-" + tag + "-" + DateTime.Now.ToString("HHmmss") + ".reg");
                ProcessStartInfo si = new ProcessStartInfo("reg.exe",
                    "export \"" + regPath + "\" \"" + file + "\" /y");
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                }
                return File.Exists(file) ? file : "";
            }
            catch (Exception ex)
            {
                Log.Warn("backup", "导出注册表失败：" + ex.Message);
                return "";
            }
        }
    }
}

