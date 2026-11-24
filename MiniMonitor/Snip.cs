// ============================================================
// 截图内核移植自 wuwei-io/wuwei-shot (src/Snip.cs, v1.x)
// MIT License, Copyright (c) 2026 KehuiPang
// 本项目仅去除了其托盘/热键外壳，改为嵌入 MiniMonitor 的挂件与热键。
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

    // 截图结束方式：取消 / 另存为 / 存盘并复制
    public enum ShotOutcome { Cancel, Save, AutoSave }

    // 标注类型
    enum Tool { None, Arrow, Line, Rect, Ellipse, Text, Mosaic }

    // 一条标注
    class Anno
    {
        public Tool Type;
        public Point A;
        public Point B;
        public string Text;
        public Color Color;
        public float Width = 3f;
    }

    // 全屏选区遮罩：拖框 -> 可选标注(箭头/直线/方框) -> 打勾复制到剪贴板
    public class OverlayForm : Form
    {
        public ShotOutcome Outcome = ShotOutcome.Cancel;
        public Bitmap Result = null;      // ✓ 结束时渲染出的成品，由外壳接管
        Bitmap _full;          // 冻结的屏幕图
        Rectangle _vs;         // 虚拟屏幕范围（含负坐标偏移）
        Point _start;
        Rectangle _sel = Rectangle.Empty;
        bool _dragging = false;   // 正在拖框选
        bool _hasSel = false;

        Tool _tool = Tool.None;
        readonly List<Anno> _annos = new List<Anno>();
        Anno _drawing = null;     // 正在画的标注
        bool _annotating = false;

        // 选框的移动/缩放（仅未选标注工具时）
        bool _moving = false, _resizing = false;
        int _handle = -1;         // 0 TL 1 TC 2 TR 3 RC 4 BR 5 BC 6 BL 7 LC
        Point _dragOrigin;
        Rectangle _selOrig;
        const int HS = 8;         // 控制点大小

        // 工具条按钮：id 1箭头 2直线 3方框 10椭圆 7文字 8马赛克 4撤销 9保存 5取消 6确认
        int _hover = 0;
        Rectangle _bar = Rectangle.Empty;
        readonly int[] _ids = { 1, 2, 3, 10, 7, 8, 4, 9, 5, 6 };
        readonly Rectangle[] _rects = new Rectangle[10];
        const int BTN = 42;

        // 颜色 / 粗细（样式子条，选了可着色工具时出现）
        Color _color = Color.FromArgb(0xfa, 0x51, 0x51);
        float _width = 3f;
        static readonly Color[] PALETTE = {
            Color.FromArgb(0xfa,0x51,0x51), // 红
            Color.FromArgb(0xf4,0xb7,0x40), // 金
            Color.FromArgb(0xe0,0x76,0x2a), // 橙
            Color.FromArgb(0x2e,0xbe,0x6e), // 绿
            Color.FromArgb(0x3b,0x82,0xf6), // 蓝
            Color.White,                    // 白
            Color.FromArgb(0x22,0x22,0x22), // 黑
        };
        static readonly float[] WIDTHS = { 2f, 4f, 7f };
        bool _showStyle = false;
        int _widthCount = 0;
        Rectangle _styleBar = Rectangle.Empty;
        readonly Rectangle[] _colorRects = new Rectangle[7];
        readonly Rectangle[] _widthRects = new Rectangle[3];
        const int SUB = 30;

        // 文字标注：用真输入框（保证中文输入法可用）。底色取自点击处背后的原图，
        // 在纯色背景上看着就是透明；宽度随字数自动伸缩，不留长条。
        static readonly Font AnnoFont = new Font("Microsoft YaHei", 13.5f);
        TextBox _tb = null;

        // 暖琥珀 VI
        static readonly Color C_GOLD   = Color.FromArgb(0xf4, 0xb7, 0x40);
        static readonly Color C_TEXT   = Color.FromArgb(0xf3, 0xec, 0xe0);
        static readonly Color C_TEXT2  = Color.FromArgb(0xb0, 0x9b, 0x80);
        static readonly Color C_DEEP   = Color.FromArgb(0x0e, 0x0a, 0x06);
        static readonly Color C_ANNO   = Color.FromArgb(0xfa, 0x51, 0x51); // 标注红（最醒目，指哪打哪）
        static readonly Color C_RED    = Color.FromArgb(0xe8, 0x50, 0x3c); // 取消 ×
        static readonly Color C_GREEN  = Color.FromArgb(0x35, 0xc4, 0x8a); // 完成 ✓
        const float ANNO_W = 3f;

        public OverlayForm(Bitmap full, Rectangle vs)
        {
            _full = full;
            _vs = vs;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = new Rectangle(0, 0, vs.Width, vs.Height);
            this.Location = vs.Location; // 覆盖整个虚拟屏幕
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.Cursor = Cursors.Cross;
            this.DoubleBuffered = true;
            this.KeyPreview = true;
            this.BackColor = Color.Black;

            this.MouseDown += OnDown;
            this.MouseMove += OnMove;
            this.MouseUp += OnUp;
            this.KeyDown += OnKey;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            // 正在打字：Enter 提交、Esc 取消这段文字，其它键交给输入框
            if (_tb != null)
            {
                if (e.KeyCode == Keys.Escape) { CancelText(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { CommitText(); e.SuppressKeyPress = true; }
                return;
            }
            if (e.KeyCode == Keys.Escape) this.Close();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
            else if (e.Control && e.KeyCode == Keys.Z && _annos.Count > 0)
            {
                _annos.RemoveAt(_annos.Count - 1);
                this.Invalidate();
            }
        }

        int HitButton(Point p)
        {
            if (!_hasSel) return 0;
            for (int i = 0; i < _rects.Length; i++)
                if (_rects[i].Contains(p)) return _ids[i];
            return 0;
        }

        // 8 个控制点（0 TL 1 TC 2 TR 3 RC 4 BR 5 BC 6 BL 7 LC）
        Point[] HandlePoints()
        {
            int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
            int cx = l + _sel.Width / 2, cy = t + _sel.Height / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t),
                new Point(r, cy), new Point(r, b), new Point(cx, b),
                new Point(l, b), new Point(l, cy)
            };
        }

        Rectangle HandleRect(Point p) { return new Rectangle(p.X - HS / 2, p.Y - HS / 2, HS, HS); }

        int HitHandle(Point pt)
        {
            if (!_hasSel) return -1;
            var pts = HandlePoints();
            for (int i = 0; i < 8; i++)
            {
                var hr = HandleRect(pts[i]); hr.Inflate(3, 3);
                if (hr.Contains(pt)) return i;
            }
            return -1;
        }

        Cursor HandleCursor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE; // 3, 7
            }
        }

        Rectangle ResizeRect(Rectangle o, int h, Point pt)
        {
            int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
            switch (h)
            {
                case 0: l = pt.X; t = pt.Y; break;
                case 1: t = pt.Y; break;
                case 2: r = pt.X; t = pt.Y; break;
                case 3: r = pt.X; break;
                case 4: r = pt.X; b = pt.Y; break;
                case 5: b = pt.Y; break;
                case 6: l = pt.X; b = pt.Y; break;
                case 7: l = pt.X; break;
            }
            l = Math.Max(0, Math.Min(l, this.Width)); r = Math.Max(0, Math.Min(r, this.Width));
            t = Math.Max(0, Math.Min(t, this.Height)); b = Math.Max(0, Math.Min(b, this.Height));
            return new Rectangle(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
        }

        void DrawHandles(Graphics g)
        {
            foreach (var p in HandlePoints())
            {
                var hr = HandleRect(p);
                using (var b = new SolidBrush(C_GOLD)) g.FillRectangle(b, hr);
                using (var pen = new Pen(Color.FromArgb(180, C_DEEP), 1f)) g.DrawRectangle(pen, hr);
            }
        }

        void OnDown(object s, MouseEventArgs e)
        {
            // 右键取消：正在打字→取消这段字；已框选→退回未框选可重画；未框选→关闭
            if (e.Button == MouseButtons.Right)
            {
                if (_tb != null) { CancelText(); return; }
                if (_hasSel || _dragging)
                {
                    _dragging = _moving = _resizing = _annotating = false;
                    _drawing = null; _handle = -1;
                    _hasSel = false; _sel = Rectangle.Empty;
                    _tool = Tool.None; _annos.Clear(); _hover = 0;
                    this.Cursor = Cursors.Cross;
                    this.Invalidate();
                    return;
                }
                this.Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 点击别处先把正在输入的文字落定
            if (_tb != null) CommitText();

            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id != 0) { HandleButton(id); return; }

                // 颜色 / 粗细子条
                if (HandleStyleClick(e.Location)) return;

                // 未选工具时：拖角缩放 / 框内移动
                if (_tool == Tool.None)
                {
                    int hd = HitHandle(e.Location);
                    if (hd >= 0)
                    {
                        _resizing = true; _handle = hd; _selOrig = _sel;
                        return;
                    }
                    if (_sel.Contains(e.Location))
                    {
                        _moving = true; _dragOrigin = e.Location; _selOrig = _sel;
                        return;
                    }
                }
                // 文字工具：在点击处开一个输入框
                if (_tool == Tool.Text && _sel.Contains(e.Location))
                {
                    BeginText(e.Location);
                    return;
                }
                // 其它标注工具（箭头/直线/方框/马赛克）：拖动画形状
                if (_tool != Tool.None && _tool != Tool.Text && _sel.Contains(e.Location))
                {
                    _annotating = true;
                    _drawing = new Anno { Type = _tool, A = e.Location, B = e.Location, Color = _color, Width = _width };
                    return;
                }
                // 否则重新框选（清掉旧标注）
            }

            _dragging = true;
            _hasSel = false;
            _tool = Tool.None;
            _annos.Clear();
            _hover = 0;
            this.Cursor = Cursors.Cross;
            _start = e.Location;
            _sel = new Rectangle(e.Location, Size.Empty);
            this.Invalidate();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (_dragging)
            {
                int x = Math.Min(_start.X, e.X);
                int y = Math.Min(_start.Y, e.Y);
                _sel = new Rectangle(x, y, Math.Abs(_start.X - e.X), Math.Abs(_start.Y - e.Y));
                this.Invalidate();
                return;
            }
            if (_moving)
            {
                var r = _selOrig;
                r.X = Math.Max(0, Math.Min(_selOrig.X + (e.X - _dragOrigin.X), this.Width - r.Width));
                r.Y = Math.Max(0, Math.Min(_selOrig.Y + (e.Y - _dragOrigin.Y), this.Height - r.Height));
                _sel = r; LayoutButtons(); this.Invalidate();
                return;
            }
            if (_resizing)
            {
                _sel = ResizeRect(_selOrig, _handle, e.Location); LayoutButtons(); this.Invalidate();
                return;
            }
            if (_annotating)
            {
                _drawing.B = e.Location;
                this.Invalidate();
                return;
            }
            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id != 0) this.Cursor = Cursors.Hand;
                else if (_showStyle && _styleBar.Contains(e.Location)) this.Cursor = Cursors.Hand;
                else if (_tool == Tool.None)
                {
                    int hd = HitHandle(e.Location);
                    if (hd >= 0) this.Cursor = HandleCursor(hd);
                    else if (_sel.Contains(e.Location)) this.Cursor = Cursors.SizeAll;
                    else this.Cursor = Cursors.Cross;
                }
                else if (_tool == Tool.Text && _sel.Contains(e.Location)) this.Cursor = Cursors.IBeam;
                else this.Cursor = Cursors.Cross;
                if (id != _hover) { _hover = id; this.Invalidate(); }
            }
        }

        void OnUp(object s, MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                if (_sel.Width >= 3 && _sel.Height >= 3) { _hasSel = true; LayoutButtons(); }
                else { _sel = Rectangle.Empty; _hasSel = false; }
                this.Invalidate();
                return;
            }
            if (_moving || _resizing)
            {
                _moving = false; _resizing = false; _handle = -1;
                LayoutButtons();
                this.Invalidate();
                return;
            }
            if (_annotating)
            {
                _annotating = false;
                int dx = _drawing.B.X - _drawing.A.X, dy = _drawing.B.Y - _drawing.A.Y;
                if (dx * dx + dy * dy >= 16) _annos.Add(_drawing);   // 太短的忽略
                _drawing = null;
                this.Invalidate();
            }
        }

        void HandleButton(int id)
        {
            switch (id)
            {
                case 1: _tool = (_tool == Tool.Arrow) ? Tool.None : Tool.Arrow; break;
                case 2: _tool = (_tool == Tool.Line) ? Tool.None : Tool.Line; break;
                case 3: _tool = (_tool == Tool.Rect) ? Tool.None : Tool.Rect; break;
                case 10: _tool = (_tool == Tool.Ellipse) ? Tool.None : Tool.Ellipse; break;
                case 7: _tool = (_tool == Tool.Text) ? Tool.None : Tool.Text; break;
                case 8: _tool = (_tool == Tool.Mosaic) ? Tool.None : Tool.Mosaic; break;
                case 4: if (_annos.Count > 0) _annos.RemoveAt(_annos.Count - 1); break;
                case 9: SavePng(); return;
                case 5: this.Close(); return;
                case 6: Confirm(); return;
            }
            LayoutButtons();   // 工具变了：重排工具条 + 样式子条并夹进屏幕
            this.Invalidate();
        }

        void LayoutButtons()
        {
            const int GAP = 8;
            int totalW = BTN * _rects.Length;
            Rectangle sc = ScreenClient();
            bool style = (_tool == Tool.Arrow || _tool == Tool.Line || _tool == Tool.Rect
                          || _tool == Tool.Ellipse || _tool == Tool.Text);
            int reserve = style ? (SUB + 6) : 0;   // 给样式子条预留

            // 水平：右对齐选区，夹进屏幕
            int bx = _sel.Right - totalW;
            if (bx + totalW > sc.Right) bx = sc.Right - totalW;
            if (bx < sc.Left) bx = sc.Left;

            // 垂直：先试选区下方 → 放不下试上方 → 再不行放选区内底部；始终留在屏幕内
            int by;
            if (_sel.Bottom + GAP + BTN + reserve <= sc.Bottom) by = _sel.Bottom + GAP;
            else if (_sel.Top - GAP - BTN - reserve >= sc.Top) by = _sel.Top - GAP - BTN - reserve;
            else by = Math.Min(_sel.Bottom - BTN - GAP, sc.Bottom - BTN - reserve - 2);
            if (by < sc.Top + 2) by = sc.Top + 2;

            _bar = new Rectangle(bx, by, totalW, BTN);
            for (int i = 0; i < _rects.Length; i++)
                _rects[i] = new Rectangle(bx + i * BTN, by, BTN, BTN);
            LayoutStyle(sc);
        }

        // 选区所在显示器的工作区（换算成 client 坐标）——用来把工具条夹在可见屏幕内
        Rectangle ScreenClient()
        {
            try
            {
                var selScreen = new Rectangle(_sel.X + _vs.X, _sel.Y + _vs.Y, Math.Max(1, _sel.Width), Math.Max(1, _sel.Height));
                var wa = Screen.FromRectangle(selScreen).WorkingArea;
                var c = new Rectangle(wa.X - _vs.X, wa.Y - _vs.Y, wa.Width, wa.Height);
                c.Intersect(new Rectangle(0, 0, this.Width, this.Height));
                if (c.Width <= 0 || c.Height <= 0) return new Rectangle(0, 0, this.Width, this.Height);
                return c;
            }
            catch { return new Rectangle(0, 0, this.Width, this.Height); }
        }

        // 样式子条：颜色 + 粗细（选了可着色工具时显示）
        void LayoutStyle(Rectangle sc)
        {
            _showStyle = (_tool == Tool.Arrow || _tool == Tool.Line || _tool == Tool.Rect
                          || _tool == Tool.Ellipse || _tool == Tool.Text);
            if (!_showStyle) return;
            _widthCount = (_tool == Tool.Text) ? 0 : WIDTHS.Length;
            int nColor = PALETTE.Length;
            int gap = (_widthCount > 0) ? 20 : 0;
            int w = 8 + nColor * SUB + gap + _widthCount * SUB + 8;
            int x = _bar.X;
            if (x + w > sc.Right) x = sc.Right - w;
            if (x < sc.Left) x = sc.Left;
            int y = _bar.Bottom + 6;
            if (y + SUB > sc.Bottom) y = _bar.Y - SUB - 6;   // 主条下方放不下就放主条上方
            if (y < sc.Top) y = sc.Top + 2;
            _styleBar = new Rectangle(x, y, w, SUB);

            int cx = x + 8;
            for (int i = 0; i < nColor; i++) { _colorRects[i] = new Rectangle(cx, y, SUB, SUB); cx += SUB; }
            cx += gap;
            for (int i = 0; i < _widthCount; i++) { _widthRects[i] = new Rectangle(cx, y, SUB, SUB); cx += SUB; }
        }

        // 点样式子条：命中就设置颜色/粗细
        bool HandleStyleClick(Point p)
        {
            if (!_showStyle) return false;
            for (int i = 0; i < PALETTE.Length; i++)
                if (_colorRects[i].Contains(p)) { _color = PALETTE[i]; this.Invalidate(); return true; }
            for (int i = 0; i < _widthCount; i++)
                if (_widthRects[i].Contains(p)) { _width = WIDTHS[i]; this.Invalidate(); return true; }
            return false;
        }

        // ---- 文字标注输入（真控件，底色贴合背景，宽度自适应）----
        void BeginText(Point p)
        {
            CommitText();
            var tb = new TextBox();
            tb.BorderStyle = BorderStyle.None;
            tb.Multiline = false;
            tb.Font = AnnoFont;
            tb.ForeColor = _color;
            tb.Location = p;
            tb.Width = 24;   // 起始很窄，随打字增长
            this.Controls.Add(tb);
            tb.BackColor = SampleBg(new Rectangle(tb.Left, tb.Top, tb.Width, tb.Height));
            tb.TextChanged += (s, e) =>
            {
                int w = TextRenderer.MeasureText(tb.Text, AnnoFont).Width + 14;
                w = Math.Max(24, Math.Min(_sel.Right - tb.Left - 2, w));
                tb.Width = w;
                tb.BackColor = SampleBg(new Rectangle(tb.Left, tb.Top, tb.Width, tb.Height));
            };
            _tb = tb;
            _tb.Focus();
        }

        // 取一小块区域的平均色（背景纯色时看起来就像透明）
        Color SampleBg(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width <= 0 || r.Height <= 0) return Color.White;
            long tr = 0, tg = 0, tb = 0; int n = 0;
            int sx = Math.Max(1, r.Width / 20), sy = Math.Max(1, r.Height / 6);
            for (int y = r.Top; y < r.Bottom; y += sy)
                for (int x = r.Left; x < r.Right; x += sx)
                {
                    Color c = _full.GetPixel(x, y);
                    tr += c.R; tg += c.G; tb += c.B; n++;
                }
            if (n == 0) return Color.White;
            return Color.FromArgb((int)(tr / n), (int)(tg / n), (int)(tb / n));
        }

        void CommitText()
        {
            if (_tb == null) return;
            var tb = _tb; _tb = null;               // 先置空，避免重入
            string txt = tb.Text;
            Point pos = tb.Location;
            this.Controls.Remove(tb);
            tb.Dispose();
            if (!string.IsNullOrEmpty(txt) && txt.Trim().Length > 0)
                _annos.Add(new Anno { Type = Tool.Text, A = pos, Text = txt, Color = _color });
            this.Focus();
            this.Invalidate();
        }

        void CancelText()
        {
            if (_tb == null) return;
            var tb = _tb; _tb = null;
            this.Controls.Remove(tb);
            tb.Dispose();
            this.Focus();
            this.Invalidate();
        }

        // 裁剪选区并把标注烧进去，返回最终图（含马赛克/箭头/文字等）
        Bitmap RenderResult()
        {
            Rectangle r = _sel;
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width <= 0 || r.Height <= 0) return null;
            var crop = new Bitmap(r.Width, r.Height);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.DrawImage(_full, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-r.X, -r.Y);
                foreach (var a in _annos) DrawAnno(g, a);
            }
            return crop;
        }

        void Confirm()
        {
            CommitText();   // 落定未回车的文字
            Result = RenderResult();
            Outcome = ShotOutcome.AutoSave;
            this.Close();
        }

        void SavePng()
        {
            CommitText();
            using (var bmp = RenderResult())
            {
                if (bmp == null) { this.Close(); return; }
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG 图片|*.png";
                    dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    try { dlg.InitialDirectory = Shot.EffectiveSaveDir; }
                    catch { }
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        try { bmp.Save(dlg.FileName, ImageFormat.Png); }
                        catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message, "截图工具"); return; }
                        Outcome = ShotOutcome.Save;
                        this.Close();
                    }
                    // 取消保存则留在界面继续编辑
                }
            }
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics);
        }

        public void Render(Graphics g)
        {
            g.DrawImageUnscaled(_full, 0, 0);
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(dim, this.ClientRectangle);

            if (_sel.Width > 0 && _sel.Height > 0)
            {
                // 选区内还原清晰原图
                g.DrawImage(_full, _sel, _sel, GraphicsUnit.Pixel);
                // 极简细边框（暖金 1px）
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(C_GOLD, 1f))
                    g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 标注（裁进选区内绘制）
                var oldClip = g.Clip;
                g.SetClip(_sel);
                foreach (var a in _annos) DrawAnno(g, a);
                if (_annotating && _drawing != null) DrawAnno(g, _drawing);
                g.Clip = oldClip;

                // 尺寸仅在拖动时显示（精简）
                if (_dragging)
                {
                    string info = _sel.Width + " × " + _sel.Height;
                    using (var f = new Font("Segoe UI", 9f))
                    using (var bg = new SolidBrush(Color.FromArgb(210, C_DEEP)))
                    using (var fg = new SolidBrush(C_GOLD))
                    {
                        SizeF sz = g.MeasureString(info, f);
                        float tx = _sel.Left, ty = _sel.Top - sz.Height - 5;
                        if (ty < 2) ty = _sel.Top + 4;
                        g.FillRectangle(bg, tx, ty, sz.Width + 10, sz.Height + 2);
                        g.DrawString(info, f, fg, tx + 5, ty + 1);
                    }
                }

                // 控制点：未选标注工具、且非拖框中时显示，提示可移动/缩放
                if (_hasSel && _tool == Tool.None && !_dragging) DrawHandles(g);

                if (_hasSel) DrawToolbar(g);
            }
            else if (!_dragging)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                string tip = "拖动框选区域        Enter 复制        Esc 取消";
                using (var f = new Font("Microsoft YaHei", 11f))
                using (var bg = new SolidBrush(Color.FromArgb(200, C_DEEP)))
                using (var fg = new SolidBrush(C_TEXT))
                {
                    SizeF sz = g.MeasureString(tip, f);
                    float tx = (this.Width - sz.Width) / 2, ty = 44;
                    using (var p = Rounded(new Rectangle((int)(tx - 18), (int)(ty - 9), (int)sz.Width + 36, (int)sz.Height + 18), 10))
                        g.FillPath(bg, p);
                    g.DrawString(tip, f, fg, tx, ty);
                }
            }
        }

        // 极简工具条：无边框，半透明暖墨底 + 细笔画图标
        void DrawToolbar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 柔和投影
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var p = Rounded(new Rectangle(_bar.X, _bar.Y + 3, _bar.Width, _bar.Height), 10))
                g.FillPath(sh, p);
            // 底板（暖墨底 + 一圈极细金边，让轮廓在任意背景上都清楚）
            using (var bg = new SolidBrush(Color.FromArgb(212, 0x1A, 0x17, 0x12)))
            using (var p = Rounded(_bar, 10))
            {
                g.FillPath(bg, p);
                using (var pen = new Pen(Color.FromArgb(88, 0x8A, 0x7A, 0x5E), 1f))
                    g.DrawPath(pen, p);
            }

            for (int i = 0; i < _rects.Length; i++)
            {
                int id = _ids[i];
                Rectangle c = _rects[i];
                bool active = (id == 1 && _tool == Tool.Arrow) || (id == 2 && _tool == Tool.Line)
                            || (id == 3 && _tool == Tool.Rect) || (id == 10 && _tool == Tool.Ellipse)
                            || (id == 7 && _tool == Tool.Text)
                            || (id == 8 && _tool == Tool.Mosaic);
                bool hover = (_hover == id);
                if (active) Chip(g, c, C_GOLD, 52);
                else if (hover) Chip(g, c, C_TEXT, 26);

                Color ic = active ? C_GOLD : (hover ? C_TEXT : C_TEXT2);
                switch (id)
                {
                    case 1: DrawArrowIcon(g, c, ic); break;
                    case 2: DrawLineIcon(g, c, ic); break;
                    case 3: DrawRectIcon(g, c, ic); break;
                    case 10: DrawEllipseIcon(g, c, ic); break;
                    case 7: DrawTextIcon(g, c, ic); break;
                    case 8: DrawMosaicIcon(g, c, ic); break;
                    case 4: DrawUndoIcon(g, c, ic); break;
                    case 9: DrawSaveIcon(g, c, ic); break;
                    case 5: DrawCross(g, c, hover ? Color.FromArgb(255, 0xF2, 0x6E, 0x5C) : C_RED); break;
                    case 6: DrawCheck(g, c, hover ? Color.FromArgb(255, 0x4F, 0xD8, 0x9E) : C_GREEN); break;
                }
            }

            if (_showStyle) DrawStyleBar(g);
        }

        // 颜色 + 粗细 子条
        void DrawStyleBar(Graphics g)
        {
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var p = Rounded(new Rectangle(_styleBar.X, _styleBar.Y + 3, _styleBar.Width, _styleBar.Height), 9))
                g.FillPath(sh, p);
            using (var bg = new SolidBrush(Color.FromArgb(212, 0x1A, 0x17, 0x12)))
            using (var p = Rounded(_styleBar, 9))
            {
                g.FillPath(bg, p);
                using (var pen = new Pen(Color.FromArgb(88, 0x8A, 0x7A, 0x5E), 1f))
                    g.DrawPath(pen, p);
            }

            // 颜色圆点
            for (int i = 0; i < PALETTE.Length; i++)
            {
                Rectangle c = _colorRects[i];
                int d = 16, ox = c.Left + (c.Width - d) / 2, oy = c.Top + (c.Height - d) / 2;
                using (var b = new SolidBrush(PALETTE[i]))
                    g.FillEllipse(b, ox, oy, d, d);
                if (PALETTE[i].GetBrightness() > 0.85f || PALETTE[i].GetBrightness() < 0.15f)   // 纯白/纯黑都描边，避免与底色糊在一起
                    using (var pen = new Pen(Color.FromArgb(90, C_TEXT2), 1f))
                        g.DrawEllipse(pen, ox, oy, d, d);
                if (_color.ToArgb() == PALETTE[i].ToArgb())  // 选中：金色外圈
                    using (var pen = new Pen(C_GOLD, 2f))
                        g.DrawEllipse(pen, ox - 3, oy - 3, d + 6, d + 6);
            }
            // 颜色组与粗细组之间的分隔线（避免两组挤在一起看成一体）
            if (_widthCount > 0)
            {
                int sx = _colorRects[PALETTE.Length - 1].Right + 9;
                using (var b = new SolidBrush(Color.FromArgb(52, C_TEXT2)))
                    g.FillRectangle(b, sx, _styleBar.Top + 9, 1, _styleBar.Height - 18);
            }
            // 粗细档：三根不同粗细的短线（与设计稿一致，所见即所得）
            for (int i = 0; i < _widthCount; i++)
            {
                Rectangle c = _widthRects[i];
                bool sel = Math.Abs(_width - WIDTHS[i]) < 0.01f;
                float len = 18f, y = c.Top + c.Height / 2f;
                using (var pen = new Pen(sel ? C_GOLD : C_TEXT2, WIDTHS[i]))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, c.Left + (c.Width - len) / 2f, y, c.Left + (c.Width + len) / 2f, y);
                }
                if (sel)   // 选中：线下方一个小金点
                    using (var b = new SolidBrush(Color.FromArgb(150, C_GOLD)))
                        g.FillEllipse(b, c.Left + c.Width / 2f - 2, y + 7, 4, 4);
            }
        }

        void Chip(Graphics g, Rectangle cell, Color c, int alpha)
        {
            Rectangle r = Rectangle.Inflate(cell, -4, -4);
            using (var b = new SolidBrush(Color.FromArgb(alpha, c)))
            using (var p = Rounded(r, 8))
                g.FillPath(b, p);
        }

        // ---- 标注绘制 ----
        void DrawAnno(Graphics g, Anno a)
        {
            Color col = (a.Color.A == 0) ? C_ANNO : a.Color;   // 兼容未设色
            float wd = a.Width > 0 ? a.Width : ANNO_W;

            if (a.Type == Tool.Mosaic)
            {
                DrawMosaic(g, Rectangle.FromLTRB(Math.Min(a.A.X, a.B.X), Math.Min(a.A.Y, a.B.Y),
                                                 Math.Max(a.A.X, a.B.X), Math.Max(a.A.Y, a.B.Y)));
                return;
            }
            if (a.Type == Tool.Text)
            {
                if (!string.IsNullOrEmpty(a.Text))
                    using (var br = new SolidBrush(col))
                        g.DrawString(a.Text, AnnoFont, br, a.A.X, a.A.Y, StringFormat.GenericTypographic);
                return;
            }
            using (var pen = new Pen(col, wd))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (a.Type == Tool.Rect)
                {
                    int x = Math.Min(a.A.X, a.B.X), y = Math.Min(a.A.Y, a.B.Y);
                    g.DrawRectangle(pen, x, y, Math.Abs(a.A.X - a.B.X), Math.Abs(a.A.Y - a.B.Y));
                }
                else if (a.Type == Tool.Ellipse)
                {
                    int x = Math.Min(a.A.X, a.B.X), y = Math.Min(a.A.Y, a.B.Y);
                    int w = Math.Abs(a.A.X - a.B.X), h = Math.Abs(a.A.Y - a.B.Y);
                    if (w >= 2 && h >= 2)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawEllipse(pen, x, y, w, h);
                    }
                }
                else if (a.Type == Tool.Line)
                {
                    g.DrawLine(pen, a.A, a.B);
                }
                else if (a.Type == Tool.Arrow)
                {
                    g.DrawLine(pen, a.A, a.B);
                    double ang = Math.Atan2(a.B.Y - a.A.Y, a.B.X - a.A.X);
                    float len = 10 + wd * 2.2f;
                    double a1 = ang + Math.PI - 0.5, a2 = ang + Math.PI + 0.5;
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a1) * len), a.B.Y + (float)(Math.Sin(a1) * len)));
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a2) * len), a.B.Y + (float)(Math.Sin(a2) * len)));
                }
            }
        }

        // 马赛克：把区域缩小再放大，得到打码块（读取冻结原图，源坐标为屏幕坐标）
        void DrawMosaic(Graphics g, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int block = 10;
            int sw = Math.Max(1, r.Width / block), sh = Math.Max(1, r.Height / block);
            using (var small = new Bitmap(sw, sh))
            {
                using (var sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.DrawImage(_full, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                }
                var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, r);
                g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
            }
        }

        // ---- 图标 ----
        static Pen IconPen(Color c, float w)
        {
            var pen = new Pen(c, w);
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            return pen;
        }

        void DrawArrowIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f, cy = c.Top + c.Height / 2f;
                float h = 11f;                                  // 斜线半长，整体居中
                var a = new PointF(cx - h, cy + h);
                var b = new PointF(cx + h, cy - h);             // 箭尖
                g.DrawLine(pen, a, b);
                float arm = 5.5f;                               // 两臂等长、水平/垂直
                g.DrawLine(pen, b, new PointF(b.X - arm, b.Y));
                g.DrawLine(pen, b, new PointF(b.X, b.Y + arm));
            }
        }

        void DrawLineIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f, cy = c.Top + c.Height / 2f, h = 11f;
                g.DrawLine(pen, cx - h, cy + h, cx + h, cy - h);
            }
        }

        void DrawRectIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
                g.DrawRectangle(pen, c.Left + 12, c.Top + 13, c.Width - 24, c.Height - 26);
        }

        void DrawEllipseIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawEllipse(pen, c.Left + 11, c.Top + 14, c.Width - 22, c.Height - 28);
            }
        }

        void DrawTextIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f;
                g.DrawLine(pen, c.Left + 12, c.Top + 13, c.Right - 12, c.Top + 13); // 顶横
                g.DrawLine(pen, cx, c.Top + 13, cx, c.Bottom - 12);                 // 竖
            }
        }

        void DrawMosaicIcon(Graphics g, Rectangle c, Color col)
        {
            float x0 = c.Left + 12, y0 = c.Top + 12;
            float cell = (c.Width - 24) / 3f, d = cell * 0.80f, off = (cell - d) / 2f;
            for (int i = 0; i < 9; i++)
            {
                int r = i / 3, cc = i % 3;
                int alpha = 255 - (r + cc) * 26;
                using (var b = new SolidBrush(Color.FromArgb(alpha, col)))
                    g.FillRectangle(b, x0 + cc * cell + off, y0 + r * cell + off, d, d);
            }
        }

        void DrawSaveIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f;
                g.DrawLine(pen, cx, c.Top + 12, cx, c.Bottom - 18);              // 竖杆
                g.DrawLine(pen, cx - 4.5f, c.Bottom - 22.5f, cx, c.Bottom - 18);  // 箭头
                g.DrawLine(pen, cx + 4.5f, c.Bottom - 22.5f, cx, c.Bottom - 18);
                g.DrawLine(pen, c.Left + 11, c.Bottom - 10, c.Right - 11, c.Bottom - 10);
            }
        }

        void DrawUndoIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.8f))   // 参考图：粗线 3/4 环 + 左上实心箭头
            {
                var box = new RectangleF(c.Left + 11, c.Top + 11, c.Width - 22, c.Height - 22);
                g.DrawArc(pen, box.X, box.Y, box.Width, box.Height, 270, 270);
                float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
                // 实心三角箭头，尖朝左上，落在缺口里
                var tri = new PointF[] {
                    new PointF(cx - 9.8f, cy - 9.8f),
                    new PointF(cx - 2.4f, cy - 7.0f),
                    new PointF(cx - 7.0f, cy - 2.4f)
                };
                using (var b = new SolidBrush(col))
                    g.FillPolygon(b, tri);
            }
        }

        void DrawCross(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.3f))
            {
                int p = 15;
                g.DrawLine(pen, c.Left + p, c.Top + p, c.Right - p, c.Bottom - p);
                g.DrawLine(pen, c.Right - p, c.Top + p, c.Left + p, c.Bottom - p);
            }
        }

        void DrawCheck(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.6f))
            {
                var p1 = new PointF(c.Left + 13f, c.Top + 22f);
                var p2 = new PointF(c.Left + 18f, c.Top + 27f);
                var p3 = new PointF(c.Left + 29f, c.Top + 15f);
                g.DrawLines(pen, new[] { p1, p2, p3 });
            }
        }

        GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 隐藏开发用：把真实绘制渲染成 PNG，便于核对视觉。用法 Snip.exe --preview out.png [hoverId]
        internal static void SavePreview(string path, int hover)
        {
            int W = 560, H = 380;
            var bg = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bg))
            {
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(48, 76, 120), Color.FromArgb(18, 26, 44), 45f))
                    g.FillRectangle(br, 0, 0, W, H);
                using (var b = new SolidBrush(Color.FromArgb(238, 238, 240)))
                    g.FillRectangle(b, 70, 70, 230, 130);
                using (var b = new SolidBrush(Color.FromArgb(150, 170, 210)))
                    g.FillRectangle(b, 320, 90, 170, 110);
            }
            var f = new OverlayForm(bg, new Rectangle(0, 0, W, H));
            f.Bounds = new Rectangle(0, 0, W, H);
            f._sel = new Rectangle(120, 95, 300, 175);
            f._hasSel = true;
            f._tool = Tool.Rect;
            f._color = PALETTE[3]; f._width = WIDTHS[1];
            f._annos.Add(new Anno { Type = Tool.Arrow, A = new Point(400, 250), B = new Point(255, 160), Color = PALETTE[0], Width = 4 });
            f._annos.Add(new Anno { Type = Tool.Rect, A = new Point(150, 118), B = new Point(240, 172), Color = PALETTE[3], Width = 4 });
            f._annos.Add(new Anno { Type = Tool.Mosaic, A = new Point(330, 100), B = new Point(410, 150) });
            f._annos.Add(new Anno { Type = Tool.Text, A = new Point(285, 250), Text = "看这里", Color = PALETTE[1] });
            f.LayoutButtons();
            f._hover = hover;
            using (var outBmp = new Bitmap(W, H))
            {
                using (var g = Graphics.FromImage(outBmp)) f.Render(g);
                outBmp.Save(path, ImageFormat.Png);
            }
            f.Dispose();
        }

        // ---- 演示动画（DemoGen 用）：设置状态并抓一帧 ----
        internal void DemoConfig(Rectangle sel, bool hasSel, bool dragging, Tool tool)
        {
            _sel = sel; _hasSel = hasSel; _dragging = dragging; _tool = tool;
            if (hasSel && !dragging) LayoutButtons();
        }
        internal void DemoStyle(Color c, float w) { _color = c; _width = w; if (_hasSel) LayoutButtons(); }
        internal void DemoHover(int id) { _hover = id; }
        internal List<Anno> DemoAnnos { get { return _annos; } }
        internal Bitmap DemoFrame()
        {
            var b = new Bitmap(this.Width, this.Height);
            using (var g = Graphics.FromImage(b)) { g.SmoothingMode = SmoothingMode.AntiAlias; Render(g); }
            return b;
        }
    }








