using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ---------- 截图：全局热键 → 拖选区域（支持多屏）→ 简单编辑 ----------
public static class Shot
{
    public const int HKID = 0xB001;
    public static uint HKMods = 0x0003;   // Alt(1)+Ctrl(2) → Ctrl+Alt
    public static byte HKVk = 0x5A;      // 'Z'
    static uint effMods = 0x0003;
    static byte effVk = 0x5A;
    public static bool Registered;
    static bool busy;

    public static string HotkeyName { get { return NameOf(effMods, effVk); } }
    public static uint Mods { get { return HKMods; } }
    public static byte Vk { get { return HKVk; } }

    // 截图保存目录：留空则用系统「图片」文件夹
    public static string SaveDir = "";

    public static string DefaultSaveDir
    {
        get
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(p)) p = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return p;
        }
    }

    public static string EffectiveSaveDir
    {
        get { return string.IsNullOrEmpty(SaveDir) ? DefaultSaveDir : SaveDir; }
    }

    internal static void L(string s)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shot.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n");
        }
        catch { }
    }

    static string SettingsFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniMonitor", "settings.ini"); }
    }

    // ---------- 快捷键配置读写 ----------
    public static bool Enabled = true;      // false = 不注册截图热键（设置界面可开关）

    public static void LoadSettings()
    {
        try
        {
            string f = SettingsFile;
            if (!File.Exists(f)) return;
            foreach (string line in File.ReadAllLines(f))
            {
                string s = line.Trim();
                if (s.StartsWith("Hotkey="))
                {
                    uint m; byte v;
                    if (ParseHotkey(s.Substring(7), out m, out v)) { HKMods = m; HKVk = v; }
                }
                else if (s.StartsWith("SaveDir="))
                {
                    string v = s.Substring(8).Trim();
                    if (v.Length > 0) SaveDir = v;
                }
                else if (s.StartsWith("ShotEnabled="))
                {
                    string v = s.Substring(12).Trim();
                    Enabled = (v != "0");
                }
            }
        }
        catch { }
    }

    public static void SaveSettings()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsFile);
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFile,
                "Hotkey=" + NameOf(HKMods, HKVk) + "\r\nSaveDir=" + SaveDir + "\r\nShotEnabled=" + (Enabled ? "1" : "0") + "\r\n");
        }
        catch { }
    }

    static bool ParseHotkey(string s, out uint mods, out byte vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrEmpty(s)) return false;
        string[] parts = s.Split('+');
        foreach (string raw in parts)
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLower())
            {
                case "ctrl": mods |= 0x0002; continue;
                case "alt": mods |= 0x0001; continue;
                case "shift": mods |= 0x0004; continue;
                case "win": mods |= 0x0008; continue;
            }
            vk = VkOf(p);
        }
        return vk != 0;
    }

    static byte VkOf(string key)
    {
        string k = key.Trim().ToUpper();
        if (k.Length == 1)
        {
            char c = k[0];
            if (c >= 'A' && c <= 'Z') return (byte)c;
            if (c >= '0' && c <= '9') return (byte)c;
        }
        if (k.StartsWith("F") && k.Length >= 2)
        {
            int n;
            if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 12) return (byte)(0x70 + n - 1);
        }
        return 0;
    }

    static string KeyName(byte vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return "?";
    }

    static string NameOf(uint m, byte vk)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        if ((m & 0x0002) != 0) sb.Append("Ctrl+");
        if ((m & 0x0001) != 0) sb.Append("Alt+");
        if ((m & 0x0004) != 0) sb.Append("Shift+");
        if ((m & 0x0008) != 0) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    public static bool Register(IntPtr hwnd)
    {
        try { UnregisterHotKey(hwnd, HKID); } catch { }
        if (!Enabled) { Registered = false; return false; }   // 用户关掉了截图功能
        effMods = HKMods; effVk = HKVk;
        Registered = RegisterHotKey(hwnd, HKID, HKMods, HKVk);
        // 用户设置被占用时退回默认，保证截图仍可用
        if (!Registered && !(HKMods == 0x0003 && HKVk == 0x5A))
        {
            effMods = 0x0003; effVk = 0x5A;
            Registered = RegisterHotKey(hwnd, HKID, effMods, effVk);
        }
        if (!Registered)
        {
            effMods = 0x0003; effVk = 0x53;
            Registered = RegisterHotKey(hwnd, HKID, effMods, effVk);
        }
        return Registered;
    }

    // 用户改了快捷键后调用：成功才存盘
    public static bool Apply(IntPtr hwnd, uint mods, byte vk)
    {
        try { UnregisterHotKey(hwnd, HKID); } catch { }
        bool ok = RegisterHotKey(hwnd, HKID, mods, vk);
        if (ok)
        {
            HKMods = effMods = mods;
            HKVk = effVk = vk;
            Registered = true;
            SaveSettings();
        }
        else
        {
            // 失败就用回原来的
            Registered = RegisterHotKey(hwnd, HKID, HKMods, HKVk);
            effMods = HKMods; effVk = HKVk;
        }
        return ok;
    }

    public static void Unregister(IntPtr hwnd)
    {
        try { UnregisterHotKey(hwnd, HKID); } catch { }
        Registered = false;
    }

    public static void Capture(Form widget)
    {
        if (busy) return;
        busy = true;

        bool wasVisible = widget != null && widget.Visible;
        Bitmap shot = null;
        try
        {
            if (wasVisible) { widget.Hide(); Application.DoEvents(); System.Threading.Thread.Sleep(90); }

            Rectangle vs = SystemInformation.VirtualScreen;
            shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(shot))
                g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);

            Bitmap result = null;
            ShotOutcome outcome = ShotOutcome.Cancel;

            // 冻结全屏 → 遮罩里拖选 / 缩放 / 标注 / 文字 → 工具条
            OverlayForm ov = new OverlayForm(shot, vs);
            try
            {
                ov.ShowDialog();
                outcome = ov.Outcome;
                result = ov.Result;
                ov.Result = null;      // 成品图交由本流程释放
            }
            finally { ov.Dispose(); }

            if (result != null)
            {
                if (outcome == ShotOutcome.AutoSave)
                {
                    AutoSave(result);
                    try { Clipboard.SetImage(result); } catch (Exception ex) { L("EX clp " + ex); }
                }
                result.Dispose();
            }
        }
        catch (Exception ex) { L("EX " + ex); }
        finally
        {
            if (shot != null) shot.Dispose();
            busy = false;
            if (wasVisible && widget != null) widget.Show();
        }
    }

    // ✓ 直接存到默认目录并复制到剪贴板
    static void AutoSave(Bitmap bmp)
    {
        try
        {
            string dir = EffectiveSaveDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) { L("EX autosave " + ex); }
    }

    static void SaveAsDialog(Bitmap bmp)
    {
        using (SaveFileDialog dlg = new SaveFileDialog())
        {
            dlg.Title = "保存截图";
            dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
            dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            try
            {
                string dir = EffectiveSaveDir;
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            catch { }
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                ImageFormat fmt = dlg.FileName.ToLower().EndsWith(".jpg") ? ImageFormat.Jpeg : ImageFormat.Png;
                bmp.Save(dlg.FileName, fmt);
                SaveDir = Path.GetDirectoryName(dlg.FileName);
                SaveSettings();
            }
            catch (Exception ex) { L("EX saveas " + ex); }
        }
    }
}

