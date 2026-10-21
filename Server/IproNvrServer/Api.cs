using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

        public ApiServer(Store s, ServerConfig c) { store = s; cfg = c; }

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

        public void Stop()
        {
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
            if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            string tok = h.Substring(7).Trim();
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
