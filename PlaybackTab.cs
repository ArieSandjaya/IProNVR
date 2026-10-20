using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Playback: pilih kamera (MASK), lompat ke tanggal/jam (GOTODATE), play, reverse, pause,
    /// frame berikut/sebelumnya, fast forward/reverse, stop. Frame JPEG ditampilkan langsung;
    /// rekaman H.264/H.265 diambil per-GOP dan bisa disimpan (.h264/.h265).</summary>
    public class PlaybackTab : UserControl, IShutdown
    {
        NumericUpDown numCam, numInterval;
        DateTimePicker dtp;
        PictureBox pic;
        Label lblTime, lblStatus;
        readonly Timer timer = new Timer();
        readonly SerialWorker worker = new SerialWorker(0);

        volatile string mode = "STOP";   // STOP, PLAY, BPLAY, FF, REW, PAUSE
        int activeCam = 1;
        int preparedCam = -1;
        DateTime? curUtc;
        byte[] lastGop;
        string lastGopExt = ".h264";

        public PlaybackTab()
        {
            Dock = DockStyle.Fill;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var row1 = Ui.Flow();
            numCam = Ui.Num(1, 256, 1, 55);
            dtp = new DateTimePicker();
            dtp.Format = DateTimePickerFormat.Custom;
            dtp.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            dtp.Width = 170;
            dtp.Margin = new Padding(3, 5, 3, 3);
            dtp.Value = DateTime.Now.AddMinutes(-10);
            numInterval = Ui.Num(300, 60000, Math.Max(300, AppSettings.Current.PlaybackIntervalMs), 70);
            row1.Controls.Add(Ui.Lbl("Kamera"));
            row1.Controls.Add(numCam);
            row1.Controls.Add(Ui.Lbl("Waktu (lokal PC)"));
            row1.Controls.Add(dtp);
            row1.Controls.Add(Ui.Btn("Lompat ke Waktu & Putar", delegate { DoGoto(); }));
            row1.Controls.Add(Ui.Lbl("Interval ambil frame (ms; spec: >= 3000)"));
            row1.Controls.Add(numInterval);

            var row2 = Ui.Flow();
            row2.Controls.Add(Ui.Btn("Play", delegate { DoPlay(); }));
            row2.Controls.Add(Ui.Btn("Reverse", delegate { DoSimple("BPLAY", "BPLAY", true, false); }));
            row2.Controls.Add(Ui.Btn("Pause", delegate { DoPause(); }));
            row2.Controls.Add(Ui.Btn("|< Frame Sebelumnya", delegate { DoStep("PREVFRAME"); }));
            row2.Controls.Add(Ui.Btn("Frame Berikut >|", delegate { DoStep("NEXTFRAME"); }));
            row2.Controls.Add(Ui.Btn("Fast Reverse", delegate { DoSimple("REW", "REW", true, true); }));
            row2.Controls.Add(Ui.Btn("Fast Forward", delegate { DoSimple("FF", "FF", true, true); }));
            row2.Controls.Add(Ui.Btn("Stop", delegate { DoStop(); }));
            row2.Controls.Add(Ui.Btn("Simpan GOP (H.264/H.265)", delegate { SaveGop(); }));
            row2.Controls.Add(Ui.Btn("Simpan Audio ~1 dtk", delegate { SaveAudio(); }));

            pic = new PictureBox();
            pic.Dock = DockStyle.Fill;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.Black;

            var bottom = Ui.Flow();
            lblTime = Ui.Lbl("Waktu frame: -");
            lblStatus = Ui.Lbl("Siap.");
            bottom.Controls.Add(lblTime);
            bottom.Controls.Add(lblStatus);

            root.Controls.Add(row1, 0, 0);
            root.Controls.Add(row2, 0, 1);
            root.Controls.Add(pic, 0, 2);
            root.Controls.Add(bottom, 0, 3);
            Controls.Add(root);

            timer.Tick += delegate { OnTick(); };
        }

        // ---------- helpers ----------

        void Status(string s)
        {
            Ui.Post(this, delegate { lblStatus.Text = s; });
        }

        static string Mask(int cam)
        {
            int n = Math.Max(AppSettings.Current.MaxChannels, cam);
            var c = new char[n];
            for (int i = 0; i < n; i++) c[i] = '0';
            c[cam - 1] = '1';
            return new string(c);
        }

        void Prepare(int cam)
        {
            NvrSession s = Sessions.Play;
            s.EnsureLogin();
            if (preparedCam != cam)
            {
                s.Cmd("hdrctl.cgi", "PC=AS60&MASK=" + Mask(cam));
                preparedCam = cam;
            }
        }

        string CurTimef(DateTime fallbackUtc)
        {
            DateTime t = curUtc.HasValue ? curUtc.Value : fallbackUtc;
            return Timef.Format(t);
        }

        void StartTimer()
        {
            Ui.Post(this, delegate
            {
                timer.Interval = (int)numInterval.Value;
                AppSettings.Current.PlaybackIntervalMs = (int)numInterval.Value;
                timer.Start();
            });
        }

        void OnTick()
        {
            string m = mode;
            if (m == "PLAY" || m == "BPLAY" || m == "FF" || m == "REW")
            {
                if (worker.Pending > 1) return; // jangan menumpuk jika NVR lambat
                worker.Post(delegate { Fetch(); });
            }
        }

        // ---------- perintah ----------

        void DoGoto()
        {
            int cam = (int)numCam.Value;
            DateTime t = dtp.Value.ToUniversalTime();
            string label = dtp.Value.ToString("yyyy-MM-dd HH:mm:ss");
            activeCam = cam;
            mode = "PLAY";
            worker.Post(delegate
            {
                Prepare(cam);
                Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=GOTODATE&2ND=OFF&TIMEF=" + Timef.Format(t) + "&SDBACKUP=0&NOTIME=ON");
                Status("Putar dari " + label + " (kamera " + cam + ")");
                Fetch();
            });
            StartTimer();
        }

        void DoPlay()
        {
            if (!curUtc.HasValue)
            {
                DoGoto();
                return;
            }
            int cam = activeCam;
            mode = "PLAY";
            worker.Post(delegate
            {
                Prepare(cam);
                Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=PLAY&2ND=OFF&TIMEF=AS60_UNKNOWN&NOTIME=ON");
                Fetch();
            });
            StartTimer();
        }

        void DoSimple(string cmd, string newMode, bool notime, bool smooth)
        {
            if (!curUtc.HasValue)
            {
                Status("Mulai playback dulu (Lompat ke Waktu & Putar).");
                return;
            }
            int cam = activeCam;
            DateTime fb = dtp.Value.ToUniversalTime();
            mode = newMode;
            worker.Post(delegate
            {
                Prepare(cam);
                Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=" + cmd + "&2ND=OFF&TIMEF=" + CurTimef(fb)
                    + (smooth ? "&SMOOTH=OFF" : "") + (notime ? "&NOTIME=ON" : ""));
                Fetch();
            });
            StartTimer();
        }

        void DoPause()
        {
            if (!curUtc.HasValue) return;
            DateTime fb = dtp.Value.ToUniversalTime();
            mode = "PAUSE";
            worker.Post(delegate
            {
                Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=PAUSE&2ND=OFF&TIMEF=" + CurTimef(fb) + "&SMOOTH=OFF&NOTIME=ON");
                Status("Pause.");
            });
        }

        void DoStep(string cmd)
        {
            if (!curUtc.HasValue)
            {
                Status("Mulai playback dulu.");
                return;
            }
            int cam = activeCam;
            DateTime fb = dtp.Value.ToUniversalTime();
            mode = "PAUSE";
            worker.Post(delegate
            {
                Prepare(cam);
                Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=" + cmd + "&2ND=OFF&TIMEF=" + CurTimef(fb) + "&NOTIME=ON");
                Fetch();
            });
        }

        void DoStop()
        {
            mode = "STOP";
            DateTime fb = dtp.Value.ToUniversalTime();
            string tf = CurTimef(fb);
            Ui.Post(this, delegate { timer.Stop(); });
            worker.Post(delegate
            {
                if (preparedCam >= 0)
                    Sessions.Play.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=STOP&2ND=OFF&TIMEF=" + tf);
                preparedCam = -1;
                curUtc = null;
                Status("Stop.");
            });
        }

        // ---------- ambil & tampilkan frame ----------

        void Fetch()
        {
            int cam = activeCam;
            byte[] data = Sessions.Play.CmdBytes("camera.cgi", "CAM=" + cam, 30000);
            ParsedFrame f = FrameParser.Parse(data);
            if (f.UtcTime.HasValue) curUtc = f.UtcTime;
            else if (f.GopStartUtc.HasValue) curUtc = f.GopStartUtc;

            if (f.Video.Count == 0)
            {
                Status("Tidak ada data video pada waktu tersebut (" + data.Length + " byte).");
                return;
            }

            if (f.Codec == "JPEG")
            {
                Image img = Ui.DecodeImage(f.Video[0]);
                Ui.Post(this, delegate
                {
                    Image old = pic.Image;
                    pic.Image = img;
                    if (old != null) old.Dispose();
                    lblTime.Text = "Waktu frame: " + (curUtc.HasValue ? curUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-");
                });
            }
            else
            {
                using (var ms = new MemoryStream())
                {
                    foreach (byte[] v in f.Video) ms.Write(v, 0, v.Length);
                    lastGop = ms.ToArray();
                }
                lastGopExt = f.Codec == "H265" ? ".h265" : ".h264";
                Ui.Post(this, delegate
                {
                    lblTime.Text = "Waktu GOP: " + (curUtc.HasValue ? curUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-");
                });
                Status(f.Codec + ": GOP berisi " + f.Video.Count + " gambar diterima (tidak ditampilkan, gunakan 'Simpan GOP').");
            }
        }

        void SaveGop()
        {
            byte[] g = lastGop;
            if (g == null)
            {
                MessageBox.Show("Belum ada GOP H.264/H.265 yang diterima.");
                return;
            }
            using (var d = new SaveFileDialog())
            {
                d.Filter = "Raw " + lastGopExt + "|*" + lastGopExt;
                d.FileName = "gop_cam" + activeCam + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + lastGopExt;
                if (d.ShowDialog() == DialogResult.OK) File.WriteAllBytes(d.FileName, g);
            }
        }

        void SaveAudio()
        {
            if (!curUtc.HasValue)
            {
                Status("Mulai playback dulu.");
                return;
            }
            int cam = activeCam;
            worker.Post(delegate
            {
                byte[] a = Sessions.Play.CmdBytes("playaudio.cgi", "CAM=" + cam, 30000);
                Ui.Post(this, delegate
                {
                    using (var d = new SaveFileDialog())
                    {
                        d.Filter = "Audio mentah (format bab 3.3.2)|*.bin";
                        d.FileName = "audio_cam" + cam + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";
                        if (d.ShowDialog() == DialogResult.OK) File.WriteAllBytes(d.FileName, a);
                    }
                });
            });
        }

        public void Shutdown()
        {
            mode = "STOP";
            timer.Stop();
        }
    }
}