// 多屏共享的选择状态
class SelState
{
    public readonly Bitmap Shot;
    public readonly Rectangle VS;
    public readonly List<SelectionOverlay> Windows = new List<SelectionOverlay>();
    public Point Anchor;           // 虚拟屏坐标起点
    public Rectangle Sel = Rectangle.Empty;
    public bool Dragging;
    public bool Done;

    // 就地编辑阶段
    public bool Editing;
    public bool Finished;
    public bool Canceled;
    public bool CopyOnly;
    public bool SaveAs;
    public string Tool = "rect";
    public Bitmap Edit;
    public Bitmap Preview;
    public readonly List<Bitmap> Undo = new List<Bitmap>();
    public ShotToolbar Toolbar;

    public SelState(Bitmap shot, Rectangle vs) { Shot = shot; VS = vs; }

    public Rectangle SelScreen()
    {
        return new Rectangle(Sel.X + VS.X, Sel.Y + VS.Y, Sel.Width, Sel.Height);
    }

    public void PushUndo()
    {
        if (Edit == null) return;
        Undo.Add(new Bitmap(Edit));
        if (Undo.Count > 12)
        {
            try { Undo[0].Dispose(); } catch { }
            Undo.RemoveAt(0);
        }
    }

    public void UndoEdit()
    {
        if (Undo.Count == 0) return;
        Bitmap prev = Undo[Undo.Count - 1];
        Undo.RemoveAt(Undo.Count - 1);
        ClearPreview();
        if (Edit != null) { try { Edit.Dispose(); } catch { } }
        Edit = prev;
        Refresh();
    }

    public void ClearPreview()
    {
        if (Preview != null) { try { Preview.Dispose(); } catch { } Preview = null; }
    }

    public void FreeEdit()
    {
        ClearPreview();
        foreach (Bitmap b in Undo) { try { b.Dispose(); } catch { } }
        Undo.Clear();
        if (Edit != null) { try { Edit.Dispose(); } catch { } Edit = null; }
    }

    public void CloseAll()
    {
        foreach (SelectionOverlay w in Windows) { try { w.Close(); } catch { } }
        Windows.Clear();
    }

