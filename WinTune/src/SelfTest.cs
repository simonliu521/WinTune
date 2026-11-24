using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinTune
{
    /// <summary>
    /// 无界面自检：验证扫描分级与 Guard 拦截逻辑，结果写文件（winexe 没有控制台）。
    /// 用法：WinTune.exe --selftest
    /// </summary>
    public static class SelfTest
    {
        public static void Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("WinTune 自检  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            sb.AppendLine("[环境]");
            sb.AppendLine("  管理员=" + SysInfo.IsAdmin());
            sb.AppendLine("  系统=" + SysInfo.OsName());
            sb.AppendLine("  架构=" + SysInfo.Arch());
            sb.AppendLine("  winget=" + SysInfo.HasWinget() + " choco=" + SysInfo.HasChoco() + " pwsh7=" + SysInfo.HasPwsh7());
            sb.AppendLine("  还原=" + SysInfo.RestorePointHint() + "  策略=" + SysInfo.PolicyHint());
            sb.AppendLine();

            sb.AppendLine("[运行依赖]");
            sb.AppendLine("  WebView2 运行时=" + (Dependency.HasWebView2() ? "已就绪" : "缺失（启动时会自动装）"));
            string mmExe = Hardware.FindMiniMonitorExe();
            sb.AppendLine("  MiniMonitor=" + (mmExe.Length > 0 ? mmExe : "未找到"));
            sb.AppendLine("  MiniMonitor 随包=" + (mmExe.Length > 0 && Hardware.MmBundled(mmExe)));
            string mmVer = mmExe.Length > 0 ? Hardware.MmVersion(mmExe) : "";
            sb.AppendLine("  MiniMonitor 版本=" + (mmVer.Length > 0 ? mmVer : "读不到（配置文件里没有 Version，exe 也没有版本资源）")
                + (mmVer.Length > 0 && Hardware.MmVersionFromConfig(mmExe) ? "（读自 MiniMonitor.config）" : ""));
            sb.AppendLine("  程序目录=" + AppDomain.CurrentDomain.BaseDirectory);
            sb.AppendLine();

            sb.AppendLine("[Guard 反例：以下必须全部为 拒绝]");
            string[] bad =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\drivers"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\报告.docx",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Desktop",
                @"C:\"
            };
            List<string> roots = Scanner.AllowedRoots();
            int badPass = 0;
            foreach (string p in bad)
            {
                string reason;
                bool allowed = Guard.CanDelete(p, roots, false, out reason);
                sb.AppendLine((allowed ? "  !! 放行（缺陷）：" : "  拒绝 ✔  ") + p + "  ← " + reason);
                if (!allowed) badPass++;
            }
            sb.AppendLine("  通过 " + badPass + "/" + bad.Length);
            sb.AppendLine();

            sb.AppendLine("[Guard 正例：以下必须全部为 允许]");
            string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            string[] good =
            {
                temp + @"\wt_selftest_dir",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Temp\wt_selftest"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download\abc.cab")
            };
            int goodPass = 0;
            foreach (string p in good)
            {
                string reason;
                bool allowed = Guard.CanDelete(p, roots, false, out reason);
                sb.AppendLine((allowed ? "  允许 ✔  " : "  !! 拒绝（缺陷）：") + p + "  ← " + reason);
                if (allowed) goodPass++;
            }
            sb.AppendLine("  通过 " + goodPass + "/" + good.Length);
            sb.AppendLine();

            sb.AppendLine("[扫描计划与大小]");
            List<CleanItem> plan = Scanner.BuildPlan();
            foreach (CleanItem it in plan)
            {
                long bytes = 0; int files = 0; string skip;
                bool ok = Scanner.MeasureItem(it, out bytes, out files, out skip);
                sb.AppendLine("  [" + it.Tier + "] " + it.Id + "  " + it.Name +
                              "  存在=" + Guard.Exists(it.Path) +
                              "  可测=" + ok + "  大小=" + bytes + "  文件=" + files +
                              (skip.Length > 0 ? "  (" + skip + ")" : ""));
            }
            sb.AppendLine();

            sb.AppendLine("[回收站实测]");
            long rbB = 0; int rbN = 0;
            if (RecycleBin.Query(out rbB, out rbN))
                sb.AppendLine("  项数=" + rbN + "  大小=" + rbB + " 字节" + (rbN > 0 ? "  → 界面应显示体积" : "  → 空，界面不列出"));
            else
                sb.AppendLine("  读不到（SHQueryRecycleBin 调用失败）");
            sb.AppendLine();

            sb.AppendLine("[注册表残留]");
            List<RegistryScan.Hit> uninst = RegistryScan.DetectUninstall();
            List<RegistryScan.Hit> runs = RegistryScan.DetectRun();
            sb.AppendLine("  无效卸载项=" + uninst.Count + "  失效启动项=" + runs.Count);
            int shown = 0;
            foreach (RegistryScan.Hit h in uninst)
            {
                if (shown++ >= 5) break;
                sb.AppendLine("    U " + h.Display + " ← " + h.Why);
            }
            shown = 0;
            foreach (RegistryScan.Hit h in runs)
            {
                if (shown++ >= 5) break;
                sb.AppendLine("    R " + h.Display + " ← " + h.Why);
            }
            sb.AppendLine();

            sb.AppendLine("[无可用卸载程序的应用]");
            try
            {
                int miss = 0, none = 0, shown2 = 0;
                foreach (Apps.App ap in Apps.List())
                {
                    string ust = Apps.UninstallerState(ap);
                    if (ust == "ok") continue;
                    if (ust == "missing") miss++; else none++;
                    if (shown2++ >= 8) continue;
                    Dictionary<string, object> lo = Json.Parse(Apps.Leftover(ap.Id));
                    sb.AppendLine("  " + (ust == "missing" ? "卸载程序丢失" : "无卸载命令") + "  " + ap.Name +
                                  "  目录=" + Json.Str(lo, "dir") +
                                  "  文件=" + Json.Str(lo, "files") +
                                  "  大小=" + Json.Str(lo, "bytes") +
                                  "  快捷方式=" + Json.Str(lo, "shortcuts") +
                                  "  敏感=" + string.Join(",", Json.StrList(lo, "sensitive").ToArray()));
                }
                sb.AppendLine("  合计：卸载程序丢失=" + miss + " 无卸载命令=" + none + "（明细只列前 8 个）");
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }
            sb.AppendLine();

            sb.AppendLine("[软件残留]");
            List<ResidueHit> res = Residue.Detect(null);
            if (res.Count == 0) sb.AppendLine("  未发现候选目录");
            foreach (ResidueHit h in res)
            {
                sb.AppendLine("  " + (h.InUse ? "在用  " : "残留  ") + h.Brand + "  " + h.Path +
                              "  " + h.Bytes + " 字节  ← " + h.Evidence);
            }

            sb.AppendLine();
            sb.AppendLine("[删除链路自检（沙箱目录，不碰系统文件）]");
            try
            {
                string sandbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "WinTune", "_selftest_del_" + DateTime.Now.ToString("HHmmss"));
                Directory.CreateDirectory(Path.Combine(sandbox, "a"));
                File.WriteAllText(Path.Combine(sandbox, "a", "x1.txt"), new string('x', 4096));
                File.WriteAllText(Path.Combine(sandbox, "b.log"), new string('y', 8192));

                CleanItem it = new CleanItem();
                it.Id = "selftest_del";
                it.Name = "删除自检（沙箱目录）";
                it.Path = sandbox;
                it.Tier = "free";
                it.Action = "delete";
                it.AllowSelf = true;
                it.DefaultOn = false;

                Cleaner.Plan p = new Cleaner.Plan();
                p.Item = it;
                p.Bytes = 12288;
                p.Runnable = true;
                p.Why = "";

                List<Cleaner.Plan> plans = new List<Cleaner.Plan>();
                plans.Add(p);

                Action<string> noop = new Action<string>(delegate(string s) { });
                CleanResult cr = Cleaner.Execute(plans, noop);

                bool gone = !Directory.Exists(sandbox);
                sb.AppendLine("  沙箱=" + sandbox);
                sb.AppendLine("  删除后目录消失=" + gone + "（应为 True）");
                sb.AppendLine("  释放=" + cr.Freed + " 删除文件=" + cr.DeletedFiles +
                              " 跳过=" + cr.Skipped + " 失败=" + cr.Failed + " 耗时=" + cr.ElapsedMs + "ms");
                sb.AppendLine("  清单=" + cr.Manifest);
                sb.AppendLine("  还原点=" + cr.RestoreNote);
                sb.AppendLine("  结果=" + ((gone && cr.Freed >= 12288 && cr.DeletedFiles >= 2) ? "通过 ✔" : "!! 未通过"));
                if (!gone) { try { Directory.Delete(sandbox, true); } catch { } }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  !! 异常：" + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("[清除残留自检（沙箱注册表项 + 沙箱目录）]");
            try
            {
                string stamp = DateTime.Now.ToString("HHmmss");
                string sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
                string keyName = "WinTuneSelfTest_" + stamp;
                string sandbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "WinTune", "_selftest_app_" + stamp);
                Directory.CreateDirectory(Path.Combine(sandbox, "Storage"));
                File.WriteAllText(Path.Combine(sandbox, "Storage", "mail.eml"), new string('m', 2048));
                File.WriteAllText(Path.Combine(sandbox, "app.dat"), new string('d', 1024));

                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(sub + "\\" + keyName))
                {
                    k.SetValue("DisplayName", "WinTune 自检残留");
                    k.SetValue("UninstallString", "\"" + Path.Combine(sandbox, "uninst.exe") + "\"");
                    k.SetValue("InstallLocation", sandbox);
                }

                string id = "HKCU|" + sub + "|" + keyName;
                Dictionary<string, object> lo = Json.Parse(Apps.Leftover(id));
                string st = Json.Str(lo, "state");
                int loFiles = Json.Int(lo, "files", 0);
                int loBytes = Json.Int(lo, "bytes", 0);
                string sens = string.Join(",", Json.StrList(lo, "sensitive").ToArray());
                string loDir = Json.Str(lo, "dir");
                bool readOk = Json.Bool(lo, "ok") && st == "missing" && loFiles == 2 && loBytes == 3072
                              && sens.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0;
                sb.AppendLine("  读取：ok=" + Json.Bool(lo, "ok") + " 状态=" + st + " 目录=" + loDir +
                              " 文件=" + loFiles + " 大小=" + loBytes + " 敏感=" + sens);
                sb.AppendLine("  结果=" + (readOk ? "通过 ✔" : "!! 未通过"));

                Action<string> noop2 = new Action<string>(delegate(string s) { });
                Apps.Remove(id, "full", noop2);

                bool keyGone = true;
                using (RegistryKey chk = Registry.CurrentUser.OpenSubKey(sub + "\\" + keyName)) { keyGone = chk == null; }
                bool dirGone = !Directory.Exists(sandbox);
                sb.AppendLine("  移除：登记已删除=" + keyGone + " 目录已删除=" + dirGone);
                sb.AppendLine("  结果=" + ((keyGone && dirGone) ? "通过 ✔" : "!! 未通过"));
                if (!dirGone) { try { Directory.Delete(sandbox, true); } catch { } }
                if (!keyGone) { try { Registry.CurrentUser.DeleteSubKeyTree(sub + "\\" + keyName, false); } catch { } }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  !! 异常：" + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("[系统调整往返自检（单项 MenuShowDelay）]");
            try
            {
                Tweak t = null;
                foreach (Tweak x in Tweaks.Catalog()) if (x.Id == "menudelay") t = x;
                if (t == null) sb.AppendLine("  !! 目录里没有 menudelay");
                else
                {
                    RegOp op = t.Ops[0];
                    bool before = Tweaks.OpApplied(op);
                    List<string> one = new List<string>();
                    one.Add(t.Id);
                    Action<string> noop2 = new Action<string>(delegate(string s) { });
                    string applyRes = Tweaks.Apply(one, noop2);
                    bool afterApply = Tweaks.OpApplied(op);
                    string revertRes = Tweaks.Revert(one, noop2);
                    bool afterRevert = Tweaks.OpApplied(op);
                    sb.AppendLine("  应用前已是目标值=" + before);
                    sb.AppendLine("  应用后=目标值：" + afterApply + "（应为 True）");
                    sb.AppendLine("  撤销后=原值：" + (!afterRevert || before) + "（应回到原始状态）");
                    sb.AppendLine("  apply=" + applyRes);
                    sb.AppendLine("  revert=" + revertRes);
                    sb.AppendLine("  结果=" + ((afterApply && (!afterRevert || before)) ? "通过 ✔" : "!! 未通过"));
                }
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("[残留占停删除自检（沙箱目录 + 子进程独占文件句柄）]");
            try
            {
                string hb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "WinTune", "_selftest_hold_" + DateTime.Now.ToString("HHmmss"));
                Directory.CreateDirectory(hb);
                string hf = Path.Combine(hb, "hold.bin");
                File.WriteAllText(hf, new string('h', 8192));

                string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                         @"WindowsPowerShell\v1.0\powershell.exe");
                string arg = "-NoProfile -WindowStyle Hidden -Command \"$f=[IO.File]::Open('" + hf +
                             "','Open','ReadWrite','None'); Start-Sleep -Seconds 25\"";
                Process hold = Process.Start(ps, arg);
                System.Threading.Thread.Sleep(3000);

                List<Occ> occ = Residue.FindAll(hb);
                StringBuilder ob = new StringBuilder();
                foreach (Occ o in occ) ob.Append(o.Kind + "=" + o.Name + " ");
                sb.AppendLine("  占用检测=" + occ.Count + " 项 " + ob.ToString());

                Action<string> noop3 = new Action<string>(delegate(string s) { });
                string stopMsg = Residue.PrepareDelete(hb, noop3);
                sb.AppendLine("  停止结果=" + stopMsg);

                bool gone2 = false;
                try { Directory.Delete(hb, true); gone2 = !Directory.Exists(hb); }
                catch (Exception ex) { sb.AppendLine("  删除异常：" + ex.Message); }

                bool holdAlive = false;
                try { holdAlive = (hold != null && !hold.HasExited); } catch { }
                if (holdAlive) { try { hold.Kill(); } catch { } }
                if (!gone2) { try { Directory.Delete(hb, true); } catch { } }

                sb.AppendLine("  停止后删除成功=" + gone2 + "（应为 True）");
                sb.AppendLine("  结果=" + ((occ.Count > 0 && gone2) ? "通过 ✔" : "!! 未通过"));
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("[网络诊断]");
            try
            {
                Dictionary<string, object> ni = Json.Parse(NetFix.InfoJson());
                sb.AppendLine("  网卡=" + Json.Int(ni, "nics", 0) + "  已连接=" + Json.Int(ni, "up", 0) +
                              "  物理口已连接=" + Json.Int(ni, "physUp", 0) + "  管理员=" + Json.Bool(ni, "admin"));
                object o = ni.ContainsKey("nicList") ? ni["nicList"] : null;
                object[] nicRows = null;
                if (o is object[]) nicRows = (object[])o;
                else if (o is System.Collections.ArrayList) nicRows = ((System.Collections.ArrayList)o).ToArray();
                if (nicRows != null)
                {
                    foreach (object x in nicRows)
                    {
                        Dictionary<string, object> d = (Dictionary<string, object>)x;
                        sb.AppendLine("  · " + Json.Str(d, "name") + " [" + Json.Str(d, "type") + "] " +
                                      Json.Str(d, "status") + "  IPv4=" + Json.Str(d, "ipv4") +
                                      "  网关=" + Json.Str(d, "gw") + "  DNS=" + Json.Str(d, "dns") +
                                      "  DHCP=" + Json.Bool(d, "dhcp") + "  虚拟=" + Json.Bool(d, "virtual"));
                    }
                }
                NetFix.HostsInfo rh = NetFix.ReadHosts();
                sb.AppendLine("  hosts=" + NetFix.HostsPath);
                sb.AppendLine("  可读=" + rh.Ok + " 总行=" + rh.TotalLines + " 映射=" + rh.Entries +
                              " 可疑=" + rh.Suspects.Count + " 可写=" + rh.Writable);
                for (int i = 0; i < rh.Suspects.Count && i < 3; i++)
                    sb.AppendLine("    L" + rh.Suspects[i].Line + " " + rh.Suspects[i].Ip + " " +
                                  rh.Suspects[i].Domains + " ← " + rh.Suspects[i].Why);
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("[hosts 解析自检（样本文本，不碰真实文件）]");
            try
            {
                string[] sample = new string[]
                {
                    "# 注释行不该算映射",
                    "",
                    "127.0.0.1 localhost",
                    "127.0.0.1 blocked.example.com",
                    "10.0.0.5 nas.local",
                    "192.168.1.10 same.example.com",
                    "203.0.113.7 same.example.com",
                    "999.1.1.1 bad-ip.example.com"
                };
                NetFix.HostsInfo ph = NetFix.ParseText(sample);
                sb.AppendLine("  有效映射=" + ph.Entries + "（应为 6）  可疑=" + ph.Suspects.Count + "（应为 3）");
                for (int i = 0; i < ph.Suspects.Count; i++)
                    sb.AppendLine("    L" + ph.Suspects[i].Line + " " + ph.Suspects[i].Ip + " " +
                                  ph.Suspects[i].Domains + " ← " + ph.Suspects[i].Why);
                bool hp = ph.Entries == 6 && ph.Suspects.Count == 3;
                sb.AppendLine("  结果=" + (hp ? "通过 ✔" : "!! 未通过"));
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("[网络体检（进页面时跑的那一套）]");
            try
            {
                Dictionary<string, object> ck = Json.Parse(NetFix.CheckJson());
                int n = Json.Int(ck, "count", 0);
                sb.AppendLine("  条目=" + n + "  耗时=" + Json.Int(ck, "ms", 0) + " ms");
                object o = ck.ContainsKey("items") ? ck["items"] : null;
                object[] rows = null;
                if (o is object[]) rows = (object[])o;
                else if (o is System.Collections.ArrayList) rows = ((System.Collections.ArrayList)o).ToArray();
                if (rows != null && rows.Length > 0)
                {
                    foreach (object x in rows)
                    {
                        Dictionary<string, object> d = (Dictionary<string, object>)x;
                        sb.AppendLine("  · [" + Json.Str(d, "level") + "] " + Json.Str(d, "title") +
                                      " → " + Json.Str(d, "fixName"));
                    }
                }
                else sb.AppendLine("  未发现问题：界面不显示任何提示");
                sb.AppendLine("  结果=" + (rows != null ? "通过 ✔" : "!! 未通过"));
            }
            catch (Exception ex) { sb.AppendLine("  !! 异常：" + ex.Message); }
            sb.AppendLine("[测速节点连通性（只测 TCP 443，不算流量）]");
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                bool ok;
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    ok = c.ConnectAsync("speed.cloudflare.com", 443).Wait(5000);
                }
                sw.Stop();
                sb.AppendLine("  speed.cloudflare.com:443=" +
                              (ok ? ("通（" + sw.ElapsedMilliseconds + " ms）") : "不通（5 秒内没连上，测速会走代理重试一次）"));
            }
            catch (Exception ex) { sb.AppendLine("  " + ex.Message); }

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTune");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "selftest.log");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
    }
}
