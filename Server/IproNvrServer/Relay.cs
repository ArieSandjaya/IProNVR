using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace IproNvrServer
{
    /// <summary>Relay RTSP memakai MediaMTX: server menarik tiap kamera SATU kali (hanya saat ada penonton) dan membagikannya ke banyak client.
    /// Kamera/NVR tidak terbebani koneksi ganda. URL kamera (berisi password) dikirim ke MediaMTX lewat API loopback dan hanya ada di memori MediaMTX,
    /// tidak ditulis ke file konfigurasi. Client membaca lewat rtsp://user:pass@SERVER:8554/nama dengan login baca-saja.
    /// Nama path (sama dengan yang dihitung client): n{host}-{port}-{channel}_s (sub) / _m (utama).</summary>
    public class MediaMtxRelay
    {
        readonly Store store;
        readonly ServerConfig cfg;
        Process proc;
        Thread worker;
        volatile bool stop;
        readonly object lk = new object();
        readonly Dictionary<string, string> applied = new Dictionary<string, string>();   // nama path -> URL sumber yang sudah terdaftar
        long lastVersion = -1;
        readonly JavaScriptSerializer js = new JavaScriptSerializer();

        public string Status = "Relay belum dijalankan";
        public bool Running;
        public int Paths { get { lock (lk) return applied.Count; } }
        public string ViewUser, ViewPass;
        public int RtspPort = 8554;
        const int ApiPort = 9997;

        public MediaMtxRelay(Store s, ServerConfig c) { store = s; cfg = c; }

        public static string PathName(string host, int port, int channel, bool main)
        {
            return "n" + Regex.Replace((host ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]", "-") + "-" + port + "-" + channel + (main ? "_m" : "_s");
        }

        string FindExe()
        {
            if (!string.IsNullOrWhiteSpace(cfg.MediaMtxPath) && File.Exists(cfg.MediaMtxPath)) return cfg.MediaMtxPath;
            string[] c = { Path.Combine(ServerConfig.Dir, "Tools", "mediamtx", "mediamtx.exe"), Path.Combine(ServerConfig.Dir, "mediamtx.exe"), Path.Combine(ServerConfig.Dir, "mediamtx") };
            foreach (string p in c) if (File.Exists(p)) return p;
            return null;
        }

        string Dir { get { return Path.Combine(cfg.DataDir, "mediamtx"); } }

        public void Start()
        {
            if (!cfg.Relay) { Status = "Relay dimatikan (server.json: Relay=false)"; return; }
            string exe = FindExe();
            if (exe == null) { Status = "mediamtx.exe tidak ditemukan (letakkan di Tools\\mediamtx di samping IproNvrServer.exe)"; Log.Write("Relay: " + Status); return; }
            RtspPort = cfg.RelayPort > 0 ? cfg.RelayPort : 8554;
            LoadCreds();
            worker = new Thread(delegate() { Loop(exe); });
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            stop = true;
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        }

        /// <summary>Login baca-saja untuk client dibuat sekali lalu disimpan di data\mediamtx\relay-cred.txt.</summary>
        void LoadCreds()
        {
            Directory.CreateDirectory(Dir);
            string f = Path.Combine(Dir, "relay-cred.txt");
            try
            {
                if (File.Exists(f))
                {
                    string[] l = File.ReadAllLines(f);
                    if (l.Length >= 2 && l[0].Length > 0 && l[1].Length > 0) { ViewUser = l[0].Trim(); ViewPass = l[1].Trim(); return; }
                }
            }
            catch { }
            ViewUser = "viewer";
            ViewPass = Program.RandomPass();
            try { File.WriteAllText(f, ViewUser + "\r\n" + ViewPass + "\r\n"); } catch { }
        }

        string Yaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("logLevel: warn");
            sb.AppendLine("logDestinations: [file]");
            sb.AppendLine("logFile: " + Path.Combine(Dir, "mediamtx.log").Replace("\\", "/"));
            sb.AppendLine("api: yes");
            sb.AppendLine("apiAddress: 127.0.0.1:" + ApiPort);
            sb.AppendLine("metrics: no");
            sb.AppendLine("pprof: no");
            sb.AppendLine("playback: no");
            sb.AppendLine("rtsp: yes");
            sb.AppendLine("rtspTransports: [tcp]");
            sb.AppendLine("rtspAddress: :" + RtspPort);
            sb.AppendLine("rtmp: no");
            sb.AppendLine("hls: no");
            sb.AppendLine("webrtc: no");
            sb.AppendLine("srt: no");
            sb.AppendLine("moq: no");
            sb.AppendLine("authMethod: internal");
            sb.AppendLine("authInternalUsers:");
            sb.AppendLine("- user: any");
            sb.AppendLine("  pass:");
            sb.AppendLine("  ips: ['127.0.0.1', '::1']");
            sb.AppendLine("  permissions:");
            sb.AppendLine("  - action: api");
            sb.AppendLine("- user: " + ViewUser);
            sb.AppendLine("  pass: " + ViewPass);
            sb.AppendLine("  permissions:");
            sb.AppendLine("  - action: read");
            sb.AppendLine("paths: {}");
            return sb.ToString();
        }

        void Loop(string exe)
        {
            int fails = 0;
            while (!stop)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Directory.CreateDirectory(Dir);
                    string yml = Path.Combine(Dir, "mediamtx.yml");
                    File.WriteAllText(yml, Yaml());
                    var psi = new ProcessStartInfo(exe, "\"" + yml + "\"");
                    psi.WorkingDirectory = Dir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    proc = Process.Start(psi);
                    Status = "Memulai MediaMTX...";
                    bool up = false;
                    for (int i = 0; i < 50 && !stop && !proc.HasExited; i++)
                    {
                        Thread.Sleep(200);
                        if (ApiUp()) { up = true; break; }
                    }
                    if (up)
                    {
                        lock (lk) applied.Clear();
                        lastVersion = -1;
                        Running = true;
                        Log.Write("Relay: MediaMTX berjalan (RTSP port " + RtspPort + ").");
                        while (!stop && !proc.HasExited)
                        {
                            try { SyncPaths(); } catch (Exception ex) { Status = "Sinkron relay gagal: " + ex.Message; Log.Write("Relay: " + ex.Message); }
                            for (int i = 0; i < 20 && !stop && !proc.HasExited; i++) Thread.Sleep(500);
                        }
                    }
                    else Status = "MediaMTX tidak merespons API (port " + ApiPort + " dipakai program lain? lihat data\\mediamtx\\mediamtx.log)";
                }
                catch (Exception ex) { Status = "Relay gagal: " + ex.Message; Log.Write("Relay: " + ex.Message); }
                Running = false;
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                if (stop) return;
                fails = sw.Elapsed.TotalSeconds < 30 ? fails + 1 : 0;
                Log.Write("Relay: MediaMTX berhenti, mengulang (" + Status + ")");
                for (int i = 0; i < Math.Min(60, 5 * (fails + 1)) * 5 && !stop; i++) Thread.Sleep(200);
            }
        }

        // ---------- API MediaMTX (loopback) ----------

        string Api(string method, string path, object body, out int code)
        {
            var rq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ApiPort + path);
            rq.Method = method;
            rq.Timeout = 8000;
            rq.Proxy = null;
            try
            {
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(js.Serialize(body));
                    rq.ContentType = "application/json";
                    rq.ContentLength = b.Length;
                    using (Stream s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                else if (method == "POST") rq.ContentLength = 0;
                using (var rs = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                {
                    code = (int)rs.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
                code = (int)resp.StatusCode;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
        }

        bool ApiUp()
        {
            try { int c; Api("GET", "/v3/config/global/get", null, out c); return c == 200; } catch { return false; }
        }

        // ---------- daftar path dari data server ----------

        static string S(Row r, string k) { object o; return r.TryGetValue(k, out o) && o != null ? Convert.ToString(o) : ""; }
        static long L(Row r, string k) { object o; long v; return r.TryGetValue(k, out o) && o != null && long.TryParse(Convert.ToString(o), out v) ? v : 0; }

        static string Build(string template, string user, string pass, string ip)
        {
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(ip)) return "";
            string t = template.Trim();
            if (string.IsNullOrEmpty(user)) t = t.Replace("{user}:{pass}@", "");
            return t.Replace("{user}", Uri.EscapeDataString(user ?? "")).Replace("{pass}", Uri.EscapeDataString(pass ?? "")).Replace("{ip}", ip.Trim());
        }

        /// <summary>URL RTSP kamera (sama dengan logika client: URL utama hasil deteksi, atau URL sub dengan penurunan ke stream utama).</summary>
        static string CamUrl(Row nvr, Row cam, bool main)
        {
            string cu = S(cam, "cam_user"), cp = S(cam, "cam_pass");
            if (cu.Length == 0) { cu = S(nvr, "cam_user"); cp = S(nvr, "cam_pass"); }
            string ip = S(cam, "ip_address");
            string mu = S(cam, "rtsp_main_url").Trim();
            if (main && mu.Length > 0) return mu.Contains("{") ? Build(mu, cu, cp, ip) : mu;
            string url = S(cam, "rtsp_url").Trim();
            if (url.Contains("{")) url = Build(url, cu, cp, ip);
            if (url.Length == 0) url = Build(S(nvr, "rtsp_template"), cu, cp, ip);
            if (url.Length > 0 && main)
            {
                url = Regex.Replace(url, @"subtype=1", "subtype=0", RegexOptions.IgnoreCase);
                url = Regex.Replace(url, @"(/Streaming/Channels/\d{1,2}0)2(?!\d)", "${1}1", RegexOptions.IgnoreCase);
            }
            return url;
        }

        Dictionary<string, string> Desired()
        {
            var d = new Dictionary<string, string>();
            var nvrs = new Dictionary<long, Row>();
            foreach (Row n in store.List(Schema.ByRoute("nvrs"))) nvrs[L(n, "id")] = n;
            foreach (Row c in store.List(Schema.ByRoute("cameras")))
            {
                Row n;
                if (!nvrs.TryGetValue(L(c, "nvr_id"), out n)) continue;
                // hanya kamera yang URL-nya bisa disusun server (IP kamera + URL/template). Selain itu client tetap memakai jalurnya sendiri.
                string brand = S(n, "brand");
                if (brand.Length > 0 && brand != "ipro" && S(c, "rtsp_url").Trim().Length == 0) continue;
                string host = S(n, "host");
                int port = (int)L(n, "port");
                int ch = (int)L(c, "channel");
                string us = CamUrl(n, c, false), um = CamUrl(n, c, true);
                if (us.Length == 0) continue;
                d[PathName(host, port, ch, false)] = us;
                d[PathName(host, port, ch, true)] = um.Length > 0 ? um : us;
            }
            return d;
        }

        void SyncPaths()
        {
            long ver = store.Version;
            if (ver == lastVersion && applied.Count > 0) return;
            Dictionary<string, string> want = Desired();
            int add = 0, del = 0;
            var drop = new List<string>();
            lock (lk) foreach (string k in applied.Keys) if (!want.ContainsKey(k)) drop.Add(k);
            foreach (string k in drop)
            {
                int c; Api("DELETE", "/v3/config/paths/delete/" + Uri.EscapeDataString(k), null, out c);
                lock (lk) applied.Remove(k); del++;
            }
            foreach (KeyValuePair<string, string> kv in want)
            {
                string cur;
                lock (lk) { if (applied.TryGetValue(kv.Key, out cur) && cur == kv.Value) continue; }
                var body = new Dictionary<string, object>();
                body["source"] = kv.Value;
                body["sourceOnDemand"] = true;
                body["sourceOnDemandStartTimeout"] = "10s";
                body["sourceOnDemandCloseAfter"] = "15s";
                body["rtspTransport"] = "tcp";
                int code;
                string name = Uri.EscapeDataString(kv.Key);
                string r = Api("POST", "/v3/config/paths/add/" + name, body, out code);
                if (code >= 400) r = Api("POST", "/v3/config/paths/replace/" + name, body, out code);
                if (code >= 400) { Log.Write("Relay: gagal mendaftarkan " + kv.Key + ": HTTP " + code + " " + r); continue; }
                lock (lk) applied[kv.Key] = kv.Value; add++;
            }
            lastVersion = ver;
            Status = "Relay aktif: " + Paths + " path (RTSP port " + RtspPort + ")";
            if (add > 0 || del > 0) Log.Write("Relay: " + add + " path didaftarkan/diubah, " + del + " dihapus.");
        }

        public void Resync() { lastVersion = -1; }

        /// <summary>Info untuk client (user sudah login).</summary>
        public Dictionary<string, object> Info(bool admin)
        {
            var o = new Dictionary<string, object>();
            o["enabled"] = Running;
            o["port"] = RtspPort;
            o["user"] = Running ? ViewUser : "";
            o["pass"] = Running ? ViewPass : "";
            o["paths"] = Paths;
            o["status"] = Status;
            return o;
        }
    }
}
