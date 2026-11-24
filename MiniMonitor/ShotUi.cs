using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 截图后原地出现的浮动工具条（深色圆角 + 图标按钮）
class ShotToolbar : Form
{
    readonly SelState st;
    readonly IconButton[] btns;
    readonly int[] seps;

    const int BW = 34;      // 按钮宽
    const int BH = 32;      // 按钮高
    const int PAD = 6;      // 内边距
    const int GAP = 2;      // 按钮间距
    const int SEPW = 11;    // 分隔占位宽

    public ShotToolbar(SelState state)
    {
        st = state;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(38, 40, 44);
        DoubleBuffered = true;
        KeyPreview = true;

        string[] kinds = new string[]
        {
            "rect", "ellipse", "arrow", "pen", "text", "mosaic",
            "|", "undo", "copy", "save", "|", "close", "ok"
        };

        int nb = 0, ns = 0;
        foreach (string k in kinds) { if (k == "|") ns++; else nb++; }

        btns = new IconButton[nb];
        seps = new int[ns];

        int idx = 0, si = 0, x = PAD;
        foreach (string k in kinds)
        {
            if (k == "|") { seps[si++] = x + SEPW / 2; x += SEPW; continue; }

            IconButton b = new IconButton(k);
            b.SetBounds(x, PAD, BW, BH);
            b.Click += delegate { OnIcon((string)b.Tag); };
            Controls.Add(b);
            btns[idx++] = b;
            x += BW + GAP;
        }

        ClientSize = new Size(x - GAP + PAD, BH + PAD * 2);
        SyncChecks();
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOSIZE = 0x0001;

    // 遮罩层被激活后会浮到最前，这里把工具条重新提到顶层
    public void BringFront()
    {
        if (!IsHandleCreated) return;
        try { SetWindowPos(Handle, HWND_TOPMOST, Location.X, Location.Y, Width, Height, SWP_NOACTIVATE | SWP_NOSIZE); } catch { }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    void Round()
    {
        try
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 14, 14);
            SetWindowRgn(Handle, rgn, true);
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Pen p = new Pen(Color.FromArgb(72, 76, 82), 1))
        {
            foreach (int sx in seps)
            {
                if (sx <= 0) continue;
                e.Graphics.DrawLine(p, sx, PAD + 4, sx, Height - PAD - 4);
            }
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Round();
        Place();
    }

    public void SyncChecks()
    {
        if (btns == null) return;
        foreach (IconButton b in btns)
        {
            string k = (string)b.Tag;
            bool on = false;
            if (k == "rect" || k == "ellipse" || k == "arrow" || k == "pen" || k == "text" || k == "mosaic")
                on = (st.Tool == k);
            if (b.Checked != on) { b.Checked = on; b.Invalidate(); }
        }
    }

    // 跟随选区：优先下方居中，放不下就放上方
    public void Place()
    {
        Rectangle sel = st.SelScreen();
        Screen sc = Screen.FromRectangle(sel);
        Rectangle wa = sc.WorkingArea;

        int x = sel.X + (sel.Width - Width) / 2;
        int y = sel.Bottom + 8;
        if (y + Height > wa.Bottom) y = sel.Top - Height - 8;
        if (y < wa.Top) y = Math.Min(sel.Bottom + 8, wa.Bottom - Height);
        if (x + Width > wa.Right) x = wa.Right - Width;
        if (x < wa.Left) x = wa.Left;

        Location = new Point(x, y);
        BringFront();
    }

    // 尺寸提示画在遮罩层上，这里只触发重绘
    public void ShowInfo()
    {
        st.Refresh();
    }

    void OnIcon(string k)
    {
        switch (k)
        {
            case "undo": st.UndoEdit(); break;
            case "copy": st.CopyOnly = true; st.Finished = true; break;
            case "save": st.SaveAs = true; st.Finished = true; break;
            case "close": st.Canceled = true; st.Finished = true; break;
            case "ok": st.Finished = true; break;
            default:
                st.Tool = k;
                SyncChecks();
                st.Refresh();
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { st.Canceled = true; st.Finished = true; }
        else if (e.Control && e.KeyCode == Keys.Z) st.UndoEdit();
        else if (e.Control && e.KeyCode == Keys.C) { st.CopyOnly = true; st.Finished = true; }
        else if (e.Control && e.KeyCode == Keys.S) { st.SaveAs = true; st.Finished = true; }
        base.OnKeyDown(e);
    }
}

// 单个图标按钮
class IconButton : Control
{
    public bool Checked;
    bool hover;

    public IconButton(string kind)
    {
        Tag = kind;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.FromArgb(38, 40, 44);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle;

        if (Checked)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(59, 130, 246)))
            using (GraphicsPath gp = RoundRect(r, 6))
                g.FillPath(b, gp);
        }
        else if (hover)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(62, 66, 72)))
            using (GraphicsPath gp = RoundRect(r, 6))
                g.FillPath(b, gp);
        }

        Color c = Checked ? Color.White : Color.FromArgb(212, 216, 222);
        ShotIcons.Draw(g, (string)Tag, r, c);
    }

    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        GraphicsPath gp = new GraphicsPath();
        int d = rad * 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }
}

