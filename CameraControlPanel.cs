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
        readonly AsmTabControl tabs = new AsmTabControl();
        readonly TabPage tCam = new TabPage("Camera");
        readonly TabPage tPb = new TabPage("Playback");
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
            Height = Asm.S(176);
            tabs.Dock = DockStyle.Fill;
            tCam.BackColor = Asm.Panel2;
            tPb.BackColor = Asm.Panel2;
            tabs.TabPages.Add(tCam);
            tabs.TabPages.Add(tPb);
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
            pad.Dock = DockStyle.Left;
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

            var fl = new FlowLayoutPanel();
            fl.Dock = DockStyle.Fill;
            fl.Padding = new Padding(Asm.S(6), Asm.S(4), 0, 0);
            fl.Tag = "nostyle";
            fl.BackColor = Asm.Panel2;

            fl.Controls.Add(OpLab("Zoom"));
            fl.Controls.Add(HoldBtn("Tele +", "Zoom in (tahan)", 1, null));
            fl.Controls.Add(HoldBtn("Wide -", "Zoom out (tahan)", -1, null));
            fl.Controls.Add(OpLab("Focus"));
            fl.Controls.Add(HoldBtn("Near", "Focus dekat", 0, "CMD=FOCUS&CTRL=NEAR&CTRL=START"));
            fl.Controls.Add(HoldBtn("Far", "Focus jauh", 0, "CMD=FOCUS&CTRL=FAR&CTRL=START"));
            fl.Controls.Add(HoldBtn("Auto", "Auto focus", 0, "CMD=FOCUS&CTRL=AUTO&CTRL=START"));
            fl.SetFlowBreak(fl.Controls[fl.Controls.Count - 1], true);

            fl.Controls.Add(OpLab("Brightness"));
            fl.Controls.Add(HoldBtn("Open", "Iris buka", 0, "CMD=IRIS&CTRL=OPEN&CTRL=START"));
            fl.Controls.Add(HoldBtn("Close", "Iris tutup", 0, "CMD=IRIS&CTRL=CLOSE&CTRL=START"));
            fl.Controls.Add(HoldBtn("Reset", "Iris reset", 0, "CMD=IRIS&CTRL=RESET&CTRL=START"));
            btnCenter = OpBtn(AsmIcon.None, "Click center", "Klik gambar = pusatkan kamera ke titik itu", null, 92);
            btnCenter.Toggle = true;
            fl.Controls.Add(btnCenter);
            fl.SetFlowBreak(btnCenter, true);

            fl.Controls.Add(OpLab("Preset"));
            numPreset = new NumericUpDown();
            numPreset.Minimum = 1; numPreset.Maximum = 256; numPreset.Value = 1; numPreset.Width = Asm.S(56);
            numPreset.Margin = new Padding(2, 6, 2, 0);
            fl.Controls.Add(numPreset);
            fl.Controls.Add(OpBtn(AsmIcon.None, "Go", "Panggil preset", delegate { SendCtl("CMD=PRESET&NO=" + (int)numPreset.Value); }, 44));
            fl.Controls.Add(OpBtn(AsmIcon.None, "Set", "Simpan posisi saat ini ke preset", delegate
            {
                if (MessageBox.Show("Simpan posisi kamera saat ini ke preset " + (int)numPreset.Value + "?", "Preset", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    SendCtl("CMD=PRESETPRG&NO=" + (int)numPreset.Value);
            }, 44));
            fl.Controls.Add(OpLab("AUX"));
            cmbAux = new ComboBox();
            cmbAux.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAux.Items.AddRange(new object[] { "AUX1", "AUX2", "AUX3", "AUX4", "AUX5", "AUX6", "AUX7", "AUX8" });
            cmbAux.SelectedIndex = 0;
            cmbAux.Width = Asm.S(70);
            cmbAux.Margin = new Padding(2, 6, 2, 0);
            fl.Controls.Add(cmbAux);
            fl.Controls.Add(OpBtn(AsmIcon.None, "On", "AUX open", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=OPEN"); }, 40));
            fl.Controls.Add(OpBtn(AsmIcon.None, "Off", "AUX close", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=CLOSE"); }, 40));
            fl.SetFlowBreak(fl.Controls[fl.Controls.Count - 1], true);

            fl.Controls.Add(OpLab("Kecepatan"));
            trkSpeed = new TrackBar();
            trkSpeed.Minimum = 1; trkSpeed.Maximum = 16; trkSpeed.Value = 8; trkSpeed.Width = Asm.S(120); trkSpeed.TickStyle = TickStyle.None;
            fl.Controls.Add(trkSpeed);
            fl.Controls.Add(OpLab("Zoom spd"));
            trkZoom = new TrackBar();
            trkZoom.Minimum = 1; trkZoom.Maximum = 16; trkZoom.Value = 8; trkZoom.Width = Asm.S(120); trkZoom.TickStyle = TickStyle.None;
            fl.Controls.Add(trkZoom);

            numPreset.BackColor = Asm.Panel; numPreset.ForeColor = Asm.Text;
            cmbAux.BackColor = Asm.Panel; cmbAux.ForeColor = Asm.Text; cmbAux.FlatStyle = FlatStyle.Flat;
            tCam.Controls.Add(fl);
            tCam.Controls.Add(pad);
        }

        /// <summary>Diisi Live window: playback berjalan di tampilan yang sama.</summary>
        public PlaybackController Pb;
        ComboBox cmbSkip;

        void BuildPlayback()
        {
            var h = new FlowLayoutPanel();
            h.Dock = DockStyle.Fill;
            h.Padding = new Padding(Asm.S(6), Asm.S(6), 0, 0);
            h.Tag = "nostyle";
            h.BackColor = Asm.Panel2;
            h.Controls.Add(OpLab("Waktu"));
            dtpGo = new DateTimePicker();
            dtpGo.Format = DateTimePickerFormat.Custom;
            dtpGo.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtpGo.Width = Asm.S(170);
            dtpGo.Margin = new Padding(2, 6, 2, 0);
            dtpGo.Value = DateTime.Now.AddMinutes(-10);
            h.Controls.Add(dtpGo);
            h.Controls.Add(OpBtn(AsmIcon.Play, "Go", "Putar dari waktu ini (di tampilan ini)", delegate
            {
                if (Pb != null && SelRef() != null) Pb.PlayAtUtc(dtpGo.Value.ToUniversalTime());
            }, 64));
            var live = OpBtn(AsmIcon.Live, "Live", "Kembali ke live", delegate { if (Pb != null) Pb.Exit(); }, 70);
            h.Controls.Add(live);
            h.SetFlowBreak(live, true);

            h.Controls.Add(OpBtn(AsmIcon.FastRew, "", "Fast reverse", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.FastReverse(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.PrevFrame, "", "Frame sebelumnya", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Step(false); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Reverse, "", "Putar mundur", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Reverse(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Pause, "", "Pause", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Pause(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Play, "", "Putar maju", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Play(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.NextFrame, "", "Frame berikutnya", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Step(true); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Minus, "Lambat", "Perlambat putaran (0.25x - 8x)", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.SpeedDown(); }); }, 74));
            h.Controls.Add(OpBtn(AsmIcon.Plus, "Cepat", "Percepat putaran (0.25x - 8x)", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.SpeedUp(); }); }, 70));
            h.Controls.Add(OpBtn(AsmIcon.FastFwd, "", "Fast forward", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.FastForward(); }); }, 40));
            var stp = OpBtn(AsmIcon.Stop, "", "Stop", delegate { if (Pb != null) Pb.Each(delegate(PlaybackEngine e) { e.Stop(); }); }, 40);
            h.Controls.Add(stp);
            h.SetFlowBreak(stp, true);

            h.Controls.Add(OpLab("Skip"));
            cmbSkip = new ComboBox();
            cmbSkip.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSkip.Items.AddRange(new object[] { "10 detik", "30 detik", "1 menit", "5 menit", "10 menit", "30 menit", "1 jam" });
            cmbSkip.SelectedIndex = 2;
            cmbSkip.Width = Asm.S(90);
            cmbSkip.Margin = new Padding(2, 6, 2, 0);
            h.Controls.Add(cmbSkip);
            h.Controls.Add(OpBtn(AsmIcon.SkipBack, "", "Mundur sebesar nilai skip", delegate { SkipBy(-1); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.SkipFwd, "", "Maju sebesar nilai skip", delegate { SkipBy(1); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Download, "Quick DL", "Quick download kamera terpilih (dari posisi ini bila sedang playback)", delegate
            {
                CamRef r = SelRef();
                if (r != null && QuickDownloadRequested != null) QuickDownloadRequested(r);
            }, 90));
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
        public void RefreshState()
        {
            ViewCell c = selected();
            bool cam = c != null && c.HasCamera && !c.Playback && AsmApp.Can(Perm.Camera);
            foreach (Control k in tCam.Controls) k.Enabled = cam;
            foreach (Control k in tPb.Controls) k.Enabled = AsmApp.Can(Perm.Playback) && ((Pb != null && Pb.Active) || (c != null && c.HasCamera));
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
