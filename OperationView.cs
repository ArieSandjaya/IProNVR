using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Operation window ala ASM300: function panel di kiri, toolbar, area gambar, status bar, dan operation panel (tab Camera / HDD).</summary>
    public class OperationView : UserControl
    {
        // panel
        readonly FunctionPanel fp = new FunctionPanel();
        readonly CollapseHandle hLeft = new CollapseHandle(false);
        readonly CollapseHandle hBottom = new CollapseHandle(true);
        readonly Panel center = new Panel();
        readonly Panel toolbar = new Panel();
        readonly FlowLayoutPanel toolFlow = new FlowLayoutPanel();
        readonly Panel statusBar = new Panel();
        readonly Label lblLoad = new Label();
        readonly Panel opPanel = new Panel();
        readonly Scrubber scrub = new Scrubber();   // garis waktu playback (hanya tampil saat playback)
        public readonly ViewGrid Grid = new ViewGrid();

        // isi function panel
        DeviceTreePanel devTree;
        GroupPanel grpPanel;
        SequencePanel seqPanel;
        QuickPlaybackPanel qpPanel;
        DownloadPanel dlPanel;
        LogPanel almLog, devLog, netLog;

        // status bar
        readonly Label lblAlm = new Label();
        readonly Label lblErr = new Label();
        readonly Label lblMode = new Label();
        readonly Label lblStep = new Label();
        readonly Label lblSeq = new Label();
        readonly Label lblTime = new Label();
        readonly Label lblInfo = new Label();
        int almCount, errCount;

        // toolbar
        readonly List<AsmButton> shortcutBtns = new List<AsmButton>();
        FlowLayoutPanel shortcutHost;

        // operation panel
        AsmTabControl opTabs;
        PtzPad pad;
        AsmButton btnCenter;
        TrackBar trkSpeed, trkZoom;
        NumericUpDown numPreset;
        ComboBox cmbAux;
        DateTimePicker dtpGo;
        ComboBox cmbSkip;
        readonly System.Windows.Forms.Timer holdTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer seqTimer = new System.Windows.Forms.Timer();
        readonly SerialWorker ptz = new SerialWorker(200);
        int holdPan, holdTilt, holdZoom;
        NvrRecord holdNvr; int holdCh;   // sasaran PTZ dikunci saat tombol ditekan agar perintah stop sampai ke kamera yang sama
        readonly ToolTip tips = new ToolTip();

        // playback
        readonly Dictionary<long, PlaybackEngine> engines = new Dictionary<long, PlaybackEngine>();

        // sequence
        SeqRec seq;
        int seqIndex;

        public OperationView()
        {
            Dock = DockStyle.Fill;
            BackColor = Asm.Bg;
            Tag = "nostyle";
            BuildLayout();
            BuildFunctionPanel();
            BuildToolbar();
            BuildStatusBar();
            BuildOperationPanel();
            WireGrid();

            holdTimer.Interval = 350;
            holdTimer.Tick += delegate
            {
                // tombol mouse sudah lepas tetapi MouseUp tidak sampai (kontrol di-disable/fokus pindah): hentikan agar kamera tidak jalan terus
                if (Control.MouseButtons == MouseButtons.None) { StopHold(); return; }
                if (holdPan != 0 || holdTilt != 0 || holdZoom != 0) SendMove(holdPan, holdTilt, holdZoom);
            };
            uiTimer.Interval = 500;
            uiTimer.Tick += delegate { UpdateStatus(); };
            uiTimer.Start();
            seqTimer.Tick += delegate
            {
                try { SeqNext(); }
                catch (Exception ex) { Log.Write("Sequence: " + ex.Message); }
            };
            IproNvrTool.Events.Added += OnEventAdded;

            string id = AppSettings.Current.OpLayout;
            Grid.SetLayout(ScreenLayout.ById(id));
            Grid.StateName = "op";
            UpdateShortcutState();
            UpdateOpPanelState();
        }

        // =============== susunan ===============

        void BuildLayout()
        {
            fp.Dock = DockStyle.Left;
            hLeft.Dock = DockStyle.Left;
            hLeft.Click += delegate { ToggleLeft(); };
            hLeft.Visible = AppSettings.Current.PanelHandle;

            center.Dock = DockStyle.Fill;
            center.BackColor = Asm.Bg;
            center.Tag = "nostyle";

            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Asm.S(38);
            toolbar.BackColor = Asm.Panel;
            toolbar.Tag = "nostyle";
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Asm.S(26);
            statusBar.BackColor = Asm.Panel;
            statusBar.Tag = "nostyle";
            opPanel.Dock = DockStyle.Bottom;
            opPanel.Height = Asm.S(176);
            opPanel.BackColor = Asm.Panel;
            opPanel.Tag = "nostyle";
            hBottom.Dock = DockStyle.Bottom;
            hBottom.Click += delegate { opPanel.Visible = !opPanel.Visible; hBottom.Collapsed = !opPanel.Visible; hBottom.Invalidate(); };

            Grid.Dock = DockStyle.Fill;

            center.Controls.Add(Grid);
            scrub.Visible = false;
            scrub.Seek += delegate(DateTime local) { if (Grid.PlaybackMode) PlayAtUtc(local.ToUniversalTime()); };
            center.Controls.Add(scrub);
            center.Controls.Add(hBottom);
            center.Controls.Add(opPanel);
            center.Controls.Add(statusBar);
            center.Controls.Add(toolbar);

            Controls.Add(center);
            Controls.Add(hLeft);
            Controls.Add(fp);
        }

        void ToggleLeft()
        {
            fp.Visible = !fp.Visible;
            hLeft.Collapsed = !fp.Visible;
            hLeft.Invalidate();
        }

        void BuildFunctionPanel()
        {
            devTree = new DeviceTreePanel();
            devTree.CameraActivated += delegate(CamRef r) { PutCamera(r); };
            grpPanel = new GroupPanel();
            grpPanel.GroupChosen += delegate(GroupRec g) { LoadGroup(g); };
            seqPanel = new SequencePanel();
            seqPanel.StartRequested += delegate(SeqRec s) { StartSequence(s); };
            seqPanel.StopRequested += delegate { StopSequence(); };
            qpPanel = new QuickPlaybackPanel();
            qpPanel.CameraSource = SelectedRef;
            qpPanel.PlayRequested += delegate(CamRef r, DateTime t) { PlayCameraAt(r, t.ToUniversalTime()); };
            dlPanel = new DownloadPanel();
            dlPanel.CameraSource = SelectedRef;
            almLog = new LogPanel(IproNvrTool.Events.Alarm);
            devLog = new LogPanel(IproNvrTool.Events.Device);
            netLog = new LogPanel(IproNvrTool.Events.Net);
            almLog.EntryActivated += delegate(LogEntry e) { OpenLogEntry(e, true); };
            devLog.EntryActivated += delegate(LogEntry e) { OpenLogEntry(e, false); };
            netLog.EntryActivated += delegate(LogEntry e) { OpenLogEntry(e, false); };

            fp.AddSection("dev", "Device", devTree, true);
            fp.AddSection("grp", "Group", grpPanel, true);
            fp.AddSection("seq", "Sequence", seqPanel, true);
            fp.AddSection("qpb", "Quick playback", qpPanel, false);
            fp.AddSection("dl", "Download", dlPanel, false);
            fp.AddSection("alm", "Alarm log", almLog, false);
            fp.AddSection("derr", "Device error log", devLog, false);
            fp.AddSection("nerr", "Network error log", netLog, false);
            fp.Open("dev");
            fp.SearchTextChanged += OnSearch;
        }

        void OnSearch(string text)
        {
            bool dev = devTree.Filter(text);
            bool grp = grpPanel.Filter(text);
            bool sq = seqPanel.Filter(text);
            fp.MarkMatch("dev", text.Length > 0 && dev);
            fp.MarkMatch("grp", text.Length > 0 && grp);
            fp.MarkMatch("seq", text.Length > 0 && sq);
            if (text.Length == 0) return;
            if (dev) fp.Open("dev");
            else if (grp) fp.Open("grp");
            else if (sq) fp.Open("seq");
        }

        // =============== toolbar ===============

        void BuildToolbar()
        {
            toolFlow.Dock = DockStyle.Fill;
            toolFlow.Padding = new Padding(Asm.S(4), Asm.S(3), 0, 0);
            toolFlow.WrapContents = false;
            toolFlow.Tag = "nostyle";
            toolbar.Controls.Add(toolFlow);

            var layBtn = new AsmButton(AsmIcon.Layout, "Pola layar", null);
            layBtn.Click += delegate { ShowLayoutMenu(layBtn); };
            toolFlow.Controls.Add(layBtn);

            shortcutHost = new FlowLayoutPanel();
            shortcutHost.AutoSize = true;
            shortcutHost.WrapContents = false;
            shortcutHost.Margin = new Padding(0);
            shortcutHost.Tag = "nostyle";
            toolFlow.Controls.Add(shortcutHost);
            BuildShortcuts();

            toolFlow.Controls.Add(Spacer());
            toolFlow.Controls.Add(new AsmButton(AsmIcon.Full, "Layar penuh (Esc untuk keluar)", delegate
            {
                using (var f = new FullScreenForm(Grid)) f.ShowDialog(this);
            }));
            toolFlow.Controls.Add(new AsmButton(AsmIcon.Camera, "Still picture", delegate { DoStill(); }));
            toolFlow.Controls.Add(new AsmButton(AsmIcon.Print, "Print", delegate { DoPrint(); }));
            toolFlow.Controls.Add(new AsmButton(AsmIcon.Close, "Kosongkan semua area", delegate { StopSequence(); ExitPlayback(); Grid.ClearAll(); }));
        }

        static Control Spacer()
        {
            var p = new Panel();
            p.Width = Asm.S(12);
            p.Height = 1;
            p.Tag = "nostyle";
            return p;
        }

        void BuildShortcuts()
        {
            shortcutHost.Controls.Clear();
            shortcutBtns.Clear();
            string[] ids = (AppSettings.Current.LayoutShortcuts ?? "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in ids)
            {
                string id = raw.Trim();
                if (!ScreenLayout.Exists(id)) continue;
                var b = new AsmButton();
                b.Label = id;
                b.Width = Asm.S(36);
                b.Tag = id;
                tips.SetToolTip(b, "Pola " + id);
                string cap = id;
                b.Click += delegate { SetLayout(cap); };
                shortcutHost.Controls.Add(b);
                shortcutBtns.Add(b);
            }
            UpdateShortcutState();
        }

        void UpdateShortcutState()
        {
            foreach (AsmButton b in shortcutBtns) b.Highlight = string.Equals(b.Tag as string, Grid.Layout.Id, StringComparison.OrdinalIgnoreCase);
            foreach (AsmButton b in shortcutBtns) b.Invalidate();
        }

        void ShowLayoutMenu(Control anchor)
        {
            ContextMenuStrip m = Asm.Menu();
            int max = AppSettings.Current.OpMaxAreas;
            foreach (ScreenLayout l in ScreenLayout.All)
            {
                if (l.Count > max) continue;
                ScreenLayout lay = l;
                ToolStripMenuItem it = Asm.Item(m, l.Id + "   (" + l.Count + " area)", delegate { SetLayout(lay.Id); });
                it.Image = l.Preview(Asm.S(30), Asm.S(20));
                it.Checked = lay.Id == Grid.Layout.Id;
            }
            m.Closed += delegate
            {
                // bebaskan menu + bitmap pratinjau (dilepas setelah klik selesai diproses)
                Ui.Post(this, delegate
                {
                    foreach (ToolStripItem ti in m.Items) { if (ti.Image != null) { Image im = ti.Image; ti.Image = null; im.Dispose(); } }
                    m.Dispose();
                });
            };
            m.Show(anchor, new Point(0, anchor.Height));
        }

        public void SetLayout(string id)
        {
            if (!ScreenLayout.Exists(id)) return;
            StopSequenceKeepIndicator();
            Grid.SetLayout(ScreenLayout.ById(id));
            AppSettings.Current.OpLayout = id;
            try { AppSettings.Current.Save(); } catch { }
            UpdateShortcutState();
        }

        void DoStill()
        {
            ViewCell c = Grid.Selected;
            if (c == null || c.CurrentImage == null) return;
            if (!AsmApp.Can(Perm.StillPrint)) { MessageBox.Show("Tidak ada hak Still/Print."); return; }
            string p = Stills.Save(c.CurrentImage, Dir.Title(c.NvrId, c.Channel));
            if (p != null) lblInfo.Text = "Tersimpan: " + p;
        }

        void DoPrint()
        {
            ViewCell c = Grid.Selected;
            if (c == null || c.CurrentImage == null) return;
            if (!AsmApp.Can(Perm.StillPrint)) { MessageBox.Show("Tidak ada hak Still/Print."); return; }
            Stills.Print(c.CurrentImage, Dir.Title(c.NvrId, c.Channel));
        }

        // =============== status bar ===============

        Label SB(string text, int w, DockStyle d)
        {
            var l = new Label();
            l.Text = text;
            l.Width = Asm.S(w);
            l.Dock = d;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.ForeColor = Asm.Text;
            l.Font = Asm.F(8.5f, true);
            l.Tag = "nostyle";
            return l;
        }

        void BuildStatusBar()
        {
            lblInfo.Dock = DockStyle.Fill;
            lblInfo.ForeColor = Asm.Muted;
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.Font = Asm.F(8.5f, false);
            lblInfo.Tag = "nostyle";
            lblInfo.AutoEllipsis = true;

            Label[] ls = { lblTime, lblSeq, lblStep, lblMode, lblErr, lblAlm, lblLoad };
            string[] txt = { "", "", "", "LIVE", "ERR 0", "ALM 0", "" };
            int[] w = { 150, 50, 60, 70, 64, 64, 230 };
            statusBar.Controls.Add(lblInfo);
            for (int i = 0; i < ls.Length; i++)
            {
                Label l = ls[i];
                l.Text = txt[i];
                l.Width = Asm.S(w[i]);
                l.Dock = DockStyle.Right;
                l.TextAlign = ContentAlignment.MiddleCenter;
                l.ForeColor = Asm.Text;
                l.Font = Asm.F(8.5f, true);
                l.Tag = "nostyle";
                statusBar.Controls.Add(l);
            }
            lblAlm.Cursor = Cursors.Hand;
            lblErr.Cursor = Cursors.Hand;
            lblAlm.Click += delegate { almCount = 0; fp.Open("alm"); UpdateStatus(); };
            lblErr.Click += delegate { errCount = 0; fp.Open("derr"); UpdateStatus(); };
        }

        void OnEventAdded(string kind, LogEntry e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed) return;
                    if (kind == IproNvrTool.Events.Alarm) almCount++;
                    else errCount++;
                    UpdateStatus();
                });
            }
            catch (InvalidOperationException) { }
        }

        void UpdateStatus()
        {
            lblAlm.Text = "ALM " + almCount;
            lblAlm.BackColor = almCount > 0 ? Asm.Danger : Asm.Panel;
            lblErr.Text = "ERR " + errCount;
            lblErr.BackColor = errCount > 0 ? Asm.Warn : Asm.Panel;
            bool pb = Grid.PlaybackMode;
            string mode = "LIVE";
            DateTime? t = null;
            int step = 1;
            if (pb)
            {
                mode = "PLAY";
                foreach (PlaybackEngine e in engines.Values)
                {
                    if (e.Mode == "PAUSE") mode = "PAUSE";
                    else if (e.Mode == "FF") mode = "FF";
                    else if (e.Mode == "REW") mode = "REW";
                    else if (e.Mode == "BPLAY") mode = "REV";
                    DateTime? shown = e.ShownUtc;
                    if (shown.HasValue && !t.HasValue) t = shown.Value.ToLocalTime();
                    step = Math.Max(step, e.FfStep);
                }
            }
            lblMode.Text = mode;
            lblMode.BackColor = pb ? Asm.BarOpen : Asm.Panel;
            double spd = 1;
            foreach (PlaybackEngine e2 in engines.Values) { spd = e2.Speed; break; }
            lblStep.Text = pb && step > 1 ? "x" + step : (pb && spd != 1 && (mode == "PLAY" || mode == "REV") ? "x" + spd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "");
            lblSeq.Text = seq != null ? "SEQ" : "";
            lblSeq.BackColor = seq != null ? Asm.Ok : Asm.Panel;
            lblSeq.ForeColor = seq != null ? Color.Black : Asm.Text;
            lblTime.Text = Asm.FormatTime(pb ? (t.HasValue ? t.Value : DateTime.Now) : DateTime.Now);
            if (scrub.Visible != pb) scrub.Visible = pb;
            if (pb && t.HasValue) scrub.SetPosition(t.Value, false);
            lblLoad.Text = AppLifecycle.LoadText();
        }

        // =============== operation panel ===============

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

        void BuildOperationPanel()
        {
            opTabs = new AsmTabControl();
            opTabs.Dock = DockStyle.Fill;
            var tCam = new TabPage("Camera");
            var tHdd = new TabPage("HDD");
            tCam.BackColor = Asm.Panel2;
            tHdd.BackColor = Asm.Panel2;
            opTabs.TabPages.Add(tCam);
            opTabs.TabPages.Add(tHdd);
            opPanel.Controls.Add(opTabs);

            // ---- Camera ----
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

            // ---- HDD ----
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
            h.Controls.Add(OpBtn(AsmIcon.Play, "Go", "Putar dari waktu ini", delegate { PlayAtUtc(dtpGo.Value.ToUniversalTime()); }, 64));
            var live = OpBtn(AsmIcon.Live, "Live", "Kembali ke live", delegate { ExitPlayback(); }, 70);
            h.Controls.Add(live);
            h.SetFlowBreak(live, true);

            h.Controls.Add(OpBtn(AsmIcon.FastRew, "", "Fast reverse", delegate { Each(delegate(PlaybackEngine e) { e.FastReverse(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.PrevFrame, "", "Frame sebelumnya", delegate { Each(delegate(PlaybackEngine e) { e.Step(false); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Reverse, "", "Putar mundur", delegate { Each(delegate(PlaybackEngine e) { e.Reverse(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Pause, "", "Pause", delegate { Each(delegate(PlaybackEngine e) { e.Pause(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Play, "", "Putar maju", delegate { Each(delegate(PlaybackEngine e) { e.Play(); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.NextFrame, "", "Frame berikutnya", delegate { Each(delegate(PlaybackEngine e) { e.Step(true); }); }, 40));
            h.Controls.Add(OpBtn(AsmIcon.Minus, "Lambat", "Perlambat putaran (0.25x - 8x)", delegate { Each(delegate(PlaybackEngine e) { e.SpeedDown(); }); }, 74));
            h.Controls.Add(OpBtn(AsmIcon.Plus, "Cepat", "Percepat putaran (0.25x - 8x)", delegate { Each(delegate(PlaybackEngine e) { e.SpeedUp(); }); }, 70));
            h.Controls.Add(OpBtn(AsmIcon.FastFwd, "", "Fast forward", delegate { Each(delegate(PlaybackEngine e) { e.FastForward(); }); }, 40));
            var stp = OpBtn(AsmIcon.Stop, "", "Stop", delegate { Each(delegate(PlaybackEngine e) { e.Stop(); }); }, 40);
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
            h.Controls.Add(OpBtn(AsmIcon.Download, "Quick DL", "Quick download dari posisi ini", delegate { QuickDownload(SelectedRef()); }, 90));

            tHdd.Controls.Add(h);
        }

        int SkipSeconds()
        {
            int[] s = { 10, 30, 60, 300, 600, 1800, 3600 };
            return s[Math.Max(0, cmbSkip.SelectedIndex)];
        }

        void SkipBy(int dir)
        {
            int sec = SkipSeconds() * dir;
            Each(delegate(PlaybackEngine e) { e.Skip(sec); });
        }

        void Each(Action<PlaybackEngine> a)
        {
            if (!Grid.PlaybackMode) return;
            foreach (PlaybackEngine e in new List<PlaybackEngine>(engines.Values)) a(e);
        }

        void UpdateOpPanelState()
        {
            ViewCell c = Grid.Selected;
            bool cam = c != null && c.HasCamera && !c.Playback && AsmApp.Can(Perm.Camera);
            foreach (TabPage tp in opTabs.TabPages)
            {
                if (tp.Text != "Camera") continue;
                foreach (Control k in tp.Controls) k.Enabled = cam;
            }
            if (opTabs.TabPages.Count > 1)
            {
                foreach (Control k in opTabs.TabPages[1].Controls) k.Enabled = AsmApp.Can(Perm.Playback);
            }
        }

        // =============== PTZ ===============

        bool NeedCam(out NvrRecord nvr, out int ch)
        {
            nvr = null; ch = 0;
            ViewCell c = Grid.Selected;
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

        // =============== grid ===============

        void WireGrid()
        {
            Grid.SelectionChanged += delegate
            {
                UpdateOpPanelState();
                qpPanel.RefreshCamera();
                dlPanel.RefreshCamera();
            };
            Grid.CamerasChanged += delegate { qpPanel.RefreshCamera(); dlPanel.RefreshCamera(); UpdateOpPanelState(); };
            Grid.MaximizedChanged += delegate { if (Grid.PlaybackMode) SyncEngines(true); };
            Grid.CellAssigned += delegate(int idx, long nid, int ch) { if (Grid.PlaybackMode) SyncEngines(true); };
            Grid.CellUnassigned += delegate(int idx) { if (Grid.PlaybackMode) SyncEngines(false); };
            Grid.QuickDownloadRequested += delegate(CamRef r) { QuickDownload(r); };
            Grid.RegisterLayoutRequested += delegate { RegisterLayout(); };
            Grid.CellImageClicked += delegate(ViewCell c, PointF p)
            {
                if (!btnCenter.Checked || c.Playback) return;
                int x = (int)Math.Round(p.X * 32);
                int y = (int)Math.Round(p.Y * 32);
                SendCtl("CMD=CENTERING&CTRL=START&X=" + x + "&Y=" + y);
            };
        }

        public CamRef SelectedRef()
        {
            ViewCell c = Grid.Selected;
            return c == null ? null : c.Ref;
        }

        /// <summary>Taruh kamera di area terpilih (pindah ke area kosong berikutnya setelahnya).</summary>
        public void PutCamera(CamRef r)
        {
            if (!AsmApp.Can(Perm.Live) && !Grid.PlaybackMode) { MessageBox.Show("Tidak ada hak live."); return; }
            StopSequenceKeepIndicator();
            int i = Grid.SelectedIndex;
            if (i < 0) i = 0;
            Grid.Assign(i, r.NvrId, r.Channel, true);
            int next = -1;
            for (int k = 1; k <= Grid.Cells.Count; k++)
            {
                int j = (i + k) % Grid.Cells.Count;
                if (!Grid.Cells[j].HasCamera) { next = j; break; }
            }
            if (next >= 0) Grid.Select(next);
        }

        public void ShowCamera(NvrRecord nvr, CameraRecord cam)
        {
            if (Grid.PlaybackMode) ExitPlayback();
            PutCamera(new CamRef(nvr.Id, cam.Channel));
        }

        public void PlayCameraAt(CamRef r, DateTime utc)
        {
            if (!AsmApp.Can(Perm.Playback)) { MessageBox.Show("Tidak ada hak playback."); return; }
            if (!Grid.Contains(r.NvrId, r.Channel))
            {
                int i = Grid.SelectedIndex;
                if (i < 0) i = 0;
                Grid.Assign(i, r.NvrId, r.Channel, true);
                Grid.Select(i);
            }
            PlayAtUtc(utc);
        }

        void OpenLogEntry(LogEntry e, bool playback)
        {
            if (e.NvrId == 0 || e.Channel <= 0) return;
            NvrRecord nvr = Dir.Nvr(e.NvrId);
            if (nvr == null) return;
            if (playback) PlayCameraAt(new CamRef(e.NvrId, e.Channel), e.Time.ToUniversalTime());
            else { if (Grid.PlaybackMode) ExitPlayback(); PutCamera(new CamRef(e.NvrId, e.Channel)); }
        }

        void QuickDownload(CamRef r)
        {
            if (r == null) { MessageBox.Show("Pilih area gambar yang berisi kamera."); return; }
            DateTime at = DateTime.Now;
            if (Grid.PlaybackMode)
            {
                PlaybackEngine e;
                if (engines.TryGetValue(r.NvrId, out e) && e.CurUtc.HasValue) at = e.CurUtc.Value.ToLocalTime();
            }
            fp.Open("dl");
            dlPanel.Quick(r, at, !Grid.PlaybackMode);
        }

        // =============== group / sequence ===============

        void LoadGroup(GroupRec g)
        {
            if (Grid.PlaybackMode) ExitPlayback();
            Grid.SetLayout(ScreenLayout.ById(g.Layout));
            Grid.LoadSlots(g.Slots);
            AppSettings.Current.OpLayout = g.Layout;
            UpdateShortcutState();
            lblInfo.Text = "Group: " + g.Name;
        }

        void RegisterLayout()
        {
            string name = Ui.Ask("Register layout to group", "Nama group:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var g = new GroupRec();
            g.Name = name.Trim();
            g.Layout = Grid.Layout.Id;
            g.Slots = Grid.Slots();
            g.Owner = AsmApp.User != null && AsmApp.User.UseGroup && !AsmApp.User.IsAdmin ? AsmApp.User.Name : "";
            try
            {
                AsmDb.SaveGroup(g);
                grpPanel.Reload();
                fp.Open("grp");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Group"); }
        }

        public void StartSequence(SeqRec s)
        {
            if (s.Steps.Count == 0) { MessageBox.Show("Sequence ini belum punya langkah."); return; }
            seq = s;
            seqIndex = -1;
            SeqNext();
        }

        void SeqNext()
        {
            if (seq == null || seq.Steps.Count == 0) { StopSequence(); return; }
            seqIndex = (seqIndex + 1) % seq.Steps.Count;
            SeqStep st = seq.Steps[seqIndex];
            GroupRec g = AsmDb.GetGroup(st.GroupId);
            if (g != null)
            {
                SeqRec keep = seq;
                if (Grid.PlaybackMode) ExitPlayback();
                Grid.SetLayout(ScreenLayout.ById(g.Layout));
                Grid.LoadSlots(g.Slots);
                UpdateShortcutState();
                lblInfo.Text = "Sequence " + keep.Name + ": " + g.Name;
            }
            seqTimer.Stop();
            seqTimer.Interval = Math.Max(1, st.Seconds) * 1000;
            seqTimer.Start();
        }

        public void StopSequence()
        {
            seqTimer.Stop();
            seq = null;
            UpdateStatus();
        }

        void StopSequenceKeepIndicator()
        {
            if (seq != null) StopSequence();
        }

        // =============== playback ===============

        void SyncEngines(bool resume)
        {
            var byNvr = new Dictionary<long, List<int>>();
            int mx = Grid.MaximizedIndex;
            foreach (ViewCell c in Grid.Cells)
            {
                if (!c.HasCamera) continue;
                if (mx >= 0 && c.Index != mx) { if (!byNvr.ContainsKey(c.NvrId)) byNvr[c.NvrId] = new List<int>(); continue; }   // di-pause
                List<int> l;
                if (!byNvr.TryGetValue(c.NvrId, out l)) { l = new List<int>(); byNvr[c.NvrId] = l; }
                l.Add(c.Channel);
            }
            foreach (long id in new List<long>(engines.Keys))
            {
                if (!byNvr.ContainsKey(id))
                {
                    engines[id].Dispose();
                    engines.Remove(id);
                }
            }
            foreach (KeyValuePair<long, List<int>> kv in byNvr)
            {
                PlaybackEngine e;
                bool isNew = false;
                if (!engines.TryGetValue(kv.Key, out e))
                {
                    NvrRecord nvr = Dir.Nvr(kv.Key);
                    if (nvr == null) continue;
                    e = new PlaybackEngine(nvr);
                    long nid = kv.Key;
                    e.Frame += delegate(int ch, byte[] jpg) { OnPlaybackFrame(nid, ch, jpg); };
                    e.ChannelStatus += delegate(int ch, string msg)
                    {
                        try
                        {
                            if (IsHandleCreated && !IsDisposed)
                                BeginInvoke((Action)delegate
                                {
                                    foreach (ViewCell c in Grid.Cells)
                                        if (c.Playback && c.NvrId == nid && c.Channel == ch) c.SetPlaybackMessage(msg);
                                });
                        }
                        catch { }
                    };
                    e.StatusChanged += delegate(string st)
                    {
                        try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)delegate { lblInfo.Text = "Playback: " + st; }); } catch { }
                    };
                    e.DecodeWidth = Grid.Layout.Count <= 4 ? 960 : (Grid.Layout.Count <= 16 ? 480 : 320);
                    engines[kv.Key] = e;
                    isNew = true;
                }
                e.SetChannels(kv.Value);
                if (resume && !isNew && kv.Value.Count > 0 && e.Active && e.CurUtc.HasValue) e.GotoUtc(e.CurUtc.Value);
            }
        }

        void OnPlaybackFrame(long nvrId, int ch, byte[] jpg)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    foreach (ViewCell c in Grid.Cells)
                        if (c.Playback && c.NvrId == nvrId && c.Channel == ch) c.ShowFrame(jpg);
                });
            }
            catch { }
        }

        void PlayAtUtc(DateTime utc)
        {
            if (!AsmApp.Can(Perm.Playback)) { MessageBox.Show("Tidak ada hak playback."); return; }
            StopSequenceKeepIndicator();
            if (!Grid.PlaybackMode)
            {
                Grid.SetPlaybackMode(true);
            }
            SyncEngines(false);
            if (engines.Count == 0) { MessageBox.Show("Belum ada kamera di area gambar."); return; }
            foreach (PlaybackEngine e in engines.Values) e.GotoUtc(utc);
            scrub.Visible = true;
            scrub.SetPosition(utc.ToLocalTime(), true);
            opTabs.SelectedIndex = 1;
            UpdateOpPanelState();
        }

        public void ExitPlayback()
        {
            if (!Grid.PlaybackMode) return;
            foreach (PlaybackEngine e in engines.Values) e.Dispose();
            engines.Clear();
            Grid.SetPlaybackMode(false);
            opTabs.SelectedIndex = 0;
            UpdateOpPanelState();
        }

        // =============== umum ===============

        public void ApplyStartup()
        {
            AppSettings s = AppSettings.Current;
            string g = s.OpStartGroup ?? "";
            string q = s.OpStartSequence ?? "";
            try
            {
                if (g.Length > 0)
                {
                    foreach (GroupRec x in AsmDb.ListGroups()) if (x.Name == g) { LoadGroup(x); return; }
                }
                if (q.Length > 0)
                {
                    foreach (SeqRec x in AsmDb.ListSequences()) if (x.Name == q) { StartSequence(x); return; }
                }
            }
            catch (Exception ex) { Log.Write("Startup group/sequence: " + ex.Message); return; }
            // tidak ada group/sequence awal: pulihkan tampilan terakhir sebelum aplikasi mati
            if (Grid.RestoreState())
            {
                AppSettings.Current.OpLayout = Grid.Layout.Id;
                UpdateShortcutState();
                lblInfo.Text = "Tampilan terakhir dipulihkan";
            }
        }

        public void RefreshFromSettings()
        {
            BuildShortcuts();
            hLeft.Visible = AppSettings.Current.PanelHandle;
            devTree.Rebuild();
            grpPanel.Reload();
            seqPanel.Reload();
        }

        public void ShowSection(string key)
        {
            if (!fp.Visible) ToggleLeft();
            fp.Open(key);
        }

        public void ReloadLists()
        {
            devTree.Rebuild();
            grpPanel.Reload();
            seqPanel.Reload();
        }

        public void Shutdown()
        {
            uiTimer.Stop();
            seqTimer.Stop();
            holdTimer.Stop();
            foreach (PlaybackEngine e in engines.Values) e.Dispose();
            engines.Clear();
            Grid.StopAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IproNvrTool.Events.Added -= OnEventAdded;
                Shutdown();
                holdTimer.Dispose(); uiTimer.Dispose(); seqTimer.Dispose();
                ptz.Close();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
