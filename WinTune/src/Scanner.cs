using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

namespace WinTune
{
    /// <summary>回收站体积查询：走 Shell 的 SHQueryRecycleBin，比自己遍历 $Recycle.Bin 准（含其他盘与各用户）。</summary>
    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string rootPath, ref RBINFO info);

        public static bool Query(out long bytes, out int items)
        {
            bytes = 0; items = 0;
            try
            {
                RBINFO i = new RBINFO();
                i.cbSize = Marshal.SizeOf(typeof(RBINFO));
                int hr = SHQueryRecycleBin(null, ref i);   /* null = 所有驱动器 */
                if (hr != 0) return false;
                bytes = i.i64Size;
                items = (int)i.i64NumItems;
                return true;
            }
            catch { return false; }
        }
    }

    public class CleanItem
    {
        public string Id;
        public string Name;
        public string Path;
        public string Tier;      // free / care / keep
        public string Note;
        public string Action;    // delete / empty_recycle / dism / hibernate / registry
        public long Bytes;
        public int Files;
        public bool DefaultOn;
        public bool Available = true;
        public string SkipReason;
        public bool IsGroup;
        /// <summary>非空时只处理这些子目录（浏览器缓存用它，绝不动整个 User Data）。</summary>
        public string[] SubDirs;
        /// <summary>允许删除路径本身（Windows.old、已卸载软件残留目录这类必须整目录删的项）。</summary>
        public bool AllowSelf;
        /// <summary>判定依据编号，对应 Evidence 里的条目；界面上点开能看到出处。</summary>
        public string Ev = "";
        /// <summary>「不建议」不等于「不能」：标了它表示这一项可以强制删除，只是要用户明确确认。</summary>
        public bool Force;
    }

    /// <summary>
    /// 只做扫描与分级，不碰文件。扫描本身全只读，随时可以中断。
    /// </summary>
    public static class Scanner
    {
        private const long SizeBudgetMs = 9000;   // 单项统计上限，超时按已知部分计数

        public static string LocalAppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        }

        public static string RoamingAppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        }

        public static string WinDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        }

        public static string ProgramDataDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
        }

        public static List<string> AllowedRoots()
        {
            List<string> roots = new List<string>();
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Temp");
            roots.Add(WinDir + @"\Temp");
            roots.Add(WinDir + @"\SoftwareDistribution\Download");
            roots.Add(WinDir + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization");
            roots.Add(WinDir + @"\Prefetch");
            roots.Add(WinDir + @"\Logs");
            roots.Add(LocalAppData + @"\CrashDumps");
            roots.Add(LocalAppData + @"\Microsoft\Windows\Explorer");
            roots.Add(LocalAppData + @"\Microsoft\Windows\WER");
            roots.Add(ProgramDataDir + @"\Microsoft\Windows\WER");
            roots.Add(WinDir + @"\ServiceProfiles\LocalService\AppData\Local\FontCache");
            roots.Add(LocalAppData + @"\Microsoft\Windows\INetCache");
            /* 1.5.3 新增的几处缓存根：缩略图/图标缓存、着色器缓存、远程桌面缓存、内核 live dump */
            roots.Add(LocalAppData + @"\D3DSCache");
            roots.Add(LocalAppData + @"\NVIDIA\DXCache");
            roots.Add(LocalAppData + @"\AMD\DxCache");
            roots.Add(LocalAppData + @"\Microsoft\Terminal Server Client\Cache");
            roots.Add(WinDir + @"\LiveKernelReports");
            roots.Add(WinDir + @"\Minidump");
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\Tencent");
            return roots;
        }

        public static string ScanAsJson(Action<string> onEvent, Func<bool> cancelled, out List<CleanItem> items)
        {
            List<CleanItem> plan = BuildPlan();
            int total = 0;
            foreach (CleanItem it in plan) { if (it.Action == "delete") total++; }

            int done = 0;
            foreach (CleanItem it in plan)
            {
                if (cancelled != null && cancelled()) break;

                if (it.Action == "delete")
                {
                    done++;
                    long bytes = 0; int files = 0;
                    string skip;
                    if (!MeasureItem(it, out bytes, out files, out skip))
                    {
                        it.Available = false;
                        it.SkipReason = skip;
                    }
                    it.Bytes = bytes;
                    it.Files = files;
                    if (bytes == 0 && files == 0 && it.Available)
                    {
                        it.Available = false;
                        it.SkipReason = "目录为空或已不存在";
                    }
                    Emit(onEvent, new JB()
                        .Add("type", "scanItem")
                        .Add("index", done)
                        .Add("total", total)
                        .Raw("item", ItemJson(it))
                        .End());
                    Log.Info("scan", it.Id + " bytes=" + bytes + " files=" + files + (it.Available ? "" : " skip=" + it.SkipReason));
                }
                else
                {
                    // 动作类项：回收站报到真实体积与件数（没有东西就直接当"空"跳过，不占列表），
                    // 其余（DISM / 休眠）只做可用性判定
                    if (it.Action == "empty_recycle")
                    {
                        long rbBytes = 0; int rbItems = 0;
                        if (RecycleBin.Query(out rbBytes, out rbItems))
                        {
                            it.Bytes = rbBytes;
                            it.Files = rbItems;
                            if (rbItems <= 0 || rbBytes <= 0)
                            {
                                it.Available = false;
                                it.SkipReason = "回收站是空的";
                            }
                        }
                        else
                        {
                            it.Available = false;
                            it.SkipReason = "读不到回收站内容";
                        }
                    }
                    else
                    {
                        it.Available = ActionAvailable(it.Action, out it.SkipReason);
                    }
                    Log.Info("scan", it.Id + " action=" + it.Action + " bytes=" + it.Bytes + " files=" + it.Files +
                                   (it.Available ? "" : " skip=" + it.SkipReason));
                    Emit(onEvent, new JB()
                        .Add("type", "scanItem")
                        .Add("index", done)
                        .Add("total", total)
                        .Raw("item", ItemJson(it))
                        .End());
                }
            }

            /* 不再扫描或删除“软件残留”目录：
               未登记安装 ≠ 已卸载。绿色软件、按用户安装、注册表缺失都可能被误判，
               曾出现把正在使用的应用整个删掉。空间清理只处理明确属于缓存/临时文件的路径。 */

            long freeSum = 0, careSum = 0, keepSum = 0;
            int freeCnt = 0, careCnt = 0, keepCnt = 0;
            foreach (CleanItem it in plan)
            {
                if (!it.Available || it.Bytes <= 0) continue;
                if (it.Tier == "free") { freeSum += it.Bytes; freeCnt++; }
                else if (it.Tier == "care") { careSum += it.Bytes; careCnt++; }
                else if (it.Tier == "keep") { keepSum += it.Bytes; keepCnt++; }
            }

            items = plan;

            return new JB()
                .Add("type", "scanDone")
                .Add("freeBytes", freeSum).Add("freeCount", freeCnt)
                .Add("careBytes", careSum).Add("careCount", careCnt)
                .Add("keepBytes", keepSum).Add("keepCount", keepCnt)
                .Add("elapsedMs", 0)
                .End();
        }

        public static string ItemJson(CleanItem it)
        {
            return new JB()
                .Add("id", it.Id)
                .Add("name", it.Name)
                .Add("path", it.Path)
                .Add("tier", it.Tier)
                .Add("note", it.Note)
                .Add("action", it.Action)
                .Add("bytes", it.Bytes)
                .Add("files", it.Files)
                .Add("def", it.DefaultOn)
                .Add("ok", it.Available)
                .Add("skip", it.SkipReason)
                .Add("ev", it.Ev).Add("force", it.Force)
                .End();
        }

        /// <summary>把所有候选项构建成清单（不含大小）。</summary>
        public static List<CleanItem> BuildPlan()
        {
            List<CleanItem> list = new List<CleanItem>();
            string tempUser = LocalAppData + @"\Temp";

            Add(list, "tmp_user", "用户临时文件", tempUser, "free",
                @"%TEMP% · 程序运行产生的临时副本，删掉不影响已装软件", "delete", true, "E01");
            Add(list, "tmp_win", "系统临时目录", WinDir + @"\Temp", "free",
                @"C:\Windows\Temp · 安装与更新过程留下的临时文件", "delete", true, "E01");
            Add(list, "upd_down", "Windows 更新缓存", WinDir + @"\SoftwareDistribution\Download", "free",
                "已下载的更新包，删后下次更新会重新下载（微软排障步骤同此做法）", "delete", true, "E02");
            Add(list, "do_cache", "传递优化缓存", WinDir + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization", "free",
                "局域网更新分发缓存，微软说明可清，删掉只影响下次更新的下载速度", "delete", true, "E03");
            /* INetCache 只登记一次（下面第 258 行那条「临时 Internet 文件」），这里不再重复添加，
               否则同一路径会出现两行同名项 */
            Add(list, "crash", "崩溃转储", LocalAppData + @"\CrashDumps", "free",
                "程序崩溃时生成的 dump，只在排障时有价值", "delete", true, "E04");
            Add(list, "wer", "错误报告队列", ProgramDataDir + @"\Microsoft\Windows\WER", "free",
                "Windows 错误报告的待上传队列与归档，属可选诊断数据", "delete", true, "E04");
            Add(list, "edge_cache", "Edge 缓存", LocalAppData + @"\Microsoft\Edge\User Data", "free",
                "只清 Cache / Code Cache / GPUCache，不动书签、密码、历史", "delete", true, "E05");
            list[list.Count - 1].SubDirs = new string[] {
                LocalAppData + @"\Microsoft\Edge\User Data\Default\Cache",
                LocalAppData + @"\Microsoft\Edge\User Data\Default\Code Cache",
                LocalAppData + @"\Microsoft\Edge\User Data\Default\GPUCache",
                LocalAppData + @"\Microsoft\Edge\User Data\Default\Service Worker\CacheStorage"
            };
            Add(list, "chrome_cache", "Chrome 缓存", LocalAppData + @"\Google\Chrome\User Data", "free",
                "只清 Cache / Code Cache / GPUCache，不动书签、密码、历史", "delete", true, "E05");
            list[list.Count - 1].SubDirs = new string[] {
                LocalAppData + @"\Google\Chrome\User Data\Default\Cache",
                LocalAppData + @"\Google\Chrome\User Data\Default\Code Cache",
                LocalAppData + @"\Google\Chrome\User Data\Default\GPUCache",
                LocalAppData + @"\Google\Chrome\User Data\Default\Service Worker\CacheStorage"
            };
            Add(list, "thumbcache", "缩略图与图标缓存", LocalAppData + @"\Microsoft\Windows\Explorer", "free",
                "thumbcache / iconcache 是预览索引，删后翻目录时自动重建，不丢原图", "delete", true, "E13");
            Add(list, "d3d_cache", "着色器缓存（DirectX）", LocalAppData + @"\D3DSCache", "care",
                "显卡编译好的着色器，删后首次进游戏/播视频会重新编译、略慢，自动重建", "delete", false, "E14");
            Add(list, "nv_cache", "着色器缓存（NVIDIA）", LocalAppData + @"\NVIDIA\DXCache", "care",
                "同上，NVIDIA 驱动自己的 DX 缓存", "delete", false, "E14");
            Add(list, "rdp_cache", "远程桌面缓存", LocalAppData + @"\Microsoft\Terminal Server Client\Cache", "free",
                "远程桌面的画面缓存，重连后自动重建，不含登录凭据", "delete", true, "E15");
            Add(list, "livekernel", "内核实时报告", WinDir + @"\LiveKernelReports", "free",
                "驱动/显卡异常时的内核转储，只在提交问题单时有用", "delete", true, "E16");
            Add(list, "inet_cache", "临时 Internet 文件", LocalAppData + @"\Microsoft\Windows\INetCache", "free",
                "程序下载的网页与脚本临时件，微软磁盘清理把它列为可直接删除的一类，删后重访自动重建", "delete", true, "E01");
            Add(list, "minidump", "崩溃转储 Minidump", WinDir + @"\Minidump", "free",
                "程序崩溃时生成的转储文件，只在分析崩溃原因时用，删掉不影响系统", "delete", true, "E38");
            /* 预读取文件：微软明确说自维护、不需要清，清了反而更慢，所以只展示占用、不参与删除 */
            Add(list, "prefetch", "预读取文件", WinDir + @"\Prefetch", "keep",
                "微软：Prefetch 由系统自维护、会自动清理，清空后头几次开机与应用启动反而更慢", "delete", false, "E06").Force = true;
            Add(list, "cbs_log", "系统日志", WinDir + @"\Logs", "care",
                "CBS / DISM 等日志，只在排障时需要，可保留最近 7 天", "delete", false, "E07");
            Add(list, "font_cache", "字体缓存", WinDir + @"\ServiceProfiles\LocalService\AppData\Local\FontCache", "care",
                "删后首次显示文字会短暂重建缓存，通常需要重启字体服务", "delete", false, "E08");

            // 旧系统备份（必须整目录删，走 AllowSelf）
            string winOld = Path.Combine(Path.GetPathRoot(WinDir), "Windows.old");
            CleanItem wold = Add(list, "windows_old", "旧系统备份 C:\\Windows.old", winOld, "care",
                "上一版本的完整备份，删后无法回滚系统版本", "delete", false, "E09");
            wold.AllowSelf = true;

            // 动作类
            AddAction(list, "recycle", "回收站", "把回收站里的内容彻底清空", "empty_recycle", "E12");
            AddAction(list, "winsxs", "WinSxS 组件回收", "用 DISM 清理旧组件，不可逆，已装更新将无法卸载", "dism", "E10");
            AddAction(list, "hibernate", "休眠文件 hiberfil.sys", "关闭休眠功能后释放，体积约等于内存容量；关闭后不能休眠", "hibernate", "E11");

            /* 不建议删 ≠ 不能删：这两项删了会丢数据或让外设装不回来，所以默认不选，
               但用户明确确认后允许强制删除。真正一删就坏系统的（DriverStore、程序本体）不列出。 */
            Add(list, "wechat", "微信 / QQ 聊天记录", RoamingAppData + @"\Tencent", "keep",
                "消息、图片与接收文件，删了不可恢复；确认无误再强制删除", "delete", false, "E17").Force = true;

            return list;
        }

        private static CleanItem Add(List<CleanItem> list, string id, string name, string path,
            string tier, string note, string action, bool defOn, string ev)
        {
            CleanItem it = new CleanItem();
            it.Id = id; it.Name = name; it.Path = path; it.Tier = tier;
            it.Note = note; it.Action = action; it.DefaultOn = defOn; it.Ev = ev;
            list.Add(it);
            return it;
        }

        private static CleanItem AddAction(List<CleanItem> list, string id, string name, string note, string action, string ev)
        {
            CleanItem it = new CleanItem();
            it.Id = id; it.Name = name; it.Path = "";
            it.Tier = "care"; it.Note = note; it.Action = action; it.DefaultOn = false; it.Ev = ev;
            it.Available = true;
            list.Add(it);
            return it;
        }

        public static bool ActionAvailable(string action, out string reason)
        {
            reason = "";
            if (action == "dism")
            {
                if (!SysInfo.IsAdmin()) { reason = "需要管理员权限"; return false; }
                return true;
            }
            if (action == "hibernate")
            {
                string h = Path.Combine(WinDir, "hiberfil.sys");
                if (!File.Exists(h)) { reason = "当前未启用休眠，无需处理"; return false; }
                return true;
            }
            return true;
        }

        /// <summary>统计大小。遇到重解析点跳过，防止跟到别的盘。</summary>
        public static bool Measure(string path, int budgetMs, out long bytes, out int files, out string skip)
        {
            bytes = 0; files = 0; skip = "";
            try
            {
                if (File.Exists(path))
                {
                    FileInfo f = new FileInfo(path);
                    bytes = f.Length; files = 1;
                    return true;
                }
                if (!Directory.Exists(path)) { skip = "路径不存在"; return false; }
                try { Directory.GetFileSystemEntries(path); }
                catch (Exception ex) { skip = "无权访问：" + ex.Message; return false; }
            }
            catch (Exception ex) { skip = "无法访问：" + ex.Message; return false; }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
            Stack<string> stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (DateTime.UtcNow > deadline) { skip = "统计超时，大小按已读取部分估算"; return true; }
                string cur = stack.Pop();
                try
                {
                    foreach (string sub in Directory.GetDirectories(cur))
                    {
                        if (Guard.IsReparsePoint(sub)) continue;
                        stack.Push(sub);
                    }
                    foreach (string f in Directory.GetFiles(cur))
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(f);
                            bytes += fi.Length; files++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return true;
        }

        /// <summary>按子目录清单统计（浏览器缓存走这里，不碰整个 User Data）。</summary>
        public static bool MeasureItem(CleanItem it, out long bytes, out int files, out string skip)
        {
            bytes = 0; files = 0; skip = "";
            if (it.SubDirs != null && it.SubDirs.Length > 0)
            {
                foreach (string p in it.SubDirs)
                {
                    long b; int f; string s;
                    if (Measure(p, (int)(SizeBudgetMs / 2), out b, out f, out s)) { bytes += b; files += f; }
                }
                if (bytes == 0 && files == 0) { skip = "未发现缓存目录"; return false; }
                return true;
            }
            return Measure(it.Path, (int)SizeBudgetMs, out bytes, out files, out skip);
        }

        /// <summary>回调包装：扫描可能无 UI 监听（自检模式）。</summary>
        public static void Emit(Action<string> cb, string json)
        {
            if (cb != null) cb(json);
        }
    }
}
