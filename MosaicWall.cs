using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Mosaic GPU: satu proses ffmpeg per layar mendekode N substream dengan NVDEC (GPU NVIDIA), mengecilkan dan menyusunnya
    /// menjadi satu gambar di GPU (scale_cuda + overlay_cuda), lalu meng-encode dengan NVENC dan mengirimnya ke localhost (UDP).
    /// Aplikasi hanya memutar SATU stream per layar, bukan N proses/JPEG/bitmap. Double-click kotak = kamera itu dalam stream utama.
    /// Butuh ffmpeg build "full" (mis. gyan.dev) dengan scale_cuda, overlay_cuda, h264_nvenc dan driver NVIDIA.</summary>
    public static class MosaicWall
    {
        const int Fps = 15;

        class Cell { public Rectangle Px; public CamRef Cam; public string Url; public bool Alive; }

        // ---------- deteksi GPU/backend ----------

        static string backend, backendError;
        static bool detected;

        static string Run(string exe, string args, int timeoutMs, out int code)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                string o2 = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } code = -1; return "timeout"; }
                p.WaitForExit();
                code = p.ExitCode;
                lock (err) return err.ToString() + o2;
            }
        }

        /// <summary>Uji nyata: ffmpeg membuat satu frame kecil di GPU lalu meng-encode-nya. Berhasil = backend tersedia di PC ini.</summary>
        // varian argumen Quick Sync yang dicoba berurutan (driver/build ffmpeg berbeda-beda soal perangkat anak d3d11/dxva2 dan opsi encoder)
        static readonly string[][] QsvVariants = new string[][]
        {
            new string[] { "qsv=hw:hw_any,child_device_type=d3d11va", "-b:v 2M -look_ahead 0" },
            new string[] { "qsv=hw:hw_any,child_device_type=dxva2", "-b:v 2M -look_ahead 0" },
            new string[] { "qsv=hw", "-b:v 2M -look_ahead 0 -low_power 0" },
            new string[] { "qsv=hw", "" },
        };
        static int qsvVariant;   // varian yang lolos uji

        static bool Probe(string exe, string which, out string log)
        {
            // uji seluruh rantai kecil: dua sumber -> upload GPU -> scale -> overlay -> encode (encoder QSV menolak gambar sangat kecil, jadi 320x240)
            string src = "-f lavfi -i color=c=black:s=320x240:r=15:d=1 -f lavfi -i color=c=gray:s=320x240:r=15:d=1 ";
            int code;
            if (which == "nvidia")
            {
                string a = "-hide_banner -loglevel error -init_hw_device cuda=cu:0 -filter_hw_device cu " + src
                  + "-filter_complex \"[0:v]format=nv12,hwupload_cuda[a];[1:v]format=nv12,hwupload_cuda,scale_cuda=160:120[b];[a][b]overlay_cuda=x=0:y=0\" -c:v h264_nvenc -f null -";
                log = Run(exe, a, 15000, out code);
                return code == 0;
            }
            var all = new StringBuilder();
            for (int v = 0; v < QsvVariants.Length; v++)
            {
                string dev = QsvVariants[v][0], enc = QsvVariants[v][1];
                string a = "-hide_banner -loglevel error -init_hw_device " + dev + " -filter_hw_device hw " + src
                  + "-filter_complex \"[0:v]format=nv12,hwupload=extra_hw_frames=16[a];[1:v]format=nv12,hwupload=extra_hw_frames=16,scale_qsv=w=160:h=120[b];[a][b]overlay_qsv=x=0:y=0\" -c:v h264_qsv " + enc + " -f null -";
                string l = Run(exe, a, 15000, out code);
                if (code == 0) { qsvVariant = v; log = l; Log.Write("Mosaic GPU: Quick Sync varian #" + (v + 1) + " lolos (" + dev + ")."); return true; }
                all.AppendLine("-- varian " + (v + 1) + " (" + dev + " " + enc + "):");
                all.AppendLine(l.Trim());
            }
            // diagnosis: apakah encoder sendiri bisa (tanpa filter GPU)? membedakan masalah driver/encoder dengan masalah filter
            string d = Run(exe, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:r=15:d=1 -vf format=nv12 -c:v h264_qsv -f null -", 15000, out code);
            all.AppendLine("-- encoder h264_qsv saja (tanpa filter GPU): " + (code == 0 ? "BERHASIL" : "GAGAL\n" + d.Trim()));
            string d2 = Run(exe, "-hide_banner -loglevel error -init_hw_device qsv=hw -filter_hw_device hw -f lavfi -i color=c=black:s=320x240:r=15:d=1 -vf format=nv12,hwupload=extra_hw_frames=16,scale_qsv=w=160:h=120,hwdownload,format=nv12 -f null -", 15000, out code);
            all.AppendLine("-- hwupload + scale_qsv saja (tanpa encoder): " + (code == 0 ? "BERHASIL" : "GAGAL\n" + d2.Trim()));
            log = all.ToString();
            return false;
        }

        /// <summary>"nvidia" / "intel" sesuai pengaturan (Otomatis = coba NVIDIA lalu Intel). Null + backendError bila tidak ada yang bisa dipakai.</summary>
        static string Detect(string exe)
        {
            string pref = (AppSettings.Current.MosaicGpu ?? "auto").ToLowerInvariant();
            if (detected && backendPref == pref) return backend;
            detected = true; backendPref = pref; backend = null; backendError = null;
            var sb = new StringBuilder();
            string[] order = pref == "nvidia" ? new string[] { "nvidia" } : pref == "intel" ? new string[] { "intel" } : new string[] { "nvidia", "intel" };
            foreach (string w in order)
            {
                string log;
                try
                {
                    if (Probe(exe, w, out log)) { backend = w; Log.Write("Mosaic GPU: backend " + w + " tersedia."); return backend; }
                }
                catch (Exception ex) { log = ex.Message; }
                string t = (log ?? "").Trim();
                if (t.Length > 2500) t = t.Substring(t.Length - 2500);
                sb.AppendLine("[" + (w == "nvidia" ? "NVIDIA (NVDEC/NVENC)" : "Intel (Quick Sync)") + "] tidak bisa dipakai:\n" + t + "\n");
                Log.Write("Mosaic GPU: backend " + w + " gagal: " + t.Replace("\n", " | "));
            }
            backendError = "Tidak ada GPU yang bisa dipakai untuk mosaic.\n\n" + sb
                + "Pastikan: ffmpeg build \"full\" (mis. gyan.dev), driver GPU terbaru, dan GPU yang dipilih di Setup > Dekode & stream > \"GPU mosaic\".";
            return null;
        }
        static string backendPref;

        // ---------- util ----------

        static int FreeUdpPort()
        {
            var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int p = ((IPEndPoint)u.Client.LocalEndPoint).Port;
            u.Close();
            return p;
        }

        static string FirstRtsp(NvrRecord nvr, CameraRecord cam, bool sub)
        {
            foreach (StreamSource s in StreamPlan.ForLive(nvr, cam, sub))
                if (s.Rtsp && !string.IsNullOrEmpty(s.Url)) return s.Url;
            return null;
        }

        static bool Reachable(string url)
        {
            try
            {
                var u = new Uri(url);
                using (var c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect(u.Host, u.Port > 0 ? u.Port : 554, null, null);
                    bool ok = r.AsyncWaitHandle.WaitOne(2000) && c.Connected;
                    try { c.Close(); } catch { }
                    return ok;
                }
            }
            catch { return false; }
        }

        static List<Cell> Build(GroupRec g, ScreenLayout lay, int W, int H)
        {
            var cells = new List<Cell>();
            for (int i = 0; i < lay.Count; i++)
            {
                Rectangle r = lay.Cells[i];
                int x = (r.X * W / lay.Cols + 1) & ~1;
                int y = (r.Y * H / lay.Rows + 1) & ~1;
                int x2 = ((r.Right * W / lay.Cols) - 1) & ~1;
                int y2 = ((r.Bottom * H / lay.Rows) - 1) & ~1;
                var c = new Cell();
                c.Px = new Rectangle(x, y, Math.Max(16, x2 - x), Math.Max(16, y2 - y));
                c.Cam = i < g.Slots.Length ? g.Slots[i] : null;
                if (c.Cam != null)
                {
                    NvrRecord nvr = Dir.Nvr(c.Cam.NvrId);
                    CameraRecord cam = Dir.Cam(c.Cam.NvrId, c.Cam.Channel);
                    if (nvr != null) c.Url = FirstRtsp(nvr, cam, lay.Count > 1);
                }
                cells.Add(c);
            }
            return cells;
        }

        // ---------- sesi mosaic (satu per layar) ----------

        class Session : IDisposable
        {
            public GroupRec Group; public ScreenLayout Lay; public Form Form; public LiveTile Tile;
            public List<Cell> Cells; public int W, H, Port; public string Exe; public string Backend;
            public volatile bool Stop;
            public volatile string Status = "memulai...";
            public volatile string Viewer = "";
            public Process Proc;
            public readonly List<string> Err = new List<string>();
            public Thread Worker;

            public void Dispose()
            {
                Stop = true;
                try { if (Proc != null && !Proc.HasExited) Proc.Kill(); } catch { }
            }
        }

        static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool", "mosaic_ffmpeg.log"); }
        }
        static readonly object fileLock = new object();
        static void FileLog(string line)
        {
            try
            {
                lock (fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + MaskArgs(line) + "\r\n");
                }
            }
            catch { }
        }

        static readonly List<Session> sessions = new List<Session>();

        static string BuildArgs(Session s, List<Cell> cells)
        {
            bool nv = s.Backend == "nvidia";
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel warning -nostats ");
            sb.Append(nv ? "-init_hw_device cuda=cu:0 -filter_hw_device cu " : "-init_hw_device " + QsvVariants[qsvVariant][0] + " -filter_hw_device hw ");
            var fc = new StringBuilder();
            int idx = 0;
            var scaled = new List<string>();
            var pos = new List<Rectangle>();
            foreach (Cell c in cells)
            {
                if (c.Url == null || !c.Alive) continue;
                if (nv) sb.Append("-hwaccel cuda -hwaccel_output_format cuda -hwaccel_device cu ");
                else sb.Append("-hwaccel qsv -hwaccel_output_format qsv -hwaccel_device hw ");
                sb.Append("-rtsp_transport tcp -fflags nobuffer -flags low_delay -probesize 200000 -analyzeduration 1000000 -timeout 8000000 ");
                sb.Append("-i \"" + c.Url.Replace("\"", "%22") + "\" ");
                fc.Append("[" + idx + ":v:0]" + (nv ? "scale_cuda=" + c.Px.Width + ":" + c.Px.Height : "scale_qsv=w=" + c.Px.Width + ":h=" + c.Px.Height) + "[s" + idx + "];");
                scaled.Add("s" + idx);
                pos.Add(c.Px);
                idx++;
            }
            if (idx == 0) return null;
            sb.Append("-f lavfi -i color=c=black:s=" + s.W + "x" + s.H + ":r=" + Fps + " ");
            fc.Append("[" + idx + ":v]format=nv12," + (nv ? "hwupload_cuda" : "hwupload=extra_hw_frames=64") + "[b0];");
            for (int i = 0; i < scaled.Count; i++)
            {
                string ov = nv ? "overlay_cuda=x=" + pos[i].X + ":y=" + pos[i].Y
                               : "overlay_qsv=x=" + pos[i].X + ":y=" + pos[i].Y;
                fc.Append("[b" + i + "][" + scaled[i] + "]" + ov + "[b" + (i + 1) + "]" + (i + 1 < scaled.Count ? ";" : ""));
            }
            sb.Append("-filter_complex \"" + fc + "\" -map \"[b" + scaled.Count + "]\" -an ");
            int mb = Math.Max(4, s.W * s.H / 250000);
            if (nv) sb.Append("-c:v h264_nvenc -preset p2 -tune ll -rc cbr -b:v " + mb + "M -g " + Fps + " -bf 0 ");
            else sb.Append("-c:v h264_qsv -preset veryfast -b:v " + mb + "M -maxrate " + mb + "M -g " + Fps + " -bf 0 " + (QsvVariants[qsvVariant][1].Contains("-look_ahead") ? "-look_ahead 0 " : "") + (QsvVariants[qsvVariant][1].Contains("-low_power") ? "-low_power 0 " : ""));
            sb.Append("-f mpegts \"udp://127.0.0.1:" + s.Port + "?pkt_size=1316\"");
            return sb.ToString();
        }

        static void Work(Session s)
        {
            int fails = 0;
            while (!s.Stop)
            {
                // kamera yang tidak terjangkau diganti hitam (satu input yang gagal membuka akan mematikan seluruh proses)
                var tasks = new List<System.Threading.Tasks.Task>();
                foreach (Cell c in s.Cells)
                {
                    Cell cc = c;
                    if (cc.Url == null) { cc.Alive = false; continue; }
                    tasks.Add(System.Threading.Tasks.Task.Run(delegate { cc.Alive = Reachable(cc.Url); }));
                }
                s.Status = "memeriksa " + tasks.Count + " kamera...";
                try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), 8000); } catch { }
                int alive = 0; foreach (Cell c in s.Cells) if (c.Alive) alive++;
                s.Status = alive + " dari " + tasks.Count + " kamera terjangkau; membuka ffmpeg (" + (s.Backend == "nvidia" ? "NVIDIA" : "Intel") + ")...";
                FileLog(s.Status);
                string args = BuildArgs(s, s.Cells);
                if (args == null) { Log.Write("Mosaic: tidak ada kamera yang terjangkau, coba lagi 15 dtk."); Sleep(s, 15000); continue; }
                var sw = Stopwatch.StartNew();
                try
                {
                    Log.Write("Mosaic: ffmpeg " + MaskArgs(args));
                    FileLog("CMD: " + args);
                    var psi = new ProcessStartInfo(s.Exe, args);
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true;
                    Process p = Process.Start(psi);
                    s.Proc = p;
                    var t = new Thread(delegate()
                    {
                        try
                        {
                            string line;
                            while ((line = p.StandardError.ReadLine()) != null)
                            {
                                line = MaskArgs(line);
                                lock (s.Err) { s.Err.Add(line); if (s.Err.Count > 40) s.Err.RemoveAt(0); }
                                FileLog(line);
                                s.Status = "ffmpeg: " + (line.Length > 110 ? line.Substring(0, 110) : line);
                                if (!s.Stop) Log.Write("mosaic ffmpeg: " + line);
                            }
                        }
                        catch { }
                    });
                    t.IsBackground = true; t.Start();
                    // hidupkan ulang bila ada kamera yang tadinya mati sudah hidup kembali (cek tiap 60 dtk)
                    int nextProbe = Environment.TickCount + 60000;
                    while (!s.Stop && !p.HasExited)
                    {
                        Thread.Sleep(500);
                        if (sw.Elapsed.TotalSeconds > 3 && s.Status.StartsWith("memeriksa") == false && s.Status.IndexOf("membuka ffmpeg") >= 0) s.Status = "ffmpeg berjalan, menunggu frame pertama (" + (int)sw.Elapsed.TotalSeconds + " dtk)...";
                        if (unchecked(Environment.TickCount - nextProbe) >= 0)
                        {
                            nextProbe = Environment.TickCount + 60000;
                            bool changed = false;
                            foreach (Cell c in s.Cells)
                                if (c.Url != null && !c.Alive && Reachable(c.Url)) changed = true;
                            if (changed) { Log.Write("Mosaic: ada kamera yang pulih, menyusun ulang."); break; }
                        }
                    }
                }
                catch (Exception ex) { Log.Write("Mosaic: " + ex.Message); FileLog("EXC: " + ex.Message); s.Status = "gagal: " + ex.Message; }
                finally { try { if (s.Proc != null && !s.Proc.HasExited) s.Proc.Kill(); } catch { } }
                if (s.Stop) return;
                FileLog("ffmpeg berhenti setelah " + (int)sw.Elapsed.TotalSeconds + " dtk");
                s.Status = "ffmpeg berhenti setelah " + (int)sw.Elapsed.TotalSeconds + " dtk, mengulang... (lihat mosaic_ffmpeg.log)";
                fails = sw.Elapsed.TotalSeconds < 20 ? fails + 1 : 0;
                if (fails >= 3)
                {
                    string last; lock (s.Err) last = string.Join("\n", s.Err.ToArray());
                    Form f = s.Form;
                    try
                    {
                        f.BeginInvoke((Action)delegate
                        {
                            MessageBox.Show(f, "Mosaic GPU gagal berulang. Keluaran ffmpeg:\n\n" + (last.Length > 1500 ? last.Substring(last.Length - 1500) : last), "Mosaic GPU");
                            f.Close();
                        });
                    }
                    catch { }
                    return;
                }
                Sleep(s, 3000);
            }
        }

        static void Sleep(Session s, int ms) { for (int t = 0; t < ms && !s.Stop; t += 200) Thread.Sleep(200); }

        static string MaskArgs(string a) { return System.Text.RegularExpressions.Regex.Replace(a, @"(rtsp://[^:/@\s]+):[^@\s]*@", "$1:***@"); }

        // ---------- buka ----------

        /// <summary>Buka mosaic GPU untuk satu group di monitor tertentu (panggil dari UI thread). Mengembalikan pesan error atau null.</summary>
        public static string Open(GroupRec g, Screen screen)
        {
            string exe = FfmpegPipe.FindExe();
            if (exe == null) return "ffmpeg.exe tidak ditemukan.";
            string be = null;
            string bk = null;
            // deteksi memakai proses uji (beberapa detik); jalankan di thread latar agar UI tidak macet
            var dt = System.Threading.Tasks.Task.Run(delegate { bk = Detect(exe); });
            while (!dt.IsCompleted) { Application.DoEvents(); Thread.Sleep(30); }
            be = bk;
            if (be == null)
            {
                string msg = backendError ?? "Mosaic GPU tidak tersedia.";
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool");
                    Directory.CreateDirectory(dir);
                    string f0 = Path.Combine(dir, "mosaic_diag.txt");
                    File.WriteAllText(f0, msg);
                    if (msg.Length > 1600) msg = msg.Substring(0, 1600) + "\n...\n(Lengkapnya ada di " + f0 + ")";
                }
                catch { }
                return msg;
            }

            ScreenLayout lay = ScreenLayout.ById(g.Layout);
            Rectangle b = screen.Bounds;
            int W = Math.Min(b.Width, 1920) & ~1, H = Math.Min(b.Height, 1080) & ~1;
            var s = new Session();
            s.Group = g; s.Lay = lay; s.W = W; s.H = H; s.Exe = exe; s.Backend = be;
            s.Port = FreeUdpPort();
            s.Cells = Build(g, lay, W, H);
            int usable = 0;
            foreach (Cell c in s.Cells) if (c.Url != null) usable++;
            if (usable == 0) return "Tidak ada kamera di group ini yang punya URL RTSP.";

            var f = new Form();
            f.Text = "Mosaic GPU (" + (be == "nvidia" ? "NVIDIA" : "Intel") + ") - " + g.Name;
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = b;
            f.BackColor = Color.Black;
            f.KeyPreview = true;
            s.Form = f;

            var tile = new LiveTile(0, null, LiveFormat.H264, 0);
            tile.Dock = DockStyle.Fill;
            tile.HideHeader();
            tile.FillCell();
            tile.BackColor = Color.Black;
            string url = "udp://127.0.0.1:" + s.Port + "?fifo_size=2000000&overrun_nonfatal=1";
            tile.Sources = new List<StreamSource> { new StreamSource { Rtsp = true, Url = url, Label = "Mosaic GPU" } };
            s.Tile = tile;
            f.Controls.Add(tile);
            try { File.WriteAllText(LogPath, ""); } catch { }

            // status diagnosis di pojok kiri atas (hilang sendiri setelah video tampil)
            var st = new Label();
            st.AutoSize = true;
            st.MaximumSize = new Size(Math.Max(300, b.Width - 40), 0);
            st.ForeColor = Color.Yellow;
            st.BackColor = Color.FromArgb(20, 20, 20);
            st.Font = new Font("Segoe UI", 9f);
            st.Location = new Point(8, 8);
            st.Parent = tile.Picture;
            st.Enabled = false;
            tile.StatusChanged += delegate(LiveTile t, string text) { s.Viewer = text; };
            var tm = new System.Windows.Forms.Timer();
            tm.Interval = 1000;
            tm.Tick += delegate
            {
                if (tile.HasPicture) { st.Visible = false; return; }
                st.Visible = true;
                st.Text = "Mosaic GPU: " + s.Status + "\nPemutar: " + s.Viewer + "\nLog: " + LogPath;
                st.BringToFront();
            };
            tm.Start();
            f.FormClosed += delegate { tm.Stop(); tm.Dispose(); };

            // nama kamera per kotak
            for (int i = 0; i < s.Cells.Count; i++)
            {
                Cell c = s.Cells[i];
                if (c.Cam == null) continue;
                var l = new Label();
                l.AutoSize = true;
                l.Text = Dir.Title(c.Cam.NvrId, c.Cam.Channel) + (c.Url == null ? " - RTSP tidak tersedia" : "");
                l.ForeColor = Color.Gainsboro;
                l.BackColor = Color.FromArgb(30, 30, 30);
                l.Font = new Font("Segoe UI", 8.5f);
                l.Tag = c;
                l.Parent = tile.Picture;
                l.Enabled = false;
            }
            Action layoutLabels = delegate
            {
                int pw = Math.Max(1, tile.Picture.Width), ph = Math.Max(1, tile.Picture.Height);
                foreach (Control l in tile.Picture.Controls)
                {
                    Cell c = l.Tag as Cell;
                    if (c == null) continue;
                    l.Location = new Point(c.Px.X * pw / s.W + 4, (c.Px.Bottom * ph / s.H) - l.Height - 3);
                }
            };
            tile.Picture.SizeChanged += delegate { layoutLabels(); };
            f.Shown += delegate { layoutLabels(); };

            tile.DoubleClicked += delegate(LiveTile t)
            {
                Point p = tile.Picture.PointToClient(Control.MousePosition);
                double fx = (double)p.X / Math.Max(1, tile.Picture.Width) * s.W, fy = (double)p.Y / Math.Max(1, tile.Picture.Height) * s.H;
                foreach (Cell c in s.Cells)
                    if (c.Cam != null && c.Px.Contains((int)fx, (int)fy)) { OpenMain(f, c); break; }
            };
            f.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.FormClosed += delegate
            {
                try { tile.Halt(); } catch { }
                s.Dispose();
                lock (sessions) sessions.Remove(s);
            };
            lock (sessions) sessions.Add(s);
            Application.ApplicationExit += delegate { s.Dispose(); };

            s.Worker = new Thread(delegate() { Work(s); });
            s.Worker.IsBackground = true;
            s.Worker.Start();
            f.Show();
            tile.Start(1500);
            return null;
        }

        static void OpenMain(Form owner, Cell c)
        {
            NvrRecord nvr = Dir.Nvr(c.Cam.NvrId);
            CameraRecord cam = Dir.Cam(c.Cam.NvrId, c.Cam.Channel);
            if (nvr == null) return;
            var f = new Form();
            f.Text = Dir.Title(c.Cam.NvrId, c.Cam.Channel);
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(1280, 720);
            f.BackColor = Color.Black;
            f.KeyPreview = true;
            var t = new LiveTile(c.Cam.Channel, null, LiveFormat.H264, 0);
            t.Dock = DockStyle.Fill;
            t.HideHeader();
            t.FillCell();
            t.Sources = StreamPlan.ForLive(nvr, cam, false);
            t.Caption = f.Text;
            f.Controls.Add(t);
            f.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) f.Close(); };
            t.DoubleClicked += delegate { f.Close(); };
            f.FormClosed += delegate { try { t.Halt(); } catch { } };
            f.Shown += delegate { t.Start(0); };
            f.Show(owner);
        }
    }
}
