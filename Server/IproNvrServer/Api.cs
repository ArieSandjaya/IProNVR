using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IproNvrTool;
using System.Web.Script.Serialization;

namespace IproNvrServer
{
    class Session
    {
        public Row User;
        public DateTime Expires;
    }

    /// <summary>Server HTTP (HttpListener) + API JSON. Semua endpoint /api/* butuh token (Authorization: Bearer ...) kecuali /api/login.</summary>
    public class ApiServer
    {
        readonly Store store;
        readonly ServerConfig cfg;
        HttpListener listener;
        Thread loop;
        volatile bool stop;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, int[]> fails = new Dictionary<string, int[]>();   // ip -> [jumlah gagal, tick terakhir]
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // ---------- impor / templat Excel: Nama NVR, Channel, IP Address ----------

        byte[] BuildIpTemplate()
        {
            var rows = new List<string[]>();
            rows.Add(new[] { "Nama NVR", "Channel", "IP Address" });
            var nvrs = new Dictionary<long, string>();
            foreach (Row n in store.List(Schema.ByRoute("nvrs"))) nvrs[Convert.ToInt64(n["id"])] = Convert.ToString(n["name"]);
            foreach (Row c in store.List(Schema.ByRoute("cameras")))
            {
                string nn;
                if (!nvrs.TryGetValue(Convert.ToInt64(c["nvr_id"]), out nn)) continue;
                rows.Add(new[] { nn, Convert.ToString(c["channel"]), Convert.ToString(c["ip_address"]) });
            }
            if (rows.Count == 1)
            {
                string sample = nvrs.Count > 0 ? new List<string>(nvrs.Values)[0] : "NVR Lobby";
                rows.Add(new[] { sample, "1", "192.168.1.101" });
                rows.Add(new[] { sample, "2", "192.168.1.102" });
            }
            return ExcelIo.Write("IP Kamera", rows, new[] { 28, 10, 22 }, new[] { 1 });
        }

        static bool IsInt(string s, out int v)
        {
            double d;
            v = 0;
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return false;
            if (d != Math.Floor(d) || d < 1 || d > 256) return false;
            v = (int)d;
            return true;
        }

        /// <summary>Body: file (base64 .xlsx / .csv), name (nama file), dry (true = hanya cek, belum disimpan).
        /// Kolom: Nama NVR, Channel, IP Address. Kamera yang belum ada dibuat; IP kosong dilewati.</summary>
        object ImportIps(Dictionary<string, object> body, string by, out string error)
        {
            error = null;
            object o;
            string b64 = body.TryGetValue("file", out o) && o != null ? Convert.ToString(o) : "";
            string fname = body.TryGetValue("name", out o) && o != null ? Convert.ToString(o) : "";
            bool dry = body.TryGetValue("dry", out o) && o != null && (o is bool ? (bool)o : Convert.ToString(o) == "true");
            if (b64.Length == 0) { error = "File kosong."; return null; }
            List<string[]> rows;
            try { rows = ExcelIo.Read(Convert.FromBase64String(b64), fname); }
            catch (Exception ex) { error = "File tidak bisa dibaca (harus .xlsx atau .csv): " + ex.Message; return null; }
            if (rows.Count == 0) { error = "File tidak berisi data."; return null; }

            int cN = 0, cC = 1, cI = 2, first = 0;
            int tmp;
            string[] h0 = rows[0];
            if (h0.Length < 2 || !IsInt(h0[1], out tmp))
            {
                first = 1;   // baris pertama = judul kolom
                for (int i = 0; i < h0.Length; i++)
                {
                    string t = h0[i].ToLowerInvariant();
                    if (t.Contains("nvr")) cN = i;
                    else if (t.Contains("channel") || t.Contains("kanal") || t == "ch") cC = i;
                    else if (t.Contains("ip") || t.Contains("alamat")) cI = i;
                }
            }
            var nvrByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (Row n in store.List(Schema.ByRoute("nvrs")))
            {
                string nm = (Convert.ToString(n["name"]) ?? "").Trim();
                if (!nvrByName.ContainsKey(nm)) nvrByName[nm] = Convert.ToInt64(n["id"]);
            }
            var cams = new Dictionary<string, Row>();
            foreach (Row c in store.List(Schema.ByRoute("cameras"))) cams[Convert.ToInt64(c["nvr_id"]) + ":" + Convert.ToInt64(c["channel"])] = c;

