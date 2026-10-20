using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Playback satu NVR (satu UID sendiri) untuk beberapa kamera sekaligus: hdrctl.cgi (MASK/PLAYCMD) + camera.cgi.
    /// Frame JPEG ditampilkan langsung; GOP H.264/H.265 didekode ffmpeg menjadi JPEG. Dipakai Operation window (tab HDD),
    /// Quick playback, dan Thumbnail search.</summary>
    public class PlaybackEngine : IDisposable
    {
        public readonly NvrRecord Nvr;
        readonly NvrSession sess;
        readonly SerialWorker worker = new SerialWorker(0);
        System.Threading.Timer timer;
        volatile string mode = "STOP";   // STOP, PLAY, BPLAY, FF, REW, PAUSE
        readonly List<int> channels = new List<int>();
        string maskApplied = "";
        readonly object curLock = new object();
        DateTime? _curUtc;
        /// <summary>Posisi waktu terakhir (UTC). Dibaca UI, ditulis thread pekerja: DateTime? tidak atomik, jadi dikunci.</summary>
        DateTime? curUtc
        {
            get { lock (curLock) return _curUtc; }
            set { lock (curLock) _curUtc = value; }
        }
        bool disposed;
        int ffStep = 1;
        DateTime? reqUtc;

        /// <summary>kanal, JPEG (dari thread latar).</summary>
        public event Action<int, byte[]> Frame;
        public event Action<string> StatusChanged;
        /// <summary>Pesan per kanal (menunggu data, gagal, tidak ada rekaman, dsb.) agar area gambar tidak hanya hitam tanpa penjelasan.</summary>
        public event Action<int, string> ChannelStatus;

        void ChStatus(int ch, string msg)
        {
            Action<int, string> h = ChannelStatus;
            if (h != null) { try { h(ch, msg); } catch { } }
        }

        static string Hex(byte[] d, int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(n, d.Length); i++) sb.Append(d[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
        public int DecodeWidth = 0;
        public int IntervalMs = 3000;

        public PlaybackEngine(NvrRecord nvr)
        {
            Nvr = nvr;
            sess = new NvrSession("playback " + nvr.Name, nvr.ToEndpoint());
            IntervalMs = Math.Max(300, AppSettings.Current.PlaybackFetchMs);
        }

        public string Mode { get { return mode; } }
        public DateTime? CurUtc { get { return curUtc; } }
        public int FfStep { get { return ffStep; } }
        public bool Active { get { return mode != "STOP"; } }

        void Status(string s)
        {
            Action<string> h = StatusChanged;
            if (h != null) h(s);
        }

        public void SetChannels(IEnumerable<int> chans)
        {
            lock (channels)
            {
                channels.Clear();
                foreach (int c in chans) if (c >= 1 && !channels.Contains(c)) channels.Add(c);
            }
        }

        string Mask()
        {
            int max = Math.Max(1, Nvr.MaxChannels);
            lock (channels)
            {
                foreach (int c in channels) if (c >= 1) max = Math.Max(max, c);
                var m = new char[max];
                for (int i = 0; i < max; i++) m[i] = '0';
                foreach (int c in channels) if (c >= 1) m[c - 1] = '1';
                return new string(m);
            }
        }

        void Prepare()
        {
            sess.EnsureLogin();
            string mask = Mask();
            if (mask != maskApplied)
            {
                sess.Cmd("hdrctl.cgi", "PC=AS60&MASK=" + mask);
                maskApplied = mask;
            }
        }

        string TimeStr(DateTime fallbackUtc)
        {
            DateTime? c0 = curUtc;
            return Timef.Format(c0.HasValue ? c0.Value : fallbackUtc);
        }

        void StartTimer()
        {
            if (timer != null) timer.Dispose();
            timer = new System.Threading.Timer(delegate { Tick(); }, null, IntervalMs, IntervalMs);
        }

        void Tick()
        {
            string m = mode;
            if (disposed) return;
            lock (channels) { if (channels.Count == 0) return; }   // semua kamera di-pause
            if (m == "PLAY" || m == "BPLAY" || m == "FF" || m == "REW")
            {
                if (worker.Pending > 1) return;
                worker.Post(delegate { Fetch(); });
            }
        }

        // ---------- perintah ----------

        public void GotoUtc(DateTime utc)
        {
            mode = "PLAY";
            ffStep = 1;
            DateTime t = utc;
            reqUtc = utc;
            lock (channels) foreach (int c0 in channels) ChStatus(c0, "Menunggu data playback " + Asm.FormatTime(t.ToLocalTime()) + "...");
            worker.Post(delegate
            {
                try { Prepare(); sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=GOTODATE&2ND=OFF&TIMEF=" + Timef.Format(t) + "&SDBACKUP=0&NOTIME=ON"); }
                catch (Exception ex)
                {
                    Log.Write("Playback " + Nvr.Name + ": " + ex.Message);
                    lock (channels) foreach (int c1 in channels) ChStatus(c1, "Gagal perintah ke NVR: " + ex.Message);
                    return;
                }
                curUtc = null;
                Status("PLAY " + Asm.FormatTime(t.ToLocalTime()));
                Fetch();
            });
            StartTimer();
        }

        public void Play()
        {
            if (!curUtc.HasValue) return;
            mode = "PLAY";
            ffStep = 1;
            worker.Post(delegate
            {
                Prepare();
                sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=PLAY&2ND=OFF&TIMEF=AS60_UNKNOWN&NOTIME=ON");
                Fetch();
            });
            StartTimer();
        }

        void Simple(string cmd, string newMode, bool smooth)
        {
            DateTime? c0 = curUtc;
            if (!c0.HasValue) return;
            DateTime fb = c0.Value;
            mode = newMode;
            worker.Post(delegate
            {
                Prepare();
                sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=" + cmd + "&2ND=OFF&TIMEF=" + TimeStr(fb) + (smooth ? "&SMOOTH=OFF" : "") + "&NOTIME=ON");
                Fetch();
            });
            StartTimer();
        }

        public void Reverse() { ffStep = 1; Simple("BPLAY", "BPLAY", false); }
        public void FastForward() { ffStep = Math.Min(7, ffStep + 1); Simple("FF", "FF", true); }
        public void FastReverse() { ffStep = Math.Min(7, ffStep + 1); Simple("REW", "REW", true); }

        public void Pause()
        {
            DateTime? c0 = curUtc;
            if (!c0.HasValue) return;
            DateTime fb = c0.Value;
            mode = "PAUSE";
            worker.Post(delegate
            {
                sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=PAUSE&2ND=OFF&TIMEF=" + TimeStr(fb) + "&SMOOTH=OFF&NOTIME=ON");
                Status("PAUSE");
            });
        }

        public void Step(bool next)
        {
            DateTime? c0 = curUtc;
            if (!c0.HasValue) return;
            DateTime fb = c0.Value;
            mode = "PAUSE";
            string cmd = next ? "NEXTFRAME" : "PREVFRAME";
            worker.Post(delegate
            {
                Prepare();
                sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=" + cmd + "&2ND=OFF&TIMEF=" + TimeStr(fb) + "&NOTIME=ON");
                Fetch();
            });
        }

        /// <summary>Lompat maju/mundur sekian detik dari posisi sekarang.</summary>
        public void Skip(int seconds)
        {
            DateTime? c0 = curUtc;
            if (!c0.HasValue) return;
            GotoUtc(c0.Value.AddSeconds(seconds));
        }

        public void Stop()
        {
            mode = "STOP";
            ffStep = 1;
            if (timer != null) { timer.Dispose(); timer = null; }
            DateTime fb = curUtc ?? DateTime.UtcNow;
            string tf = TimeStr(fb);
            worker.Post(delegate
            {
                if (maskApplied.Length > 0)
                {
                    try { sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=STOP&2ND=OFF&TIMEF=" + tf); } catch { }
                }
                maskApplied = "";
                curUtc = null;
                Status("STOP");
            });
        }

        // ---------- ambil frame ----------

        int lastFetchTick;
        double periodMs = 1500;

        void Fetch()
        {
            int nowTick = Environment.TickCount;
            if (lastFetchTick != 0)
            {
                double d = unchecked(nowTick - lastFetchTick);
                if (d > 200 && d < 20000) periodMs = periodMs * 0.6 + Math.Max(500, d) * 0.4; // jeda antar putaran ambil data (untuk menebar gambar GOP)
            }
            lastFetchTick = nowTick;
            List<int> list;
            lock (channels) list = new List<int>(channels);
            if (list.Count == 0) return;
            var po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, list.Count)); // ambil beberapa kamera sekaligus: satu putaran tidak lagi menumpuk 16 unduhan berurutan
            try
            {
                Parallel.ForEach(list, po, delegate(int ch)
                {
                    try { FetchOne(ch); }
                    catch (Exception ex) { Log.Write("Playback CAM " + ch + ": " + ex.Message); }
                });
            }
            catch (AggregateException) { }
        }

        void FetchOne(int ch)
        {
            {
                if (disposed || mode == "STOP") return;
                byte[] data;
                try { data = sess.CmdBytes("camera.cgi", "CAM=" + ch, 30000); }
                catch (Exception ex) { Log.Write("Playback CAM " + ch + ": " + ex.Message); ChStatus(ch, "Gagal ambil data dari NVR: " + ex.Message); return; }
                ParsedFrame f;
                try { f = FrameParser.Parse(data); }
                catch (Exception ex) { Log.Write("Playback CAM " + ch + " parse: " + ex.Message); ChStatus(ch, "Data playback tidak bisa dibaca: " + ex.Message); return; }
                if (f.UtcTime.HasValue) curUtc = f.UtcTime;
                else if (f.GopStartUtc.HasValue) curUtc = f.GopStartUtc;
                else
                {
                    // NVR tidak menyertakan waktu di data: pakai waktu yang diminta, maju sesuai interval saat PLAY
                    DateTime? c1 = curUtc ?? reqUtc;
                    if (c1.HasValue) curUtc = (mode == "PLAY" || mode == "FF") ? c1.Value.AddMilliseconds(IntervalMs) : c1.Value;
                }
                if (f.Video.Count == 0)
                {
                    string why = data.Length < 64
                        ? "Tidak ada rekaman pada waktu ini (respons NVR " + data.Length + " byte)"
                        : "Data playback tidak dikenali (" + data.Length + " byte, awal: " + Hex(data, 32) + ")";
                    if (data.Length >= 64) why += DumpFailure(ch, data);
                    Status(why + " CAM " + ch);
                    ChStatus(ch, why);
                    Log.Write("Playback CAM " + ch + ": " + why);
                    return;
                }
                ChStatus(ch, null);

                if (f.Codec == "JPEG")
                {
                    Action<int, byte[]> h = Frame;
                    if (h != null) h(ch, f.Video[0]);
                }
                else
                {
                    Decode(ch, f);
                }
            }
        }

        DateTime lastDump = DateTime.MinValue;
        /// <summary>Simpan respons NVR yang tidak bisa dibaca ke folder log (playback_gagal_camN.bin) agar susunannya bisa dianalisis.</summary>
        string DumpFailure(int ch, byte[] data)
        {
            try
            {
                if ((DateTime.Now - lastDump).TotalSeconds < 30) return "";
                lastDump = DateTime.Now;
                string path = System.IO.Path.Combine(Log.Directory, "playback_gagal_cam" + ch + ".bin");
                int n = Math.Min(data.Length, 600 * 1024);
                var cut = new byte[n];
                Buffer.BlockCopy(data, 0, cut, 0, n);
                System.IO.File.WriteAllBytes(path, cut);
                var sb = new System.Text.StringBuilder();
                sb.Append("Playback CAM " + ch + " tidak dikenali, " + data.Length + " byte. Awal: " + Hex(data, 160));
                var marks = new List<string>();
                for (int i = 0; i + 3 < data.Length && marks.Count < 24; i++)
                {
                    if (data[i] == 0xFF && (data[i + 1] == 0x05 || data[i + 1] == 0x06 || data[i + 1] == 0x15 || data[i + 1] == 0x16 || data[i + 1] == 0xD8))
                        marks.Add("FF" + data[i + 1].ToString("X2") + "@" + i);
                    else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) marks.Add("SC4@" + i);
                    else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) marks.Add("SC3@" + i);
                }
                sb.Append(" | Penanda: " + string.Join(" ", marks.ToArray()));
                Log.Write(sb.ToString());
                return "  Data disimpan: " + path;
            }
            catch { return ""; }
        }

        class ChanOut
        {
            public readonly Queue<List<byte[]>> Q = new Queue<List<byte[]>>();
            public bool Running;
        }
        readonly Dictionary<int, ChanOut> outs = new Dictionary<int, ChanOut>();
        static readonly SemaphoreSlim decodeSlots = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        void Decode(int ch, ParsedFrame f)
        {
            string exe = FfmpegPipe.FindExe();
            if (exe == null)
            {
                Status("Playback H.264/H.265 butuh ffmpeg.exe (letakkan di folder aplikasi atau isi path di Setup > Device > Koneksi)");
                ChStatus(ch, "Butuh ffmpeg.exe untuk playback H.264/H.265");
                return;
            }
            bool h265 = f.Codec == "H265";
            byte[] data;
            using (var ms = new MemoryStream())
            {
                foreach (byte[] v in f.Video) ms.Write(v, 0, v.Length);
                data = ms.ToArray();
            }
            int width = DecodeWidth;
            bool still = Mode == "PAUSE";
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool got = false;
                try
                {
                    decodeSlots.Wait();
                    got = true;
                    if (disposed) return;
                    string err;
                    List<byte[]> jpgs = FfmpegPipe.DecodeOnce(exe, h265, width, data, out err, AppSettings.Current.HwAccel != "none");
                    if (jpgs.Count == 0)
                    {
                        Status("Decode CAM " + ch + " gagal: " + err);
                        ChStatus(ch, "Decode gagal: " + err);
                        Log.Write("Decode playback CAM " + ch + ": " + err);
                        return;
                    }
                    Action<int, byte[]> h = Frame;
                    if (h == null) return;
                    if (still) { h(ch, jpgs[jpgs.Count - 1]); return; }
                    Enqueue(ch, jpgs);
                }
                catch (Exception ex) { Log.Write("Decode playback CAM " + ch + ": " + ex.Message); }
                finally { if (got) decodeSlots.Release(); }
            });
        }

        /// <summary>Antrian putar per kamera: GOP berikutnya tidak dibuang saat GOP sebelumnya masih diputar (penyebab loncat/patah-patah).</summary>
        void Enqueue(int ch, List<byte[]> jpgs)
        {
            ChanOut co;
            bool start = false;
            lock (outs)
            {
                if (!outs.TryGetValue(ch, out co)) { co = new ChanOut(); outs[ch] = co; }
            }
            lock (co.Q)
            {
                co.Q.Enqueue(jpgs);
                while (co.Q.Count > 3) co.Q.Dequeue(); // tertinggal jauh: lompat ke yang terbaru
                if (!co.Running) { co.Running = true; start = true; }
            }
            if (start) ThreadPool.QueueUserWorkItem(delegate { PlayOut(ch, co); });
        }

        void PlayOut(int ch, ChanOut co)
        {
            try
            {
                while (!disposed && mode != "STOP")
                {
                    List<byte[]> g; int pending;
                    lock (co.Q)
                    {
                        if (co.Q.Count == 0) { co.Running = false; return; }
                        g = co.Q.Dequeue();
                        pending = co.Q.Count;
                    }
                    Action<int, byte[]> h = Frame;
                    if (h == null) continue;
                    // gambar satu GOP ditebar merata selama satu putaran ambil data; bila antrian menumpuk, diputar lebih cepat
                    double span = Math.Max(400, periodMs * 0.95) / (1 + pending);
                    double gap = span / g.Count;
                    int step = 1;
                    if (gap < 40) { step = (int)Math.Ceiling(40.0 / gap); gap = span / Math.Ceiling((double)g.Count / step); }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int shown = 0;
                    for (int i = 0; i < g.Count && !disposed && mode != "STOP"; i += step)
                    {
                        h(ch, g[i]);
                        shown++;
                        double due = shown * gap;
                        double wait = due - sw.Elapsed.TotalMilliseconds;
                        if (wait > 1) Thread.Sleep((int)wait);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Playback putar CAM " + ch + ": " + ex.Message); }
            finally { lock (co.Q) { co.Running = false; } }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            mode = "STOP";
            if (timer != null) { timer.Dispose(); timer = null; }
            // logout dijalankan di thread pekerja setelah antrian habis: kalau di thread lain, perintah yang masih antre
            // (Prepare -> EnsureLogin) bisa login ulang setelah logout dan sesi NVR bocor.
            worker.Post(delegate
            {
                try
                {
                    if (sess.IsLoggedIn && maskApplied.Length > 0)
                    {
                        try { sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=STOP&2ND=OFF&TIMEF=" + Timef.Format(DateTime.UtcNow)); } catch { }
                    }
                    sess.Logout();
                }
                catch { }
            });
            worker.Close();
        }

        // ---------- satu frame untuk thumbnail ----------

        /// <summary>Ambil satu gambar (JPEG) kamera pada waktu tertentu memakai sesi sendiri; null bila tidak ada rekaman. Blokir (panggil dari thread latar).</summary>
        public static byte[] CaptureStill(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            var s = new NvrSession("thumbnail " + nvr.Name, nvr.ToEndpoint());
            byte[] result = null;
            FfmpegPipe ff = null;
            try
            {
                s.EnsureLogin();
                int max = Math.Max(Math.Max(1, nvr.MaxChannels), channel);
                var m = new char[max];
                for (int i = 0; i < max; i++) m[i] = '0';
                m[channel - 1] = '1';
                s.Cmd("hdrctl.cgi", "PC=AS60&MASK=" + new string(m));
                s.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=GOTODATE&2ND=OFF&TIMEF=" + Timef.Format(utc) + "&SDBACKUP=0&NOTIME=ON");
                byte[] data = s.CmdBytes("camera.cgi", "CAM=" + channel, 30000);
                ParsedFrame f = FrameParser.Parse(data);
                if (f.Video.Count == 0) return null;
                if (f.Codec == "JPEG") return f.Video[0];

                string exe = FfmpegPipe.FindExe();
                if (exe == null) return null;
                var got = new ManualResetEvent(false);
                byte[] first = null;
                ff = new FfmpegPipe(exe, f.Codec == "H265", width, "thumb", delegate(byte[] jpg)
                {
                    if (first == null) { first = jpg; got.Set(); }
                }, false);
                foreach (byte[] v in f.Video) ff.Input.Write(v, 0, v.Length);
                ff.Input.Flush();
                got.WaitOne(8000);
                result = first;
                try { s.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=STOP&2ND=OFF&TIMEF=" + Timef.Format(utc)); } catch { }
            }
            catch (Exception ex)
            {
                Log.Write("Thumbnail CAM " + channel + ": " + ex.Message);
            }
            finally
            {
                if (ff != null) { try { ff.Dispose(); } catch { } }
                try { s.Logout(); } catch { }
            }
            return result;
        }
    }
}
