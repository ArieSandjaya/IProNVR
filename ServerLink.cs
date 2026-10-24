using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Mode client-server: konfigurasi (NVR, kamera, group, sequence) dikelola di IPRO NVR Server.
    /// Client menarik konfigurasi lewat API dan menyalinnya ke database lokal (cache), sehingga seluruh aplikasi tetap membaca
    /// database lokal seperti biasa dan tetap jalan bila server mati. Login memakai akun server (offline: akun yang pernah login).</summary>
    public static class ServerLink
    {
        class ApiEx : Exception
        {
            public int Code;
            public bool Unreachable;
            public ApiEx(int code, string msg, bool unreachable) : base(msg) { Code = code; Unreachable = unreachable; }
        }

        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object syncLock = new object();
        static string syncTok;

        /// <summary>Token login interaktif terakhir (dipakai untuk menyimpan group/sequence/user ke server).</summary>
        public static string UserToken;
        public static string Status = "Mode server belum aktif";
        public static bool Online;
        public static DateTime LastSync;

        public static bool Enabled
        {
            get { AppSettings s = AppSettings.Current; return s.ServerMode && !string.IsNullOrWhiteSpace(s.ServerUrl); }
        }

        static string Base { get { return AppSettings.Current.ServerUrl.Trim().TrimEnd('/'); } }

        // ---------- HTTP ----------

        static object Call(string method, string path, object body, string tok, int timeoutMs)
        {
            var rq = (HttpWebRequest)WebRequest.Create(Base + path);
            rq.Method = method;
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.Proxy = null;
            if (!string.IsNullOrEmpty(tok)) rq.Headers["Authorization"] = "Bearer " + tok;
            try
            {
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(js.Serialize(body));
                    rq.ContentType = "application/json";
                    rq.ContentLength = b.Length;
                    using (Stream s = rq.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                using (var rs = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                {
                    string t = sr.ReadToEnd();
                    return t.Length == 0 ? null : js.DeserializeObject(t);
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new ApiEx(0, "Server tidak terjangkau: " + ex.Message, true);
                string msg = resp.StatusDescription;
                try
                {
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var d = js.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                        if (d != null && d.ContainsKey("error")) msg = Convert.ToString(d["error"]);
                    }
                }
                catch { }
                throw new ApiEx((int)resp.StatusCode, msg, false);
            }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        static long Lng(Dictionary<string, object> d, string k)
        {
            long r; long.TryParse(Str(d, k), NumberStyles.Any, CultureInfo.InvariantCulture, out r); return r;
        }

        static List<Dictionary<string, object>> Rows(Dictionary<string, object> d, string k)
        {
            var l = new List<Dictionary<string, object>>();
            object v;
            if (d != null && d.TryGetValue(k, out v) && v is object[])
                foreach (object o in (object[])v) { var r = o as Dictionary<string, object>; if (r != null) l.Add(r); }
            return l;
        }

        // ---------- login ----------

        /// <summary>Login ke server. Null + unreachable=true bila server tidak terjangkau (boleh dicoba offline).</summary>
        public static UserRec Login(string name, string pass, out string error, out bool unreachable)
        {
            error = null; unreachable = false;
            try
            {
                var d = Call("POST", "/api/login", new Dictionary<string, object> { { "name", name }, { "pass", pass } }, null, 8000) as Dictionary<string, object>;
                UserToken = Str(d, "token");
                var u = d["user"] as Dictionary<string, object>;
                var r = new UserRec();
                r.Id = Lng(u, "id");
                r.Name = Str(u, "name");
                r.IsAdmin = Lng(u, "is_admin") == 1;
                r.Level = r.IsAdmin ? 0 : (int)Lng(u, "level");     // level 0 = admin penuh di client
                r.UseGroup = Lng(u, "use_group") == 1;
                return r;
            }
            catch (ApiEx ex) { error = ex.Message; unreachable = ex.Unreachable; return null; }
        }

        static string SyncToken()
        {
            if (syncTok != null) return syncTok;
            AppSettings s = AppSettings.Current;
            string err; bool un;
            string saved = UserToken;
            UserRec u = Login(s.ServerUser, Secret.Unprotect(s.ServerPassEnc), out err, out un);
            if (u == null) throw new ApiEx(un ? 0 : 401, err ?? "login gagal", un);
            syncTok = UserToken;
            UserToken = saved;     // jangan menimpa token login interaktif
            return syncTok;
        }

        // ---------- sinkron ----------

        /// <summary>Sinkron sekali saat aplikasi dibuka (bila server mati, dipakai data tersimpan terakhir). Selanjutnya hanya manual lewat menu / tombol.</summary>
        public static void Start()
        {
            if (!Enabled) return;
            SyncNow(false, 6000);
        }

        /// <summary>Periksa perubahan di server sekarang (menu "Periksa pembaruan dari server"). Mengembalikan teks hasil.</summary>
        public static string CheckNow(out bool changed)
        {
            changed = SyncNow(false, 10000);
            return changed ? "Konfigurasi diperbarui dari server.\n\n" + Status : Status;
        }

        /// <summary>Tarik konfigurasi bila versi di server berubah (force = selalu). True bila data lokal diperbarui.</summary>
        public static bool SyncNow(bool force, int timeoutMs)
        {
            if (!Enabled) return false;
            lock (syncLock)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        string tok = SyncToken();
                        var v = Call("GET", "/api/version", null, tok, timeoutMs) as Dictionary<string, object>;
                        long sv = Lng(v, "version");
                        AppSettings s = AppSettings.Current;
                        if (!force && sv == s.ServerVersion && s.ServerVersion > 0)
                        {
                            Online = true; LastSync = DateTime.Now;
                            Status = "Terhubung ke server (versi " + sv + "), data sudah terbaru";
                            return false;
                        }
                        var cfg = Call("GET", "/api/config", null, tok, Math.Max(timeoutMs, 20000)) as Dictionary<string, object>;
                        BackupOnce();
                        Apply(cfg);
                        s.ServerVersion = sv;
                        try { s.Save(); } catch { }
                        Online = true; LastSync = DateTime.Now;
                        Status = "Sinkron dari server selesai (versi " + sv + ", " + Rows(cfg, "nvrs").Count + " NVR, " + Rows(cfg, "cameras").Count + " kamera)";
                        Log.Write(Status);
                        Db.RaiseChanged();
                        return true;
                    }
                    catch (ApiEx ex)
                    {
                        if (ex.Code == 401 && attempt == 0) { syncTok = null; continue; }
                        Online = false;
                        Status = ex.Unreachable ? "Server tidak terjangkau - memakai data tersimpan terakhir" : "Server menolak: " + ex.Message;
                        Log.Write("Sinkron server: " + ex.Message);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Online = false;
                        Status = "Sinkron gagal: " + ex.Message;
                        Log.Write("Sinkron server: " + ex);
                        return false;
                    }
                }
                return false;
            }
        }

        static void BackupOnce()
        {
            if (AppSettings.Current.ServerVersion > 0) return;
            try
            {
                string bak = Db.FilePath + ".sebelum-server-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                using (SQLiteConnection c = Db.Open())
                using (var cmd = new SQLiteCommand("VACUUM INTO '" + bak.Replace("'", "''") + "'", c))
                    cmd.ExecuteNonQuery();
                Log.Write("Database lokal dicadangkan sebelum memakai server: " + bak);
            }
            catch (Exception ex) { Log.Write("Cadangan database sebelum mode server gagal: " + ex.Message); }
        }

        static void Exec(SQLiteConnection c, SQLiteTransaction tx, string sql, params object[] kv)
        {
            using (var cmd = new SQLiteCommand(sql, c, tx))
            {
                for (int i = 0; i + 1 < kv.Length; i += 2) Db.P(cmd, (string)kv[i], kv[i + 1]);
                cmd.ExecuteNonQuery();
            }
        }

        static void Apply(Dictionary<string, object> cfg)
        {
            Db.Init();
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            using (SQLiteConnection c = Db.Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                var nvrs = Rows(cfg, "nvrs");
                var cams = Rows(cfg, "cameras");
                var nvrIds = new HashSet<long>();
                foreach (var n in nvrs) nvrIds.Add(Lng(n, "id"));

                // group & sequence dikelola server: dibangun ulang seluruhnya
                Exec(c, tx, "DELETE FROM seq_step");
                Exec(c, tx, "DELETE FROM seq");
                Exec(c, tx, "DELETE FROM grp_slot");
                Exec(c, tx, "DELETE FROM grp");

                // NVR yang tidak ada di server dihapus (kamera ikut terhapus)
                if (nvrIds.Count == 0) Exec(c, tx, "DELETE FROM nvr");
                else
                {
                    var ids = new List<string>();
                    foreach (long i in nvrIds) ids.Add(i.ToString(CultureInfo.InvariantCulture));
                    Exec(c, tx, "DELETE FROM nvr WHERE id NOT IN (" + string.Join(",", ids.ToArray()) + ")");
                }

                foreach (var n in nvrs)
                {
                    long id = Lng(n, "id");
                    string host = Str(n, "host");
                    int port = (int)Lng(n, "port");
                    Exec(c, tx, "DELETE FROM nvr WHERE host = @h AND port = @p AND id <> @id", "@h", host, "@p", port, "@id", id);
                    string tpl = Str(n, "rtsp_template");
                    if (tpl.Length == 0) tpl = Rtsp.DefaultTemplate;
                    Exec(c, tx, @"INSERT INTO nvr (id, name, host, port, use_https, username, password_enc, max_channels, model, location, notes,
                                  cam_user, cam_pass_enc, rtsp_template, substream_param, brand, registered_at)
                                  VALUES (@id, @name, @host, @port, @https, @user, @pass, @maxch, @model, @loc, @notes, @cu, @cp, @tpl, @sub, @brand, @now)
                                  ON CONFLICT (id) DO UPDATE SET name = excluded.name, host = excluded.host, port = excluded.port, use_https = excluded.use_https,
                                  username = excluded.username, password_enc = excluded.password_enc, max_channels = excluded.max_channels, model = excluded.model,
                                  location = excluded.location, notes = excluded.notes, cam_user = excluded.cam_user, cam_pass_enc = excluded.cam_pass_enc,
                                  rtsp_template = excluded.rtsp_template, substream_param = excluded.substream_param, brand = excluded.brand",
                        "@id", id, "@name", Str(n, "name"), "@host", host, "@port", port, "@https", Lng(n, "use_https") != 0 ? 1 : 0,
                        "@user", Str(n, "username"), "@pass", Secret.Protect(Str(n, "password")), "@maxch", (int)Math.Max(1, Lng(n, "max_channels")),
                        "@model", Str(n, "model"), "@loc", Str(n, "location"), "@notes", Str(n, "notes"),
                        "@cu", Str(n, "cam_user"), "@cp", Secret.Protect(Str(n, "cam_pass")), "@tpl", tpl, "@sub", Str(n, "substream_param"), "@brand", BrandCgi.Norm(Str(n, "brand")), "@now", now);
                }

                // kamera: yang tidak ada di server dihapus, sisanya di-update (status/hasil scan lokal dipertahankan)
                var keep = new Dictionary<long, List<int>>();
                foreach (var k in cams)
                {
                    long nid = Lng(k, "nvr_id");
                    if (!nvrIds.Contains(nid)) continue;
                    List<int> l;
                    if (!keep.TryGetValue(nid, out l)) { l = new List<int>(); keep[nid] = l; }
                    l.Add((int)Lng(k, "channel"));
                }
                foreach (long nid in nvrIds)
                {
                    List<int> l;
                    if (!keep.TryGetValue(nid, out l) || l.Count == 0) { Exec(c, tx, "DELETE FROM camera WHERE nvr_id = @n", "@n", nid); continue; }
                    var cs = new List<string>();
                    foreach (int ch in l) cs.Add(ch.ToString(CultureInfo.InvariantCulture));
                    Exec(c, tx, "DELETE FROM camera WHERE nvr_id = @n AND channel NOT IN (" + string.Join(",", cs.ToArray()) + ")", "@n", nid);
                }
                foreach (var k in cams)
                {
                    long nid = Lng(k, "nvr_id");
                    if (!nvrIds.Contains(nid)) continue;
                    Exec(c, tx, @"INSERT INTO camera (nvr_id, channel, name, ip_address, location, note, rtsp_url, rtsp_main_url, codec, width, height, scan_detail, cam_user, cam_pass_enc)
                                  VALUES (@n, @ch, @name, @ip, @loc, @note, @url, @murl, @codec, @w, @h, @det, @cu, @cp)
                                  ON CONFLICT (nvr_id, channel) DO UPDATE SET name = excluded.name, ip_address = excluded.ip_address, location = excluded.location,
                                  note = excluded.note, rtsp_url = excluded.rtsp_url, rtsp_main_url = excluded.rtsp_main_url, scan_detail = excluded.scan_detail,
                                  cam_user = excluded.cam_user, cam_pass_enc = excluded.cam_pass_enc,
                                  codec = CASE WHEN excluded.codec <> '' THEN excluded.codec ELSE camera.codec END,
                                  width = CASE WHEN excluded.width > 0 THEN excluded.width ELSE camera.width END,
                                  height = CASE WHEN excluded.height > 0 THEN excluded.height ELSE camera.height END",
                        "@n", nid, "@ch", (int)Lng(k, "channel"), "@name", Str(k, "name"), "@ip", Str(k, "ip_address"), "@loc", Str(k, "location"),
                        "@note", Str(k, "note"), "@url", Str(k, "rtsp_url"), "@murl", Str(k, "rtsp_main_url"), "@codec", Str(k, "codec"),
                        "@w", Lng(k, "width") > 0 ? (object)(int)Lng(k, "width") : null, "@h", Lng(k, "height") > 0 ? (object)(int)Lng(k, "height") : null,
                        "@det", Str(k, "scan_detail"), "@cu", Str(k, "cam_user"), "@cp", Secret.Protect(Str(k, "cam_pass")));
                }

                // group
                var groupIds = new HashSet<long>();
                foreach (var g in Rows(cfg, "groups"))
                {
                    long gid = Lng(g, "id");
                    groupIds.Add(gid);
                    Exec(c, tx, "INSERT INTO grp (id, name, layout, owner, sort) VALUES (@id, @n, @l, @o, @s)",
                        "@id", gid, "@n", Str(g, "name"), "@l", Str(g, "layout"), "@o", Str(g, "owner"), "@s", (int)Lng(g, "sort"));
                    string[] parts = Str(g, "slots").Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string[] p = parts[i].Split(':');
                        long nid; int ch;
                        if (p.Length != 2 || !long.TryParse(p[0], out nid) || !int.TryParse(p[1], out ch) || !nvrIds.Contains(nid) || ch <= 0) continue;
                        Exec(c, tx, "INSERT OR IGNORE INTO grp_slot (group_id, slot_index, nvr_id, channel) VALUES (@g, @i, @n, @c)", "@g", gid, "@i", i, "@n", nid, "@c", ch);
                    }
                }
                foreach (var q in Rows(cfg, "sequences"))
                {
                    long qid = Lng(q, "id");
                    Exec(c, tx, "INSERT INTO seq (id, name, sort) VALUES (@id, @n, @s)", "@id", qid, "@n", Str(q, "name"), "@s", (int)Lng(q, "sort"));
                    string[] parts = Str(q, "steps").Split(',');
                    int idx = 0;
                    foreach (string part in parts)
                    {
                        string[] p = part.Split(':');
                        long gid; int sec;
                        if (p.Length != 2 || !long.TryParse(p[0], out gid) || !int.TryParse(p[1], out sec) || !groupIds.Contains(gid)) continue;
                        Exec(c, tx, "INSERT INTO seq_step (seq_id, step_index, group_id, seconds) VALUES (@s, @i, @g, @sec)", "@s", qid, "@i", idx++, "@g", gid, "@sec", Math.Max(1, sec));
                    }
                }
                tx.Commit();
            }
        }

        // ---------- tulis ke server (group, sequence, user) ----------

        static string WriteToken()
        {
            return !string.IsNullOrEmpty(UserToken) ? UserToken : SyncToken();
        }

        static object Write(string method, string path, object body)
        {
            try
            {
                return Call(method, path, body, WriteToken(), 15000);
            }
            catch (ApiEx ex)
            {
                string m = ex.Code == 403 ? "Akun ini bukan admin server, perubahan ditolak." : ex.Message;
                Log.Write("Simpan ke server: " + m);
                MessageBox.Show("Gagal menyimpan ke server: " + m, "Server konfigurasi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        static long Saved(object r) { var d = r as Dictionary<string, object>; return d == null ? 0 : Lng(d, "id"); }

        public static long SaveGroup(GroupRec g)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < g.Slots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                CamRef r = g.Slots[i];
                if (r != null && r.NvrId > 0 && r.Channel > 0) sb.Append(r.NvrId).Append(':').Append(r.Channel);
            }
            var body = new Dictionary<string, object> { { "name", g.Name }, { "layout", g.Layout }, { "slots", sb.ToString() }, { "owner", g.Owner ?? "" } };
            if (g.Id > 0) body["id"] = g.Id;
            long id = Saved(Write("POST", "/api/groups", body));
            if (id > 0) { g.Id = id; SyncNow(true, 15000); }
            return g.Id;
        }

        public static void DeleteGroup(long id) { if (Write("DELETE", "/api/groups/" + id, null) != null) SyncNow(true, 15000); }

        public static long SaveSequence(SeqRec s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Steps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(s.Steps[i].GroupId).Append(':').Append(Math.Max(1, s.Steps[i].Seconds));
            }
            var body = new Dictionary<string, object> { { "name", s.Name }, { "steps", sb.ToString() } };
            if (s.Id > 0) body["id"] = s.Id;
            long id = Saved(Write("POST", "/api/sequences", body));
            if (id > 0) { s.Id = id; SyncNow(true, 15000); }
            return s.Id;
        }

        public static void DeleteSequence(long id) { if (Write("DELETE", "/api/sequences/" + id, null) != null) SyncNow(true, 15000); }

        public static void Reorder(string route, List<long> ids)
        {
            for (int i = 0; i < ids.Count; i++)
                Write("POST", "/api/" + route, new Dictionary<string, object> { { "id", ids[i] }, { "sort", i } });
            SyncNow(true, 15000);
        }

        public static List<UserRec> ListUsers()
        {
            try
            {
                var arr = Call("GET", "/api/users", null, WriteToken(), 10000) as object[];
                var l = new List<UserRec>();
                if (arr != null)
                    foreach (object o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        bool adm = Lng(d, "is_admin") == 1;
                        l.Add(new UserRec { Id = Lng(d, "id"), Name = Str(d, "name"), Level = adm ? 0 : (int)Lng(d, "level"), IsAdmin = adm, UseGroup = Lng(d, "use_group") == 1 });
                    }
                return l;
            }
            catch (ApiEx) { return null; }     // null = pakai cache lokal
        }

        public static void SaveUser(UserRec u, string pass)
        {
            var body = new Dictionary<string, object> { { "name", u.Name }, { "level", u.Level }, { "is_admin", u.IsAdmin ? 1 : 0 }, { "use_group", u.UseGroup ? 1 : 0 } };
            if (u.Id > 0) body["id"] = u.Id;
            if (pass != null) body["password"] = pass;
            long id = Saved(Write("POST", "/api/users", body));
            if (id > 0) u.Id = id;
        }

        public static void DeleteUser(long id) { Write("DELETE", "/api/users/" + id, null); }

        /// <summary>Tes koneksi + login akun sinkron, lalu tarik konfigurasi (dipakai tombol di Setup).</summary>
        public static string TestAndSync()
        {
            syncTok = null;
            SyncNow(true, 10000);
            return Status;
        }
    }
}
