using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace IproNvrTool
{
    /// <summary>Playback untuk merek selain i-PRO (satu proses ffmpeg per kanal, tanpa IP kamera). Hikvision: ffmpeg membuka
    /// RTSP playback di NVR/DVR. Dahua: data diambil lewat HTTP (loadfile.cgi), atau RTSP bila diaktifkan di Setup.
    /// Kecepatan 1x; cepat maju/mundur = lompat 30 detik; putar mundur &amp; per-frame tidak didukung.</summary>
    public class RtspPlaybackEngine : PlaybackEngine
    {
        readonly INvrDriver drv;
        readonly object gate = new object();
        readonly List<int> chans = new List<int>();
        readonly Dictionary<int, PbStream> pipes = new Dictionary<int, PbStream>();
        readonly Dictionary<int, bool> gotFrame = new Dictionary<int, bool>();
        volatile string mode = "STOP";
        DateTime? startUtc;                    // posisi rekaman saat ffmpeg dimulai
        readonly Stopwatch clock = new Stopwatch();
        DateTime? pausedAt;
        bool disposed;
        System.Threading.Timer watch;

        public RtspPlaybackEngine(NvrRecord nvr, INvrDriver driver) : base(nvr) { drv = driver; }

        public override string Mode { get { return mode; } }
        public override bool Active { get { return mode != "STOP"; } }
        public override double Speed { get { return 1.0; } }

        public override DateTime? CurUtc
        {
            get
            {
                lock (gate)
                {
                    if (mode == "PAUSE") return pausedAt;
                    if (!startUtc.HasValue || mode == "STOP") return null;
                    return startUtc.Value.AddSeconds(clock.Elapsed.TotalSeconds);
                }
            }
        }
        public override DateTime? ShownUtc { get { return CurUtc; } }

        public override void SetChannels(IEnumerable<int> cs)
        {
            lock (gate)
            {
                chans.Clear();
                foreach (int c in cs) if (c >= 1 && !chans.Contains(c)) chans.Add(c);
            }
        }

        void KillPipes()
        {
            List<PbStream> l;
            lock (gate) { l = new List<PbStream>(pipes.Values); pipes.Clear(); gotFrame.Clear(); }
            foreach (PbStream p in l) { try { p.Dispose(); } catch { } }
        }

        void Begin(DateTime utc)
        {
            KillPipes();
            string exe = FfmpegPipe.FindExe();
            List<int> cs;
            lock (gate) cs = new List<int>(chans);
            if (exe == null)
            {
                foreach (int c in cs) ChStatus(c, "ffmpeg.exe tidak ditemukan (dibutuhkan untuk playback " + drv.BrandName + ").");
                return;
            }
            var dahua = drv as DahuaDriver;
            bool http = dahua != null && !AppSettings.Current.DahuaPlaybackRtsp;
            DateTime end = utc.AddHours(6);
            if (http)
            {
                // HTTP: rentang dibatasi 1 jam dan tidak melewati waktu sekarang
                end = utc.AddHours(1);
                if (end > DateTime.UtcNow) end = DateTime.UtcNow;
                if ((end - utc).TotalSeconds < 1)
                {
                    foreach (int c in cs) ChStatus(c, "Waktu yang diminta belum terekam.");
                    return;
                }
            }
            lock (gate) { startUtc = utc; clock.Reset(); clock.Start(); pausedAt = null; mode = "PLAY"; }
            int w = 0;
            try { if (DecodeWidthProvider != null) w = DecodeWidthProvider(); } catch { }
            Status("PLAY " + Asm.FormatTime(utc.ToLocalTime()));
            foreach (int ch0 in cs)
            {
                int ch = ch0;
                ChStatus(ch, "Menunggu data playback " + Asm.FormatTime(utc.ToLocalTime()) + "...");
                try
                {
                    Action<byte[]> onJpeg = delegate(byte[] jpg)
                    {
                        bool first;
                        lock (gate)
                        {
                            first = !gotFrame.ContainsKey(ch);
                            // HTTP: perangkat butuh waktu menyiapkan data, jadi jam putar dihitung sejak gambar pertama
                            if (first && http && gotFrame.Count == 0 && mode == "PLAY") clock.Restart();
                            if (first) gotFrame[ch] = true;
                        }
                        if (first) ChStatus(ch, null);
                        RaiseFrame(ch, jpg);
                    };
                    PbStream stream;
                    if (http)
                    {
                        string path = dahua.HttpPlaybackPath(ch, utc, end);
                        Log.Write("Playback " + Nvr.Name + " CAM " + ch + " -> HTTP " + path);
                        HttpWebRequest req = NvrSession.CreateRequest(Nvr.ToEndpoint(), path, 30000);
                        stream = new PbStream(exe, req, w, "PB CAM " + ch, onJpeg, HwAccels.Usable);
                    }
                    else
                    {
                        string url = drv.PlaybackSource(Nvr, ch, utc, end).RtspUrl;
                        Log.Write("Playback " + Nvr.Name + " CAM " + ch + " -> " + FfmpegPipe.HidePassword(url));
                        stream = new PbStream(FfmpegPipe.ForRtsp(exe, url, w, "PB CAM " + ch, onJpeg, HwAccels.Usable));
                    }
                    lock (gate) pipes[ch] = stream;
                }
                catch (Exception ex)
                {
                    Log.Write("Playback " + Nvr.Name + " CAM " + ch + ": " + ex.Message);
                    ChStatus(ch, "Gagal membuka playback: " + ex.Message);
                }
            }
            if (watch != null) watch.Dispose();
            watch = new System.Threading.Timer(delegate { Watch(); }, null, 6000, 3000);
        }

        void Watch()
        {
            if (disposed || mode != "PLAY") return;
            List<KeyValuePair<int, PbStream>> l;
            lock (gate) l = new List<KeyValuePair<int, PbStream>>(pipes);
            foreach (KeyValuePair<int, PbStream> kv in l)
            {
                bool got;
                lock (gate) got = gotFrame.ContainsKey(kv.Key);
                string why = kv.Value.LastError;
                if (why.Length > 160) why = why.Substring(why.Length - 160);   // bagian akhir memuat alasannya
                if (why.Length > 0) why = "\n" + why;
                if (kv.Value.HasExited)
                    ChStatus(kv.Key, got ? "Rekaman berakhir / koneksi terputus." : "Tidak ada rekaman pada waktu ini (atau playback ditolak " + drv.BrandName + ")." + why);
                else if (!got && clock.Elapsed.TotalSeconds > 15)
                    ChStatus(kv.Key, "Belum ada gambar dari " + drv.BrandName + " setelah 15 detik." + why);
            }
        }

        public override void GotoUtc(DateTime utc) { Begin(utc); }

        public override void Play()
        {
            DateTime? at;
            lock (gate) at = (mode == "PAUSE") ? pausedAt : null;
            if (at.HasValue) Begin(at.Value);
        }

        public override void Pause()
        {
            DateTime? now = CurUtc;
            if (!now.HasValue || mode != "PLAY") return;
            lock (gate) { pausedAt = now; mode = "PAUSE"; clock.Stop(); }
            KillPipes();                  // gambar terakhir tetap tampil di area
            Status("PAUSE");
        }

        public override void Skip(int seconds)
        {
            DateTime? c = CurUtc;
            if (c.HasValue) Begin(c.Value.AddSeconds(seconds));
        }

        public override void FastForward() { Skip(30); }
        public override void FastReverse() { Skip(-30); }
        public override void Reverse() { lock (gate) foreach (int c in chans) ChStatus(c, "Putar mundur belum didukung untuk " + drv.BrandName + "."); }
        public override void Step(bool next) { Skip(next ? 1 : -1); }

        public override void Stop()
        {
            lock (gate) { mode = "STOP"; startUtc = null; pausedAt = null; clock.Reset(); }
            if (watch != null) { watch.Dispose(); watch = null; }
            KillPipes();
            Status("STOP");
        }

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            mode = "STOP";
            if (watch != null) { watch.Dispose(); watch = null; }
            KillPipes();
            base.Dispose();
        }
    }

    /// <summary>Aliran playback satu kanal: ffmpeg membuka RTSP sendiri, atau aplikasi mengambil data lewat HTTP
    /// (digest) dan meneruskannya ke ffmpeg.</summary>
    class PbStream : IDisposable
    {
        readonly object gate = new object();
        readonly HttpWebRequest req;           // null = RTSP
        volatile FfmpegPipe pipe;
        volatile bool closed, feeding;
        volatile string httpError;

        public PbStream(FfmpegPipe rtsp) { pipe = rtsp; }

        public PbStream(string exe, HttpWebRequest request, int width, string name, Action<byte[]> onJpeg, bool hw)
        {
            req = request;
            req.ReadWriteTimeout = 120000;
            feeding = true;
            var t = new Thread(delegate() { Feed(exe, width, name, onJpeg, hw); });
            t.IsBackground = true;
            t.Start();
        }

        public bool HasExited
        {
            get
            {
                if (feeding) return false;
                FfmpegPipe p = pipe;
                return p == null || p.HasExited;
            }
        }

        public string LastError
        {
            get
            {
                if (httpError != null) return httpError;
                FfmpegPipe p = pipe;
                return p == null ? "" : p.LastError;
            }
        }

        void Feed(string exe, int width, string name, Action<byte[]> onJpeg, bool hw)
        {
            try
            {
                using (WebResponse resp = req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    var buf = new byte[64 * 1024];
                    int len = 0, n;
                    while (len < 16 && (n = st.Read(buf, len, buf.Length - len)) > 0) len += n;
                    if (len == 0) { httpError = "Perangkat tidak mengirim data."; return; }
                    string head = Encoding.ASCII.GetString(buf, 0, Math.Min(len, 120));
                    bool dav = head.StartsWith("DHAV", StringComparison.Ordinal) || head.StartsWith("DAHUA", StringComparison.Ordinal);
                    if (head.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<", StringComparison.Ordinal))
                    {
                        httpError = "Jawaban perangkat: " + head.Replace("\r", " ").Replace("\n", " ").Trim();
                        Log.Write(name + ": " + httpError);
                        return;
                    }
                    lock (gate)
                    {
                        if (closed) return;
                        pipe = FfmpegPipe.ForPlaybackData(exe, dav ? "dhav" : null, width, name, onJpeg, hw);
                    }
                    Stream input = pipe.Input;
                    do { input.Write(buf, 0, len); input.Flush(); }
                    while (!closed && (len = st.Read(buf, 0, buf.Length)) > 0);
                }
            }
            catch (Exception ex)
            {
                if (closed) return;
                string reason = DahuaDriver.Describe(ex);                          // sekali saja: isi jawaban hanya bisa dibaca sekali
                Log.Write(name + ": " + reason);
                if (pipe != null && ex is IOException) Thread.Sleep(300);          // ffmpeg berhenti: alasannya ada di pesan ffmpeg
                if (pipe == null || pipe.LastError.Length == 0) httpError = reason;
            }
            finally
            {
                FfmpegPipe p = pipe;
                if (p != null && !closed) p.EndInput();
                feeding = false;
            }
        }

        public void Dispose()
        {
            lock (gate) closed = true;
            if (req != null) { try { req.Abort(); } catch { } }
            FfmpegPipe p = pipe;
            if (p != null) p.Dispose();
        }
    }
}
