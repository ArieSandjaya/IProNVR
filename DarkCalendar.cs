using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Kalender bulan gaya ASM300: bingkai terang, header bulan dengan panah, tanggal terpilih berupa lingkaran.</summary>
    public class DarkCalendar : Control
    {
        DateTime month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DateTime value = DateTime.Today;
        int hotDay = -1;
        public event Action ValueChanged;

        public DarkCalendar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Tag = "nostyle";
            Height = Asm.S(240);
        }

        public DateTime Value
        {
            get { return value; }
            set
            {
                this.value = value.Date;
                month = new DateTime(value.Year, value.Month, 1);
                Invalidate();
            }
        }

        int HeadH { get { return Asm.S(34); } }
        int WeekH { get { return Asm.S(30); } }
        Rectangle Inner { get { return new Rectangle(Asm.S(3), Asm.S(3), Width - Asm.S(6), Height - Asm.S(6)); } }

        Rectangle Cell(int row, int col)
        {
            Rectangle r = Inner;
            int top = r.Y + HeadH + WeekH;
            int cw = r.Width / 7, ch = (r.Bottom - top) / 6;
            return new Rectangle(r.X + col * cw, top + row * ch, cw, ch);
        }

        DateTime FirstShown { get { return month.AddDays(-(int)month.DayOfWeek); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = Inner;
            using (var b = new SolidBrush(Asm.Panel2)) g.FillRectangle(b, r);
            Rectangle head = new Rectangle(r.X, r.Y, r.Width, HeadH);
            using (var b = new SolidBrush(Asm.Label)) g.FillRectangle(b, head);
            Color ink = Color.FromArgb(48, 54, 67);
            TextRenderer.DrawText(g, month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), Asm.F(9.5f, true), head, ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            int ah = Asm.S(8);
            using (var b = new SolidBrush(ink))
            {
                int cy = head.Y + head.Height / 2, lx = head.X + Asm.S(16), rx = head.Right - Asm.S(16);
                g.FillPolygon(b, new[] { new Point(lx + ah / 2, cy - ah), new Point(lx + ah / 2, cy + ah), new Point(lx - ah / 2, cy) });
                g.FillPolygon(b, new[] { new Point(rx - ah / 2, cy - ah), new Point(rx - ah / 2, cy + ah), new Point(rx + ah / 2, cy) });
            }
            string[] wd = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
            for (int c = 0; c < 7; c++)
            {
                Rectangle cr = Cell(0, c);
                TextRenderer.DrawText(g, wd[c], Asm.F(9f, true), new Rectangle(cr.X, r.Y + HeadH, cr.Width, WeekH), Color.FromArgb(205, 214, 230),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
            DateTime d = FirstShown;
            for (int i = 0; i < 42; i++, d = d.AddDays(1))
            {
                Rectangle cr = Cell(i / 6 == 0 ? 0 : i / 7, i % 7);
                cr = Cell(i / 7, i % 7);
                bool inMonth = d.Month == month.Month;
                if (!inMonth) continue;                       // seperti ASM300: hari bulan lain dikosongkan
                bool sel = d.Date == value.Date;
                bool today = d.Date == DateTime.Today;
                Color fg = Asm.Text;
                if (sel || hotDay == i)
                {
                    int s = Math.Min(cr.Width, cr.Height) - Asm.S(4);
                    var circle = new Rectangle(cr.X + (cr.Width - s) / 2, cr.Y + (cr.Height - s) / 2, s, s);
                    using (var b = new SolidBrush(sel ? Color.FromArgb(23, 41, 58) : Color.FromArgb(44, 54, 74))) g.FillEllipse(b, circle);
                }
                if (today && !sel) fg = Asm.Accent;
                TextRenderer.DrawText(g, d.Day.ToString(), Asm.F(9.5f, false), cr, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
            using (var p = new Pen(Color.White, Asm.S(2))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        int HitDay(Point pt)
        {
            for (int i = 0; i < 42; i++)
                if (Cell(i / 7, i % 7).Contains(pt) && FirstShown.AddDays(i).Month == month.Month) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitDay(e.Location);
            if (h != hotDay) { hotDay = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hotDay != -1) { hotDay = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            Rectangle head = new Rectangle(Inner.X, Inner.Y, Inner.Width, HeadH);
            if (head.Contains(e.Location))
            {
                month = month.AddMonths(e.X < head.X + head.Width / 2 ? -1 : 1);
                Invalidate();
                return;
            }
            int h = HitDay(e.Location);
            if (h >= 0)
            {
                value = FirstShown.AddDays(h).Date;
                Invalidate();
                if (ValueChanged != null) ValueChanged();
            }
            base.OnMouseDown(e);
        }
    }
}
