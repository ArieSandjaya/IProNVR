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
            Height = Asm.S(39);
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
            using (var b = new SolidBrush(Asm.Bg)) g.FillRectangle(b, r);
            using (var p = new Pen(Asm.BtnBorder)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            ViewCell c = selected == null ? null : selected();
            bool has = c != null && c.HasCamera;
            int y = 0, h = Height - Asm.S(2);

            // nama kamera (lebar tetap 383 seperti ASM300)
            Rectangle name = new Rectangle(Asm.S(2), y, Asm.S(383), h);
            Box(g, name);
            if (has)
                TextRenderer.DrawText(g, Dir.Title(c.NvrId, c.Channel), Asm.F(12f, false), Rectangle.Inflate(name, -Asm.S(8), 0), Asm.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // ikon alarm / error
            int sz = Asm.S(45);
            Rectangle a1 = new Rectangle(name.Right + Asm.S(2), y, sz, h);
            Rectangle a2 = new Rectangle(a1.Right, y, sz, h);
            foreach (Rectangle r in new[] { a1, a2 })
                using (var b = new SolidBrush(Color.FromArgb(48, 54, 67))) g.FillRectangle(b, r);
            AsmIcons.Draw(g, AsmIcon.Alarm, Rectangle.Inflate(a1, -Asm.S(9), -Asm.S(7)), Asm.Text);
            using (var b = new SolidBrush(Asm.Text))
            {
                int d = h - Asm.S(14);
                g.FillEllipse(b, a2.X + (a2.Width - d) / 2, a2.Y + (a2.Height - d) / 2, d, d);
            }
            TextRenderer.DrawText(g, "!", Asm.F(12f, true), a2, Color.FromArgb(48, 54, 67),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            // REC + jam
            Rectangle right = new Rectangle(Width - Asm.S(384), y, Asm.S(382), h);
            Rectangle box2 = new Rectangle(right.X - Asm.S(128), y, Asm.S(124), h);
            Rectangle rec = new Rectangle(box2.X - Asm.S(44), y, Asm.S(44), h);
            Rectangle tbox = new Rectangle(rec.X - Asm.S(126), y, Asm.S(124), h);
            Box(g, box2);
            Box(g, tbox); Box(g, right);
            string when = !has ? "" : c.Playback ? "PLAYBACK" : DateTime.Now.ToString(AppSettings.Current.DateFormat.Replace("yyyy", "yyyy") + " HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            TextRenderer.DrawText(g, when, Asm.F(11f, false), tbox, has ? Asm.Text : Asm.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            bool recording = has && !c.Playback && Dir.IsRecording(c.NvrId, c.Channel);
            TextRenderer.DrawText(g, "REC", Asm.F(10f, true), new Rectangle(rec.X, rec.Y, rec.Width, h / 2 + Asm.S(2)), Asm.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int d2 = Asm.S(11);
            using (var b = new SolidBrush(recording ? Color.FromArgb(230, 40, 40) : Color.FromArgb(67, 71, 85)))
                g.FillEllipse(b, rec.X + (rec.Width - d2) / 2, rec.Y + h / 2 + Asm.S(3), d2, d2);
            if (has)
                TextRenderer.DrawText(g, c.Playback ? "Playback" : "Live", Asm.F(11f, false), right, Asm.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
    }
}
