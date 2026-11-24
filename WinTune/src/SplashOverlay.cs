using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WinTune
{
    /// <summary>
    /// 盖在 WebView2 上面的启动过渡层：深色底 + W 呼吸光 + 旋转弧 + 进度微光。
    /// WebView2 第一次创建会有几秒空白，这一层负责把这段时间接住；
    /// 前端把 CPU 温度、内存等首屏数据真正画出来以后，再淡出。
    /// </summary>
    public class SplashOverlay : Control
    {
        private readonly Timer timer;
        private float phase;
        private DateTime? finishAt;
        private DateTime? fadeAt;
        private int alpha = 255;
        private string statusText = "正在准备界面";

        private static readonly Color BgTop = Color.FromArgb(18, 24, 31);
        private static readonly Color BgBottom = Color.FromArgb(10, 13, 18);
        private static readonly Color Mint = Color.FromArgb(92, 255, 190);
        private static readonly Color Cyan = Color.FromArgb(45, 207, 255);
        private static readonly Color TextMain = Color.FromArgb(231, 237, 245);
        private static readonly Color TextDim = Color.FromArgb(128, 143, 159);

        public SplashOverlay()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(14, 17, 22);
            TabStop = false;
            timer = new Timer();
            timer.Interval = 33;
            timer.Tick += OnTick;
            timer.Start();
        }

        public void Finish()
        {
            if (finishAt.HasValue) return;
            finishAt = DateTime.UtcNow.AddMilliseconds(300);
        }

        /// <summary>更新底部状态文字，不打断动画。</summary>
        public void SetStatus(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                statusText = text;
                Invalidate();
            }
        }

        public void Fail()
        {
            finishAt = DateTime.UtcNow;
        }

        private void OnTick(object sender, EventArgs e)
        {
            phase += 0.055f;
            if (finishAt.HasValue && !fadeAt.HasValue && DateTime.UtcNow >= finishAt.Value)
                fadeAt = DateTime.UtcNow;
            if (fadeAt.HasValue)
            {
                double ms = (DateTime.UtcNow - fadeAt.Value).TotalMilliseconds;
                alpha = 255 - (int)Math.Min(255, ms / 280.0 * 255);
                if (alpha <= 0)
                {
                    timer.Stop();
                    Visible = false;
                    try { Dispose(); } catch { }
                    return;
                }
            }
            Invalidate();
        }

        private Color Fade(Color c)
        {
            int a = c.A * alpha / 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            int w = Width, h = Height;
            using (LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h)), BgTop, BgBottom, 90f))
                g.FillRectangle(bg, 0, 0, w, h);

            float cx = w / 2f;
            float cy = h / 2f - Math.Min(64f, h * 0.07f);
            float scale = Math.Max(1.75f, Math.Min(2.35f, Math.Min(w, h) / 520f));

            // 中心柔光
            float gr = 150f;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(38 * alpha / 255, 58, 220, 160);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 58, 220, 160) };
                    g.FillEllipse(pb, cx - gr, cy - gr, gr * 2, gr * 2);
                }
            }

            // 旋转外弧
            using (Pen arc = new Pen(Fade(Color.FromArgb(130, 45, 207, 255)), 1.4f))
            {
                float r = 102f;
                g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, phase * 42f, 46f);
                using (Pen arc2 = new Pen(Fade(Color.FromArgb(70, 92, 255, 190)), 1.2f))
                    g.DrawArc(arc2, cx - r, cy - r, r * 2, r * 2, phase * 42f + 180f, 28f);
            }

            // W 标
            PointF[] pts = new PointF[]
            {
                new PointF(cx - 29f * scale, cy - 19f * scale),
                new PointF(cx - 13.9f * scale, cy + 23f * scale),
                new PointF(cx, cy - 5.5f * scale),
                new PointF(cx + 13.9f * scale, cy + 23f * scale),
                new PointF(cx + 29f * scale, cy - 19f * scale)
            };
            using (GraphicsPath wp = new GraphicsPath())
            {
                wp.AddLines(pts);
                RectangleF box = new RectangleF(cx - 34f * scale, cy - 26f * scale, 68f * scale, 56f * scale);
                using (LinearGradientBrush lg = new LinearGradientBrush(box, Mint, Cyan, 20f))
                using (Pen pen = new Pen(lg, 8.2f * scale))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawPath(pen, wp);
                }
                // DrawPath 对内嵌折线的端头有时不画圆帽，这里补两个端头圆，避免顶部被切成平口
                float capR = 4.1f * scale;
                using (SolidBrush cap1 = new SolidBrush(Fade(Mint)))
                    g.FillEllipse(cap1, pts[0].X - capR, pts[0].Y - capR, capR * 2, capR * 2);
                using (SolidBrush cap2 = new SolidBrush(Fade(Cyan)))
                    g.FillEllipse(cap2, pts[4].X - capR, pts[4].Y - capR, capR * 2, capR * 2);
            }

            // 文字
            float ty = cy + 78f * scale;
            using (Font f1 = new Font("Microsoft YaHei UI", 25f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font f2 = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush tb = new SolidBrush(Fade(TextMain)))
                    g.DrawString("WinTune", f1, tb, new RectangleF(0, ty, w, 36), sf);
                int dots = ((int)(phase * 3f)) % 4;
                string status = statusText + new string('·', dots);
                using (SolidBrush sb = new SolidBrush(Fade(TextDim)))
                    g.DrawString(status, f2, sb, new RectangleF(0, ty + 40, w, 24), sf);
            }

            // 底部进度微光
            float bw = Math.Min(250f, Math.Max(180f, w * 0.2f));
            float bx = cx - bw / 2f;
            float by = ty + 86;
            using (SolidBrush track = new SolidBrush(Fade(Color.FromArgb(42, 62, 76))))
                g.FillRectangle(track, bx, by, bw, 2f);
            float pos = (float)((phase * 0.42f) % 1.0);
            float sw = bw * 0.34f;
            using (LinearGradientBrush shine = new LinearGradientBrush(
                new RectangleF(bx, by - 1, bw, 4), Color.FromArgb(0, 92, 255, 190),
                Color.FromArgb(210 * alpha / 255, 92, 255, 190), 0f))
            {
                ColorBlend blend = new ColorBlend();
                blend.Colors = new Color[] {
                    Color.FromArgb(0, 92, 255, 190),
                    Color.FromArgb(210 * alpha / 255, 92, 255, 190),
                    Color.FromArgb(0, 45, 207, 255)
                };
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                shine.InterpolationColors = blend;
                g.FillRectangle(shine, bx + pos * (bw - sw), by - 1, sw, 4f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null)
            {
                try { timer.Stop(); timer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