// 矢量图标
static class ShotIcons
{
    public static void Draw(Graphics g, string kind, Rectangle box, Color c)
    {
        // 关闭用红色、确定用绿色
        if (kind == "close") c = Color.FromArgb(240, 82, 82);
        else if (kind == "ok") c = Color.FromArgb(52, 199, 89);

        int cx = box.Width / 2, cy = box.Height / 2;
        int s = 9;
        using (Pen p = new Pen(c, 1.7f))
        using (SolidBrush b = new SolidBrush(c))
        {
            switch (kind)
            {
                case "rect":
                    g.DrawRectangle(p, cx - s, cy - s + 1, s * 2, s * 2 - 2);
                    break;
                case "ellipse":
                    g.DrawEllipse(p, cx - s, cy - s + 1, s * 2, s * 2 - 2);
                    break;
                case "arrow":
                    {
                        Point a = new Point(cx - s, cy + s - 1);
                        Point d = new Point(cx + s, cy - s + 1);
                        g.DrawLine(p, a, d);
                        double ang = Math.Atan2(d.Y - a.Y, d.X - a.X);
                        PointF t1 = new PointF((float)(d.X - 7 * Math.Cos(ang - 0.5)), (float)(d.Y - 7 * Math.Sin(ang - 0.5)));
                        PointF t2 = new PointF((float)(d.X - 7 * Math.Cos(ang + 0.5)), (float)(d.Y - 7 * Math.Sin(ang + 0.5)));
                        g.DrawLine(p, d, Point.Round(t1));
                        g.DrawLine(p, d, Point.Round(t2));
                    }
                    break;
                case "pen":
                    {
                        using (Pen pw = new Pen(c, 2.6f)) g.DrawLine(pw, cx - s + 1, cy + s - 1, cx + s - 2, cy - s + 3);
                        g.FillEllipse(b, cx + s - 4, cy - s, 4, 4);
                    }
                    break;
                case "text":
                    {
                        // 粗体 T：横杠两端略下垂，竖干贯穿
                        g.FillRectangle(b, cx - 10, cy - 9, 21, 3);
                        g.FillRectangle(b, cx - 10, cy - 9, 3, 9);
                        g.FillRectangle(b, cx + 8, cy - 9, 3, 9);
                        g.FillRectangle(b, cx - 2, cy - 6, 4, 18);
                    }
                    break;
                case "mosaic":
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            if ((i + j) % 2 == 0)
                                g.FillRectangle(b, cx - s + i * 6, cy - s + 1 + j * 6, 5, 5);
                    break;
                case "undo":
                    {
                        // 环形回退箭头：自右侧经底绕到左上，箭头指左上
                        Rectangle arc = new Rectangle(cx - 8, cy - 7, 16, 16);
                        using (Pen pp = new Pen(c, 2f)) g.DrawArc(pp, arc, 0, 196);
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(cx - 10, cy - 9),
                            new PointF(cx - 13, cy),
                            new PointF(cx - 3, cy)
                        });
                    }
                    break;
                case "copy":
                    using (Pen p2 = new Pen(c, 1.4f))
                    {
                        g.DrawRectangle(p2, cx - s, cy - s + 4, s * 2 - 6, s * 2 - 6);
                        g.DrawRectangle(p2, cx - s + 6, cy - s, s * 2 - 6, s * 2 - 6);
                    }
                    break;
                case "save":
                    {
                        g.DrawLine(p, cx, cy - s, cx, cy + s - 4);
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(cx, cy + s - 1),
                            new PointF(cx - 5, cy + s - 8),
                            new PointF(cx + 5, cy + s - 8)
                        });
                        using (Pen p3 = new Pen(c, 2f)) g.DrawLine(p3, cx - s, cy + s + 2, cx + s, cy + s + 2);
                    }
                    break;
                case "close":
                    g.DrawLine(p, cx - s + 1, cy - s + 1, cx + s - 1, cy + s - 1);
                    g.DrawLine(p, cx + s - 1, cy - s + 1, cx - s + 1, cy + s - 1);
                    break;
                case "ok":
                    using (Pen p4 = new Pen(c, 2f))
                    {
                        g.DrawLine(p4, cx - s - 1, cy, cx - 2, cy + s - 2);
                        g.DrawLine(p4, cx - 2, cy + s - 2, cx + s + 1, cy - s + 1);
                    }
                    break;
            }
        }
    }
}
