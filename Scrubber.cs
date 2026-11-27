using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace IproNvrTool
{
    public enum PbCommand { SkipBack, Rew, PrevFrame, Reverse, Pause, Play, NextFrame, FastForward, SkipForward }

    /// <summary>Bar playback ala XProtect Smart Client di bawah area gambar: kontrol putar di atas, garis waktu yang bergerak di bawah
    /// penanda tetap di tengah, tombol PLAYBACK / LIVE, dan slider zoom. Saat live hanya tombol PLAYBACK / LIVE yang tampil.
    /// Seret garis waktu atau klik untuk loncat; roda mouse = zoom; klik kanan = menu.</summary>
    public class Scrubber : Control
    {
        static readonly int[] Spans = { 60, 300, 600, 1800, 3600, 10800, 21600, 43200, 86400 };   // detik yang terlihat
        static readonly int[] TickMajor = { 10, 60, 120, 300, 600, 1800, 3600, 7200, 14400 };     // jarak label (detik)
        static readonly int[] TickMinor = { 2, 10, 30, 60, 120, 300, 600, 1800, 3600 };
        static readonly string[] SpanText = { "1 menit", "5 menit", "10 menit", "30 menit", "1 jam", "3 jam", "6 jam", "12 jam", "24 jam" };
        static readonly double[] SpeedValues = { 0.25, 0.5, 1, 2, 4, 8, 16 };
        static readonly Color SelColor = Color.FromArgb(250, 204, 21);

        class Hit
        {
            public string Id;
            public Rectangle R;
            public string Tip;
            public Action Act;
        }

        int spanIdx = 1;
        bool playback;
        string mode = "STOP";
        double speed = 1;
        int step = 1;
        string camera = "";
        DateTime? pos;                  // posisi playback (waktu lokal) = waktu di bawah penanda tengah
        bool pressing, panned, zoomDrag;
        int pressX;
        DateTime pressPos, dragTime;
        int holdUntil;                  // abaikan posisi lama dari engine sebentar setelah loncat
        DateTime? holdTarget;
        DateTime? selA, selB;           // rentang untuk download
        int dragHandle;                 // 0 tidak ada, 1 = awal, 2 = akhir
        DateTime menuTime;
        string hotId = "", downId = "";
        readonly ContextMenuStrip menu;
        readonly ContextMenuStrip speedMenu;
        readonly ToolTip tips = new ToolTip();

        /// <summary>Loncat ke waktu lokal ini.</summary>
        public event Action<DateTime> Seek;
        /// <summary>Dipicu bila pengguna memilih download rentang (waktu lokal awal, akhir).</summary>
        public event Action<DateTime, DateTime> ExportRange;
        public event Action<PbCommand> Command;
        public event Action<double> SpeedChosen;
        public event Action LiveRequested;
        public event Action PlaybackRequested;

        public bool Dragging { get { return (pressing && panned) || dragHandle != 0; } }

        public Scrubber()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = CompactHeight;
            Dock = DockStyle.Bottom;
            BackColor = Asm.Bg;
            Tag = "nostyle";
            menu = Asm.Menu();
            for (int i = 0; i < Spans.Length; i++)
            {
                int k = i;
                Asm.Item(menu, "Zoom " + SpanText[i], delegate { SetSpan(k); });
            }
            menu.Items.Add(new ToolStripSeparator());
            Asm.Item(menu, "Go to date/time...", delegate { GoTo(); });
            Asm.Item(menu, "Set range start here", delegate { SetSel(true); });
            Asm.Item(menu, "Set range end here", delegate { SetSel(false); });
            Asm.Item(menu, "Download selected range...", delegate { DoExport(); });
            Asm.Item(menu, "Clear range", delegate { ClearSel(); });
            Asm.StyleMenu(menu);
            speedMenu = Asm.Menu();
            foreach (double v0 in SpeedValues)
            {
                double v = v0;
                var it = Asm.Item(speedMenu, SpeedText(v), delegate { speed = v; Invalidate(); Action<double> h = SpeedChosen; if (h != null) h(v); });
                it.Tag = v;
            }
            Asm.StyleMenu(speedMenu);
        }

        static int CompactHeight { get { return Asm.S(34); } }
        static int FullHeight { get { return Asm.S(128); } }

        // ---------- keadaan dari luar ----------

        /// <summary>Dipanggil berkala oleh tampilan: sedang playback atau live, mode engine, kecepatan, STEP FF/REW, dan nama kamera.</summary>
        public void SetState(bool isPlayback, string engineMode, double engineSpeed, int ffStep, string cameraName)
        {
            engineMode = engineMode ?? "STOP";
            cameraName = cameraName ?? "";
            if (isPlayback == playback && engineMode == mode && engineSpeed == speed && ffStep == step && cameraName == camera) return;
            if (isPlayback != playback)
            {
                playback = isPlayback;
                Height = playback ? FullHeight : CompactHeight;
                pressing = panned = zoomDrag = false;
                dragHandle = 0;
                if (!playback) { pos = null; holdTarget = null; selA = selB = null; }
            }
            mode = engineMode; speed = engineSpeed; step = ffStep; camera = cameraName;
            Invalidate();
        }

        /// <summary>Posisi playback terkini; diabaikan saat diseret atau sesaat setelah loncat. force = posisi tujuan loncat.</summary>
        public void SetPosition(DateTime local, bool force)
        {
            if (Dragging) return;
            if (force) Hold(local);
            else if (holdTarget.HasValue && unchecked(Environment.TickCount - holdUntil) < 0
                && Math.Abs((local - holdTarget.Value).TotalSeconds) > 10) return;   // masih posisi lama
            else holdTarget = null;
            if (pos == local) return;
            pos = local;
            Invalidate();
        }

        void Hold(DateTime target)
        {
            holdTarget = target;
            holdUntil = unchecked(Environment.TickCount + 4000);
        }

        // ---------- geometri ----------

        Rectangle RulerRect { get { return new Rectangle(0, Asm.S(34), Width, Asm.S(40)); } }
        Rectangle TrackRect { get { return new Rectangle(0, Asm.S(76), Width, Asm.S(18)); } }
        Rectangle TimeBox { get { return new Rectangle(Width / 2 - Asm.S(105), Asm.S(35), Asm.S(210), Asm.S(19)); } }
        int BottomY { get { return playback ? Asm.S(99) : Asm.S(4); } }
        Rectangle ZoomTrack { get { return new Rectangle(Width - Asm.S(150), BottomY, Asm.S(110), Asm.S(26)); } }

        double SecondsPerPixel { get { return (double)Spans[spanIdx] / Math.Max(1, Width); } }

        DateTime Center { get { return Dragging && pressing ? dragTime : (pos ?? DateTime.Now); } }

        int XOf(DateTime t)
        {
            double x = Width / 2 + (t - Center).TotalSeconds / SecondsPerPixel;
            return (int)Math.Max(-10000, Math.Min(10000, x));
        }

        DateTime TimeAt(int x) { return Center.AddSeconds((x - Width / 2) * SecondsPerPixel); }

        static DateTime NotFuture(DateTime t) { DateTime now = DateTime.Now; return t > now ? now : t; }

        List<Hit> Hits()
        {
            var l = new List<Hit>();
            Action<string, Rectangle, string, Action> add = delegate(string id, Rectangle r, string tip, Action act)
            {
                var h = new Hit(); h.Id = id; h.R = r; h.Tip = tip; h.Act = act; l.Add(h);
            };
            int bw = Asm.S(80);
            add("pb", new Rectangle(Width / 2 - bw, BottomY, bw, Asm.S(24)), "Playback (mulai 5 menit lalu bila sedang live)", delegate { if (!playback) Raise(PlaybackRequested); });
            add("live", new Rectangle(Width / 2, BottomY, bw, Asm.S(24)), "Kembali ke live", delegate { if (playback) Raise(LiveRequested); });
            if (!playback) return l;

            int y = Asm.S(4), h2 = Asm.S(27), w = Asm.S(30), gap = Asm.S(4);
            add("sel", new Rectangle(Asm.S(8), y, w, h2), "Pilih rentang waktu di garis waktu", delegate { ToggleSel(); });
            add("seldlg", new Rectangle(Asm.S(8) + w + gap, y, w, h2), "Isi waktu awal dan akhir rentang", delegate { SelDialog(); });
            int leftEnd = Asm.S(8) + 2 * (w + gap);
            if (selA.HasValue && selB.HasValue)
            {
                add("dl", new Rectangle(leftEnd, y, Asm.S(88), h2), "Download rentang terpilih", delegate { DoExport(); });
                add("clear", new Rectangle(leftEnd + Asm.S(88) + gap, y, w, h2), "Hapus rentang", delegate { ClearSel(); });
                leftEnd += Asm.S(88) + w + 2 * gap;
            }
            string[] ids = { "skipb", "rew", "prevf", "rev", "pause", "play", "nextf", "ff", "skipf" };
            string[] captions = { "Mundur sebesar SKIP TIME", "Fast reverse (tekan lagi = lebih cepat)", "Frame sebelumnya", "Putar mundur", "Pause",
                             "Putar maju", "Frame berikutnya", "Fast forward (tekan lagi = lebih cepat)", "Maju sebesar SKIP TIME" };
            // jendela sempit: tombol putar dikecilkan agar tombol kecepatan tetap terlihat
            int speedW = Asm.S(76), margin = Asm.S(8);
            int room = Width - leftEnd - speedW - 3 * margin;
            int pw = Math.Max(Asm.S(16), Math.Min(w, (room - (ids.Length - 1) * gap) / ids.Length));
            int total = ids.Length * pw + (ids.Length - 1) * gap;
            int x = Math.Max(leftEnd + margin, Math.Min(Width / 2 - total / 2, Width - speedW - 2 * margin - total));
            for (int i = 0; i < ids.Length; i++)
            {
                PbCommand c = (PbCommand)i;
                add(ids[i], new Rectangle(x + i * (pw + gap), y, pw, h2), captions[i], delegate { Action<PbCommand> a = Command; if (a != null) a(c); });
            }
            Rectangle sp = new Rectangle(Math.Max(x + total + margin, Width - speedW - margin), y, speedW, h2);
            add("speed", sp, "Kecepatan putar (0.25x - 16x)", delegate { ShowSpeedMenu(sp); });
            add("time", TimeBox, "Pergi ke tanggal / jam", delegate { GoTo(); });
            Rectangle z = ZoomTrack;
            add("zout", new Rectangle(z.Left - Asm.S(24), z.Top, Asm.S(22), Asm.S(24)), "Perkecil (rentang lebih panjang)", delegate { SetSpan(spanIdx + 1); });
            add("zin", new Rectangle(z.Right + Asm.S(2), z.Top, Asm.S(22), Asm.S(24)), "Perbesar (rentang lebih pendek)", delegate { SetSpan(spanIdx - 1); });
            return l;
        }

        Hit HitAt(Point p)
        {
            foreach (Hit h in Hits()) if (h.R.Contains(p)) return h;
            return null;
        }

        static void Raise(Action a) { if (a != null) a(); }

        // ---------- aksi ----------

        void SetSpan(int idx)
        {
            spanIdx = Math.Max(0, Math.Min(Spans.Length - 1, idx));
            Invalidate();
        }

        void ZoomFromX(int x)
        {
            Rectangle z = ZoomTrack;
            double f = Math.Max(0, Math.Min(1, (double)(x - z.Left) / Math.Max(1, z.Width)));
            SetSpan((int)Math.Round((1 - f) * (Spans.Length - 1)));   // kanan = diperbesar
        }

        void JumpTo(DateTime t)
        {
            t = NotFuture(t);
            Hold(t);
            pos = t;
            Invalidate();
            Action<DateTime> h = Seek;
            if (h != null) h(t);
        }

        void ShowSpeedMenu(Rectangle r)
        {
            if (mode == "FF" || mode == "REW") return;   // kecepatan FF/REW diatur NVR (STEP)
            foreach (ToolStripMenuItem it in speedMenu.Items) it.Checked = (double)it.Tag == speed;
            speedMenu.Show(this, new Point(r.Left, r.Bottom));
        }

        void ToggleSel()
        {
            if (selA.HasValue) { ClearSel(); return; }
            double q = Spans[spanIdx] * 0.15;
            selA = Center.AddSeconds(-q);
            selB = NotFuture(Center.AddSeconds(q));
            Invalidate();
        }

        void ClearSel() { selA = selB = null; Invalidate(); }

        void SetSel(bool start)
        {
            DateTime t = menuTime;
            if (start) { selA = t; if (!selB.HasValue || selB.Value <= t) selB = t.AddMinutes(5); }
            else { selB = t; if (!selA.HasValue || selA.Value >= t) selA = t.AddMinutes(-5); }
            Invalidate();
        }

        void DoExport()
        {
            if (!selA.HasValue || !selB.HasValue) { MessageBox.Show("Select a range first: use the range button at the top left of the timeline, or right-click the timeline."); return; }
            Action<DateTime, DateTime> h = ExportRange;
            if (h == null) return;
            DateTime a = selA.Value < selB.Value ? selA.Value : selB.Value, b = selA.Value < selB.Value ? selB.Value : selA.Value;
            h(a, b);
        }

        static DateTimePicker Picker(Form f, int top, DateTime value)
        {
            var dt = new DateTimePicker();
            dt.Format = DateTimePickerFormat.Custom; dt.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dt.Left = Asm.S(80); dt.Top = top; dt.Width = Asm.S(240);
            try { dt.Value = value; } catch { }
            f.Controls.Add(dt);
            return dt;
        }

        static Form Dialog(string title, int height, string okText)
        {
            var f = new Form();
            f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.ClientSize = new Size(Asm.S(340), height);
            f.BackColor = Asm.Panel; f.ForeColor = Asm.Text;
            var ok = new Button(); ok.Text = okText; ok.DialogResult = DialogResult.OK; ok.SetBounds(Asm.S(160), height - Asm.S(44), Asm.S(76), Asm.S(30));
            var cancel = new Button(); cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(Asm.S(244), height - Asm.S(44), Asm.S(76), Asm.S(30));
            Asm.FlatButton(ok); Asm.FlatButton(cancel);
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f;
        }

        /// <summary>Dialog "Go to": pilih tanggal dan jam lalu loncat ke sana.</summary>
        void GoTo()
        {
            DateTime to;
            using (Form f = Dialog("Go to", Asm.S(110), "Go"))
            {
                DateTimePicker dt = Picker(f, Asm.S(18), pos ?? DateTime.Now);
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                to = dt.Value;
            }
            JumpTo(to);
        }

        void SelDialog()
        {
            using (Form f = Dialog("Select time range", Asm.S(150), "OK"))
            {
                var la = new Label(); la.Text = "Start"; la.SetBounds(Asm.S(14), Asm.S(20), Asm.S(60), Asm.S(22)); f.Controls.Add(la);
                var lb = new Label(); lb.Text = "End"; lb.SetBounds(Asm.S(14), Asm.S(58), Asm.S(60), Asm.S(22)); f.Controls.Add(lb);
                DateTimePicker a = Picker(f, Asm.S(18), selA ?? Center.AddMinutes(-1));
                DateTimePicker b = Picker(f, Asm.S(56), selB ?? Center);
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (b.Value <= a.Value) { MessageBox.Show("End time must be after start time."); return; }
                selA = a.Value; selB = b.Value;
            }
            Invalidate();
        }

        // ---------- gambar ----------

        static string SpeedText(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture) + "x"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            using (var p = new Pen(Asm.Border)) g.DrawLine(p, 0, 0, Width, 0);
            if (playback) PaintTimeline(g);
            foreach (Hit h in Hits()) PaintHit(g, h);
            if (playback) PaintBottom(g);
        }

        void PaintTimeline(Graphics g)
        {
            Rectangle ru = RulerRect, tr = TrackRect, box = TimeBox;
            DateTime c = Center;
            using (var b = new SolidBrush(Asm.Panel)) g.FillRectangle(b, 0, ru.Top, Width, tr.Bottom - ru.Top);
            using (var b = new SolidBrush(Asm.Bar)) g.FillRectangle(b, tr);
            // waktu yang belum terjadi
            int xNow = XOf(DateTime.Now);
            if (xNow < Width) using (var b = new SolidBrush(Asm.Bg)) g.FillRectangle(b, Math.Max(0, xNow), tr.Top, Width - Math.Max(0, xNow), tr.Height);

            // tanda waktu
            long major = TickMajor[spanIdx] * TimeSpan.TicksPerSecond, minor = TickMinor[spanIdx] * TimeSpan.TicksPerSecond;
            DateTime from = TimeAt(0), to = TimeAt(Width);
            Font tf = Asm.F(9f, false);        // font dari Asm.F dipakai bersama (cache): jangan di-Dispose
            using (var pen = new Pen(Asm.Muted))
            using (var tb = new SolidBrush(Asm.Muted))
            {
                for (long k = from.Ticks - from.Ticks % minor; k <= to.Ticks; k += minor)
                {
                    var t = new DateTime(k);
                    int x = XOf(t);
                    bool big = k % major == 0;
                    g.DrawLine(pen, x, tr.Top - (big ? Asm.S(8) : Asm.S(4)), x, tr.Top - 2);
                    if (!big) continue;
                    string s = t.TimeOfDay.Ticks == 0 ? t.ToString("dd/MM") : t.ToString(TickMajor[spanIdx] < 60 ? "HH:mm:ss" : "HH:mm");
                    SizeF sz = g.MeasureString(s, tf);
                    g.DrawString(s, tf, tb, x - sz.Width / 2, box.Bottom);
                }
            }

            // rentang terpilih
            if (selA.HasValue && selB.HasValue)
            {
                int xa = XOf(selA.Value < selB.Value ? selA.Value : selB.Value), xb = XOf(selA.Value < selB.Value ? selB.Value : selA.Value);
                var r = new Rectangle(xa, tr.Top - Asm.S(3), Math.Max(1, xb - xa), tr.Height + Asm.S(5));
                using (var b = new SolidBrush(Color.FromArgb(60, SelColor))) g.FillRectangle(b, r);
                using (var p = new Pen(SelColor, 1)) g.DrawRectangle(p, r);
                using (var b = new SolidBrush(SelColor))
                {
                    g.FillRectangle(b, xa - Asm.S(3), r.Top, Asm.S(6), r.Height);
                    g.FillRectangle(b, xb - Asm.S(3), r.Top, Asm.S(6), r.Height);
                }
            }

            if (camera.Length > 0)
            {
                Font cf = Asm.F(9f, false);
                SizeF sz = g.MeasureString(camera, cf);
                using (var b = new SolidBrush(Color.FromArgb(190, Asm.Bg))) g.FillRectangle(b, 0, tr.Top, sz.Width + Asm.S(12), tr.Height);
                using (var tb = new SolidBrush(Asm.Label)) g.DrawString(camera, cf, tb, Asm.S(6), tr.Top + (tr.Height - sz.Height) / 2);
            }

            // penanda tetap di tengah + kotak waktu
            using (var p = new Pen(Color.White, 2)) g.DrawLine(p, Width / 2, box.Bottom, Width / 2, tr.Bottom + Asm.S(2));
            bool moving = pressing && panned;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = Asm.RoundRect(box, Asm.S(4)))
            using (var b = new SolidBrush(moving ? Asm.Accent : (hotId == "time" ? Color.White : Color.FromArgb(235, 240, 237)))) g.FillPath(b, gp);
            g.SmoothingMode = SmoothingMode.Default;
            string txt = pos.HasValue || moving ? Asm.FormatTime(c) : "menunggu data...";
            TextRenderer.DrawText(g, txt, Asm.F(10f, true), box, Asm.Bg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void PaintBottom(Graphics g)
        {
            Rectangle z = ZoomTrack;
            int mid = z.Top + Asm.S(12);
            using (var p = new Pen(Asm.Muted, 2)) g.DrawLine(p, z.Left, mid, z.Right, mid);
            int kx = z.Left + (int)((double)(Spans.Length - 1 - spanIdx) / (Spans.Length - 1) * z.Width);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - Asm.S(6), mid - Asm.S(6), Asm.S(12), Asm.S(12));
            g.SmoothingMode = SmoothingMode.Default;
            var spanRect = new Rectangle(z.Left - Asm.S(110), z.Top, Asm.S(82), Asm.S(24));
            if (spanRect.Left > Width / 2 + Asm.S(80))   // jendela sempit: teks tidak boleh menimpa tombol LIVE
                TextRenderer.DrawText(g, SpanText[spanIdx], Asm.F(9f, false), spanRect, Asm.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (selA.HasValue && selB.HasValue)
            {
                DateTime a = selA.Value < selB.Value ? selA.Value : selB.Value, b = selA.Value < selB.Value ? selB.Value : selA.Value;
                TimeSpan d = b - a;
                string s = "Rentang " + a.ToString("HH:mm:ss") + " - " + b.ToString("HH:mm:ss") + "  (" + (int)d.TotalHours + ":" + d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00") + ")";
                TextRenderer.DrawText(g, s, Asm.F(9f, false), new Rectangle(Asm.S(8), z.Top, Math.Max(0, Width / 2 - Asm.S(96)), Asm.S(24)), SelColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        void PaintHit(Graphics g, Hit h)
        {
            if (h.Id == "time") return;   // digambar bersama garis waktu
            Rectangle r = h.R;
            bool toggle = h.Id == "pb" || h.Id == "live";
            bool on = (h.Id == "pb" && playback) || (h.Id == "live" && !playback)
                || (h.Id == "rev" && mode == "BPLAY") || (h.Id == "pause" && mode == "PAUSE") || (h.Id == "play" && mode == "PLAY")
                || (h.Id == "rew" && mode == "REW") || (h.Id == "ff" && mode == "FF") || (h.Id == "sel" && selA.HasValue);
            Color bg = on ? Asm.TabLine : (h.Id == downId ? Asm.TabBg : (h.Id == hotId ? Asm.BarHot : Asm.Panel2));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            if (!toggle) using (var p = new Pen(on ? Asm.Accent : Asm.BtnBorder)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            Color fg = on ? Color.White : Asm.Text;
            int pad = Asm.S(6);
            pad = Math.Min(pad, r.Width / 5);
            var icon = new Rectangle(r.X + pad, r.Y + pad, r.Width - 2 * pad, r.Height - 2 * pad);
            switch (h.Id)
            {
                case "pb": DrawLabel(g, "PLAYBACK", r, fg); break;
                case "live": DrawLabel(g, "LIVE", r, fg); break;
                case "skipb": AsmIcons.Draw(g, AsmIcon.PrevChap, icon, fg); break;
                case "rew": AsmIcons.Draw(g, AsmIcon.FastRew, icon, fg); break;
                case "prevf": AsmIcons.Draw(g, AsmIcon.StepBack, icon, fg); break;
                case "rev": AsmIcons.Draw(g, AsmIcon.Reverse, icon, fg); break;
                case "pause": AsmIcons.Draw(g, AsmIcon.Pause, icon, fg); break;
                case "play": AsmIcons.Draw(g, AsmIcon.Play, icon, fg); break;
                case "nextf": AsmIcons.Draw(g, AsmIcon.StepFwd, icon, fg); break;
                case "ff": AsmIcons.Draw(g, AsmIcon.FastFwd, icon, fg); break;
                case "skipf": AsmIcons.Draw(g, AsmIcon.NextChap, icon, fg); break;
                case "speed": DrawLabel(g, mode == "FF" || mode == "REW" ? "STEP " + step : SpeedText(speed) + "  ▾", r, fg); break;
                case "sel": DrawLabel(g, "[■]", r, fg); break;
                case "seldlg": DrawLabel(g, "[…]", r, fg); break;
                case "dl": DrawLabel(g, "Download", r, fg); break;
                case "clear": AsmIcons.Draw(g, AsmIcon.Close, icon, fg); break;
                case "zout": AsmIcons.Draw(g, AsmIcon.Minus, new Rectangle(r.X + 2, r.Y + 3, r.Width - 4, r.Height - 6), fg); break;
                case "zin": AsmIcons.Draw(g, AsmIcon.Plus, new Rectangle(r.X + 2, r.Y + 3, r.Width - 4, r.Height - 6), fg); break;
            }
        }

        static void DrawLabel(Graphics g, string s, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, s, Asm.F(9.5f, true), r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------- mouse ----------

        bool InTimeArea(Point p) { return playback && p.Y >= RulerRect.Top && p.Y <= TrackRect.Bottom + Asm.S(3); }

        int HandleAt(Point p)
        {
            if (!InTimeArea(p) || !selA.HasValue || !selB.HasValue) return 0;
            if (Math.Abs(p.X - XOf(selA.Value)) <= Asm.S(6)) return 1;
            if (Math.Abs(p.X - XOf(selB.Value)) <= Asm.S(6)) return 2;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            menuTime = TimeAt(e.X);
            if (e.Button != MouseButtons.Left) return;
            dragHandle = HandleAt(e.Location);
            if (dragHandle != 0) { Capture = true; return; }
            Hit h = HitAt(e.Location);
            if (h != null) { downId = h.Id; Invalidate(); return; }
            if (playback && ZoomTrack.Contains(e.Location)) { zoomDrag = true; Capture = true; ZoomFromX(e.X); return; }
            if (InTimeArea(e.Location))
            {
                pressPos = Center;
                dragTime = pressPos;
                pressX = e.X;
                pressing = true;
                panned = false;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragHandle != 0)
            {
                DateTime t = NotFuture(TimeAt(e.X));
                if (dragHandle == 1) selA = t; else selB = t;
                Invalidate();
                return;
            }
            if (zoomDrag) { ZoomFromX(e.X); return; }
            if (pressing)
            {
                if (Math.Abs(e.X - pressX) >= Asm.S(4)) panned = true;
                if (panned) { dragTime = NotFuture(pressPos.AddSeconds((pressX - e.X) * SecondsPerPixel)); Invalidate(); }
                return;
            }
            Hit h = HitAt(e.Location);
            Cursor = HandleAt(e.Location) != 0 ? Cursors.SizeWE : (h != null || InTimeArea(e.Location) || (playback && ZoomTrack.Contains(e.Location)) ? Cursors.Hand : Cursors.Default);
            string id = h != null ? h.Id : "";
            if (id != hotId)
            {
                hotId = id;
                tips.SetToolTip(this, h != null ? h.Tip : "");
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hotId.Length > 0) { hotId = ""; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { if (playback) { menuTime = TimeAt(e.X); menu.Show(this, e.Location); } return; }
            if (e.Button != MouseButtons.Left) return;
            if (dragHandle != 0) { dragHandle = 0; Capture = false; Invalidate(); return; }
            if (zoomDrag) { zoomDrag = false; Capture = false; return; }
            if (downId.Length > 0)
            {
                string id = downId;
                downId = "";
                Invalidate();
                Hit h = HitAt(e.Location);
                if (h != null && h.Id == id) h.Act();
                return;
            }
            if (pressing)
            {
                DateTime to = panned ? dragTime : TimeAt(e.X);
                pressing = false;
                panned = false;
                Capture = false;
                JumpTo(to);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (playback) SetSpan(spanIdx + (e.Delta > 0 ? -1 : 1));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { menu.Dispose(); speedMenu.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
