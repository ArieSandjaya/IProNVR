using System;
using System.Drawing;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Garis waktu playback di bawah area gambar: seret (atau klik) untuk loncat ke waktu lain.
    /// Roda mouse = zoom (24 jam / 6 jam / 1 jam / 10 menit) di sekitar kursor; klik kanan = menu zoom.</summary>
    public class Scrubber : Control
    {
        static readonly int[] Spans = { 1440, 360, 60, 10 };          // menit
        static readonly int[] TickMin = { 120, 30, 5, 1 };            // jarak label (menit) per zoom
        static readonly string[] SpanText = { "24 jam", "6 jam", "1 jam", "10 menit" };

        int spanIdx;
        DateTime winStart = DateTime.Today;
        DateTime? pos;                  // posisi playback (waktu lokal)
        bool dragging;
        DateTime dragTime;
        int holdUntil;                  // abaikan posisi dari engine sebentar setelah loncat (engine butuh beberapa detik)
        readonly ContextMenuStrip menu;
        // pilihan rentang waktu untuk export (seperti Time Selection Mode di XProtect)
        DateTime? selA, selB;
        int dragHandle;                 // 0 tidak ada, 1 = awal, 2 = akhir
        DateTime? menuTime;

        /// <summary>Dipicu bila pengguna memilih "Download rentang ini" (waktu lokal awal, akhir).</summary>
        public event Action<DateTime, DateTime> ExportRange;

        /// <summary>Dipicu saat tombol mouse dilepas (waktu lokal tujuan).</summary>
        public event Action<DateTime> Seek;

        public bool Dragging { get { return dragging; } }

        public Scrubber()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Asm.S(44) + Asm.S(19);   // +19 px (~0,5 cm) ruang kosong di bawah: strip naik
            Dock = DockStyle.Bottom;
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
            Tag = "nostyle";
            menu = Asm.Menu();
            for (int i = 0; i < Spans.Length; i++)
            {
                int k = i;
                Asm.Item(menu, "Zoom " + SpanText[i], delegate { SetSpan(k, pos ?? DateTime.Now); });
            }
            menu.Items.Add(new ToolStripSeparator());
            Asm.Item(menu, "Go to date/time...", delegate { GoTo(); });
            Asm.Item(menu, "Set range start here", delegate { SetSel(true); });
            Asm.Item(menu, "Set range end here", delegate { SetSel(false); });
            Asm.Item(menu, "Download selected range...", delegate { DoExport(); });
            Asm.Item(menu, "Clear range", delegate { selA = selB = null; Invalidate(); });
        }

        void SetSel(bool start)
        {
            DateTime t = menuTime ?? (pos ?? DateTime.Now);
            if (start) { selA = t; if (!selB.HasValue || selB.Value <= t) selB = t.AddMinutes(5); }
            else { selB = t; if (!selA.HasValue || selA.Value >= t) selA = t.AddMinutes(-5); }
            Invalidate();
        }

        void DoExport()
        {
            if (!selA.HasValue || !selB.HasValue) { MessageBox.Show("Select a range first: right-click the timeline, then 'Set range start here' and 'Set range end here' (or drag the green handles)."); return; }
            Action<DateTime, DateTime> h = ExportRange;
            if (h == null) return;
            DateTime a = selA.Value < selB.Value ? selA.Value : selB.Value, b = selA.Value < selB.Value ? selB.Value : selA.Value;
            h(a, b);
        }

        /// <summary>Dialog "Go to": pilih tanggal dan jam lalu loncat ke sana.</summary>
        void GoTo()
        {
            using (var f = new Form())
            {
                f.Text = "Go to"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.ClientSize = new Size(Asm.S(300), Asm.S(110));
                f.BackColor = Asm.Panel; f.ForeColor = Asm.Text;
                var dt = new DateTimePicker();
                dt.Format = DateTimePickerFormat.Custom; dt.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
                dt.Left = Asm.S(14); dt.Top = Asm.S(18); dt.Width = Asm.S(272);
                try { dt.Value = pos ?? DateTime.Now; } catch { }
                var ok = new Button(); ok.Text = "Go"; ok.DialogResult = DialogResult.OK; ok.Left = Asm.S(130); ok.Top = Asm.S(64); ok.Width = Asm.S(76);
                var cancel = new Button(); cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel; cancel.Left = Asm.S(210); cancel.Top = Asm.S(64); cancel.Width = Asm.S(76);
                f.Controls.Add(dt); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                DateTime to = dt.Value;
                pos = to;
                holdUntil = unchecked(Environment.TickCount + 4000);
                if (to < winStart || to >= WinEnd) { if (spanIdx == 0) winStart = to.Date; else winStart = to.AddMinutes(-Spans[spanIdx] * 0.1); }
                Invalidate();
                Action<DateTime> h = Seek;
                if (h != null) h(to);
            }
        }

        DateTime WinEnd { get { return winStart.AddMinutes(Spans[spanIdx]); } }

        void SetSpan(int idx, DateTime center)
        {
            spanIdx = Math.Max(0, Math.Min(Spans.Length - 1, idx));
            if (spanIdx == 0) winStart = center.Date;
            else winStart = center.AddMinutes(-Spans[spanIdx] / 2.0);
            Invalidate();
        }

        /// <summary>Posisi playback terkini; diabaikan saat diseret atau sesaat setelah loncat.</summary>
        public void SetPosition(DateTime local, bool force)
        {
            if (dragging) return;
            if (!force && unchecked(Environment.TickCount - holdUntil) < 0) return;
            pos = local;
            if (local < winStart || local >= WinEnd)
            {
                if (spanIdx == 0) winStart = local.Date;
                else winStart = local.AddMinutes(-Spans[spanIdx] * 0.1);   // geser jendela, posisi di dekat awal
            }
            Invalidate();
        }

        int XOf(DateTime t)
        {
            double f = (t - winStart).TotalMinutes / Spans[spanIdx];
            return Pad() + (int)(f * (Width - 2 * Pad()));
        }

        DateTime TimeAt(int x)
        {
            double f = (double)(x - Pad()) / Math.Max(1, Width - 2 * Pad());
            f = Math.Max(0, Math.Min(1, f));
            return winStart.AddMinutes(f * Spans[spanIdx]);
        }

        static int Pad() { return Asm.S(14); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);   // latar menyatu dengan area gambar
            int top = Asm.S(20), h = Asm.S(10);
            var track = new Rectangle(Pad(), top, Math.Max(1, Width - 2 * Pad()), h);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var gp = Asm.RoundRect(track, Math.Max(2, track.Height / 2)))
            using (var b = new SolidBrush(Asm.Bar)) g.FillPath(b, gp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            if (selA.HasValue && selB.HasValue)
            {
                DateTime sa = selA.Value < selB.Value ? selA.Value : selB.Value, sb = selA.Value < selB.Value ? selB.Value : selA.Value;
                int xa = Math.Max(track.Left, Math.Min(track.Right, XOf(sa))), xb = Math.Max(track.Left, Math.Min(track.Right, XOf(sb)));
                if (xb > xa)
                    using (var b = new SolidBrush(Color.FromArgb(150, 5, 150, 105))) g.FillRectangle(b, xa, track.Top - Asm.S(3), xb - xa, track.Height + Asm.S(6));
                using (var p = new Pen(Color.FromArgb(52, 211, 153), 2))
                {
                    if (sa >= winStart && sa <= WinEnd) g.DrawLine(p, xa, track.Top - Asm.S(6), xa, track.Bottom + Asm.S(6));
                    if (sb >= winStart && sb <= WinEnd) g.DrawLine(p, xb, track.Top - Asm.S(6), xb, track.Bottom + Asm.S(6));
                }
                Font rf = Asm.F(8f, false);
                string rs = "range " + sa.ToString("HH:mm:ss") + " - " + sb.ToString("HH:mm:ss") + "  (" + (int)(sb - sa).TotalMinutes + " min)";
                using (var tb = new SolidBrush(Color.FromArgb(110, 231, 183))) g.DrawString(rs, rf, tb, Width - Asm.S(260), Asm.S(1));
            }

            DateTime shown = dragging ? dragTime : (pos ?? winStart);
            bool havePos = dragging || pos.HasValue;
            // bagian yang sudah dilewati
            if (havePos)
            {
                int px = Math.Max(track.Left, Math.Min(track.Right, XOf(shown)));
                using (var b = new SolidBrush(Asm.BarOpen)) g.FillRectangle(b, track.Left, track.Top, px - track.Left, track.Height);
            }

            // tanda jam
            int stepMin = TickMin[spanIdx];
            DateTime t = winStart.Date;
            while (t < winStart) t = t.AddMinutes(stepMin);
            Font tf = Asm.F(7.5f, false);     // font dari Asm.F dipakai bersama (cache): jangan di-Dispose
            using (var pen = new Pen(Asm.Muted))
            using (var tb = new SolidBrush(Asm.Muted))
            {
                int lastLabelX = -1000;
                for (; t <= WinEnd; t = t.AddMinutes(stepMin))
                {
                    int x = XOf(t);
                    g.DrawLine(pen, x, track.Bottom, x, track.Bottom + Asm.S(4));
                    string s = t.ToString(spanIdx == 0 && t.Hour == 0 && t.Minute == 0 ? "dd/MM" : "HH:mm");
                    SizeF sz = g.MeasureString(s, tf);
                    if (x - sz.Width / 2 > lastLabelX + Asm.S(4))
                    {
                        g.DrawString(s, tf, tb, x - sz.Width / 2, track.Bottom + Asm.S(4));
                        lastLabelX = (int)(x + sz.Width / 2);
                    }
                }
                g.DrawString("zoom: " + SpanText[spanIdx] + "   (wheel = zoom, right-click = menu: go to / range / download)", tf, tb, Pad(), Asm.S(1));
            }

            // penanda posisi
            if (havePos && shown >= winStart && shown <= WinEnd)
            {
                int x = XOf(shown);
                using (var pen = new Pen(dragging ? Asm.Accent : Asm.Danger, 2))
                    g.DrawLine(pen, x, track.Top - Asm.S(4), x, track.Bottom + Asm.S(2));
                {
                    Font f = Asm.F(8.5f, true);
                    string s = shown.ToString("HH:mm:ss");
                    SizeF sz = g.MeasureString(s, f);
                    float lx = Math.Max(2, Math.Min(Width - sz.Width - 2, x - sz.Width / 2));
                    using (var bg = new SolidBrush(dragging ? Asm.Accent : Asm.Danger))
                        g.FillRectangle(bg, lx - 2, 0, sz.Width + 4, sz.Height);
                    using (var tb = new SolidBrush(Color.White)) g.DrawString(s, f, tb, lx, 0);
                }
            }
            else if (havePos)
            {
                Font f = Asm.F(8f, false);
                using (var tb = new SolidBrush(Asm.Danger))
                    g.DrawString(shown.ToString("yyyy/MM/dd HH:mm:ss") + " (di luar jendela)", f, tb, Width - Asm.S(190), Asm.S(1));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            menuTime = TimeAt(e.X);
            if (e.Button != MouseButtons.Left) return;
            if (selA.HasValue && selB.HasValue)
            {
                if (Math.Abs(e.X - XOf(selA.Value)) <= Asm.S(6)) { dragHandle = 1; Capture = true; return; }
                if (Math.Abs(e.X - XOf(selB.Value)) <= Asm.S(6)) { dragHandle = 2; Capture = true; return; }
            }
            dragging = true;
            dragTime = TimeAt(e.X);
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragHandle != 0) { if (dragHandle == 1) selA = TimeAt(e.X); else selB = TimeAt(e.X); Invalidate(); return; }
            if (selA.HasValue && selB.HasValue)
                Cursor = (Math.Abs(e.X - XOf(selA.Value)) <= Asm.S(6) || Math.Abs(e.X - XOf(selB.Value)) <= Asm.S(6)) ? Cursors.SizeWE : Cursors.Hand;
            if (!dragging) return;
            dragTime = TimeAt(e.X);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { menuTime = TimeAt(e.X); menu.Show(this, e.Location); return; }
            if (dragHandle != 0) { dragHandle = 0; Capture = false; return; }
            if (!dragging) return;
            dragging = false;
            Capture = false;
            pos = TimeAt(e.X);
            holdUntil = unchecked(Environment.TickCount + 4000);
            Invalidate();
            Action<DateTime> h = Seek;
            if (h != null) h(pos.Value);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            DateTime at = TimeAt(e.X);
            SetSpan(spanIdx + (e.Delta > 0 ? 1 : -1), at);
            if (spanIdx != 0) winStart = at.AddMinutes(-Spans[spanIdx] * ((double)(e.X - Pad()) / Math.Max(1, Width - 2 * Pad())));
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }
}
