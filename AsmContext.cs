using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Cache daftar NVR + kamera dari database; diperbarui otomatis saat data berubah.</summary>
    public static class Dir
    {
        static readonly object lk = new object();
        static List<NvrRecord> nvrs = new List<NvrRecord>();
        static Dictionary<long, List<CameraRecord>> cams = new Dictionary<long, List<CameraRecord>>();
        static readonly Dictionary<string, int> liveStatus = new Dictionary<string, int>();
        static bool inited;

        /// <summary>Dipicu (bisa dari thread latar) setelah daftar NVR/kamera berubah.</summary>
        public static event Action Changed;

        /// <summary>Status koneksi kamera berubah (hasil polling).</summary>
        public static event Action StatusChanged;

        public static void RaiseStatusChanged()
        {
            Action h = StatusChanged;
            if (h != null) { try { h(); } catch { } }
        }

        public static void Init()
        {
            if (inited) return;
            inited = true;
            Db.DataChanged += delegate { Reload(); };
            Reload();
        }

        public static void Reload()
        {
            try
            {
                List<NvrRecord> n = Db.ListNvr();
                var c = new Dictionary<long, List<CameraRecord>>();
                foreach (NvrRecord r in n) c[r.Id] = Db.ListCameras(r.Id);
                lock (lk)
                {
                    nvrs = n;
                    cams = c;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Dir.Reload: " + ex.Message);
            }
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        public static List<NvrRecord> Nvrs
        {
            get { lock (lk) return new List<NvrRecord>(nvrs); }
        }

        public static NvrRecord Nvr(long id)
        {
            lock (lk)
            {
                foreach (NvrRecord n in nvrs) if (n.Id == id) return n;
            }
            return null;
        }

        public static List<CameraRecord> Cameras(long nvrId)
        {
            lock (lk)
            {
                List<CameraRecord> l;
                return cams.TryGetValue(nvrId, out l) ? new List<CameraRecord>(l) : new List<CameraRecord>();
            }
        }

        public static CameraRecord Cam(long nvrId, int channel)
        {
            lock (lk)
            {
                List<CameraRecord> l;
                if (cams.TryGetValue(nvrId, out l))
                {
                    foreach (CameraRecord c in l) if (c.Channel == channel) return c;
                }
            }
            return null;
        }

        /// <summary>Status koneksi terkini (hasil polling EventMonitor bila ada, selain itu dari database).</summary>
        public static int Status(long nvrId, int channel)
        {
            lock (lk)
            {
                int s;
                if (liveStatus.TryGetValue(nvrId + ":" + channel, out s)) return s;
            }
            CameraRecord c = Cam(nvrId, channel);
            return c == null ? 0 : c.RegStatus;
        }

        static readonly Dictionary<long, bool> nvrRecording = new Dictionary<long, bool>();

        /// <summary>Hasil polling HDD: true bila ada HDD NVR yang sedang merekam (tanda (R) di recordedtime.cgi).</summary>
        public static void SetNvrRecording(long nvrId, bool recording)
        {
            lock (lk) nvrRecording[nvrId] = recording;
        }

        /// <summary>Perkiraan kamera sedang direkam: NVR merekam (HDD bertanda R) dan kamera terhubung.
        /// Spec NVR tidak memberi status rekam per kamera, jadi ini turunan dari status HDD dan koneksi kamera.</summary>
        public static bool IsRecording(long nvrId, int channel)
        {
            lock (lk)
            {
                bool r;
                if (!nvrRecording.TryGetValue(nvrId, out r) || !r) return false;
            }
            return Status(nvrId, channel) != 2;
        }

        public static void SetLiveStatus(long nvrId, int channel, int status)
        {
            lock (lk) liveStatus[nvrId + ":" + channel] = status;
        }

        public static string CamName(long nvrId, int channel)
        {
            CameraRecord c = Cam(nvrId, channel);
            return c != null && !string.IsNullOrEmpty(c.Name) ? c.Name : "CAM " + channel;
        }

        /// <summary>Teks judul area: "NVR : nama kamera".</summary>
        public static string Title(long nvrId, int channel)
        {
            NvrRecord n = Nvr(nvrId);
            return (n != null ? n.Name : "NVR?") + " : " + CamName(nvrId, channel);
        }

        /// <summary>Kamera terdaftar (status 1 atau 2) untuk ditampilkan di pohon perangkat.</summary>
        public static List<CameraRecord> Registered(long nvrId)
        {
            var l = new List<CameraRecord>();
            foreach (CameraRecord c in Cameras(nvrId)) if (c.RegStatus != 0) l.Add(c);
            return l;
        }
    }

    /// <summary>Kumpulan sesi (UID) NVR untuk live: maks. 16 stream per UID dan satu kamera tidak boleh dua kali dalam satu UID.</summary>
    public static class Pool
    {
        const int MaxPerUid = 16;

        class Entry
        {
            public NvrSession Sess;
            public HashSet<int> Channels = new HashSet<int>();
            public bool Keep;
            public int Pending;      // STOP ke NVR masih berjalan: sesi jangan di-logout dulu
        }

        static readonly object lk = new object();
        static readonly Dictionary<long, List<Entry>> map = new Dictionary<long, List<Entry>>();

        public static NvrSession Acquire(NvrRecord nvr, int channel)
        {
            lock (lk)
            {
                List<Entry> list;
                if (!map.TryGetValue(nvr.Id, out list))
                {
                    list = new List<Entry>();
                    map[nvr.Id] = list;
                }
                foreach (Entry e in list)
                {
                    if (e.Channels.Count < MaxPerUid && !e.Channels.Contains(channel))
                    {
                        e.Channels.Add(channel);
                        return e.Sess;
                    }
                }
                var ne = new Entry();
                ne.Sess = new NvrSession("operasi " + nvr.Name + " #" + (list.Count + 1), nvr.ToEndpoint());
                ne.Channels.Add(channel);
                list.Add(ne);
                return ne.Sess;
            }
        }

        /// <summary>Kanal dilepas sekarang (boleh langsung dipakai lagi pada sesi yang sama), tetapi sesi tetap hidup sampai EndRelease
        /// supaya perintah STOP ke NVR masih bisa dikirim.</summary>
        public static void BeginRelease(long nvrId, NvrSession s, int channel)
        {
            lock (lk)
            {
                List<Entry> list;
                if (!map.TryGetValue(nvrId, out list)) return;
                foreach (Entry e in list)
                {
                    if (e.Sess != s) continue;
                    e.Channels.Remove(channel);
                    e.Pending++;
                    break;
                }
            }
        }

        public static void EndRelease(long nvrId, NvrSession s)
        {
            NvrSession toLogout = null;
            lock (lk)
            {
                List<Entry> list;
                if (!map.TryGetValue(nvrId, out list)) return;
                foreach (Entry e in list)
                {
                    if (e.Sess != s) continue;
                    if (e.Pending > 0) e.Pending--;
                    if (e.Channels.Count == 0 && e.Pending == 0 && !e.Keep)
                    {
                        list.Remove(e);
                        toLogout = e.Sess;
                    }
                    break;
                }
            }
            if (toLogout != null) Task.Run(delegate { try { toLogout.Logout(); } catch { } });
        }

        public static void Release(long nvrId, NvrSession s, int channel)
        {
            NvrSession toLogout = null;
            lock (lk)
            {
                List<Entry> list;
                if (!map.TryGetValue(nvrId, out list)) return;
                foreach (Entry e in list)
                {
                    if (e.Sess != s) continue;
                    e.Channels.Remove(channel);
                    if (e.Channels.Count == 0 && !e.Keep)
                    {
                        list.Remove(e);
                        toLogout = e.Sess;
                    }
                    break;
                }
            }
            if (toLogout != null) Task.Run(delegate { try { toLogout.Logout(); } catch { } });
        }

        /// <summary>Sesi untuk perintah kontrol kamera (PTZ, preset, AUX) ke NVR ini.</summary>
        public static NvrSession Control(NvrRecord nvr)
        {
            lock (lk)
            {
                List<Entry> list;
                if (!map.TryGetValue(nvr.Id, out list))
                {
                    list = new List<Entry>();
                    map[nvr.Id] = list;
                }
                if (list.Count > 0) return list[0].Sess;
                var ne = new Entry();
                ne.Keep = true;
                ne.Sess = new NvrSession("kontrol " + nvr.Name, nvr.ToEndpoint());
                list.Add(ne);
                return ne.Sess;
            }
        }

        public static void LogoutAll()
        {
            var all = new List<NvrSession>();
            lock (lk)
            {
                foreach (List<Entry> l in map.Values) foreach (Entry e in l) all.Add(e.Sess);
                map.Clear();
            }
            foreach (NvrSession s in all) { try { s.Logout(); } catch { } }
        }
    }

    public class LogEntry
    {
        public DateTime Time = DateTime.Now;
        public string Source = "";
        public string Text = "";
        public long NvrId;
        public int Channel;
    }

    /// <summary>Alarm log / Device error log / Network error log + notifikasi.</summary>
    public static class Events
    {
        public const string Alarm = "alarm";
        public const string Device = "device";
        public const string Net = "net";
        const int Max = 1000;

        static readonly object lk = new object();
        static readonly Dictionary<string, List<LogEntry>> logs = new Dictionary<string, List<LogEntry>>();

        /// <summary>kind, entri; dipicu dari thread mana saja.</summary>
        public static event Action<string, LogEntry> Added;

        public static List<LogEntry> Get(string kind)
        {
            lock (lk)
            {
                List<LogEntry> l;
                return logs.TryGetValue(kind, out l) ? new List<LogEntry>(l) : new List<LogEntry>();
            }
        }

        public static void Add(string kind, LogEntry e, bool notify)
        {
            lock (lk)
            {
                List<LogEntry> l;
                if (!logs.TryGetValue(kind, out l))
                {
                    l = new List<LogEntry>();
                    logs[kind] = l;
                }
                l.Insert(0, e);
                if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
            }
            if (!notify) return;
            Action<string, LogEntry> h = Added;
            if (h != null) { try { h(kind, e); } catch { } }
        }

        public static void Clear(string kind)
        {
            lock (lk) logs.Remove(kind);
        }

        static bool hooked;

        /// <summary>Catat baris log aplikasi yang menandakan kegagalan komunikasi sebagai network error.</summary>
        public static void HookNetworkLog()
        {
            if (hooked) return;
            hooked = true;
            Log.Line += delegate(string line)
            {
                string l = line.ToLowerInvariant();
                if (l.Contains("gagal") || l.Contains("error") || l.Contains("terputus") || l.Contains("timeout") || l.Contains("tidak dapat"))
                {
                    var e = new LogEntry();
                    e.Source = "aplikasi";
                    e.Text = line.Length > 12 ? line.Substring(12).Trim() : line;
                    Add(Net, e, false);
                }
            };
        }
    }

    /// <summary>Memantau semua NVR terdaftar: status koneksi kamera (as_getinfo.cgi?FILE=2) dan error log NVR (loglist.cgi).
    /// Hasilnya masuk ke Device error log dan memunculkan notifikasi.</summary>
    public static class EventMonitor
    {
        static Thread thread;
        static volatile bool stop;
        static readonly Dictionary<string, int> prev = new Dictionary<string, int>();
        static readonly Dictionary<long, HashSet<string>> seenErr = new Dictionary<long, HashSet<string>>();
        static readonly HashSet<long> down = new HashSet<long>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        public static void Start()
        {
            if (thread != null) return;
            stop = false;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public static void Stop()
        {
            stop = true;
            wake.Set();
            thread = null;
        }

        /// <summary>Segera cek ulang (setelah pengaturan diubah).</summary>
        public static void Poke()
        {
            wake.Set();
        }

        static void Loop()
        {
            Thread.Sleep(4000);
            while (!stop)
            {
                int sec = AppSettings.Current.EventPollSec;
                if (sec > 0)
                {
                    foreach (NvrRecord n in Dir.Nvrs)
                    {
                        if (stop) return;
                        try { PollNvr(n); }
                        catch (Exception ex) { Log.Write("Monitor " + n.Name + ": " + ex.Message); }
                    }
                }
                wake.WaitOne(Math.Max(5, sec <= 0 ? 15 : sec) * 1000);
            }
        }

        static void PollNvr(NvrRecord n)
        {
            NvrEndpoint ep = n.ToEndpoint();
            CameraListResult res;
            try
            {
                res = NvrScanner.FetchCameraList(ep);
                if (down.Remove(n.Id))
                {
                    var ok = new LogEntry();
                    ok.Source = n.Name;
                    ok.Text = "NVR tersambung kembali";
                    ok.NvrId = n.Id;
                    Events.Add(Events.Device, ok, false);
                }
            }
            catch (Exception ex)
            {
                if (down.Add(n.Id))
                {
                    var e = new LogEntry();
                    e.Source = n.Name;
                    e.Text = "Recorder communication error: " + ex.Message;
                    e.NvrId = n.Id;
                    Events.Add(Events.Device, e, true);
                }
                return;
            }

            foreach (CamStatus s in res.Items)
            {
                string key = n.Id + ":" + s.Channel;
                Dir.SetLiveStatus(n.Id, s.Channel, s.Status);
                int old;
                bool had = prev.TryGetValue(key, out old);
                prev[key] = s.Status;
                if (s.Status == 2 && (!had || old != 2))
                {
                    var e = new LogEntry();
                    e.Source = n.Name + " / " + Dir.CamName(n.Id, s.Channel);
                    e.Text = "Koneksi kamera terputus (CAM " + s.Channel + ")";
                    e.NvrId = n.Id;
                    e.Channel = s.Channel;
                    Events.Add(Events.Device, e, true);
                }
                else if (s.Status == 1 && had && old == 2)
                {
                    var e = new LogEntry();
                    e.Source = n.Name + " / " + Dir.CamName(n.Id, s.Channel);
                    e.Text = "Koneksi kamera pulih (CAM " + s.Channel + ")";
                    e.NvrId = n.Id;
                    e.Channel = s.Channel;
                    Events.Add(Events.Device, e, false);
                }
            }
            try
            {
                bool rec = false;
                foreach (DiskRecord d in NvrScanner.FetchDisks(ep)) if (d.Recording) rec = true;
                Dir.SetNvrRecording(n.Id, rec);
            }
            catch (Exception ex) { Log.Write("Monitor " + n.Name + ": info HDD tidak terbaca: " + ex.Message); }
            Dir.RaiseStatusChanged();

            if (AppSettings.Current.ErrLogPoll) PollErrorLog(n, ep);
        }

        static void PollErrorLog(NvrRecord n, NvrEndpoint ep)
        {
            var s = new NvrSession("monitor " + n.Name, ep);
            string text;
            try
            {
                text = s.Cmd("loglist.cgi", "KIND=ERR&HTML=as_errorlog.html");
            }
            finally
            {
                try { s.Logout(); } catch { }
            }
            int i0 = text.IndexOf("ERROR_LOG=", StringComparison.Ordinal);
            string body = i0 >= 0 ? text.Substring(i0) : text;
            MatchCollection m = Regex.Matches(body, "\"([^\"]*)\"");
            HashSet<string> seen;
            bool first = false;
            if (!seenErr.TryGetValue(n.Id, out seen))
            {
                seen = new HashSet<string>();
                seenErr[n.Id] = seen;
                first = true;
            }
            for (int i = m.Count / 5 * 5 - 5; i >= 0; i -= 5)
            {
                string t = m[i + 1].Groups[1].Value;
                string err = m[i + 3].Groups[1].Value;
                string key = t + "|" + err;
                if (!seen.Add(key)) continue;
                var e = new LogEntry();
                e.Source = n.Name;
                e.Text = err + (t.Length > 0 ? "  (" + t + ")" : "");
                e.NvrId = n.Id;
                Events.Add(Events.Device, e, !first);
            }
        }
    }

    /// <summary>Penghubung antar jendela (Operation / Live / Map / Search / Setup) dan hak akses user yang sedang login.</summary>
    public static class AsmApp
    {
        public static UserRec User;     // null = tanpa login (semua hak)

        public static bool Can(Perm p)
        {
            if (User == null) return true;
            if (User.IsAdmin) return true;
            return (AsmDb.GetPerm(User.Level) & p) == p;
        }

        /// <summary>Tampilkan kamera di area terpilih Operation window (dan pindah ke tab itu).</summary>
        public static Action<NvrRecord, CameraRecord> ShowCamera;
        /// <summary>Putar rekaman kamera pada waktu tertentu (UTC) di Operation window.</summary>
        public static Action<NvrRecord, CameraRecord, DateTime> PlayCameraAt;
        /// <summary>Buka tab/jendela: "live", "map", "setup", "search", "operation".</summary>
        public static Action<string> OpenWindow;
        /// <summary>Daftar group milik user saat ini + group umum.</summary>
        public static string UserKey { get { return User == null ? "" : User.Name; } }

        public static string DisplayName(long nvrId, int ch)
        {
            return Dir.Title(nvrId, ch);
        }
    }

    /// <summary>Bunyi buzzer saat ada notifikasi sampai dihentikan.</summary>
    public static class Buzzer
    {
        static System.Windows.Forms.Timer timer;
        static DateTime startedAt;
        public static bool On { get { return timer != null && timer.Enabled; } }

        public static void Start()
        {
            if (!AppSettings.Current.EventBuzzer) return;
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 1800;
                timer.Tick += delegate
                {
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                    // jangan berbunyi tanpa henti di PC tanpa operator: diam otomatis setelah 5 menit
                    if ((DateTime.Now - startedAt).TotalMinutes > 5) timer.Stop();
                };
            }
            startedAt = DateTime.Now;
            if (!timer.Enabled)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                timer.Start();
            }
        }

        public static void Stop()
        {
            if (timer != null) timer.Stop();
        }
    }
}
