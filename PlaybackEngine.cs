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
            IntervalMs = Math.Max(300, AppSettings.Current.PlaybackIntervalMs);
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

        void Fetch()
        {
            List<int> list;
            lock (channels) list = new List<int>(channels);
            foreach (int ch in list)
            {
                if (disposed || mode == "STOP") return;
                byte[] data;
                try { data = sess.CmdBytes("camera.cgi", "CAM=" + ch, 30000); }
                catch (Exception ex) { Log.Write("Playback CAM " + ch + ": " + ex.Message); ChStatus(ch, "Gagal ambil data dari NVR: " + ex.Message); continue; }
                ParsedFrame f;
                try { f = FrameParser.Parse(data); }
                catch (Exception ex) { Log.Write("Playback CAM " + ch + " parse: " + ex.Message); ChStatus(ch, "Data playback tidak bisa dibaca: " + ex.Message); continue; }
                if (f.UtcTime.HasValue) curUtc = f.UtcTime;
                else if (f.GopStartUtc.HasValue) curUtc = f.GopStartUtc;
                if (f.Video.Count == 0)
                {
                    string why = data.Length < 64
                        ? "Tidak ada rekaman pada waktu ini (respons NVR " + data.Length + " byte)"
                        : "Data playback tidak dikenali (" + data.Length + " byte, awal: " + Hex(data, 12) + ")";
                    Status(why + " CAM " + ch);
                    ChStatus(ch, why);
                    Log.Write("Playback CAM " + ch + ": " + why);
                    continue;
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

        readonly HashSet<int> decoding = new HashSet<int>();

        /// <summary>GOP H.264/H.265 -> JPEG lewat ffmpeg sekali pakai per GOP (semua gambar keluar), lalu ditampilkan berurutan.</summary>
        void Decode(int ch, ParsedFrame f)
        {
            string exe = FfmpegPipe.FindExe();
            if (exe == null)
            {
                Status("Playback H.264/H.265 butuh ffmpeg.exe (letakkan di folder aplikasi atau isi path di Setup > Device > Koneksi)");
                ChStatus(ch, "Butuh ffmpeg.exe untuk playback H.264/H.265");
                return;
            }
            lock (decoding) { if (decoding.Contains(ch)) return; decoding.Add(ch); }
            bool h265 = f.Codec == "H265";
            byte[] data;
            using (var ms = new MemoryStream())
            {
                foreach (byte[] v in f.Video) ms.Write(v, 0, v.Length);
                data = ms.ToArray();
            }
            int width = DecodeWidth;
            int gap = Mode == "PAUSE" ? 0 : 70;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string err;
                    List<byte[]> jpgs = FfmpegPipe.DecodeOnce(exe, h265, width, data, out err);
                    if (jpgs.Count == 0)
                    {
                        Status("Decode CAM " + ch + " gagal: " + err);
                    ChStatus(ch, "Decode gagal: " + err);
                        Log.Write("Decode playback CAM " + ch + ": " + err);
                        return;
                    }
                    Action<int, byte[]> h = Frame;
                    if (h == null) return;
                    // diam (pause/step): tampilkan gambar terakhir saja; jalan: putar semua gambar GOP berurutan
                    if (gap == 0) { h(ch, jpgs[jpgs.Count - 1]); return; }
                    int step = Math.Max(1, jpgs.Count * gap / Math.Max(500, IntervalMs - 500));
                    for (int i = 0; i < jpgs.Count && !disposed && mode != "STOP"; i += step)
                    {
                        h(ch, jpgs[i]);
                        Thread.Sleep(gap);
                    }
                }
                catch (Exception ex) { Log.Write("Decode playback CAM " + ch + ": " + ex.Message); }
                finally { lock (decoding) decoding.Remove(ch); }
            });
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            mode = "STOP";
            if (timer != null) { timer.Dispose(); timer = null; }
            worker.Close();
            string mk = maskApplied;
            Task.Run(delegate
            {
                try
                {
                    if (sess.IsLoggedIn && mk.Length > 0)
                    {
                        try { sess.Cmd("hdrctl.cgi", "PC=AS60&PLAYCMD=STOP&2ND=OFF&TIMEF=" + Timef.Format(DateTime.UtcNow)); } catch { }
                    }
                    sess.Logout();
                }
                catch { }
            });
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
