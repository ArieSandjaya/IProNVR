using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Format live dari NVR. Nilai angka = nilai yang tersimpan di pengaturan/database (kompatibel dengan versi lama:
    /// nilai lain, mis. bekas JPEG/Auto, dianggap H.264).</summary>
    public enum LiveFormat
    {
        H264 = 2,
        H265 = 3
    }

    public static class LiveFormats
    {
        public static readonly string[] Items = new string[] { "H.264", "H.265" };

        public static int ToIndex(int stored) { return stored == 3 ? 1 : 0; }
        public static int ToStored(int index) { return index == 1 ? 3 : 2; }
        public static LiveFormat FromIndex(int index) { return index == 1 ? LiveFormat.H265 : LiveFormat.H264; }
        public static LiveFormat FromStored(int stored) { return stored == 3 ? LiveFormat.H265 : LiveFormat.H264; }
    }

    /// <summary>Satu kemungkinan sumber video untuk satu kamera: stream NVR (push.cgi, boleh dengan parameter tambahan
    /// untuk substream) atau RTSP langsung ke kamera. LiveTile mencoba sumber berurutan sampai ada gambar.</summary>
    public class StreamSource
    {
        public bool Rtsp;                 // false = NVR (push.cgi)
        public string NvrExtra = "";      // parameter tambahan push.cgi, mis. "STREAM=2"
        public string Url = "";           // URL RTSP lengkap
        public string Label = "";         // tampil di judul tile, mis. "RTSP sub"

        /// <summary>URL untuk log: password disamarkan.</summary>
        public static string MaskUrl(string url)
        {
            return System.Text.RegularExpressions.Regex.Replace(url ?? "", @"(://[^:/@]+):[^@]*@", "$1:***@");
        }
    }

    /// <summary>Satu kotak kamera. H.264/H.265 (dari NVR atau RTSP kamera) didekode oleh ffmpeg.exe
    /// (lihat FfmpegPipe) lalu ditampilkan.</summary>
    public class LiveTile : Panel
    {
        public int Cam { get; private set; }
        public byte[] LastJpeg;
        /// <summary>Pengolah frame sebelum tampil (mis. dewarp fisheye); null/mengembalikan null = tampil apa adanya.</summary>
        public Func<Image, Image> FrameFilter;
        public event Action<LiveTile, PointF> ImageClicked;
        public event Action<LiveTile> Selected;
        public event Action<LiveTile> DoubleClicked;

        /// <summary>Pasang menu klik kanan pada gambar dan judul tile.</summary>
        public void SetMenu(ContextMenuStrip m)
        {
            _pic.ContextMenuStrip = m;
            _lbl.ContextMenuStrip = m;
            _hdr.ContextMenuStrip = m;
        }

        readonly NvrSession _sess;
        readonly LiveFormat _fmt;
        readonly int _decodeWidth;
        readonly PictureBox _pic;
        readonly Label _lbl;
        readonly Panel _hdr;

        /// <summary>Teks status terbaru (mis. "CAM 3 - live (RTSP sub)") dan dipicu saat berubah; dipakai tampilan ASM300.</summary>
        public event Action<LiveTile, string> StatusChanged;

        /// <summary>Sembunyikan judul bawaan tile (pemilik tile menampilkan info sendiri).</summary>
        public void HideHeader()
        {
            _hdr.Visible = false;
        }

        public PictureBox Picture { get { return _pic; } }

        /// <summary>Gambar terakhir yang sedang tampil (salinan tidak dibuat).</summary>
        public Image CurrentImage { get { return _pic.Image; } }

        public bool HasPicture { get { return _frames > 0; } }
        HttpWebRequest _req;
        FfmpegPipe _ff;
        Thread _thread;
        volatile bool _stop;
        volatile bool _busy;
        volatile string _comp;   // format yang terakhir di-START (untuk STOP); null bila sumber RTSP
        volatile string _srcLabel;
        volatile bool _forceSoftware;   // true setelah akselerasi GPU gagal: dekode CPU
        volatile bool _hwError;
        int _lastTick = Environment.TickCount;
        System.Threading.Timer _watch;
        int _failStreak;
        volatile bool _connFail;
        int _attemptGen;
        readonly Random _rnd = new Random();

        /// <summary>Daftar sumber yang dicoba berurutan. Kosong = stream utama NVR.</summary>
        public List<StreamSource> Sources;
        bool _warnedNoVideo;
        int _frames;

        public LiveTile(int cam, NvrSession sess, LiveFormat fmt, int decodeWidth)
        {
            Cam = cam;
            _sess = sess;
            _fmt = fmt;
            _decodeWidth = decodeWidth;
            Padding = new Padding(2);
            BackColor = Theme.Light.Tile;

            _lbl = new Label();
            _lbl.Text = "CAM " + cam + " - menghubungkan...";
            _lbl.Dock = DockStyle.Fill;
            _lbl.ForeColor = Color.White;
            _lbl.BackColor = Color.FromArgb(34, 37, 45);
            _hdr = new Panel();
            _hdr.Dock = DockStyle.Top;
            _hdr.Height = 18;
            _hdr.Controls.Add(_lbl);

            _pic = new PictureBox();
            _pic.Dock = DockStyle.Fill;
            _pic.SizeMode = PictureBoxSizeMode.Zoom;
            _pic.BackColor = Color.Black;
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(_pic, true, null);
            }
            catch { }
            _pic.MouseClick += OnPicClick;
            _pic.MouseDoubleClick += delegate { if (DoubleClicked != null) DoubleClicked(this); };

            Controls.Add(_pic);
            Controls.Add(_hdr);
        }

        public void SetSelected(bool sel)
        {
            BackColor = sel ? Theme.Light.Accent : Theme.Light.Tile;
        }

        void OnPicClick(object sender, MouseEventArgs e)
        {
            if (Selected != null) Selected(this);
            Image img = _pic.Image;
            if (img == null || ImageClicked == null) return;
            double scale = Math.Min((double)_pic.Width / img.Width, (double)_pic.Height / img.Height);
            double w = img.Width * scale;
            double h = img.Height * scale;
            double ox = (_pic.Width - w) / 2.0;
            double oy = (_pic.Height - h) / 2.0;
            double nx = (e.X - ox) / w;
            double ny = (e.Y - oy) / h;
            if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return;
            ImageClicked(this, new PointF((float)nx, (float)ny));
        }

        /// <summary>delayMs: jeda sebelum START supaya NVR tidak dibanjiri banyak permintaan sekaligus.</summary>
        static int _active;
        bool _counted;
        /// <summary>Jumlah stream live yang sedang berjalan (untuk indikator beban).</summary>
        public static int ActiveStreams { get { return _active; } }

        public void Start(int delayMs)
        {
            _stop = false;
            if (!_counted) { _counted = true; Interlocked.Increment(ref _active); }
            _lastTick = Environment.TickCount;
            _watch = new System.Threading.Timer(delegate { WatchTick(); }, null, 5000, 5000);
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Start(delayMs);
        }

        /// <summary>Teks judul kustom (mis. "NVR-1 - CAM 3 - Lobby") pengganti awalan "CAM n" pada judul tile.</summary>
        public string Caption;

        string Titled(string t)
        {
            string head = "CAM " + Cam;
            if (!string.IsNullOrEmpty(Caption) && t.StartsWith(head, StringComparison.Ordinal)) return Caption + t.Substring(head.Length);
            return t;
        }

        /// <summary>Video memenuhi seluruh kotak (ditarik, tanpa pinggiran hitam dan tanpa bingkai) - dipakai Multiview.</summary>
        public void FillCell()
        {
            _pic.SizeMode = PictureBoxSizeMode.StretchImage;
            Padding = new Padding(0);
        }

        void SetTitle(string t)
        {
            t = Titled(t);
            Action<LiveTile, string> h = StatusChanged;
            if (h != null) h(this, t);
            Ui.Post(_lbl, () => { _lbl.Text = t; });
        }

        static string Hex(byte[] d)
        {
            int n = Math.Min(16, d.Length);
            return n == 0 ? "(kosong)" : BitConverter.ToString(d, 0, n);
        }

        static string Describe(Exception ex)
        {
            var we = ex as WebException;
            if (we != null && we.Response != null)
            {
                try
                {
                    var hr = we.Response as HttpWebResponse;
                    string body = "";
                    using (Stream s = we.Response.GetResponseStream())
                    {
                        var buf = new byte[300];
                        int n = s.Read(buf, 0, buf.Length);
                        body = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)).Replace("\r", " ").Replace("\n", " ");
                    }
                    return ex.Message + (hr != null ? " [HTTP " + (int)hr.StatusCode + "]" : "") + " " + body;
                }
                catch { }
            }
            return ex.Message;
        }

        /// <summary>Jaga 24x7: bila stream putus/gagal, sambung ulang terus dengan jeda bertahap (3 dtk ... 60 dtk).</summary>
        void Run(object delayObj)
        {
            int delay = (int)delayObj;
            if (delay > 0) Thread.Sleep(delay);
            int backoff = 0;
            while (!_stop)
            {
                var sw = Stopwatch.StartNew();
                bool good = false;
                try { good = RunOnce(); }
                catch (Exception ex) { if (!_stop) Log.Write("CAM " + Cam + ": " + Describe(ex)); }
                if (_stop) return;
                if (good && sw.Elapsed.TotalSeconds > 30) backoff = 0;
                backoff = backoff == 0 ? 4 : Math.Min(60, backoff * 2);
                bool conn = _connFail; _connFail = false;
                if (good) _failStreak = 0;
                else if (conn)
                {
                    // hanya kegagalan koneksi/HTTP (NVR restart, UID kedaluwarsa) yang dianggap masalah sesi; kamera mati / stream kosong
                    // TIDAK boleh membuang UID karena UID dipakai bersama sampai 16 kamera lain
                    _failStreak++;
                    // gagal berulang: UID mungkin kedaluwarsa / NVR restart -> login ulang
                    if (_failStreak >= 3 && _sess != null)
                    {
                        _failStreak = 0;
                        try { _sess.Invalidate(_attemptGen); } catch { }
                    }
                }
                if (_sess != null && _comp != null)
                {
                    try { SendStop(); } catch { }
                }
                int wait = backoff * 1000 + _rnd.Next(0, 2000);
                SetTitle("CAM " + Cam + " - terputus, sambung ulang " + (wait / 1000) + " dtk");
                for (int t = 0; t < wait && !_stop; t += 200) Thread.Sleep(200);
            }
        }

        void WatchTick()
        {
            if (_stop) return;
            // ada gambar sebelumnya tetapi berhenti > 30 dtk (ffmpeg macet / koneksi setengah mati): putuskan paksa agar tersambung ulang
            if (_frames > 0 && unchecked(Environment.TickCount - _lastTick) > 30000)
            {
                _lastTick = Environment.TickCount;
                Log.Write("CAM " + Cam + ": tidak ada gambar > 30 dtk, memutus untuk sambung ulang.");
                try { if (_req != null) _req.Abort(); } catch { }
                FfmpegPipe ff = _ff;
                if (ff != null) { try { ff.Dispose(); } catch { } }
            }
        }

        bool RunOnce()
        {
            bool good = false;
            try
            {
                if (_stop) return false;

                List<StreamSource> list = Sources;
                if (list == null || list.Count == 0)
                {
                    list = new List<StreamSource>();
                    list.Add(new StreamSource());
                }
                for (int i = 0; i < list.Count; i++)
                {
                    if (_stop) return false;
                    StreamSource src = list[i];
                    _srcLabel = string.IsNullOrEmpty(src.Label) ? null : src.Label;
                    _frames = 0;
                    if (src.Rtsp)
                    {
                        RunRtsp(src);
                    }
                    else
                    {
                        if (_sess == null)
                        {
                            Log.Write("CAM " + Cam + ": tidak ada sesi NVR untuk sumber '" + src.Label + "'.");
                            continue;
                        }
                        _sess.EnsureLiveSetup(); // hdrctl.cgi sekali per UID (bukan per kamera)
                        _attemptGen = _sess.Generation;
                        if (_stop) return false;
                        RunStream(_fmt == LiveFormat.H265 ? "H265" : "H264", src.NvrExtra);
                    }
                    if (_stop) return false;
                    if (_frames >= 3) good = true;
                    if (_frames == 0 && _hwError && !_forceSoftware)
                    {
                        // akselerasi GPU bermasalah (driver/perangkat): ulangi sumber yang sama dengan dekode CPU
                        _forceSoftware = true;
                        _hwError = false;
                        Log.Write("CAM " + Cam + ": akselerasi GPU gagal, mengulang dengan dekode CPU.");
                        if (!src.Rtsp)
                        {
                            SendStop();
                            Thread.Sleep(3200);
                        }
                        i--;
                        continue;
                    }
                    if (_frames >= 3) break; // sempat tampil lalu putus: jangan ganti sumber, tampilkan "terputus"
                    if (i + 1 < list.Count)
                    {
                        Log.Write("CAM " + Cam + ": sumber '" + src.Label + "' tidak menghasilkan gambar, mencoba berikutnya: '" + list[i + 1].Label + "'.");
                        if (!src.Rtsp)
                        {
                            SendStop();
                            Thread.Sleep(3200); // spec: jarak antar perintah >= 3 dtk
                        }
                    }
                }

                if (!_stop) SetTitle("CAM " + Cam + " - terputus");
            }
            catch (Exception ex)
            {
                if (!_stop)
                {
                    Log.Write("CAM " + Cam + ": " + Describe(ex));
                    SetTitle("CAM " + Cam + " - error");
                }
            }
            return good;
        }

        /// <summary>RTSP langsung ke kamera: ffmpeg membuka URL, mendekode, dan menghasilkan gambar.
        /// Berakhir bila ffmpeg berhenti, tidak ada gambar dalam 15 detik, atau tile dihentikan.</summary>
        void RunRtsp(StreamSource src)
        {
            var sw = Stopwatch.StartNew();
            FfmpegPipe ff = null;
            _comp = null; // tidak ada STOP ke NVR untuk sumber RTSP
            try
            {
                string exe = FfmpegPipe.FindExe();
                if (exe == null)
                {
                    Log.Write("CAM " + Cam + ": ffmpeg.exe tidak ditemukan. Letakkan ffmpeg.exe di folder aplikasi, tambahkan ke PATH, atau isi path-nya di tab Koneksi.");
                    SetTitle("CAM " + Cam + " - butuh ffmpeg.exe");
                    return;
                }
                Log.Write("CAM " + Cam + ": membuka " + src.Label + " " + StreamSource.MaskUrl(src.Url));
                SetTitle("CAM " + Cam + " - menghubungkan RTSP...");
                ff = FfmpegPipe.ForRtsp(exe, src.Url, _decodeWidth, "CAM " + Cam, ShowJpeg, !_forceSoftware);
                _ff = ff;
                while (!_stop && !ff.HasExited)
                {
                    Thread.Sleep(200);
                    if (_frames == 0 && sw.ElapsedMilliseconds > 15000)
                    {
                        Log.Write("CAM " + Cam + ": RTSP tidak menghasilkan gambar dalam 15 dtk.");
                        break;
                    }
                }
                if (!_stop)
                    Log.Write("CAM " + Cam + ": RTSP berakhir setelah " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " dtk, " + _frames + " gambar.");
            }
            catch (Exception ex)
            {
                if (!_stop) Log.Write("CAM " + Cam + ": RTSP gagal: " + ex.Message);
            }
            finally
            {
                if (ff != null)
                {
                    if (ff.HwError) _hwError = true;
                    ff.Dispose();
                    _ff = null;
                }
            }
        }

        /// <summary>Satu sesi push.cgi START (H264 / H265) sampai stream berakhir; extra = parameter tambahan (substream).
        /// Tidak melempar exception; hasilnya tercermin di _frames dan log.</summary>
        void RunStream(string comp, string extra)
        {
            var sw = Stopwatch.StartNew();
            int parts = 0;
            _frames = 0;
            _comp = comp;
            FfmpegPipe ff = null;
            try
            {
                string exe = FfmpegPipe.FindExe();
                if (exe == null)
                {
                    Log.Write("CAM " + Cam + ": ffmpeg.exe tidak ditemukan. Letakkan ffmpeg.exe di folder aplikasi, tambahkan ke PATH, atau isi path-nya di tab Koneksi.");
                    SetTitle("CAM " + Cam + " - butuh ffmpeg.exe");
                    return;
                }
                ff = new FfmpegPipe(exe, comp == "H265", _decodeWidth, "CAM " + Cam, ShowJpeg, !_forceSoftware);
                _ff = ff;
                var dep = new RtpDepacketizer(comp == "H265", ff.Input);
                if (_stop) return;

                string query = "CAM=" + Cam + "&CMD=START&COMP=" + comp + "&INTERNETMODE=ON" + (string.IsNullOrEmpty(extra) ? "" : "&" + extra);
                _req = _sess.Request("push.cgi", query, 20000);
                _req.ReadWriteTimeout = 25000; // stream mati tanpa data > 25 dtk: putus lalu sambung ulang
                using (HttpWebResponse resp = (HttpWebResponse)_req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    Log.Write("CAM " + Cam + ": " + comp + (string.IsNullOrEmpty(extra) ? "" : " [" + extra + "]") + " HTTP " + (int)resp.StatusCode + ", Content-Type: " + resp.ContentType);
                    SetTitle("CAM " + Cam + " - menunggu " + comp + "...");
                    var mr = new MultipartReader(st);
                    MultipartPart part;
                    while (!_stop && (part = mr.ReadPart()) != null)
                    {
                        parts++;
                        if (parts == 1)
                            Log.Write("CAM " + Cam + ": bagian pertama " + part.H("Content-Type") + ", " + part.Body.Length + " byte, awal: " + Hex(part.Body));
                        dep.Feed(part.Body, part.Body.Length);
                        if (RtpDepacketizer.Marker(part.Body)) ff.Input.Flush();
                    }
                }

                if (!_stop)
                {
                    string hint = parts == 0
                        ? " NVR menutup stream tanpa mengirim data: cek nomor kamera (harus sesuai nomor di NVR), batas jumlah stream NVR, atau format (H.264/H.265) tidak sesuai kamera."
                        : "";
                    Log.Write("CAM " + Cam + ": stream " + comp + " berakhir setelah " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " dtk, " + parts + " bagian, " + _frames + " gambar." + hint);
                }
            }
            catch (Exception ex)
            {
                if (ex is WebException) _connFail = true;
                if (!_stop)
                {
                    Log.Write("CAM " + Cam + ": " + comp + " gagal: " + Describe(ex) + " (setelah " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " dtk, " + parts + " bagian)");
                    SetTitle("CAM " + Cam + " - error");
                }
            }
            finally
            {
                if (ff != null)
                {
                    if (ff.HwError) _hwError = true;
                    ff.Dispose();
                    _ff = null;
                }
            }
        }

        int _shown, _fpsShown;
        int _lastShowTick, _fpsTick = Environment.TickCount;
        string _fpsText = "";

        /// <summary>Batas fps tampilan: makin banyak stream aktif, makin rendah, supaya UI thread tidak jenuh (penyebab gambar patah-patah).</summary>
        static int DisplayGapMs()
        {
            int n = ActiveStreams;
            if (n > 40) return 120;   // ~8 fps
            if (n > 16) return 80;    // ~12 fps
            if (n > 6) return 50;     // ~20 fps
            return 0;
        }

        void ShowJpeg(byte[] jpeg)
        {
            _lastTick = Environment.TickCount;
            if (_busy)
            {
                Interlocked.Increment(ref _frames);
                return;
            }
            int gap = DisplayGapMs();
            if (gap > 0 && unchecked(Environment.TickCount - _lastShowTick) < gap)
            {
                Interlocked.Increment(ref _frames);
                return;
            }
            Image img;
            try { img = Ui.DecodeImage(jpeg); }
            catch (Exception ex)
            {
                if (!_warnedNoVideo)
                {
                    _warnedNoVideo = true;
                    Log.Write("CAM " + Cam + ": gagal decode JPEG (" + jpeg.Length + " byte): " + ex.Message);
                }
                return;
            }
            Interlocked.Increment(ref _frames);
            LastJpeg = jpeg;
            Func<Image, Image> flt = FrameFilter;
            if (flt != null)
            {
                try
                {
                    Image fx = flt(img);
                    if (fx != null) { img.Dispose(); img = fx; }
                }
                catch (Exception ex) { Log.Write("CAM " + Cam + " filter frame: " + ex.Message); }
            }
            _busy = true;
            _lastShowTick = Environment.TickCount;
            string comp = _srcLabel ?? _comp;
            Ui.Post(_pic, () =>
            {
                try
                {
                    Image old = _pic.Image;
                    _pic.Image = img;
                    if (old != null) old.Dispose();
                    _shown++;
                    int el = unchecked(Environment.TickCount - _fpsTick);
                    if (el >= 3000)
                    {
                        _fpsText = " " + (_shown * 1000 / el) + " fps";
                        _shown = 0; _fpsTick = Environment.TickCount;
                    }
                    string live = Titled("CAM " + Cam + " - live (" + comp + _fpsText + ")");
                    _lbl.Text = live;
                    Action<LiveTile, string> h2 = StatusChanged;
                    if (h2 != null) h2(this, live);
                }
                finally { _busy = false; }
            });
            if (!_pic.IsHandleCreated) _busy = false;
        }

        /// <summary>Hentikan pembacaan lokal (cepat, aman dipanggil dari UI thread).</summary>
        public void Halt()
        {
            _stop = true;
            if (_counted) { _counted = false; Interlocked.Decrement(ref _active); }
            try { if (_watch != null) { _watch.Dispose(); _watch = null; } } catch { }
            try { if (_req != null) _req.Abort(); } catch { }
            FfmpegPipe ff = _ff;
            if (ff != null)
            {
                try { ff.Dispose(); } catch { }
            }
        }

        /// <summary>Kirim push.cgi CMD=STOP ke NVR secara sinkron (panggil dari thread latar); tidak berefek untuk sumber RTSP.</summary>
        public void SendStop()
        {
            string comp = _comp;
            if (comp == null || _sess == null || !_sess.IsLoggedIn) return;
            try { _sess.Cmd("push.cgi", "CAM=" + Cam + "&CMD=STOP&COMP=" + comp); }
            catch { }
        }

        public void Stop()
        {
            Halt();
            Task.Run(() => SendStop());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _pic != null && _pic.Image != null) _pic.Image.Dispose();
            base.Dispose(disposing);
        }
    }
}
