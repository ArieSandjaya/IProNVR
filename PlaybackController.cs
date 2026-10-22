using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Playback di dalam satu ViewGrid (dipakai Live window): satu PlaybackEngine per NVR, gambar masuk ke area kamera
    /// yang sama, jadi tetap di tampilan live (tidak pindah ke Operation window).</summary>
    public class PlaybackController : IDisposable
    {
        readonly ViewGrid grid;
        readonly Control host;
        readonly Dictionary<long, PlaybackEngine> engines = new Dictionary<long, PlaybackEngine>();
        public event Action<string> Info;
        public event Action ModeChanged;

        public PlaybackController(ViewGrid grid, Control host)
        {
            this.grid = grid;
            this.host = host;
            grid.MaximizedChanged += delegate { if (grid.PlaybackMode) SyncEngines(true); };
            grid.CellAssigned += delegate(int idx, long nid, int ch) { if (grid.PlaybackMode) SyncEngines(true); };
            grid.CellUnassigned += delegate(int idx) { if (grid.PlaybackMode) SyncEngines(false); };
        }

        public bool Active { get { return grid.PlaybackMode; } }

        /// <summary>Lebar decode playback: mengikuti lebar area di layar (maks. 2560), satu kamera diperbesar = hampir lebar area penuh. Tidak pernah diperbesar melebihi resolusi rekaman.</summary>
        static int PbWidth(ViewGrid g)
        {
            int w = 0;
            int mx = g.MaximizedIndex;
            foreach (ViewCell c in g.Cells)
            {
                if (mx >= 0 && c.Index != mx) continue;
                w = Math.Max(w, c.Width);
            }
            if (w <= 0) w = 640;
            return Math.Max(320, Math.Min(2560, w));
        }


        void Say(string t) { Action<string> h = Info; if (h != null) h(t); }

        public void Each(Action<PlaybackEngine> a)
        {
            if (!grid.PlaybackMode) return;
            foreach (PlaybackEngine e in new List<PlaybackEngine>(engines.Values)) a(e);
        }

        /// <summary>Waktu rekaman (lokal) pada gambar yang tampil, null bila belum ada.</summary>
        public DateTime? ShownLocal()
        {
            foreach (PlaybackEngine e in engines.Values)
            {
                DateTime? t = e.ShownUtc;
                if (t.HasValue) return t.Value.ToLocalTime();
            }
            return null;
        }

        public string ModeText()
        {
            string mode = "PLAY";
            foreach (PlaybackEngine e in engines.Values)
            {
                if (e.Mode == "PAUSE") mode = "PAUSE";
                else if (e.Mode == "FF") mode = "FF x" + e.FfStep;
                else if (e.Mode == "REW") mode = "REW x" + e.FfStep;
                else if (e.Mode == "BPLAY") mode = "REV";
                else if (e.Speed != 1) mode = "PLAY x" + e.Speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
            return mode;
        }

        void SyncEngines(bool resume)
        {
            var byNvr = new Dictionary<long, List<int>>();
            int mx = grid.MaximizedIndex;
            foreach (ViewCell c in grid.Cells)
            {
                if (!c.HasCamera) continue;
                if (mx >= 0 && c.Index != mx) { if (!byNvr.ContainsKey(c.NvrId)) byNvr[c.NvrId] = new List<int>(); continue; }   // di-pause
                List<int> l;
                if (!byNvr.TryGetValue(c.NvrId, out l)) { l = new List<int>(); byNvr[c.NvrId] = l; }
                l.Add(c.Channel);
            }
            foreach (long id in new List<long>(engines.Keys))
            {
                if (!byNvr.ContainsKey(id)) { engines[id].Dispose(); engines.Remove(id); }
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
                    e.Frame += delegate(int ch, byte[] jpg) { OnFrame(nid, ch, jpg); };
                    e.ChannelStatus += delegate(int ch, string msg)
                    {
                        try
                        {
                            if (host.IsHandleCreated && !host.IsDisposed)
                                host.BeginInvoke((Action)delegate
                                {
                                    foreach (ViewCell c in grid.Cells)
                                        if (c.Playback && c.NvrId == nid && c.Channel == ch) c.SetPlaybackMessage(msg);
                                });
                        }
                        catch { }
                    };
                    e.StatusChanged += delegate(string st)
                    {
                        try { if (host.IsHandleCreated && !host.IsDisposed) host.BeginInvoke((Action)delegate { Say("Playback: " + st); }); } catch { }
                    };
                    e.DecodeWidthProvider = delegate { return PbWidth(grid); };
                    engines[kv.Key] = e;
                    isNew = true;
                }
                e.SetChannels(kv.Value);
                if (resume && !isNew && kv.Value.Count > 0 && e.Active && e.CurUtc.HasValue) e.GotoUtc(e.CurUtc.Value);
            }
        }

        void OnFrame(long nvrId, int ch, byte[] jpg)
        {
            if (!host.IsHandleCreated || host.IsDisposed) return;
            try
            {
                host.BeginInvoke((Action)delegate
                {
                    foreach (ViewCell c in grid.Cells)
                        if (c.Playback && c.NvrId == nvrId && c.Channel == ch) c.ShowFrame(jpg);
                });
            }
            catch { }
        }

        /// <summary>Semua kamera di tampilan ini diputar dari waktu itu (UTC), tetap di area yang sama.</summary>
        public bool PlayAtUtc(DateTime utc)
        {
            if (!AsmApp.Can(Perm.Playback)) { MessageBox.Show("Tidak ada hak playback."); return false; }
            if (!grid.PlaybackMode) grid.SetPlaybackMode(true);
            SyncEngines(false);
            if (engines.Count == 0)
            {
                MessageBox.Show("Belum ada kamera di area gambar.");
                grid.SetPlaybackMode(false);
                return false;
            }
            foreach (PlaybackEngine e in engines.Values) e.GotoUtc(utc);
            Action h = ModeChanged; if (h != null) h();
            return true;
        }

        public void Exit()
        {
            if (!grid.PlaybackMode) return;
            foreach (PlaybackEngine e in engines.Values) e.Dispose();
            engines.Clear();
            grid.SetPlaybackMode(false);
            Action h = ModeChanged; if (h != null) h();
        }

        public void Dispose()
        {
            foreach (PlaybackEngine e in engines.Values) e.Dispose();
            engines.Clear();
        }
    }
}