            int updated = 0, created = 0, same = 0;
            var skipped = new List<object>();
            var seen = new Dictionary<string, string>();
            var work = new List<KeyValuePair<string, object>>();
            var re = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9._\-]+$");
            for (int r = first; r < rows.Count; r++)
            {
                string[] row = rows[r];
                string nn = cN < row.Length ? row[cN].Trim() : "";
                string cs = cC < row.Length ? row[cC].Trim() : "";
                string ip = cI < row.Length ? row[cI].Trim() : "";
                int line = r + 1;
                Action<string> skip = delegate(string why) { skipped.Add(new Dictionary<string, object> { { "row", line }, { "reason", why } }); };
                long nid; int ch;
                if (nn.Length == 0 && cs.Length == 0 && ip.Length == 0) continue;
                if (!nvrByName.TryGetValue(nn, out nid)) { skip("NVR \"" + nn + "\" tidak ditemukan (nama harus sama dengan di menu NVR)"); continue; }
                if (!IsInt(cs, out ch)) { skip("Channel \"" + cs + "\" tidak valid (angka 1-256)"); continue; }
                if (ip.Length == 0) { skip("IP kosong"); continue; }
                if (!re.IsMatch(ip)) { skip("IP \"" + ip + "\" tidak valid (tanpa spasi / http://)"); continue; }
                string key = nid + ":" + ch;
                string prev;
                if (seen.TryGetValue(key, out prev) && prev != ip) { skip("Baris ganda untuk kanal ini (dipakai baris terakhir)"); }
                seen[key] = ip;
                work.RemoveAll(delegate(KeyValuePair<string, object> kv) { return kv.Key == key; });
                work.Add(new KeyValuePair<string, object>(key, new object[] { nid, ch, ip }));
            }
            foreach (KeyValuePair<string, object> kv in work)
            {
                object[] a = (object[])kv.Value;
                long nid = (long)a[0]; int ch = (int)a[1]; string ip = (string)a[2];
                Row cur;
                bool exists = cams.TryGetValue(kv.Key, out cur);
                if (exists && Convert.ToString(cur["ip_address"]) == ip) { same++; continue; }
                if (exists) updated++; else created++;
                if (dry) continue;
                var b = new Dictionary<string, object> { { "nvr_id", (double)nid }, { "channel", (double)ch }, { "ip_address", ip } };
                if (!exists) b["name"] = "CH" + ch;
                store.Upsert(Schema.ByRoute("cameras"), b);
            }
            if (!dry) Log.Write("Impor IP dari Excel oleh " + by + ": diperbarui " + updated + ", dibuat " + created + ", sama " + same + ", dilewati " + skipped.Count);
            return new Dictionary<string, object> { { "dry", dry }, { "updated", updated }, { "created", created }, { "unchanged", same }, { "skipped", skipped }, { "rows", rows.Count - first } };
        }