    public void Refresh()
    {
        foreach (SelectionOverlay w in Windows) { try { w.Invalidate(); } catch { } }
    }

    // 只重绘变化区域，拖动时保持顺滑
    public void Refresh(Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            if (r.Width == 0 && r.Height == 0) return;
        }
        Rectangle rr = r;
        rr.Inflate(4, 4);
        foreach (SelectionOverlay w in Windows)
        {
            try
            {
                Rectangle local = new Rectangle(rr.X - (w.Bounds.X - VS.X), rr.Y - (w.Bounds.Y - VS.Y), rr.Width, rr.Height);
                Rectangle inter = Rectangle.Intersect(local, w.ClientRectangle);
                if (inter.Width > 0 && inter.Height > 0) w.Invalidate(inter);
            }
            catch { }
        }
    }
}

// 每个显示器一块遮罩窗口
class SelectionOverlay : Form
{
    readonly SelState st;
    readonly Rectangle own;
    readonly Bitmap baseShot;
    readonly Bitmap dimmed;

    public SelectionOverlay(SelState state, Rectangle bounds)
    {
        st = state;
        own = bounds;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.Black;

        int ox = own.X - st.VS.X, oy = own.Y - st.VS.Y;
        // 本屏原图
        baseShot = new Bitmap(own.Width, own.Height, PixelFormat.Format32bppArgb);
        using (Graphics gb = Graphics.FromImage(baseShot))
        {
            gb.InterpolationMode = InterpolationMode.NearestNeighbor;
            gb.PixelOffsetMode = PixelOffsetMode.Half;
            gb.DrawImage(st.Shot, new Rectangle(0, 0, own.Width, own.Height),
                new Rectangle(ox, oy, own.Width, own.Height), GraphicsUnit.Pixel);
        }
        // 一次性烘焙暗化图，交给窗口背景，避免每帧重复合成
        dimmed = new Bitmap(own.Width, own.Height, PixelFormat.Format32bppArgb);
        using (Graphics gd = Graphics.FromImage(dimmed))
        {
            gd.DrawImageUnscaled(baseShot, 0, 0);
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(95, 0, 0, 0)))
                gd.FillRectangle(dim, 0, 0, own.Width, own.Height);
        }
        BackgroundImage = dimmed;
        BackgroundImageLayout = ImageLayout.None;
    }

    // 就地编辑拖拽状态
    bool drawing;
    int handle = -1;
    Point downPt;
    Rectangle sel0;
    Point p0;
    Point lastSel;

    static readonly Color Accent = Color.FromArgb(240, 68, 56);   // 标注颜色

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintCore(e.Graphics); }
        catch (Exception ex) { Shot.L("EX paint " + ex.Message + " sel=" + st.Sel); }
    }

    void PaintCore(Graphics g)
    {
        int ox = own.X - st.VS.X, oy = own.Y - st.VS.Y;

        if (st.Sel.Width <= 0 || st.Sel.Height <= 0) return;

        Rectangle local = new Rectangle(st.Sel.X - ox, st.Sel.Y - oy, st.Sel.Width, st.Sel.Height);
        Rectangle inter = Rectangle.Intersect(local, ClientRectangle);

        Bitmap src = st.Preview != null ? st.Preview : st.Edit;

        if (inter.Width > 0 && inter.Height > 0)
        {
            if (st.Editing && src != null)
            {
                // 编辑内容按比例映射到选区（拖控制点后位图尺寸与选区可能不一致）
                float rx = src.Width / (float)Math.Max(1, local.Width);
                float ry = src.Height / (float)Math.Max(1, local.Height);
                Rectangle s = new Rectangle(
                    (int)Math.Round((inter.X - local.X) * rx),
                    (int)Math.Round((inter.Y - local.Y) * ry),
                    Math.Max(1, (int)Math.Round(inter.Width * rx)),
                    Math.Max(1, (int)Math.Round(inter.Height * ry)));
                s.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                if (s.Width > 0 && s.Height > 0) g.DrawImage(src, inter, s, GraphicsUnit.Pixel);
            }
            else
            {
                // baseShot 的像素坐标就是本屏客户区坐标，源矩形必须与目标矩形相同
                g.DrawImage(baseShot, inter, inter, GraphicsUnit.Pixel);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (st.Editing)
        {
            using (Pen p = new Pen(Color.FromArgb(59, 130, 246), 1))
                g.DrawRectangle(p, local);
            int[][] hp = HandlePoints(local);
            for (int i = 0; i < hp.Length; i++)
            {
                int hx = hp[i][0] - 3, hy = hp[i][1] - 3;
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, hx, hy, 7, 7);
                using (Pen p = new Pen(Color.FromArgb(150, 90, 130, 190), 1)) g.DrawEllipse(p, hx, hy, 7, 7);
            }
            DrawSizeTag(g, local);
        }
        else
        {
            using (Pen p = new Pen(Color.FromArgb(255, 92, 140), 2))
                g.DrawRectangle(p, local);
            DrawSizeTag(g, local);
        }
    }

    void DrawSizeTag(Graphics g, Rectangle local)
    {
        string txt = st.Sel.Width + " × " + st.Sel.Height;
        using (Font f = new Font("Segoe UI", 9f))
        {
            Size sz = TextRenderer.MeasureText(txt, f);
            int x = local.X + 4;
            int y = local.Y - sz.Height - 6;
            if (y < 2) y = local.Y + 6;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(170, 20, 22, 26)))
                g.FillRectangle(bg, x - 3, y - 1, sz.Width + 8, sz.Height + 3);
            g.DrawString(txt, f, Brushes.White, x, y);
        }
    }

    static int[][] HandlePoints(Rectangle r)
    {
        int l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        int mx = r.Left + r.Width / 2, my = r.Top + r.Height / 2;
        return new int[][] {
            new int[]{ l, t }, new int[]{ mx, t }, new int[]{ rr, t },
            new int[]{ rr, my },
            new int[]{ rr, b }, new int[]{ mx, b }, new int[]{ l, b },
            new int[]{ l, my }
        };
    }

    static int HitHandle(Rectangle local, Point p)
    {
        int[][] hp = HandlePoints(local);
        for (int i = 0; i < hp.Length; i++)
            if (Math.Abs(p.X - hp[i][0]) <= 6 && Math.Abs(p.Y - hp[i][1]) <= 6) return i;
        return -1;
    }

    // 局部坐标 → 选区位图坐标
    Point ToSel(Point local)
    {
        int ox = own.X - st.VS.X, oy = own.Y - st.VS.Y;
        return new Point(local.X - (st.Sel.X - ox), local.Y - (st.Sel.Y - oy));
    }

    static Rectangle Norm(Point a, Point b)
    {
        return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    static void DrawArrow(Graphics g, Pen p, Point a, Point b)
    {
        g.DrawLine(p, a, b);
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
        float len = 12f, spread = 0.42f;
        PointF t1 = new PointF(b.X - (float)(len * Math.Cos(ang - spread)), b.Y - (float)(len * Math.Sin(ang - spread)));
        PointF t2 = new PointF(b.X - (float)(len * Math.Cos(ang + spread)), b.Y - (float)(len * Math.Sin(ang + spread)));
        using (SolidBrush br = new SolidBrush(p.Color))
            g.FillPolygon(br, new PointF[] { new PointF(b.X, b.Y), t1, t2 });
    }

    void PaintShape(Graphics g, Point a, Point b)
    {
        Rectangle r = Norm(a, b);
        if (r.Width < 1 || r.Height < 1) return;
        using (Pen p = new Pen(Accent, 2))
        {
            switch (st.Tool)
            {
                case "ellipse": g.DrawEllipse(p, r); break;
                case "arrow": DrawArrow(g, p, a, b); break;
                default: g.DrawRectangle(p, r); break;
            }
        }
    }

    // 拖动实时预览（形状 / 马赛克都渲染在副本上）
    void PreviewRange(Point p)
    {
        st.ClearPreview();
        if (st.Edit == null) return;
        st.Preview = new Bitmap(st.Edit);
        using (Graphics g = Graphics.FromImage(st.Preview))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (st.Tool == "mosaic")
            {
                Rectangle r = Norm(p0, p);
                r.Intersect(new Rectangle(0, 0, st.Preview.Width, st.Preview.Height));
                ShotEditor.Mosaic(st.Preview, r, 10);
            }
            else PaintShape(g, p0, p);
        }
        st.Refresh();
    }

    void CommitPreview()
    {
        if (st.Preview == null) return;
        Bitmap pv = st.Preview;
        st.Preview = null;
        if (st.Edit != null) { try { st.Edit.Dispose(); } catch { } }
        st.Edit = pv;
        st.Refresh();
    }

    void PenTo(Point p)
    {
        if (st.Edit == null) return;
        using (Graphics g = Graphics.FromImage(st.Edit))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Accent, 2.4f)) g.DrawLine(pen, lastSel, p);
        }
        lastSel = p;
        st.Refresh();
    }

    void TextTool(Point local)
    {
        if (st.Edit == null) return;
        Point pt = ToSel(local);
        string s = TextInput.Ask(this, "输入文字");
        if (string.IsNullOrEmpty(s)) return;
        st.PushUndo();
        using (Graphics g = Graphics.FromImage(st.Edit))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (Font f = new Font("Microsoft YaHei", 14f, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Accent))
                g.DrawString(s, f, b, pt.X, pt.Y);
        }
        st.Refresh();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (st.Toolbar != null) st.Toolbar.BringFront();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            if (st.Editing) { st.Canceled = true; st.Finished = true; }
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (st.Editing)
        {
            if (st.Toolbar != null) st.Toolbar.BringFront();
            int ox = own.X - st.VS.X, oy = own.Y - st.VS.Y;
            Rectangle local = new Rectangle(st.Sel.X - ox, st.Sel.Y - oy, st.Sel.Width, st.Sel.Height);

            int h = HitHandle(local, e.Location);
            if (h >= 0)
            {
                handle = h;
                sel0 = local;
                downPt = e.Location;
                return;
            }
            Shot.L("md edit local=" + local + " p=" + e.Location + " hit=-1 tool=" + st.Tool);
            if (local.Contains(e.Location))
            {
                if (st.Tool == "text") { TextTool(e.Location); return; }
                st.PushUndo();
                st.ClearPreview();
                drawing = true;
                p0 = lastSel = ToSel(e.Location);
            }
            return;
        }

        st.Anchor = ToShot(e.Location);
        st.Sel = Rectangle.Empty;
        st.Dragging = true;
        st.Refresh();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (st.Editing)
        {
            if (handle >= 0) { ResizeTo(e.Location); return; }
            if (drawing)
            {
                Point cur = ToSel(e.Location);
                if (st.Tool == "pen") PenTo(cur);
                else PreviewRange(cur);
            }
            return;
        }

        if (!st.Dragging) return;
        UpdateSel(ToShot(e.Location));
    }

    void ResizeTo(Point p)
    {
        int dx = p.X - downPt.X, dy = p.Y - downPt.Y;
        int l = sel0.Left, t = sel0.Top, w = sel0.Width, h = sel0.Height;
        switch (handle)
        {
            case 0: l += dx; t += dy; w -= dx; h -= dy; break;
            case 1: t += dy; h -= dy; break;
            case 2: w += dx; t += dy; h -= dy; break;
            case 3: w += dx; break;
            case 4: w += dx; h += dy; break;
            case 5: h += dy; break;
            case 6: l += dx; w -= dx; h += dy; break;
            case 7: l += dx; w -= dx; break;
        }
        if (w < 24) w = 24;
        if (h < 24) h = 24;

        Rectangle ns = new Rectangle(l + (own.X - st.VS.X), t + (own.Y - st.VS.Y), w, h);
        // Sel 以虚拟屏左上角为原点，因此边界也用该坐标系
        Rectangle vsr = new Rectangle(0, 0, st.VS.Width, st.VS.Height);
        ns = Rectangle.Intersect(ns, vsr);
        st.Sel = ns;
        st.Refresh();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (st.Editing)
        {
            if (handle >= 0)
            {
                handle = -1;
                if (st.Toolbar != null) { st.Toolbar.Place(); st.Toolbar.ShowInfo(); }
                return;
            }
            if (drawing)
            {
                drawing = false;
                if (st.Tool == "pen") { }
                else
                {
                    PreviewRange(ToSel(e.Location));
                    CommitPreview();
                }
            }
            return;
        }

        if (!st.Dragging) return;
        st.Dragging = false;
        UpdateSel(ToShot(e.Location));
        if (st.Sel.Width > 4 && st.Sel.Height > 4) st.Done = true;
        else { st.Sel = Rectangle.Empty; st.Refresh(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (st.Editing)
        {
            if (e.KeyCode == Keys.Escape) { st.Canceled = true; st.Finished = true; }
            else if (e.Control && e.KeyCode == Keys.Z) st.UndoEdit();
            else if (e.Control && e.KeyCode == Keys.C) { st.CopyOnly = true; st.Finished = true; }
            else if (e.Control && e.KeyCode == Keys.S) { st.SaveAs = true; st.Finished = true; }
            return;
        }
        if (e.KeyCode == Keys.Escape) { st.Sel = Rectangle.Empty; st.Done = true; }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BackgroundImage = null;
            try { baseShot.Dispose(); } catch { }
            try { dimmed.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    Point ToShot(Point local)
    {
        return new Point(own.X - st.VS.X + local.X, own.Y - st.VS.Y + local.Y);
    }

    void UpdateSel(Point shotPt)
    {
        Rectangle old = st.Sel;
        int x1 = Math.Min(st.Anchor.X, shotPt.X), y1 = Math.Min(st.Anchor.Y, shotPt.Y);
        int x2 = Math.Max(st.Anchor.X, shotPt.X), y2 = Math.Max(st.Anchor.Y, shotPt.Y);
        st.Sel = new Rectangle(x1, y1, x2 - x1, y2 - y1);
        if (old.Width > 0 && old.Height > 0) st.Refresh(Rectangle.Union(old, st.Sel));
        else st.Refresh(st.Sel);
    }
}

// 截图编辑器
class ShotEditor : Form
{
    Bitmap canvas;
    readonly Stack<Bitmap> undo = new Stack<Bitmap>();
    string tool = "mosaic";
    Point p0;
    bool drawing;
    Point last;
    Panel view;
    PictureBox pic;
    Label hint;
    Bitmap tempBmp;
    static readonly Color Accent = Color.FromArgb(230, 92, 100);

    public ShotEditor(Bitmap crop)
    {
        canvas = crop;
        Text = "截图编辑  " + canvas.Width + " × " + canvas.Height;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.FromArgb(28, 28, 30);
        Size = new Size(Math.Min(canvas.Width + 24, Screen.PrimaryScreen.WorkingArea.Width - 80),
                        Math.Min(canvas.Height + 88, Screen.PrimaryScreen.WorkingArea.Height - 80));

        ToolStrip bar = new ToolStrip();
        bar.GripStyle = ToolStripGripStyle.Hidden;
        bar.Padding = new Padding(4, 2, 4, 2);
        bar.Items.Add(MakeBtn("马赛克", "mosaic"));
        bar.Items.Add(MakeBtn("矩形", "rect"));
        bar.Items.Add(MakeBtn("箭头", "arrow"));
        bar.Items.Add(MakeBtn("画笔", "pen"));
        bar.Items.Add(MakeBtn("文字", "text"));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(MakeBtn("撤销", "undo"));
        bar.Items.Add(MakeBtn("复制", "copy"));
        bar.Items.Add(MakeBtn("保存", "save"));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(MakeBtn("关闭", "close"));
        Controls.Add(bar);

        pic = new PictureBox();
        pic.Image = canvas;
        pic.SizeMode = PictureBoxSizeMode.Normal;
        pic.Size = canvas.Size;
        pic.Location = new Point(0, 0);
        pic.MouseDown += PicDown;
        pic.MouseMove += PicMove;
        pic.MouseUp += PicUp;

        // 底部提示条不再显示，hint 仅保留作内部状态记录
        hint = new Label();
        hint.Dock = DockStyle.Bottom;
        hint.Height = 22;
        hint.Visible = false;

        view = new Panel();
        view.AutoScroll = true;
        view.BackColor = Color.FromArgb(24, 24, 26);
        view.Dock = DockStyle.Fill;
        view.Controls.Add(pic);
        Controls.Add(view);
        view.BringToFront();

        KeyPreview = true;
    }

    ToolStripButton MakeBtn(string text, string cmd)
    {
        ToolStripButton b = new ToolStripButton(text);
        b.DisplayStyle = ToolStripItemDisplayStyle.Text;
        b.Tag = cmd;
        b.Click += delegate { Do(cmd); };
        return b;
    }

    void Do(string cmd)
    {
        switch (cmd)
        {
            case "undo":
                if (undo.Count > 0)
                {
                    Bitmap prev = undo.Pop();
                    Bitmap oldTemp = tempBmp;
                    tempBmp = null;
                    if (oldTemp != null && !ReferenceEquals(oldTemp, canvas)) oldTemp.Dispose();
                    canvas.Dispose();
                    canvas = prev;
                    pic.Image = canvas;
                    pic.Size = canvas.Size;
                    pic.Invalidate();
                    hint.Text = "已撤销";
                }
                return;
            case "copy":
                try { Clipboard.SetImage(canvas); hint.Text = "已复制到剪贴板"; } catch { hint.Text = "复制失败"; }
                return;
            case "save": Save(); return;
            case "close": Close(); return;
        }
        tool = cmd;
        if (tool == "mosaic") hint.Text = "马赛克：拖出要打码的区域";
        else if (tool == "rect") hint.Text = "矩形：拖出标注框";
        else if (tool == "arrow") hint.Text = "箭头：从起点拖到箭头指向的位置";
        else if (tool == "pen") hint.Text = "画笔：按住拖动即可涂画";
        else if (tool == "text") hint.Text = "文字：单击要写字的位置";
    }

    void Save()
    {
        string dir = Shot.EffectiveSaveDir;
        try { Directory.CreateDirectory(dir); } catch { }
        using (SaveFileDialog dlg = new SaveFileDialog())
        {
            dlg.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
            dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    if (dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) canvas.Save(dlg.FileName, ImageFormat.Jpeg);
                    else canvas.Save(dlg.FileName, ImageFormat.Png);
                    hint.Text = "已保存：" + dlg.FileName;
                    // 这次选的目录和之前不同就记住它
                    string picked = Path.GetDirectoryName(dlg.FileName);
                    if (!string.IsNullOrEmpty(picked) && picked != Shot.EffectiveSaveDir)
                    {
                        Shot.SaveDir = picked;
                        Shot.SaveSettings();
                    }
                    try { Clipboard.SetImage(canvas); } catch { }
                }
                catch (Exception ex) { hint.Text = "保存失败：" + ex.Message; }
            }
        }
    }

    void PushUndo()
    {
        undo.Push(new Bitmap(canvas));
        if (undo.Count > 10)
        {
            Bitmap[] arr = new Bitmap[undo.Count];
            int i = 0;
            foreach (Bitmap b in undo) arr[i++] = b;
            undo.Clear();
            for (int k = 1; k < arr.Length; k++) undo.Push(arr[k]);
        }
    }

    void PicDown(object s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (tool == "text")
        {
            string txt = TextInput.Ask(this, "输入文字");
            if (!string.IsNullOrEmpty(txt))
            {
                PushUndo();
                using (Graphics g = Graphics.FromImage(canvas))
                using (Font f = new Font("Microsoft YaHei", 12f, FontStyle.Bold))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawString(txt, f, Brushes.Black, e.X + 2, e.Y + 2);
                    g.DrawString(txt, f, new SolidBrush(Accent), e.X, e.Y);
                }
                pic.Invalidate();
                hint.Text = "文字已添加";
            }
            return;
        }
        drawing = true; p0 = e.Location; last = e.Location;
        PushUndo();
    }

    void PicMove(object s, MouseEventArgs e)
    {
        if (!drawing) return;
        if (tool == "pen")
        {
            using (Graphics g = Graphics.FromImage(canvas))
            using (Pen p = new Pen(Accent, 3))
                g.DrawLine(p, last, e.Location);
            last = e.Location;
            pic.Invalidate();
            return;
        }
        if (tool == "mosaic")
        {
            // 拖动时实时预览打码效果
            Rectangle r = Norm(p0, e.Location);
            if (r.Width < 3 || r.Height < 3) return;
            Bitmap b0 = undo.Count > 0 ? undo.Peek() : canvas;
            Bitmap temp = new Bitmap(b0);
            Mosaic(temp, r, 10);
            ShowTemp(temp);
            return;
        }
        DrawPreview(e.Location, false);
    }

    void ShowTemp(Bitmap temp)
    {
        Bitmap old = tempBmp;
        tempBmp = temp;
        pic.Image = temp;
        pic.Invalidate();
        if (old != null && !ReferenceEquals(old, canvas)) old.Dispose();
    }

    void ResetPic()
    {
        Bitmap old = tempBmp;
        tempBmp = null;
        pic.Image = canvas;
        if (old != null && !ReferenceEquals(old, canvas)) old.Dispose();
        pic.Invalidate();
    }

    void PicUp(object s, MouseEventArgs e)
    {
        if (!drawing) return;
        drawing = false;
        if (tool == "mosaic")
        {
            Rectangle r = Norm(p0, e.Location);
            if (r.Width > 2 && r.Height > 2) Mosaic(canvas, r, 10);
            ResetPic();
            hint.Text = "马赛克已应用（Ctrl+Z 可撤销）";
        }
        else DrawPreview(e.Location, true);
    }

    static Rectangle Norm(Point a, Point b)
    {
        return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    void DrawPreview(Point cur, bool commit)
    {
        if (commit)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(Accent, 2.5f))
                {
                    if (tool == "rect") g.DrawRectangle(p, Norm(p0, cur));
                    else if (tool == "arrow") DrawArrow(g, p, p0, cur);
                }
            }
            pic.Image = canvas;
            pic.Invalidate();
            hint.Text = "已应用（Ctrl+Z 可撤销）";
            return;
        }

        Bitmap baseBmp = undo.Count > 0 ? undo.Peek() : canvas;
        Bitmap temp = new Bitmap(baseBmp);
        using (Graphics g = Graphics.FromImage(temp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Accent, 2.5f))
            {
                if (tool == "rect") g.DrawRectangle(p, Norm(p0, cur));
                else if (tool == "arrow") DrawArrow(g, p, p0, cur);
            }
        }
        ShowTemp(temp);
    }

    static void DrawArrow(Graphics g, Pen p, Point a, Point b)
    {
        g.DrawLine(p, a, b);
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
        double len = 12, spread = 0.45;
        PointF t1 = new PointF((float)(b.X - len * Math.Cos(ang - spread)), (float)(b.Y - len * Math.Sin(ang - spread)));
        PointF t2 = new PointF((float)(b.X - len * Math.Cos(ang + spread)), (float)(b.Y - len * Math.Sin(ang + spread)));
        g.DrawLine(p, b, Point.Round(t1));
        g.DrawLine(p, b, Point.Round(t2));
        using (SolidBrush br = new SolidBrush(p.Color))
            g.FillPolygon(br, new PointF[] { new PointF(b.X, b.Y), t1, t2 });
    }

    public static void Mosaic(Bitmap bmp, Rectangle r, int block)
    {
        r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width < 2 || r.Height < 2) return;
        int bw = Math.Max(1, r.Width / block), bh = Math.Max(1, r.Height / block);
        using (Bitmap small = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, new Rectangle(0, 0, bw, bh), r, GraphicsUnit.Pixel);
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, r, new Rectangle(0, 0, bw, bh), GraphicsUnit.Pixel);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.Control && e.KeyCode == Keys.S) Save();
        else if (e.Control && e.KeyCode == Keys.C) Do("copy");
        else if (e.Control && e.KeyCode == Keys.Z) Do("undo");
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (tempBmp != null) { try { tempBmp.Dispose(); } catch { } }
        while (undo.Count > 0) { try { undo.Pop().Dispose(); } catch { } }
        base.OnFormClosed(e);
    }
}

