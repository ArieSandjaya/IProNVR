using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
#if MONOTEST
using Conn = Mono.Data.Sqlite.SqliteConnection;
using Cmd = Mono.Data.Sqlite.SqliteCommand;
#else
using Conn = System.Data.SQLite.SQLiteConnection;
using Cmd = System.Data.SQLite.SQLiteCommand;
#endif

namespace IproNvrServer
{
    /// <summary>Definisi kolom tabel yang boleh dibaca / ditulis lewat API.</summary>
    public class Col
    {
        public string Name;
        public char Kind;        // T teks, I bilangan bulat, R desimal
        public bool Secret;      // disimpan terenkripsi di database, dikirim apa adanya ke client yang login
        public object Default;
        public Col(string n, char k, object def, bool secret = false) { Name = n; Kind = k; Default = def; Secret = secret; }
    }

    public class TableDef
    {
        public string Route;     // nama di URL /api/{route}
        public string Table;     // nama tabel SQLite
        public Col[] Cols;
        public string[] NatKey;  // kolom kunci alami (mencegah duplikat: pendaftaran ulang = update)
        public string Order;
        public bool AdminOnlyRead;
    }

    public static class Schema
    {
        public const string Sql = @"
CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS nvr (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL DEFAULT '', host TEXT NOT NULL, port INTEGER NOT NULL DEFAULT 80,
    use_https INTEGER NOT NULL DEFAULT 0, username TEXT NOT NULL DEFAULT '', password TEXT NOT NULL DEFAULT '',
    max_channels INTEGER NOT NULL DEFAULT 16, model TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '',
    notes TEXT NOT NULL DEFAULT '', cam_user TEXT NOT NULL DEFAULT '', cam_pass TEXT NOT NULL DEFAULT '',
    rtsp_template TEXT NOT NULL DEFAULT '', substream_param TEXT NOT NULL DEFAULT '',
    UNIQUE (host, port)
);
CREATE TABLE IF NOT EXISTS camera (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    nvr_id INTEGER NOT NULL REFERENCES nvr(id) ON DELETE CASCADE,
    channel INTEGER NOT NULL, name TEXT NOT NULL DEFAULT '', ip_address TEXT NOT NULL DEFAULT '',
    location TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '', rtsp_url TEXT NOT NULL DEFAULT '',
    codec TEXT NOT NULL DEFAULT '', width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0,
    scan_detail TEXT NOT NULL DEFAULT '', cam_user TEXT NOT NULL DEFAULT '', cam_pass TEXT NOT NULL DEFAULT '',
    reg_status INTEGER NOT NULL DEFAULT 0,
    UNIQUE (nvr_id, channel)
);
CREATE TABLE IF NOT EXISTS app_user (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE, pass_hash TEXT NOT NULL DEFAULT '', salt TEXT NOT NULL DEFAULT '',
    level INTEGER NOT NULL DEFAULT 5, is_admin INTEGER NOT NULL DEFAULT 0, use_group INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS grp (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE, layout TEXT NOT NULL DEFAULT '4',
    slots TEXT NOT NULL DEFAULT '', owner TEXT NOT NULL DEFAULT '', sort INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS seq (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE, steps TEXT NOT NULL DEFAULT '', sort INTEGER NOT NULL DEFAULT 0
);
";

        // slots grup: ""nvr:ch,nvr:ch,,..."" (kosong = area kosong); steps sequence: ""groupId:detik,groupId:detik""
        public static readonly TableDef[] Tables = new TableDef[]
        {
            new TableDef { Route = "nvrs", Table = "nvr", Order = "name, id", NatKey = new[] { "host", "port" }, Cols = new Col[] {
                new Col("name",'T',""), new Col("host",'T',""), new Col("port",'I',80), new Col("use_https",'I',0),
                new Col("username",'T',""), new Col("password",'T',"",true), new Col("max_channels",'I',16),
                new Col("model",'T',""), new Col("location",'T',""), new Col("notes",'T',""),
                new Col("cam_user",'T',""), new Col("cam_pass",'T',"",true),
                new Col("rtsp_template",'T',""), new Col("substream_param",'T',"") } },
            new TableDef { Route = "cameras", Table = "camera", Order = "nvr_id, channel", NatKey = new[] { "nvr_id", "channel" }, Cols = new Col[] {
                new Col("nvr_id",'I',0), new Col("channel",'I',0), new Col("name",'T',""), new Col("ip_address",'T',""),
                new Col("location",'T',""), new Col("note",'T',""), new Col("rtsp_url",'T',""),
                new Col("codec",'T',""), new Col("width",'I',0), new Col("height",'I',0), new Col("scan_detail",'T',""),
                new Col("cam_user",'T',""), new Col("cam_pass",'T',"",true), new Col("reg_status",'I',0) } },
            new TableDef { Route = "groups", Table = "grp", Order = "sort, name", NatKey = new[] { "name" }, Cols = new Col[] {
                new Col("name",'T',""), new Col("layout",'T',"4"), new Col("slots",'T',""), new Col("owner",'T',""), new Col("sort",'I',0) } },
            new TableDef { Route = "sequences", Table = "seq", Order = "sort, name", NatKey = new[] { "name" }, Cols = new Col[] {
                new Col("name",'T',""), new Col("steps",'T',""), new Col("sort",'I',0) } },
            new TableDef { Route = "users", Table = "app_user", Order = "is_admin DESC, name", NatKey = new[] { "name" }, AdminOnlyRead = true, Cols = new Col[] {
                new Col("name",'T',""), new Col("level",'I',5), new Col("is_admin",'I',0), new Col("use_group",'I',1) } },
        };

        public static TableDef ByRoute(string r)
        {
            foreach (TableDef t in Tables) if (t.Route == r) return t;
            return null;
        }
    }

    public class Row : Dictionary<string, object> { }

    /// <summary>Penyimpanan SQLite server: CRUD generik, enkripsi rahasia, nomor versi konfigurasi.</summary>
    public class Store
    {
        readonly string connStr;
        readonly object writeLock = new object();
        readonly byte[] key;

        public Store(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string db = Path.Combine(dataDir, "server.db");
#if MONOTEST
            connStr = "Data Source=" + db + ";Version=3;";
#else
            connStr = "Data Source=" + db + ";Version=3;Pooling=True;";
#endif
            key = LoadKey(Path.Combine(dataDir, "server.key"));
            using (Conn c = Open())
            {
                Exec(c, Schema.Sql);
                // database lama: tambahkan kolom yang baru
                try { Exec(c, "ALTER TABLE camera ADD COLUMN scan_detail TEXT NOT NULL DEFAULT ''"); } catch { }
                try { Exec(c, "ALTER TABLE camera ADD COLUMN cam_user TEXT NOT NULL DEFAULT ''"); } catch { }
                try { Exec(c, "ALTER TABLE camera ADD COLUMN cam_pass TEXT NOT NULL DEFAULT ''"); } catch { }
                try { Exec(c, "ALTER TABLE camera ADD COLUMN reg_status INTEGER NOT NULL DEFAULT 0"); } catch { }
                Exec(c, "INSERT OR IGNORE INTO meta (k, v) VALUES ('version', '1')");
            }
        }

        // ---------- dasar ----------

        Conn Open()
        {
            var c = new Conn(connStr);
            c.Open();
            Exec(c, "PRAGMA foreign_keys = ON");
            return c;
        }

        static void Exec(Conn c, string sql, params object[] kv)
        {
            using (var cmd = new Cmd(sql, c))
            {
                for (int i = 0; i + 1 < kv.Length; i += 2) cmd.Parameters.AddWithValue((string)kv[i], kv[i + 1] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        static List<Row> Query(Conn c, string sql, params object[] kv)
        {
            var list = new List<Row>();
            using (var cmd = new Cmd(sql, c))
            {
                for (int i = 0; i + 1 < kv.Length; i += 2) cmd.Parameters.AddWithValue((string)kv[i], kv[i + 1] ?? DBNull.Value);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var row = new Row();
                        for (int i = 0; i < r.FieldCount; i++) { object v = r.GetValue(i); row[r.GetName(i)] = v is DBNull ? null : v; }
                        list.Add(row);
                    }
            }
            return list;
        }

        static long LastId(Conn c)
        {
            return Convert.ToInt64(Query(c, "SELECT last_insert_rowid() AS i")[0]["i"]);
        }

        // ---------- rahasia (AES-256-CBC, kunci di server.key) ----------

        static byte[] LoadKey(string path)
        {
            if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());
            var k = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(k);
            File.WriteAllText(path, Convert.ToBase64String(k));
            return k;
        }

        string Enc(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] ct;
                using (var t = aes.CreateEncryptor()) ct = t.TransformFinalBlock(data, 0, data.Length);
                var all = new byte[16 + ct.Length];
                Buffer.BlockCopy(aes.IV, 0, all, 0, 16);
                Buffer.BlockCopy(ct, 0, all, 16, ct.Length);
                return "e1:" + Convert.ToBase64String(all);
            }
        }

        string Dec(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (!s.StartsWith("e1:", StringComparison.Ordinal)) return s;
            try
            {
                byte[] all = Convert.FromBase64String(s.Substring(3));
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    var iv = new byte[16];
                    Buffer.BlockCopy(all, 0, iv, 0, 16);
                    aes.IV = iv;
                    using (var t = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(t.TransformFinalBlock(all, 16, all.Length - 16));
                }
            }
            catch { return ""; }
        }

        // ---------- versi ----------

        public long Version
        {
            get { using (Conn c = Open()) return Convert.ToInt64(Query(c, "SELECT v FROM meta WHERE k = 'version'")[0]["v"]); }
        }

        static void Bump(Conn c) { Exec(c, "UPDATE meta SET v = CAST(v AS INTEGER) + 1 WHERE k = 'version'"); }

        // ---------- CRUD generik ----------

        public List<Row> List(TableDef t)
        {
            using (Conn c = Open())
            {
                var res = new List<Row>();
                foreach (Row r in Query(c, "SELECT * FROM " + t.Table + " ORDER BY " + t.Order))
                {
                    var o = new Row();
                    o["id"] = Convert.ToInt64(r["id"]);
                    foreach (Col col in t.Cols)
                    {
                        object v = r[col.Name];
                        if (col.Kind == 'T') { string s = v == null ? "" : Convert.ToString(v); o[col.Name] = col.Secret ? Dec(s) : s; }
                        else if (col.Kind == 'I') o[col.Name] = v == null ? 0L : Convert.ToInt64(v);
                        else o[col.Name] = v == null ? 0.0 : Convert.ToDouble(v);
                    }
                    res.Add(o);
                }
                return res;
            }
        }

        static object Coerce(Col col, object v)
        {
            if (v == null) v = col.Default;
            if (col.Kind == 'I')
            {
                if (v is bool) return (bool)v ? 1L : 0L;
                return Convert.ToInt64(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (col.Kind == 'R') return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ToString(v) ?? "";
        }

        /// <summary>Simpan (id &gt; 0 atau kunci alami sudah ada = update; selain itu insert). Mengembalikan id.
        /// Untuk tabel users, field "password" (opsional) diubah menjadi hash.</summary>
        public long Upsert(TableDef t, Dictionary<string, object> body)
        {
            lock (writeLock)
            using (Conn c = Open())
            {
                Exec(c, "BEGIN");
                try
                {
                    long id = 0;
                    object oid;
                    if (body.TryGetValue("id", out oid) && oid != null) id = Convert.ToInt64(Convert.ToDouble(oid, System.Globalization.CultureInfo.InvariantCulture));
                    // kunci alami (host+port, nvr_id+channel, nama): pendaftaran ulang = update, bukan duplikat
                    if (id == 0 && t.NatKey != null)
                    {
                        string w = "";
                        var kv = new List<object>();
                        foreach (string k in t.NatKey)
                        {
                            Col kc = null;
                            foreach (Col cc in t.Cols) if (cc.Name == k) kc = cc;
                            object v;
                            if (kc == null || !body.TryGetValue(k, out v)) { w = null; break; }
                            w += (w.Length > 0 ? " AND " : "") + k + " = @" + k;
                            kv.Add("@" + k); kv.Add(Coerce(kc, v));
                        }
                        if (w != null)
                        {
                            List<Row> ex = Query(c, "SELECT id FROM " + t.Table + " WHERE " + w, kv.ToArray());
                            if (ex.Count > 0) id = Convert.ToInt64(ex[0]["id"]);
                        }
                    }
                    var vals = new Dictionary<string, object>();
                    foreach (Col col in t.Cols)
                    {
                        object v;
                        bool has = body.TryGetValue(col.Name, out v);
                        if (!has && id > 0) continue;               // update parsial: kolom yang tidak dikirim tidak diubah
                        object cv = Coerce(col, has ? v : null);
                        if (col.Secret) cv = Enc((string)cv);
                        vals[col.Name] = cv;
                    }
                    if (id > 0)
                    {
                        if (vals.Count > 0)
                        {
                            string set = "";
                            var kv = new List<object>();
                            foreach (KeyValuePair<string, object> p in vals) { set += (set.Length > 0 ? ", " : "") + p.Key + " = @" + p.Key; kv.Add("@" + p.Key); kv.Add(p.Value); }
                            kv.Add("@id"); kv.Add(id);
                            Exec(c, "UPDATE " + t.Table + " SET " + set + " WHERE id = @id", kv.ToArray());
                        }
                    }
                    else
                    {
                        string cols = "", ps = "";
                        var kv = new List<object>();
                        foreach (KeyValuePair<string, object> p in vals) { cols += (cols.Length > 0 ? ", " : "") + p.Key; ps += (ps.Length > 0 ? ", " : "") + "@" + p.Key; kv.Add("@" + p.Key); kv.Add(p.Value); }
                        Exec(c, "INSERT INTO " + t.Table + " (" + cols + ") VALUES (" + ps + ")", kv.ToArray());
                        id = LastId(c);
                    }
                    object pw;
                    if (t.Table == "app_user" && body.TryGetValue("password", out pw) && pw != null && Convert.ToString(pw).Length > 0)
                    {
                        string salt = NewSalt();
                        Exec(c, "UPDATE app_user SET pass_hash = @h, salt = @s WHERE id = @id", "@h", Hash(Convert.ToString(pw), salt), "@s", salt, "@id", id);
                    }
                    Bump(c);
                    Exec(c, "COMMIT");
                    return id;
                }
                catch { try { Exec(c, "ROLLBACK"); } catch { } throw; }
            }
        }

        public bool Delete(TableDef t, long id)
        {
            lock (writeLock)
            using (Conn c = Open())
            {
                if (t.Table == "app_user")
                {
                    // admin terakhir tidak boleh dihapus
                    List<Row> u = Query(c, "SELECT is_admin FROM app_user WHERE id = @id", "@id", id);
                    if (u.Count > 0 && Convert.ToInt64(u[0]["is_admin"]) == 1 &&
                        Convert.ToInt64(Query(c, "SELECT COUNT(*) AS n FROM app_user WHERE is_admin = 1")[0]["n"]) <= 1)
                        throw new InvalidOperationException("Admin terakhir tidak boleh dihapus.");
                }
                Exec(c, "BEGIN");
                Exec(c, "DELETE FROM " + t.Table + " WHERE id = @id", "@id", id);
                Bump(c);
                Exec(c, "COMMIT");
                return true;
            }
        }

        // ---------- user ----------

        static string Hash(string pass, string saltB64)
        {
            byte[] salt = Convert.FromBase64String(saltB64);
            using (var kdf = new Rfc2898DeriveBytes(pass ?? "", salt, 10000))
                return Convert.ToBase64String(kdf.GetBytes(32));
        }

        static string NewSalt()
        {
            var b = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return Convert.ToBase64String(b);
        }

        public bool HasAdmin()
        {
            using (Conn c = Open()) return Query(c, "SELECT id FROM app_user WHERE is_admin = 1").Count > 0;
        }

        /// <summary>Buat / atur ulang user admin (dipakai saat pertama jalan dan perintah setpass).</summary>
        public void SetAdmin(string name, string pass)
        {
            var body = new Dictionary<string, object>();
            body["name"] = name; body["is_admin"] = 1L; body["level"] = 1L; body["password"] = pass;
            Upsert(Schema.ByRoute("users"), body);
        }

        public Row Login(string name, string pass)
        {
            using (Conn c = Open())
            {
                List<Row> rows = Query(c, "SELECT * FROM app_user WHERE name = @n", "@n", name ?? "");
                if (rows.Count == 0) { Hash(pass, Convert.ToBase64String(new byte[16])); return null; }   // waktu respons relatif sama
                Row r = rows[0];
                string h = Hash(pass, Convert.ToString(r["salt"]));
                if (!ConstEq(h, Convert.ToString(r["pass_hash"]))) return null;
                var u = new Row();
                u["id"] = Convert.ToInt64(r["id"]);
                u["name"] = Convert.ToString(r["name"]);
                u["level"] = Convert.ToInt64(r["level"]);
                u["is_admin"] = Convert.ToInt64(r["is_admin"]);
                u["use_group"] = Convert.ToInt64(r["use_group"]);
                return u;
            }
        }

        static bool ConstEq(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }
}
