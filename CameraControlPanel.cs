using System;
using System.Drawing;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Panel di bawah area gambar pada Live window: tab Camera (PTZ, fokus, iris, preset, AUX) dan tab Playback
    /// (pilih waktu lalu buka di Operation window, Quick download). Kamera sasaran = area yang sedang dipilih.</summary>
    public class CameraControlPanel : Panel
    {
        readonly Func<ViewCell> selected;
        readonly VTabs tabs = new VTabs();
        readonly Panel tCam = new Panel();
        readonly Panel tPb = new Panel();
        PtzPad pad;
        AsmButton btnCenter;
        TrackBar trkSpeed, trkZoom;
        NumericUpDown numPreset;
        ComboBox cmbAux;
        DateTimePicker dtpGo;
        readonly System.Windows.Forms.Timer holdTimer = new System.Windows.Forms.Timer();
        readonly SerialWorker ptz = new SerialWorker(200);
        readonly ToolTip tips = new ToolTip();
        int holdPan, holdTilt, holdZoom;
        NvrRecord holdNvr; int holdCh;

        public event Action<CamRef> QuickDownloadRequested;

        public bool CenterMode { get { return btnCenter != null && btnCenter.Checked; } }

        public CameraControlPanel(Func<ViewCell> selected)
        {
            this.selected = selected;
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Height = Asm.S(181);
            tabs.Dock = DockStyle.Fill;
            tCam.Tag = "nostyle"; tPb.Tag = "nostyle";
            tCam.BackColor = Asm.Bg;
            tPb.BackColor = Asm.Panel2;
            tabs.AddPage("Camera", tCam);
            tabs.AddPage("HDD", tPb);
            Controls.Add(tabs);
            holdTimer.Interval = 350;
            holdTimer.Tick += delegate
            {
                if (holdPan != 0 || holdTilt != 0 || holdZoom != 0) SendMove(holdPan, holdTilt, holdZoom);
            };
            BuildCamera();
            BuildPlayback();
            RefreshState();
        }

        AsmButton OpBtn(AsmIcon icon, string label, string tip, EventHandler click, int w)
        {
            var b = new AsmButton();
            b.Icon = icon;
            b.Label = label;
            b.Width = Asm.S(w);
            b.Height = Asm.S(28);
            b.Margin = new Padding(2);
            if (!string.IsNullOrEmpty(tip)) tips.SetToolTip(b, tip);
            if (click != null) b.Click += click;
            return b;
        }

        static Label OpLab(string t)
        {
            var l = new Label();
            l.Text = t;
            l.ForeColor = Asm.Muted;
            l.AutoSize = true;
            l.Margin = new Padding(6, 8, 2, 0);
            l.Tag = "nostyle";
            return l;
        }

        AsmButton HoldBtn(string label, string tip, int dz, string ctl)
        {
            var b = OpBtn(AsmIcon.None, label, tip, null, 54);
            b.MouseDown += delegate
            {
                if (ctl != null) { SendCtl(ctl); return; }
                holdZoom = dz * trkZoom.Value;
                holdPan = 0; holdTilt = 0;
                BeginHold();
                SendMove(0, 0, holdZoom);
                holdTimer.Start();
            };
            b.MouseUp += delegate { if (ctl == null) StopHold(); };
            return b;
        }

        void BuildCamera()
        {
            pad = new PtzPad();
            pad.Width = Asm.S(130);
            pad.Move += delegate(int dx, int dy, double frac)
            {
                int sp = Math.Max(1, (int)Math.Round(frac * trkSpeed.Maximum));
                holdPan = dx * sp; holdTilt = dy * sp; holdZoom = 0;
                BeginHold();
                SendMove(holdPan, holdTilt, 0);
                holdTimer.Start();
            };
            pad.Released += delegate { StopHold(); };

            var cp = new Panel();
            cp.Dock = DockStyle.Fill;
            cp.Tag = "nostyle";
            cp.BackColor = Asm.Bg;
            Action<string, int, int, int> cap = delegate(string t, int x, int y, int w)
            {
                var l = new CapLabel();
                l.Caption = t;
                l.SetBounds(Asm.S(x), Asm.S(y), Asm.S(w), Asm.S(18));
                cp.Controls.Add(l);
            };
            Func<AsmButton, int, int, int, int, AsmButton> put = delegate(AsmButton b, int x, int y, int bw, int bh)
            {
                b.SetBounds(Asm.S(x), Asm.S(y), Asm.S(bw), Asm.S(bh));
                b.Margin = new Padding(0);
                b.Font = Asm.F(11.5f, false);
                cp.Controls.Add(b);
                return b;
            };

            // Zoom / Focus
            put(HoldBtn("Wide -", "Zoom out (tahan)", -1, null), 8, 11, 76, 56);
            btnCenter = OpBtn(AsmIcon.None, "Center", "Klik gambar = pusatkan kamera ke titik itu", null, 76);
            btnCenter.Toggle = true;
            put(btnCenter, 88, 11, 76, 56);
            put(HoldBtn("Tele +", "Zoom in (tahan)", 1, null), 168, 11, 76, 56);
            cap("Zoom", 8, 69, 236);
            put(HoldBtn("Auto", "Auto focus", 0, "CMD=FOCUS&CTRL=AUTO&CTRL=START"), 8, 104, 76, 56);
            put(HoldBtn("Near", "Focus dekat", 0, "CMD=FOCUS&CTRL=NEAR&CTRL=START"), 88, 104, 76, 56);
            put(HoldBtn("Far", "Focus jauh", 0, "CMD=FOCUS&CTRL=FAR&CTRL=START"), 168, 104, 76, 56);
            cap("Focus", 8, 161, 236);

            // Brightness + kecepatan
            put(HoldBtn("-", "Iris tutup", 0, "CMD=IRIS&CTRL=CLOSE&CTRL=START"), 262, 11, 76, 56);
            put(HoldBtn("Reset", "Iris reset", 0, "CMD=IRIS&CTRL=RESET&CTRL=START"), 342, 11, 76, 56);
            put(HoldBtn("+", "Iris buka", 0, "CMD=IRIS&CTRL=OPEN&CTRL=START"), 422, 11, 76, 56);
            cap("Brightness", 262, 69, 236);
            var l1 = new Label(); l1.Text = "Speed"; l1.ForeColor = Asm.Muted; l1.Tag = "nostyle"; l1.SetBounds(Asm.S(262), Asm.S(108), Asm.S(70), Asm.S(20)); cp.Controls.Add(l1);
            trkSpeed = new TrackBar();
            trkSpeed.Minimum = 1; trkSpeed.Maximum = 16; trkSpeed.Value = 8; trkSpeed.TickStyle = TickStyle.None;
            trkSpeed.SetBounds(Asm.S(336), Asm.S(104), Asm.S(162), Asm.S(28)); cp.Controls.Add(trkSpeed);
            var l2 = new Label(); l2.Text = "Zoom spd"; l2.ForeColor = Asm.Muted; l2.Tag = "nostyle"; l2.SetBounds(Asm.S(262), Asm.S(138), Asm.S(70), Asm.S(20)); cp.Controls.Add(l2);
            trkZoom = new TrackBar();
            trkZoom.Minimum = 1; trkZoom.Maximum = 16; trkZoom.Value = 8; trkZoom.TickStyle = TickStyle.None;
            trkZoom.SetBounds(Asm.S(336), Asm.S(134), Asm.S(162), Asm.S(28)); cp.Controls.Add(trkZoom);

            // pad PTZ
            pad.Dock = DockStyle.None;
            pad.SetBounds(Asm.S(516), Asm.S(12), Asm.S(150), Asm.S(150));
            cp.Controls.Add(pad);

            // Preset
            numPreset = new NumericUpDown();
            numPreset.Minimum = 1; numPreset.Maximum = 256; numPreset.Value = 1;
            numPreset.BackColor = Asm.Panel; numPreset.ForeColor = Asm.Text;
            numPreset.SetBounds(Asm.S(682), Asm.S(8), Asm.S(168), Asm.S(26)); cp.Controls.Add(numPreset);
            put(OpBtn(AsmIcon.None, "Go", "Panggil preset", delegate { SendCtl("CMD=PRESET&NO=" + (int)numPreset.Value); }, 80), 682, 38, 82, 36);
            put(OpBtn(AsmIcon.None, "Program", "Simpan posisi saat ini ke preset", delegate
            {
                if (MessageBox.Show("Simpan posisi kamera saat ini ke preset " + (int)numPreset.Value + "?", "Preset", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    SendCtl("CMD=PRESETPRG&NO=" + (int)numPreset.Value);
            }, 80), 768, 38, 82, 36);
            cap("Preset", 682, 76, 168);

            // AUX
            cmbAux = new ComboBox();
            cmbAux.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAux.Items.AddRange(new object[] { "AUX1", "AUX2", "AUX3", "AUX4", "AUX5", "AUX6", "AUX7", "AUX8" });
            cmbAux.SelectedIndex = 0;
            cmbAux.BackColor = Asm.Panel; cmbAux.ForeColor = Asm.Text; cmbAux.FlatStyle = FlatStyle.Flat;
            cmbAux.SetBounds(Asm.S(682), Asm.S(100), Asm.S(168), Asm.S(26)); cp.Controls.Add(cmbAux);
            put(OpBtn(AsmIcon.None, "Open", "AUX open", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=OPEN"); }, 80), 682, 128, 82, 34);
            put(OpBtn(AsmIcon.None, "Close", "AUX close", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=CLOSE"); }, 80), 768, 128, 82, 34);
            cap("AUX", 682, 163, 168);
            tCam.Controls.Add(cp);
        }

        /// <summary>Diisi Live window: playback berjalan di tampilan yang sama.</summary>
        public PlaybackController Pb;
        ComboBox cmbSkip;

        void BuildPlayback()
        {
            ComboBox cs; DateTimePicker dg;
            Panel h = HddPage.Build(tips,
                delegate(Action<PlaybackEngine> a) { if (Pb != null) Pb.Each(a); },
                delegate(int d) { SkipBy(d); },
                delegate(DateTime u) { if (Pb != null && SelRef() != null) Pb.PlayAtUtc(u); },
                delegate { if (Pb != null) Pb.Exit(); },
                delegate { CamRef r = SelRef(); if (r != null && QuickDownloadRequested != null) QuickDownloadRequested(r); },
                out cs, out dg);
            cmbSkip = cs; dtpGo = dg;
            tPb.Controls.Add(h);
        }

        void SkipBy(int dir)
        {
            int[] sec = { 10, 30, 60, 300, 600, 1800, 3600 };
            int sk = sec[Math.Max(0, cmbSkip.SelectedIndex)] * dir;
            if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Skip(sk); });
        }

        /// <summary>Pindah ke tab Playback (saat playback mulai) atau Camera (kembali ke live).</summary>
        public void ShowTab(bool playback)
        {
            tabs.SelectedIndex = playback ? 1 : 0;
        }

        CamRef SelRef()
        {
            ViewCell c = selected();
            return c != null && c.HasCamera ? c.Ref : null;
        }

        /// <summary>Panggil saat pilihan area / isi kamera berubah.</summary>
        static void EnableExceptLabels(Control root, bool on)
        {
            foreach (Control k in root.Controls)
            {
                if (k is Label) continue;
                if (k is Panel) { k.Enabled = true; EnableExceptLabels(k, on); }
                else k.Enabled = on;
            }
        }

        public void RefreshState()
        {
            ViewCell c = selected();
            bool cam = c != null && c.HasCamera && !c.Playback && AsmApp.Can(Perm.Camera);
            EnableExceptLabels(tCam, cam);
            EnableExceptLabels(tPb, AsmApp.Can(Perm.Playback) && ((Pb != null && Pb.Active) || (c != null && c.HasCamera)));
        }

        // ---- PTZ ----

        bool NeedCam(out NvrRecord nvr, out int ch)
        {
            nvr = null; ch = 0;
            ViewCell c = selected();
            if (c == null || !c.HasCamera || c.Playback) return false;
            if (!AsmApp.Can(Perm.Camera)) return false;
            nvr = Dir.Nvr(c.NvrId);
            ch = c.Channel;
            return nvr != null;
        }

        void BeginHold()
        {
            NvrRecord n; int c;
            if (holdNvr == null && NeedCam(out n, out c)) { holdNvr = n; holdCh = c; }
        }

        void SendMove(int pan, int tilt, int zoom)
        {
            NvrRecord nvr; int ch;
            if (holdNvr != null) { nvr = holdNvr; ch = holdCh; }
            else if (!NeedCam(out nvr, out ch)) return;
            ptz.Post(delegate
            {
                NvrSession s = Pool.Control(nvr);
                s.EnsureLogin();
                s.Cmd("directctrl.cgi", "CH=" + ch + "&PAN=" + pan + "&TILT=" + tilt + "&ZOOM=" + zoom + "&PC=AS60");
            });
        }

        void SendCtl(string query)
        {
            NvrRecord nvr; int ch;
            if (!NeedCam(out nvr, out ch)) return;
            ptz.Post(delegate
            {
                NvrSession s = Pool.Control(nvr);
                s.EnsureLogin();
                s.Cmd("camctl.cgi", "CH=" + ch + "&" + query + "&PC=AS60");
            });
        }

        void StopHold()
        {
            holdTimer.Stop();
            holdPan = holdTilt = holdZoom = 0;
            SendMove(0, 0, 0);
            holdNvr = null;
        }

        /// <summary>Klik gambar saat "Click center" aktif: pusatkan kamera ke titik (0..1, 0..1).</summary>
        public void Center(PointF p)
        {
            if (!CenterMode) return;
            int x = (int)Math.Round(p.X * 32);
            int y = (int)Math.Round(p.Y * 32);
            SendCtl("CMD=CENTERING&CTRL=START&X=" + x + "&Y=" + y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                holdTimer.Stop();
                holdTimer.Dispose();
                try { ptz.Close(); } catch { }
                tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
