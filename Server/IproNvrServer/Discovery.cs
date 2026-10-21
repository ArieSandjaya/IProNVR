using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IproNvrTool;

namespace IproNvrServer
{
    /// <summary>Satu pekerjaan deteksi RTSP otomatis (berjalan di server; hasil langsung disimpan ke data kamera).</summary>
    class DiscoJob
    {
        public string Id, State = "running", Nvr = "", By = "";
        public int Total, Done, Found;
        public readonly List<string> Lines = new List<string>();
        public CancellationTokenSource Cts = new CancellationTokenSource();
        public DateTime Started = DateTime.Now;
    }

    /// <summary>Deteksi URL RTSP + main/sub kamera (ONVIF, templat NVR, pola merk; tiap kandidat diuji dengan ffmpeg).
    /// Memakai kode yang sama dengan aplikasi client (RtspDiscovery.cs / StreamProbe.cs dipakai bersama).</summary>
    public class DiscoveryManager
    {
        readonly Store store;
        readonly ServerConfig cfg;
        readonly Dictionary<string, DiscoJob> jobs = new Dictionary<string, DiscoJob>();
        readonly object lk = new object();
        int seq;

        public DiscoveryManager(Store s, ServerConfig c) { store = s; cfg = c; }

        public string FindFfmpeg()
        {
            if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath) && File.Exists(cfg.FfmpegPath)) return cfg.FfmpegPath;
            foreach (string n in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                string local = Path.Combine(ServerConfig.Dir, n);
                if (File.Exists(local)) return local;
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    foreach (string n in new[] { "ffmpeg.exe", "ffmpeg" })
                    {
                        string f = Path.Combine(d, n);
                        if (File.Exists(f)) return f;
                    }
                }
                catch { }
            }
            return null;
        }

        static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null && (v is bool ? (bool)v : Convert.ToString(v) == "1" || Convert.ToString(v).ToLowerInvariant() == "true");
        }

        /// <summary>Body: nvr_id (wajib), camera_ids (opsional: daftar id kamera; kosong = semua kamera NVR yang punya IP), overwrite (timpa URL RTSP yang sudah ada).</summary>
        public string Start(Dictionary<string, object> body, string by, out string error)
        {
            error = null;
            string exe = FindFfmpeg();
            if (exe == null) { error = "ffmpeg tidak ditemukan di server. Letakkan ffmpeg.exe di folder IproNvrServer.exe atau isi FfmpegPath di server.json."; return null; }
            object o;
            long nvrId = 0;
            if (body.TryGetValue("nvr_id", out o) && o != null) nvrId = Convert.ToInt64(Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture));
            Row nvr = null;
            foreach (Row r in store.List(Schema.ByRoute("nvrs"))) if (Convert.ToInt64(r["id"]) == nvrId) nvr = r;
            if (nvr == null) { error = "NVR tidak ditemukan."; return null; }

            var want = new HashSet<long>();
            if (body.TryGetValue("camera_ids", out o) && o is System.Collections.IEnumerable && !(o is string))
                foreach (object x in (System.Collections.IEnumerable)o) want.Add(Convert.ToInt64(Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture)));
            bool overwrite = B(body, "overwrite");

            var todo = new List<Row>();
            foreach (Row c in store.List(Schema.ByRoute("cameras")))
            {
                if (Convert.ToInt64(c["nvr_id"]) != nvrId) continue;
                if (want.Count > 0 && !want.Contains(Convert.ToInt64(c["id"]))) continue;
                if (string.IsNullOrWhiteSpace(Convert.ToString(c["ip_address"]))) continue;
                todo.Add(c);
            }
            if (todo.Count == 0) { error = "Tidak ada kamera dengan IP. Isi kolom ip_address dulu."; return null; }

            DiscoJob job;
            lock (lk)
            {
                foreach (DiscoJob j in jobs.Values) if (j.State == "running") { error = "Masih ada deteksi yang berjalan (job " + j.Id + ")."; return null; }
                job = new DiscoJob();
                job.Id = (++seq).ToString();
                job.Total = todo.Count;
                job.Nvr = Convert.ToString(nvr["name"]);
                job.By = by;
                jobs[job.Id] = job;
                while (jobs.Count > 20) { string oldest = null; foreach (string k in jobs.Keys) { oldest = k; break; } jobs.Remove(oldest); }
            }
            string tpl = Convert.ToString(nvr["rtsp_template"]);
            string cu = Convert.ToString(nvr["cam_user"]), cp = Convert.ToString(nvr["cam_pass"]);
            Log.Write("Deteksi RTSP dimulai oleh " + by + ": NVR " + job.Nvr + ", " + todo.Count + " kamera");
            Task.Run(delegate { Run(job, exe, todo, tpl, cu, cp, overwrite); });
            return job.Id;
        }

        void Add(DiscoJob j, string line) { lock (lk) { if (j.Lines.Count < 2000) j.Lines.Add(line); } }

        void Run(DiscoJob job, string exe, List<Row> todo, string tpl, string cu, string cp, bool overwrite)
        {
            try
            {
                var opt = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = job.Cts.Token };
                Parallel.ForEach(todo, opt, delegate(Row cam)
                {
                    long id = Convert.ToInt64(cam["id"]);
                    string name = Convert.ToString(cam["name"]);
                    string ip = Convert.ToString(cam["ip_address"]);
                    string label = "CH" + cam["channel"] + (name.Length > 0 ? " " + name : "") + " (" + ip + ")";
                    string line;
                    try
                    {
                        DiscoveryResult r = RtspDiscovery.Discover(exe, ip, cu, cp, tpl, job.Cts.Token);
                        var upd = new Dictionary<string, object>();
                        upd["id"] = id;
                        upd["scan_detail"] = r.Summary();
                        bool had = !string.IsNullOrWhiteSpace(Convert.ToString(cam["rtsp_url"]));
                        if (r.SubTemplate != null)
                        {
                            if (overwrite || !had) upd["rtsp_url"] = r.SubTemplate;
                            upd["codec"] = r.Sub.Codec;
                            upd["width"] = r.Sub.Width;
                            upd["height"] = r.Sub.Height;
                            lock (lk) job.Found++;
                            line = "OK   " + label + ": " + r.Summary() + (had && !overwrite ? "  (URL lama dipertahankan)" : "");
                        }
                        else line = "GAGAL " + label + ": " + r.Note;
                        store.Upsert(Schema.ByRoute("cameras"), upd);
                    }
                    catch (OperationCanceledException) { line = "BATAL " + label; }
                    catch (Exception ex) { line = "ERROR " + label + ": " + ex.Message; }
                    Add(job, line);
                    lock (lk) job.Done++;
                });
                lock (lk) job.State = job.Cts.IsCancellationRequested ? "cancelled" : "done";
            }
            catch (OperationCanceledException) { lock (lk) job.State = "cancelled"; }
            catch (Exception ex) { Add(job, "ERROR: " + ex.Message); lock (lk) job.State = "error"; }
            Log.Write("Deteksi RTSP selesai (NVR " + job.Nvr + "): " + job.Found + "/" + job.Total + " ditemukan");
        }

        public void Cancel(string id)
        {
            lock (lk) { DiscoJob j; if (jobs.TryGetValue(id, out j)) j.Cts.Cancel(); }
        }

        object View(DiscoJob j)
        {
            lock (lk)
                return new Dictionary<string, object> {
                    { "id", j.Id }, { "state", j.State }, { "nvr", j.Nvr }, { "by", j.By }, { "total", j.Total },
                    { "done", j.Done }, { "found", j.Found }, { "lines", new List<string>(j.Lines) } };
        }

        public object Get(string id)
        {
            lock (lk) { DiscoJob j; return jobs.TryGetValue(id, out j) ? View(j) : null; }
        }

        public object Latest()
        {
            lock (lk)
            {
                DiscoJob last = null;
                foreach (DiscoJob j in jobs.Values) last = j;
                return last == null ? (object)new Dictionary<string, object> { { "state", "none" } } : View(last);
            }
        }
    }
}
