using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Strip info di atas area gambar (ala ASM300): nama kamera terpilih, ikon alarm/error, indikator REC dan jam.</summary>
    public class InfoStrip : Control
    {
        readonly Func<ViewCell> selected;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public InfoStrip(Func<ViewCell> selected)
        {
            this.selected = selected;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Tag = "nostyle";
            Height = Asm.S(40);
            timer.Interval = 1000;
            timer.Tick += delegate { if (Visible) Invalidate(); };
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        void Box(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(14, 17, 24))) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(40, 48, 64))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ViewCell c = selected == null ? null : selected();
            bool has = c != null && c.HasCamera;
            int y = Asm.S(4), h = Height - Asm.S(8);

            // nama kamera
            Rectangle name = new Rectangle(Asm.S(6), y, Math.Max(Asm.S(120), Width * 36 / 100), h);
            Box(g, name);
            if (has)
                TextRenderer.DrawText(g, Dir.Title(c.NvrId, c.Channel), Asm.F(10f, false), Rectangle.Inflate(name, -Asm.S(8), 0), Asm.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // ikon alarm / error
            int sz = h;
            Rectangle a1 = new Rectangle(name.Right + Asm.S(4), y, sz, h);
            Rectangle a2 = new Rectangle(a1.Right + Asm.S(2), y, sz, h);
            foreach (Rectangle r in new[] { a1, a2 })
                using (var b = new SolidBrush(Color.FromArgb(54, 64, 84))) g.FillRectangle(b, r);
            AsmIcons.Draw(g, AsmIcon.Alarm, Rectangle.Inflate(a1, -Asm.S(6), -Asm.S(6)), Color.FromArgb(150, 160, 178));
            using (var b = new SolidBrush(Color.FromArgb(150, 160, 178)))
            {
                int d = h - Asm.S(12);
                g.FillEllipse(b, a2.X + (a2.Width - d) / 2, a2.Y + (a2.Height - d) / 2, d, d);
            }
            TextRenderer.DrawText(g, "!", Asm.F(10f, true), a2, Color.FromArgb(54, 64, 84),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            // REC + jam
            int midX = Width * 62 / 100;
            Rectangle tbox = new Rectangle(midX - Asm.S(150), y, Asm.S(146), h);
            Rectangle rec = new Rectangle(midX, y, Asm.S(46), h);
            Rectangle right = new Rectangle(rec.Right + Asm.S(4), y, Math.Max(Asm.S(60), Width - rec.Right - Asm.S(10)), h);
            Box(g, tbox); Box(g, right);
            string when = !has ? "" : c.Playback ? "PLAYBACK" : DateTime.Now.ToString(AppSettings.Current.DateFormat.Replace("yyyy", "yyyy") + " HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            TextRenderer.DrawText(g, when, Asm.F(9.5f, false), tbox, has ? Asm.Text : Asm.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            bool recording = has && !c.Playback && Dir.IsRecording(c.NvrId, c.Channel);
            TextRenderer.DrawText(g, "REC", Asm.F(8.5f, true), new Rectangle(rec.X, rec.Y, rec.Width, h / 2 + Asm.S(2)), Asm.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int d2 = Asm.S(11);
            using (var b = new SolidBrush(recording ? Color.FromArgb(230, 40, 40) : Color.FromArgb(80, 90, 108)))
                g.FillEllipse(b, rec.X + (rec.Width - d2) / 2, rec.Y + h / 2 + Asm.S(3), d2, d2);
            if (has)
                TextRenderer.DrawText(g, c.Playback ? "Playback" : "Live", Asm.F(9.5f, false), right, Asm.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }
}
