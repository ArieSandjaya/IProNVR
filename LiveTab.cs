using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Live: multiview H.264/H.265 (maks. 16 kamera per UID), kontrol PTZ/Focus/Iris/Preset/AUX,
    /// snapshot, dan perekaman stream mentah H.264/H.265/Audio.</summary>
    public class LiveTab : UserControl, IShutdown
    {
        TextBox txtCams;
        ComboBox cmbFormat;
        NumericUpDown numDecW;
        TableLayoutPanel grid;
        readonly List<LiveTile> tiles = new List<LiveTile>();
        LiveTile selected;

        TrackBar trkSpeed, trkZoom;
        CheckBox chk256, chkCenter, chkInvertX;
        Label lblSel;
        NumericUpDown numPreset;
        ComboBox cmbAux;
        readonly SerialWorker ptz = new SerialWorker(200);
        readonly Timer holdTimer = new Timer();
        int holdDirX, holdDirY, holdDirZ;

        // rekam mentah
        NumericUpDown numRecCam, numUdpPort;
        ComboBox cmbCodec, cmbTransport;
        TextBox txtRecFolder;
        Button btnRec;
        Label lblRec;
        readonly Timer recTimer = new Timer();
        RawRecorder recorder = new RawRecorder();

        public LiveTab()
        {
            Dock = DockStyle.Fill;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // ---- toolbar ----
            var top = Ui.Flow();
            txtCams = Ui.Txt(AppSettings.Current.LastCameras, 160);
            top.Controls.Add(Ui.Lbl("Kamera (pisah koma, maks 16):"));
            top.Controls.Add(txtCams);
            cmbFormat = Ui.Combo(LiveFormats.Items, LiveFormats.ToIndex(AppSettings.Current.LiveFormat), 90);
            numDecW = Ui.Num(0, 4000, AppSettings.Current.DecodeWidth, 60);
            top.Controls.Add(Ui.Lbl("Format:"));
            top.Controls.Add(cmbFormat);
            top.Controls.Add(Ui.Lbl("Lebar dekode px (0=asli):"));
            top.Controls.Add(numDecW);
            top.Controls.Add(Ui.Btn("Mulai Live", delegate { StartAll(); }));
            top.Controls.Add(Ui.Btn("Stop", delegate { StopAll(null); }));
            top.Controls.Add(Ui.Btn("Simpan Snapshot (tile terpilih)", delegate { Snapshot(); }));
            root.Controls.Add(top, 0, 0);
            root.SetColumnSpan(top, 2);

            // ---- grid ----
            grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.BackColor = System.Drawing.Color.FromArgb(25, 25, 25);
            root.Controls.Add(grid, 0, 1);

            // ---- PTZ panel ----
            root.Controls.Add(BuildPtzPanel(), 1, 1);

            // ---- rekam mentah ----
            root.Controls.Add(BuildRecPanel(), 0, 2);
            root.SetColumnSpan(root.GetControlFromPosition(0, 2), 2);

            Controls.Add(root);

            holdTimer.Interval = 1500; // kamera i-PRO bergerak +-2 dtk per perintah
            holdTimer.Tick += delegate { SendMove(holdDirX, holdDirY, holdDirZ); };

            recTimer.Interval = 1000;
            recTimer.Tick += delegate { UpdateRecStatus(); };
            recTimer.Start();
        }

        // ===================== PTZ panel =====================

        Control BuildPtzPanel()
        {
            var p = new FlowLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.FlowDirection = FlowDirection.TopDown;
            p.WrapContents = false;
            p.AutoScroll = true;

            lblSel = new Label();
            lblSel.Text = "Kamera terpilih: (klik sebuah tile)";
            lblSel.AutoSize = true;
            lblSel.Margin = new Padding(6, 8, 3, 3);
            p.Controls.Add(lblSel);

            chk256 = new CheckBox();
            chk256.Text = "Kecepatan 256 level (bukan 16)";
            chk256.AutoSize = true;
            chk256.CheckedChanged += delegate { UpdateSpeedRange(); };
            p.Controls.Add(chk256);

            p.Controls.Add(Ui.Lbl("Kecepatan pan/tilt"));
            trkSpeed = new TrackBar();
            trkSpeed.Minimum = 1;
            trkSpeed.Maximum = 16;
            trkSpeed.Value = 8;
            trkSpeed.Width = 240;
            p.Controls.Add(trkSpeed);

            p.Controls.Add(Ui.Lbl("Kecepatan zoom (1-4)"));
            trkZoom = new TrackBar();
            trkZoom.Minimum = 1;
            trkZoom.Maximum = 4;
            trkZoom.Value = 2;
            trkZoom.Width = 240;
            p.Controls.Add(trkZoom);

            // d-pad
            var pad = new TableLayoutPanel();
            pad.ColumnCount = 3;
            pad.RowCount = 3;
            pad.AutoSize = true;
            int[,] dirs = { { -1, -1 }, { 0, -1 }, { 1, -1 }, { -1, 0 }, { 0, 0 }, { 1, 0 }, { -1, 1 }, { 0, 1 }, { 1, 1 } };
            string[] txt = { "↖", "▲", "↗", "◀", "Stop", "▶", "↙", "▼", "↘" };
            for (int i = 0; i < 9; i++)
            {
                Button b = new Button();
                b.Text = txt[i];
                b.Width = 60;
                b.Height = 36;
                int dx = dirs[i, 0];
                int dy = dirs[i, 1];
                if (dx == 0 && dy == 0)
                {
                    b.Click += delegate { StopHold(); };
                }
                else
                {
                    HookHold(b, dx, dy, 0);
                }
                pad.Controls.Add(b);
            }
            p.Controls.Add(pad);

            var zoomRow = Ui.Flow();
            zoomRow.AutoSize = true;
            zoomRow.Dock = DockStyle.None;
            Button zin = new Button();
            zin.Text = "Zoom +";
            zin.Width = 80;
            HookHold(zin, 0, 0, 1);
            Button zout = new Button();
            zout.Text = "Zoom -";
            zout.Width = 80;
            HookHold(zout, 0, 0, -1);
            zoomRow.Controls.Add(zin);
            zoomRow.Controls.Add(zout);
            p.Controls.Add(zoomRow);

            // focus & iris
            var fi = Ui.Flow();
            fi.Dock = DockStyle.None;
            fi.Width = 250;
            fi.Controls.Add(Ui.Btn("Focus Near", delegate { SendCtl("CMD=FOCUS&CTRL=NEAR&CTRL=START"); }));
            fi.Controls.Add(Ui.Btn("Focus Far", delegate { SendCtl("CMD=FOCUS&CTRL=FAR&CTRL=START"); }));
            fi.Controls.Add(Ui.Btn("Auto Focus", delegate { SendCtl("CMD=FOCUS&CTRL=AUTO&CTRL=START"); }));
            fi.Controls.Add(Ui.Btn("Iris Open", delegate { SendCtl("CMD=IRIS&CTRL=OPEN&CTRL=START"); }));
            fi.Controls.Add(Ui.Btn("Iris Close", delegate { SendCtl("CMD=IRIS&CTRL=CLOSE&CTRL=START"); }));
            fi.Controls.Add(Ui.Btn("Iris Reset", delegate { SendCtl("CMD=IRIS&CTRL=RESET&CTRL=START"); }));
            p.Controls.Add(fi);

            // preset
            var pr = Ui.Flow();
            pr.Dock = DockStyle.None;
            pr.Width = 250;
            numPreset = Ui.Num(1, 256, 1, 60);
            pr.Controls.Add(Ui.Lbl("Preset"));
            pr.Controls.Add(numPreset);
            pr.Controls.Add(Ui.Btn("Simpan", delegate { SendCtl("CMD=PRESETPRG&NO=" + (int)numPreset.Value); }));
            pr.Controls.Add(Ui.Btn("Panggil", delegate { SendCtl("CMD=PRESET&NO=" + (int)numPreset.Value); }));
            p.Controls.Add(pr);

            // AUX
            var ax = Ui.Flow();
            ax.Dock = DockStyle.None;
            ax.Width = 250;
            cmbAux = Ui.Combo(new string[] { "AUX1", "AUX2", "AUX3", "AUX4", "AUX5", "AUX6", "AUX7", "AUX8" }, 0, 70);
            ax.Controls.Add(cmbAux);
            ax.Controls.Add(Ui.Btn("Open", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=OPEN"); }));
            ax.Controls.Add(Ui.Btn("Close", delegate { SendCtl("CMD=" + cmbAux.SelectedItem + "&CTRL=CLOSE"); }));
            p.Controls.Add(ax);

            chkCenter = new CheckBox();
            chkCenter.Text = "Klik gambar = pusatkan (Pan/Tilt XY)";
            chkCenter.AutoSize = true;
            p.Controls.Add(chkCenter);

            chkInvertX = new CheckBox();
            chkInvertX.Text = "Balik sumbu X (jika arah terbalik)";
            chkInvertX.AutoSize = true;
            p.Controls.Add(chkInvertX);

            return p;
        }

        void UpdateSpeedRange()
        {
            int max = chk256.Checked ? 256 : 16;
            if (trkSpeed.Value > max) trkSpeed.Value = max;
            trkSpeed.Maximum = max;
            trkSpeed.TickFrequency = chk256.Checked ? 16 : 1;
            trkSpeed.Value = chk256.Checked ? 128 : 8;
        }

        void HookHold(Button b, int dx, int dy, int dz)
        {
            b.MouseDown += delegate
            {
                int sp = trkSpeed.Value;
                holdDirX = dx * sp;
                holdDirY = dy * sp;
                holdDirZ = dz * trkZoom.Value;
                SendMove(holdDirX, holdDirY, holdDirZ);
                holdTimer.Start();
            };
            b.MouseUp += delegate { StopHold(); };
        }

        void StopHold()
        {
            holdTimer.Stop();
            holdDirX = holdDirY = holdDirZ = 0;
            SendMove(0, 0, 0);
        }

        bool NeedSelection()
        {
            if (selected == null)
            {
                Log.Write("Pilih kamera dulu: klik salah satu tile live.");
                return false;
            }
            return true;
        }

        void SendMove(int pan, int tilt, int zoom)
        {
            if (!NeedSelection()) return;
            int cam = selected.Cam;
            // PAN + = kanan, TILT + = bawah, ZOOM + = zoom in (spec 4.2.1/4.2.2)
            ptz.Post(delegate
            {
                Sessions.Live.Cmd("directctrl.cgi", "CH=" + cam + "&PAN=" + pan + "&TILT=" + tilt + "&ZOOM=" + zoom + "&PC=AS60");
            });
        }

        void SendCtl(string query)
        {
            if (!NeedSelection()) return;
            int cam = selected.Cam;
            ptz.Post(delegate
            {
                Sessions.Live.Cmd("camctl.cgi", "CH=" + cam + "&" + query + "&PC=AS60");
            });
        }

        void OnImageClicked(LiveTile t, System.Drawing.PointF pt)
        {
            SelectTile(t);
            if (!chkCenter.Checked) return;
            int x = (int)Math.Round(pt.X * 32);
            int y = (int)Math.Round(pt.Y * 32);
            if (chkInvertX.Checked) x = 32 - x;
            SendCtl("CMD=CENTERING&CTRL=START&X=" + x + "&Y=" + y);
        }

        void SelectTile(LiveTile t)
        {
            selected = t;
            foreach (LiveTile x in tiles) x.SetSelected(x == t);
            lblSel.Text = "Kamera terpilih: CAM " + t.Cam;
        }

        // ===================== live =====================

        List<int> ParseCams()
        {
            var list = new List<int>();
            foreach (string s in txtCams.Text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int n;
                if (int.TryParse(s.Trim(), out n) && n > 0 && !list.Contains(n)) list.Add(n);
            }
            return list;
        }

        void StartAll()
        {
            List<int> cams = ParseCams();
            if (cams.Count == 0)
            {
                MessageBox.Show("Isi nomor kamera, mis. 1,2,3,4");
                return;
            }
            if (cams.Count > 16)
            {
                MessageBox.Show("Satu UID maksimal 16 stream live. Hanya 16 kamera pertama yang dipakai.");
                cams = cams.GetRange(0, 16);
            }
            AppSettings.Current.LastCameras = txtCams.Text;
            AppSettings.Current.LiveFormat = LiveFormats.ToStored(cmbFormat.SelectedIndex);
            AppSettings.Current.DecodeWidth = (int)numDecW.Value;
            try { AppSettings.Current.Save(); } catch { }

            // stream lama dihentikan dulu (STOP dikirim sinkron) supaya tidak menabrak START yang baru
            StopAll(delegate { BuildTiles(cams); });
        }

        void BuildTiles(List<int> cams)
        {
            int n = cams.Count;
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling(n / (double)cols);
            grid.SuspendLayout();
            grid.Controls.Clear();
            grid.RowStyles.Clear();
            grid.ColumnStyles.Clear();
            grid.ColumnCount = cols;
            grid.RowCount = rows;
            for (int c = 0; c < cols; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int r = 0; r < rows; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            foreach (int cam in cams)
            {
                var tile = new LiveTile(cam, Sessions.Live, LiveFormats.FromIndex(cmbFormat.SelectedIndex), (int)numDecW.Value);
                tile.Dock = DockStyle.Fill;
                tile.Margin = new Padding(2);
                tile.ImageClicked += OnImageClicked;
                tile.Selected += SelectTile;
                tiles.Add(tile);
                grid.Controls.Add(tile);
            }
            grid.ResumeLayout();
            SelectTile(tiles[0]);
            for (int i = 0; i < tiles.Count; i++) tiles[i].Start(i * 300); // jeda antar START
        }

        void StopAll(Action after)
        {
            var old = new List<LiveTile>(tiles);
            tiles.Clear();
            selected = null;
            grid.Controls.Clear();
            foreach (LiveTile t in old) t.Halt();
            Task.Run(delegate
            {
                foreach (LiveTile t in old) t.SendStop();
            }).ContinueWith(delegate
            {
                Ui.Post(this, delegate
                {
                    foreach (LiveTile t in old) t.Dispose();
                    if (after != null) after();
                });
            });
        }

        /// <summary>Isi kolom kamera (mis. dari Daftar NVR: kamera berstatus normal).</summary>
        public void SetCameras(string csv)
        {
            txtCams.Text = csv;
        }

        void Snapshot()
        {
            if (selected == null || selected.LastJpeg == null)
            {
                MessageBox.Show("Belum ada gambar. Mulai live dan pilih sebuah tile.");
                return;
            }
            using (var d = new SaveFileDialog())
            {
                d.Filter = "JPEG|*.jpg";
                d.FileName = "cam" + selected.Cam + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
                if (d.ShowDialog() == DialogResult.OK) File.WriteAllBytes(d.FileName, selected.LastJpeg);
            }
        }

        // ===================== rekam mentah =====================

        Control BuildRecPanel()
        {
            var g = new GroupBox();
            g.Text = "Rekam stream mentah (H.264 / H.265 / Audio) ke file - audio butuh stream video aktif lebih dulu";
            g.AutoSize = true;
            g.Dock = DockStyle.Fill;

            var f = Ui.Flow();
            numRecCam = Ui.Num(1, 256, 1, 55);
            cmbCodec = Ui.Combo(new string[] { "H264", "H265", "AUDIO" }, 0, 70);
            cmbTransport = Ui.Combo(new string[] { "HTTP", "RTP/UDP" }, 0, 80);
            numUdpPort = Ui.Num(1024, 65000, 50100, 70);
            txtRecFolder = Ui.Txt(AppSettings.Current.ResolveRecordFolder(), 280);
            btnRec = Ui.Btn("Mulai Rekam", delegate { ToggleRec(); });
            lblRec = Ui.Lbl("siap");

            f.Controls.Add(Ui.Lbl("Kamera"));
            f.Controls.Add(numRecCam);
            f.Controls.Add(Ui.Lbl("Format"));
            f.Controls.Add(cmbCodec);
            f.Controls.Add(Ui.Lbl("Transport"));
            f.Controls.Add(cmbTransport);
            f.Controls.Add(Ui.Lbl("Port UDP"));
            f.Controls.Add(numUdpPort);
            f.Controls.Add(Ui.Lbl("Folder"));
            f.Controls.Add(txtRecFolder);
            f.Controls.Add(Ui.Btn("...", delegate
            {
                string d = Ui.BrowseFolder(txtRecFolder.Text);
                if (d != null) txtRecFolder.Text = d;
            }));
            f.Controls.Add(btnRec);
            f.Controls.Add(lblRec);
            g.Controls.Add(f);
            return g;
        }

        void ToggleRec()
        {
            if (recorder.Running)
            {
                recorder.Stop();
                btnRec.Text = "Mulai Rekam";
                return;
            }
            AppSettings.Current.RecordFolder = txtRecFolder.Text;
            try { AppSettings.Current.Save(); } catch { }
            recorder = new RawRecorder();
            recorder.Start(Sessions.Rec, (int)numRecCam.Value, (string)cmbCodec.SelectedItem,
                cmbTransport.SelectedIndex == 1, (int)numUdpPort.Value, txtRecFolder.Text);
            btnRec.Text = "Stop Rekam";
        }

        void UpdateRecStatus()
        {
            if (recorder.Running)
                lblRec.Text = "merekam: " + (recorder.Bytes / 1024) + " KB -> " + Path.GetFileName(recorder.FilePath);
            else if (btnRec.Text == "Stop Rekam")
            {
                btnRec.Text = "Mulai Rekam";
                lblRec.Text = "berhenti (" + (recorder.Bytes / 1024) + " KB) " + recorder.FilePath;
            }
        }

        public void Shutdown()
        {
            holdTimer.Stop();
            recTimer.Stop();
            recorder.Stop();
            foreach (LiveTile t in tiles) t.Stop();
            tiles.Clear();
            selected = null;
        }
    }
}
