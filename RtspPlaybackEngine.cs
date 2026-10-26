using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IproNvrTool
{
    /// <summary>Playback untuk merek selain i-PRO (Hikvision, Dahua): ffmpeg membuka RTSP playback di NVR/DVR itu sendiri
    /// (satu proses per kanal, tanpa IP kamera). Kecepatan 1x; cepat maju/mundur = lompat 30 detik; putar mundur &amp; per-frame tidak ada di RTSP.</summary>
    public class RtspPlaybackEngine : PlaybackEngine
    {
        readonly INvrDriver drv;
        readonly object gate = new object();
        readonly List<int> chans = new List<int>();
        readonly Dictionary<int, FfmpegPipe> pipes = new Dictionary<int, FfmpegPipe>();
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
            List<FfmpegPipe> l;
            lock (gate) { l = new List<FfmpegPipe>(pipes.Values); pipes.Clear(); gotFrame.Clear(); }
            foreach (FfmpegPipe p in l) { try { p.Dispose(); } catch { } }
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
                    string url = drv.PlaybackSource(Nvr, ch, utc, utc.AddHours(6)).RtspUrl;
                    FfmpegPipe ff = FfmpegPipe.ForRtsp(exe, url, w, "PB CAM " + ch, delegate(byte[] jpg)
                    {
                        bool first;
                        lock (gate) { first = !gotFrame.ContainsKey(ch); if (first) gotFrame[ch] = true; }
                        if (first) ChStatus(ch, null);
                        RaiseFrame(ch, jpg);
                    }, HwAccels.Usable);
                    lock (gate) pipes[ch] = ff;
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
            List<KeyValuePair<int, FfmpegPipe>> l;
            lock (gate) l = new List<KeyValuePair<int, FfmpegPipe>>(pipes);
            foreach (KeyValuePair<int, FfmpegPipe> kv in l)
            {
                bool got;
                lock (gate) got = gotFrame.ContainsKey(kv.Key);
                if (kv.Value.HasExited)
                    ChStatus(kv.Key, got ? "Rekaman berakhir / koneksi terputus." : "Tidak ada rekaman pada waktu ini (atau playback RTSP ditolak " + drv.BrandName + ").");
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
        public override void Reverse() { lock (gate) foreach (int c in chans) ChStatus(c, "Putar mundur tidak didukung lewat RTSP " + drv.BrandName + "."); }
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
}