// 简易文字输入框
class TextInput : Form
{
    TextBox box;
    public string Value { get; private set; }

    TextInput(string caption)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(320, 92);
        TopMost = true;
        BackColor = Color.FromArgb(40, 40, 44);

        box = new TextBox();
        box.SetBounds(12, 12, 296, 24);
        box.Font = new Font("Microsoft YaHei", 10f);
        Controls.Add(box);

        Button ok = new Button();
        ok.Text = "确定"; ok.SetBounds(170, 52, 74, 26);
        ok.Click += delegate { Value = box.Text; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "取消"; cancel.SetBounds(250, 52, 58, 26);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += delegate { box.Focus(); };
    }

    public static string Ask(IWin32Window owner, string caption)
    {
        using (TextInput dlg = new TextInput(caption))
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Value : null;
    }
}

// 截图快捷键设置
class HotkeyDialog : Form
{
    public uint Mod;
    public byte Vk;
    CheckBox cbCtrl, cbAlt, cbShift;
    ComboBox keys;

    public HotkeyDialog()
    {
        Text = "截图快捷键";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(320, 168);
        TopMost = true;
        BackColor = Color.FromArgb(40, 40, 44);
        ForeColor = Color.FromArgb(235, 235, 235);

        Label tip = new Label();
        tip.Text = "选择组合键（至少一个修饰键）";
        tip.AutoSize = true;
        tip.Location = new Point(14, 12);
        Controls.Add(tip);

        cbCtrl = new CheckBox(); cbCtrl.Text = "Ctrl"; cbCtrl.AutoSize = true;
        cbCtrl.Location = new Point(16, 44); cbCtrl.ForeColor = ForeColor;
        cbAlt = new CheckBox(); cbAlt.Text = "Alt"; cbAlt.AutoSize = true;
        cbAlt.Location = new Point(96, 44); cbAlt.ForeColor = ForeColor;
        cbShift = new CheckBox(); cbShift.Text = "Shift"; cbShift.AutoSize = true;
        cbShift.Location = new Point(176, 44); cbShift.ForeColor = ForeColor;
        Controls.Add(cbCtrl); Controls.Add(cbAlt); Controls.Add(cbShift);

        keys = new ComboBox();
        keys.DropDownStyle = ComboBoxStyle.DropDownList;
        keys.SetBounds(14, 84, 290, 26);
        for (char c = 'A'; c <= 'Z'; c++) keys.Items.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) keys.Items.Add(c.ToString());
        for (int i = 1; i <= 12; i++) keys.Items.Add("F" + i);
        Controls.Add(keys);