        /// <summary>Baca daftar kamera yang terdaftar di NVR (as_getinfo.cgi?FILE=2) dan buat kamera yang belum ada di server.</summary>
        object FetchNvrCameras(Dictionary<string, object> body, string by, out string error)
        {
            error = null;
            object o;
            long nvrId = 0;
            if (body.TryGetValue("nvr_id", out o) && o != null) nvrId = Convert.ToInt64(Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture));
            Row nvr = null;
            foreach (Row r in store.List(Schema.ByRoute("nvrs"))) if (Convert.ToInt64(r["id"]) == nvrId) nvr = r;
            if (nvr == null) { error = "NVR tidak ditemukan."; return null; }
            string host = Convert.ToString(nvr["host"]);
            int port = Convert.ToInt32(nvr["port"]);
            bool https = Convert.ToInt32(nvr["use_https"]) != 0;
            string nu = Convert.ToString(nvr["username"]), np = Convert.ToString(nvr["password"]);
            string text;
            try
            {
                // 1) tes login (UID), 2) daftar kamera yang terdaftar di NVR: sama dengan "Tes login" + "Ambil Daftar Kamera" di aplikasi client
                string uid = NvrList.Login(host, port, https, nu, np);
                NvrList.Logout(host, port, https, nu, np, uid);
                text = NvrList.Download(host, port, https, nu, np, "/cgi-bin/as_getinfo.cgi?FILE=2", 20000);
            }
            catch (Exception ex) { error = "Gagal menghubungi NVR " + host + ":" + port + " - " + NvrList.Explain(ex); return null; }
            SortedDictionary<int, int> cl = NvrList.Parse(text);
            if (cl.Count == 0)
            {
                string raw = (text ?? "").Replace("\r", " ").Replace("\n", " | ");
                error = "NVR menjawab tetapi tidak ada baris CAM_CONNECT_nnCH. Cuplikan respons: " + (raw.Length > 200 ? raw.Substring(0, 200) : raw);
                return null;
            }
            var have = new HashSet<long>();
            foreach (Row c in store.List(Schema.ByRoute("cameras")))
                if (Convert.ToInt64(c["nvr_id"]) == nvrId) have.Add(Convert.ToInt64(c["channel"]));
            int registered = 0, added = 0, bad = 0, gone = 0;
            int maxCh = 0;
            foreach (KeyValuePair<int, int> cs in cl)
            {
                maxCh = Math.Max(maxCh, cs.Key);
                var cb = new Dictionary<string, object> { { "nvr_id", (double)nvrId }, { "channel", (double)cs.Key }, { "reg_status", (double)cs.Value } };
                if (cs.Value == 0)
                {
                    if (have.Contains(cs.Key)) { store.Upsert(Schema.ByRoute("cameras"), cb); gone++; }   // sudah ada di server tetapi dihapus dari NVR: ditandai, tidak dihapus
                    continue;
                }
                registered++;
                if (cs.Value == 2) bad++;
                if (!have.Contains(cs.Key)) { cb["name"] = "CH" + cs.Key; added++; }
                store.Upsert(Schema.ByRoute("cameras"), cb);
            }
            if (maxCh > 0 && maxCh != Convert.ToInt32(nvr["max_channels"]))
                store.Upsert(Schema.ByRoute("nvrs"), new Dictionary<string, object> { { "id", (double)nvrId }, { "max_channels", (double)maxCh } });
            Log.Write("Ambil kamera dari NVR " + Convert.ToString(nvr["name"]) + " oleh " + by + ": terdaftar " + registered + ", baru " + added);
            return new Dictionary<string, object> { { "registered", registered }, { "added", added }, { "error_conn", bad }, { "unregistered", gone }, { "total_slots", cl.Count }, { "max_channels", maxCh } };
        }

        readonly DiscoveryManager disc;
        public ApiServer(Store s, ServerConfig c) { store = s; cfg = c; disc = new DiscoveryManager(s, c); }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://+:" + cfg.Port + "/");
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                // tanpa hak urlacl: hanya bisa localhost
                Log.Write("Tidak bisa listen di semua alamat (" + ex.Message + "). Jalankan 'IproNvrServer install' sebagai Administrator. Sementara hanya localhost.");
                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + cfg.Port + "/");
                listener.Start();
            }
            stop = false;
            loop = new Thread(Loop) { IsBackground = true, Name = "http" };
            loop.Start();
            Log.Write("Server berjalan di port " + cfg.Port + ", data: " + cfg.DataDir);
        }

        // ---------- pratinjau live (MJPEG) ----------

        class Preview { public string Url, Error = ""; public DateTime Expires; }
        readonly Dictionary<string, Preview> previews = new Dictionary<string, Preview>();
        readonly List<Process> streams = new List<Process>();
        const int MaxStreams = 4, MaxStreamSeconds = 600;

        string NewKey()
        {
            var b = new byte[12];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Body: nvr_id, ip_address, cam_user, cam_pass, rtsp_url (nilai form saat ini, belum harus tersimpan).</summary>
        string MakePreview(Dictionary<string, object> b, out string error)
        {
            error = null;
            Func<string, string> g = delegate(string k) { object v; return b.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; };
            long nid; long.TryParse(g("nvr_id"), out nid);
            Row nvr = null;
            foreach (Row r in store.List(Schema.ByRoute("nvrs"))) if (Convert.ToInt64(r["id"]) == nid) nvr = r;
            string tpl = g("rtsp_url"), user = g("cam_user"), pass = g("cam_pass");
            if (tpl.Length == 0 && nvr != null) tpl = Convert.ToString(nvr["rtsp_template"]);
            if (user.Length == 0 && nvr != null) { user = Convert.ToString(nvr["cam_user"]); pass = Convert.ToString(nvr["cam_pass"]); }
            string url = Rtsp.Build(tpl, user, pass, g("ip_address"));
            if (url.Length == 0) { error = "URL RTSP belum ada: isi IP kamera dan URL RTSP (atau template RTSP di data NVR), atau jalankan Deteksi."; return null; }
            string key = NewKey();
            lock (previews)
            {
                var dead = new List<string>();
                foreach (KeyValuePair<string, Preview> p in previews) if (p.Value.Expires < DateTime.UtcNow) dead.Add(p.Key);
                foreach (string k in dead) previews.Remove(k);
                previews[key] = new Preview { Url = url, Expires = DateTime.UtcNow.AddMinutes(10) };
            }
            return key;
        }

        void SetPreviewError(Preview pv, string e) { lock (previews) pv.Error = e; }

        void Stream(HttpListenerContext ctx)
        {
            HttpListenerResponse rs = ctx.Response;
            Preview pv;
            string key = ctx.Request.QueryString["k"] ?? "";
            lock (previews) { if (!previews.TryGetValue(key, out pv) || pv.Expires < DateTime.UtcNow) pv = null; }
            if (pv == null) { Err(rs, 404, "Pratinjau kedaluwarsa, klik Live lagi."); return; }
            string exe = disc.FindFfmpeg();
            if (exe == null) { string m = "ffmpeg tidak ditemukan di server (letakkan ffmpeg.exe di folder server atau isi FfmpegPath di server.json)."; SetPreviewError(pv, m); Err(rs, 500, m); return; }
            int w = 640, fps = 8;
            int.TryParse(ctx.Request.QueryString["w"], out w); if (w < 160 || w > 1280) w = 640;
            bool rtsp = pv.Url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
            string args = "-hide_banner -loglevel error " + (rtsp ? "-rtsp_transport tcp -fflags nobuffer -flags low_delay " : "-re ")
                + "-i \"" + pv.Url.Replace("\"", "%22") + "\" -an -vf scale=" + w + ":-2 -r " + fps + " -q:v 6 -f mpjpeg pipe:1";
            Process p;
            lock (streams)
            {
                if (streams.Count >= MaxStreams) { string m = "Terlalu banyak pratinjau live bersamaan (maks " + MaxStreams + "). Tutup yang lain dulu."; SetPreviewError(pv, m); Err(rs, 429, m); return; }
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                p = Process.Start(psi);
                streams.Add(p);
            }
            var errText = new StringBuilder();
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null && errText.Length < 4000) lock (errText) errText.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            try
            {
                System.IO.Stream so = p.StandardOutput.BaseStream;
                var buf = new byte[65536];
                Task<int> first = so.ReadAsync(buf, 0, buf.Length);
                int n = 0;
                if (!first.Wait(20000) || (n = first.Result) <= 0)
                {
                    try { p.Kill(); } catch { }
                    string last = "";
                    lock (errText) foreach (string line in errText.ToString().Split('\n')) if (line.Trim().Length > 0) last = line.Trim();
                    bool exited = false;
                    try { exited = p.WaitForExit(500); } catch { }
                    string m = (exited ? "Gagal membuka stream kamera" : "Kamera tidak memberi gambar dalam 20 detik") + (last.Length > 0 ? ": " + last : ".");
                    if (pv.Url.Contains("@")) m = m.Replace(pv.Url, "(url)");
                    SetPreviewError(pv, m);
                    Err(rs, 502, m);
                    return;
                }
                rs.SendChunked = true;
                rs.StatusCode = 200;
                rs.ContentType = "multipart/x-mixed-replace;boundary=ffmpeg";
                rs.Headers["Cache-Control"] = "no-store";
                DateTime end = DateTime.UtcNow.AddSeconds(MaxStreamSeconds);
                while (n > 0 && DateTime.UtcNow < end)
                {
                    rs.OutputStream.Write(buf, 0, n);
                    rs.OutputStream.Flush();
                    n = so.Read(buf, 0, buf.Length);
                }
            }
            catch { /* browser menutup gambar: normal */ }
            finally
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                lock (streams) streams.Remove(p);
                try { p.Dispose(); } catch { }
            }
        }

        public void Stop()
        {
            lock (streams) foreach (Process p in streams) { try { p.Kill(); } catch { } }
            stop = true;
            try { listener.Stop(); listener.Close(); } catch { }
        }

        void Loop()
        {
            while (!stop)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { if (stop) return; Thread.Sleep(200); continue; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        // ---------- util ----------

        static void Send(HttpListenerResponse r, int code, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            r.StatusCode = code;
            r.ContentType = type + "; charset=utf-8";
            r.ContentLength64 = b.Length;
            r.Headers["Cache-Control"] = "no-store";
            r.OutputStream.Write(b, 0, b.Length);
        }

        void Json(HttpListenerResponse r, int code, object o) { Send(r, code, "application/json", json.Serialize(o)); }

        void Err(HttpListenerResponse r, int code, string msg)
        {
            var o = new Dictionary<string, object>();
            o["error"] = msg;
            Json(r, code, o);
        }

        Dictionary<string, object> ReadBody(HttpListenerRequest rq)
        {
            using (var sr = new StreamReader(rq.InputStream, Encoding.UTF8))
            {
                string s = sr.ReadToEnd();
                if (s.Length == 0) return new Dictionary<string, object>();
                return json.Deserialize<Dictionary<string, object>>(s);
            }
        }

        Session Auth(HttpListenerRequest rq)
        {
            string h = rq.Headers["Authorization"] ?? "";
            string tok;
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tok = h.Substring(7).Trim();
            else if (rq.Url.AbsolutePath == "/api/stream") tok = rq.QueryString["t"] ?? "";   // <img> tidak bisa mengirim header
            else return null;
            lock (sessions)
            {
                Session s;
                if (!sessions.TryGetValue(tok, out s)) return null;
                if (s.Expires < DateTime.UtcNow) { sessions.Remove(tok); return null; }
                s.Expires = DateTime.UtcNow.AddDays(cfg.TokenDays);   // sliding
                return s;
            }
        }

        string NewToken(Row user)
        {
            var b = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            string tok = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            lock (sessions)
            {
                // buang token kedaluwarsa
                var dead = new List<string>();
                foreach (KeyValuePair<string, Session> p in sessions) if (p.Value.Expires < DateTime.UtcNow) dead.Add(p.Key);
                foreach (string k in dead) sessions.Remove(k);
                sessions[tok] = new Session { User = user, Expires = DateTime.UtcNow.AddDays(cfg.TokenDays) };
            }
            return tok;
        }

        bool Locked(string ip)
        {
            lock (fails)
            {
                int[] f;
                if (!fails.TryGetValue(ip, out f)) return false;
                if (Environment.TickCount - f[1] > 60000) { fails.Remove(ip); return false; }
                return f[0] >= 5;
            }
        }

        void Fail(string ip)
        {
            lock (fails)
            {
                int[] f;
                if (!fails.TryGetValue(ip, out f)) { f = new int[2]; fails[ip] = f; }
                f[0]++; f[1] = Environment.TickCount;
            }
        }

        static bool IsAdmin(Session s) { return Convert.ToInt64(s.User["is_admin"]) == 1; }

        // ---------- routing ----------

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest rq = ctx.Request;
            HttpListenerResponse rs = ctx.Response;
            try
            {
                string path = rq.Url.AbsolutePath;
                string ip = rq.RemoteEndPoint != null ? rq.RemoteEndPoint.Address.ToString() : "?";
                if (path == "/" || path == "/index.html") { Send(rs, 200, "text/html", AdminPage.Html); return; }
                if (path == "/api/ping") { Json(rs, 200, new Dictionary<string, object> { { "ok", true }, { "version", store.Version } }); return; }
                if (path == "/api/login" && rq.HttpMethod == "POST")
                {
                    if (Locked(ip)) { Err(rs, 429, "Terlalu banyak percobaan, coba lagi 1 menit."); return; }
                    Dictionary<string, object> b = ReadBody(rq);
                    object n, p;
                    b.TryGetValue("name", out n); b.TryGetValue("pass", out p);
                    Row u = store.Login(Convert.ToString(n), Convert.ToString(p));
                    if (u == null) { Fail(ip); Log.Write("Login gagal: " + n + " dari " + ip); Err(rs, 401, "Nama atau password salah."); return; }
                    Log.Write("Login: " + u["name"] + " dari " + ip);
                    var o = new Dictionary<string, object>();
                    o["token"] = NewToken(u);
                    o["user"] = u;
                    Json(rs, 200, o);
                    return;
                }
                if (!path.StartsWith("/api/", StringComparison.Ordinal)) { Err(rs, 404, "Tidak ditemukan."); return; }

                Session s = Auth(rq);
                if (s == null) { Err(rs, 401, "Belum login."); return; }

                string[] seg = path.Substring(5).Trim('/').Split('/');
                string head = seg[0];

                if (head == "version") { Json(rs, 200, new Dictionary<string, object> { { "version", store.Version } }); return; }
                if (head == "me") { Json(rs, 200, s.User); return; }
                if (head == "logout")
                {
                    string h = rq.Headers["Authorization"].Substring(7).Trim();
                    lock (sessions) sessions.Remove(h);
                    Json(rs, 200, new Dictionary<string, object> { { "ok", true } });
                    return;
                }
                if (head == "schema") { Json(rs, 200, SchemaInfo()); return; }
                if (head == "config")
                {
                    // seluruh konfigurasi dalam satu respons: dipakai client untuk sinkron (cache lokal)
                    long ver = store.Version;
                    var o = new Dictionary<string, object>();
                    o["version"] = ver;
                    foreach (TableDef t in Schema.Tables)
                    {
                        if (t.AdminOnlyRead && !IsAdmin(s)) continue;
                        o[t.Route] = store.List(t);
                    }
                    Json(rs, 200, o);
                    return;
                }

                if (head == "stream") { Stream(ctx); return; }
                if (head == "preview")
                {
                    if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    if (rq.HttpMethod == "POST")
                    {
                        string pe;
                        string k = MakePreview(ReadBody(rq), out pe);
                        if (k == null) { Err(rs, 400, pe); return; }
                        Json(rs, 200, new Dictionary<string, object> { { "key", k } });
                        return;
                    }
                    Preview pp;
                    lock (previews) { previews.TryGetValue(seg.Length > 1 ? seg[1] : "", out pp); }
                    Json(rs, 200, new Dictionary<string, object> { { "error", pp == null ? "Pratinjau kedaluwarsa." : pp.Error } });
                    return;
                }
                if (head == "ip-template" && rq.HttpMethod == "GET")
                {
                    if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    byte[] xb = BuildIpTemplate();
                    rs.StatusCode = 200;
                    rs.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    rs.Headers["Content-Disposition"] = "attachment; filename=\"ip-kamera.xlsx\"";
                    rs.Headers["Cache-Control"] = "no-store";
                    rs.ContentLength64 = xb.Length;
                    rs.OutputStream.Write(xb, 0, xb.Length);
                    return;
                }
                if (head == "import-ip" && rq.HttpMethod == "POST")
                {
                    if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    string ie;
                    object ir = ImportIps(ReadBody(rq), s.User["name"] + "", out ie);
                    if (ir == null) { Err(rs, 400, ie); return; }
                    Json(rs, 200, ir);
                    return;
                }
                if (head == "nvr-cameras" && rq.HttpMethod == "POST")
                {
                    if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    string fe;
                    object fr = FetchNvrCameras(ReadBody(rq), s.User["name"] + "", out fe);
                    if (fr == null) { Err(rs, 400, fe); return; }
                    Json(rs, 200, fr);
                    return;
                }
                if (head == "discover" && rq.HttpMethod == "POST")
                {
                    if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    Dictionary<string, object> b = ReadBody(rq);
                    string e;
                    string jid = disc.Start(b, s.User["name"] + "", out e);
                    if (jid == null) { Err(rs, 400, e); return; }
                    Json(rs, 200, new Dictionary<string, object> { { "job", jid } });
                    return;
                }
                if (head == "jobs")
                {
                    if (seg.Length < 2) { Json(rs, 200, disc.Latest()); return; }
                    if (rq.HttpMethod == "DELETE")
                    {
                        if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                        disc.Cancel(seg[1]);
                        Json(rs, 200, new Dictionary<string, object> { { "ok", true } });
                        return;
                    }
                    object jo = disc.Get(seg[1]);
                    if (jo == null) { Err(rs, 404, "Job tidak ada."); return; }
                    Json(rs, 200, jo);
                    return;
                }

                TableDef td = Schema.ByRoute(head);
                if (td == null) { Err(rs, 404, "Tidak ditemukan."); return; }

                if (rq.HttpMethod == "GET")
                {
                    if (td.AdminOnlyRead && !IsAdmin(s)) { Err(rs, 403, "Hanya admin."); return; }
                    Json(rs, 200, store.List(td));
                    return;
                }
                // tulis: hanya admin
                if (!IsAdmin(s)) { Err(rs, 403, "Hanya admin yang boleh mengubah konfigurasi."); return; }
                if (rq.HttpMethod == "POST" || rq.HttpMethod == "PUT")
                {
                    long id = store.Upsert(td, ReadBody(rq));
                    Log.Write(s.User["name"] + " simpan " + td.Route + " #" + id);
                    Json(rs, 200, new Dictionary<string, object> { { "id", id }, { "version", store.Version } });
                    return;
                }
                if (rq.HttpMethod == "DELETE")
                {
                    long id;
                    if (seg.Length < 2 || !long.TryParse(seg[1], out id)) { Err(rs, 400, "id wajib: DELETE /api/" + head + "/{id}"); return; }
                    store.Delete(td, id);
                    Log.Write(s.User["name"] + " hapus " + td.Route + " #" + id);
                    Json(rs, 200, new Dictionary<string, object> { { "ok", true }, { "version", store.Version } });
                    return;
                }
                Err(rs, 405, "Metode tidak didukung.");
            }
            catch (Exception ex)
            {
                Log.Write("Error " + ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath + ": " + ex.Message);
                try { Err(rs, ex is InvalidOperationException ? 400 : 500, ex.Message); } catch { }
            }
            finally
            {
                try { rs.OutputStream.Close(); } catch { }
            }
        }

        object SchemaInfo()
        {
            var l = new List<object>();
            foreach (TableDef t in Schema.Tables)
            {
                var cols = new List<object>();
                foreach (Col c in t.Cols)
                    cols.Add(new Dictionary<string, object> { { "name", c.Name }, { "kind", c.Kind.ToString() }, { "secret", c.Secret } });
                l.Add(new Dictionary<string, object> { { "route", t.Route }, { "cols", cols }, { "password", t.Table == "app_user" } });
            }
            return l;
        }
    }
}