        Button ok = new Button();
        ok.Text = "确定"; ok.SetBounds(170, 126, 74, 26);
        ok.Click += delegate { OnOk(); };
        Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "取消"; cancel.SetBounds(250, 126, 58, 26);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        cbCtrl.Checked = (Shot.Mods & 0x0002) != 0;
        cbAlt.Checked = (Shot.Mods & 0x0001) != 0;
        cbShift.Checked = (Shot.Mods & 0x0004) != 0;
        string kn = VkText(Shot.Vk);
        keys.SelectedItem = keys.Items.Contains(kn) ? kn : "S";
        if (keys.SelectedIndex < 0) keys.SelectedIndex = 0;
    }

    static string VkText(byte vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return "S";
    }

    void OnOk()
    {
        uint m = 0;
        if (cbCtrl.Checked) m |= 0x0002;
        if (cbAlt.Checked) m |= 0x0001;
        if (cbShift.Checked) m |= 0x0004;
        if (m == 0)
        {
            MessageBox.Show(this, "至少要勾选一个修饰键。", "截图快捷键");
            return;
        }
        string k = (string)keys.SelectedItem;
        byte vk;
        if (k.Length == 1) vk = (byte)k[0];
        else vk = (byte)(0x70 + int.Parse(k.Substring(1)) - 1);
        Mod = m; Vk = vk;
        DialogResult = DialogResult.OK;
        Close();
    }
}
