using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IproNvrTool
{
    /// <summary>Enkripsi DPAPI (hanya bisa dibuka oleh user Windows yang sama di PC yang sama).
    /// Format sama dengan AppSettings.Password.</summary>
    public static class Secret
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }
    }

    public class NvrRecord
    {
        public long Id;
        public string Name = "";
        public string Host = "";
        public int Port = 80;
        public bool UseHttps;
        public string Username = "";
        public string PasswordEnc = "";
        public int MaxChannels = 16;
        public string Model = "";
        public string Location = "";
        public string Notes = "";
        public string Status = "";          // OK / GAGAL / (kosong = belum dites)
        public string LastError = "";
        public string RegisteredAt = "";
        public string LastCheckedAt = "";
        public string LastScanAt = "";
        public string LastScanSummary = "";
        public string CamInfoRaw = "";      // respons mentah as_getinfo.cgi?FILE=2 terakhir
        public string Brand = "ipro";      // merek/driver NVR: ipro | hikvision | dahua (lihat NvrDrivers)
        public string CamUser = "";         // user kamera untuk RTSP langsung (Multiview)
        public string CamPassEnc = "";      // password kamera, terenkripsi DPAPI
        public string RtspTemplate = Rtsp.DefaultTemplate; // URL RTSP substream; {user} {pass} {ip}
        public string SubstreamParam = "";  // parameter tambahan push.cgi NVR untuk substream (kosong = NVR tidak dipakai untuk substream)
        public int CameraTotal;             // dihitung saat query
        public int CameraOk;                // terdaftar & normal
        public int CameraErr;               // terdaftar & error koneksi

        public string Password
        {
            get { return Secret.Unprotect(PasswordEnc); }
            set { PasswordEnc = Secret.Protect(value); }
        }

        public string CamPassword
        {
            get { return Secret.Unprotect(CamPassEnc); }
            set { CamPassEnc = Secret.Protect(value); }
        }

        public NvrEndpoint ToEndpoint()
        {
            var e = new NvrEndpoint();
            e.Host = Host;
            e.Port = Port;
            e.UseHttps = UseHttps;
            e.Username = Username;
            e.Password = Password;
            return e;
        }
    }

    public class CameraRecord
    {
        public long Id;
        public long NvrId;
        public int Channel;
        public string Name = "";
        public string IpAddress = "";
        public string Location = "";
        public string Note = "";
        public int RegStatus;               // 0 tidak terdaftar, 1 terdaftar & normal, 2 terdaftar & error koneksi
        public string Codec = "";
        public int? Width;
        public int? Height;
        public double? Fps;
        public string LastSeenAt = "";      // terakhir terlihat normal
        public string ScannedAt = "";
        public string ScanDetail = "";      // hasil deteksi stream (opsional)
        public string RtspUrl = "";         // URL RTSP substream khusus kamera ini (kosong = pakai template NVR + IP kamera)
        public string CamUser = "";         // login khusus kamera ini (kosong = pakai login kamera di data NVR)
        public string CamPassEnc = "";
        public string CamPassword { get { return Secret.Unprotect(CamPassEnc); } }
    }

    /// <summary>Satu baris CAM_CONNECT_nnCH=s dari as_getinfo.cgi?FILE=2.</summary>
    public class CamStatus
    {
        public int Channel;
        public int Status;
    }

    public class DiskRecord
    {
        public string Disk = "";
        public int? CapacityGb;
        public int? RemainGb;
        public bool Recording;
        public string Oldest = "";
        public string Newest = "";
    }

    /// <summary>Hasil deteksi stream (opsional) untuk satu kanal.</summary>
    public class ScanResult
    {
        public int Channel;
        public bool Available;
        public string Codec = "";
        public int? Width;
        public int? Height;
        public double? Fps;
        public string Detail = "";
    }

    /// <summary>Satu slot multiview: kamera (NVR + kanal) pada kotak ke-Index (0 = kiri atas, urut baris).</summary>
    public class MultiviewSlotRecord
    {
        public int Index;
        public long NvrId;
        public int Channel;
        public string CamName = "";
    }

    /// <summary>Konfigurasi satu jendela multiview, termasuk posisi jendela dan status terakhir.</summary>
    public class MultiviewRecord
    {
        public long Id;
        public string Name = "";
        public int GridSize = 3;
        public int Format = 2;           // LiveFormat tersimpan: 2 H.264, 3 H.265 (nilai lama 0/1 dianggap H.264)
        public int DecodeWidth = 640;
        public int GapMs = 1000;
        public bool HasWindow;
        public int X, Y, W, H;
        public bool Maximized;
        public bool Fullscreen;
        public bool WasOpen;             // jendela terbuka saat aplikasi terakhir ditutup -> dibuka lagi saat aplikasi start
        public bool WasRunning;          // stream sedang jalan -> dijalankan lagi
        public List<MultiviewSlotRecord> Slots = new List<MultiviewSlotRecord>();
    }

    /// <summary>Semua SQL di satu tempat (diuji terpisah terhadap SQLite).</summary>
    public static class Sql
    {
        public const string Schema = @"
PRAGMA user_version = 1;

CREATE TABLE IF NOT EXISTS nvr (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    name              TEXT    NOT NULL,
    host              TEXT    NOT NULL,
    port              INTEGER NOT NULL DEFAULT 80,
    use_https         INTEGER NOT NULL DEFAULT 0,
    username          TEXT    NOT NULL DEFAULT '',
    password_enc      TEXT    NOT NULL DEFAULT '',
    max_channels      INTEGER NOT NULL DEFAULT 16,
    model             TEXT    NOT NULL DEFAULT '',
    location          TEXT    NOT NULL DEFAULT '',
    notes             TEXT    NOT NULL DEFAULT '',
    status            TEXT    NOT NULL DEFAULT '',
    last_error        TEXT    NOT NULL DEFAULT '',
    registered_at     TEXT    NOT NULL,
    last_checked_at   TEXT    NOT NULL DEFAULT '',
    last_scan_at      TEXT    NOT NULL DEFAULT '',
    last_scan_summary TEXT    NOT NULL DEFAULT '',
    cam_info_raw      TEXT    NOT NULL DEFAULT '',
    cam_user          TEXT    NOT NULL DEFAULT '',
    cam_pass_enc      TEXT    NOT NULL DEFAULT '',
    rtsp_template     TEXT    NOT NULL DEFAULT 'rtsp://{user}:{pass}@{ip}:554/cam/realmonitor?channel=1&subtype=1',
    substream_param   TEXT    NOT NULL DEFAULT '',
    brand             TEXT    NOT NULL DEFAULT 'ipro',
    UNIQUE (host, port)
);

CREATE TABLE IF NOT EXISTS camera (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    nvr_id         INTEGER NOT NULL REFERENCES nvr(id) ON DELETE CASCADE,
    channel        INTEGER NOT NULL,
    name           TEXT    NOT NULL DEFAULT '',
    ip_address     TEXT    NOT NULL DEFAULT '',
    location       TEXT    NOT NULL DEFAULT '',
    note           TEXT    NOT NULL DEFAULT '',
    reg_status     INTEGER NOT NULL DEFAULT 0,
    codec          TEXT    NOT NULL DEFAULT '',
    width          INTEGER,
    height         INTEGER,
    fps            REAL,
    last_seen_at   TEXT    NOT NULL DEFAULT '',
    scanned_at     TEXT    NOT NULL DEFAULT '',
    scan_detail    TEXT    NOT NULL DEFAULT '',
    rtsp_url       TEXT    NOT NULL DEFAULT '',
    UNIQUE (nvr_id, channel)
);

CREATE TABLE IF NOT EXISTS nvr_disk (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    nvr_id      INTEGER NOT NULL REFERENCES nvr(id) ON DELETE CASCADE,
    disk        TEXT    NOT NULL,
    capacity_gb INTEGER,
    remain_gb   INTEGER,
    recording   INTEGER NOT NULL DEFAULT 0,
    oldest      TEXT    NOT NULL DEFAULT '',
    newest      TEXT    NOT NULL DEFAULT '',
    captured_at TEXT    NOT NULL,
    UNIQUE (nvr_id, disk)
);

CREATE INDEX IF NOT EXISTS ix_camera_nvr ON camera(nvr_id);

CREATE TABLE IF NOT EXISTS multiview (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    grid_size    INTEGER NOT NULL DEFAULT 3,
    format       INTEGER NOT NULL DEFAULT 2,
    decode_width INTEGER NOT NULL DEFAULT 640,
    gap_ms       INTEGER NOT NULL DEFAULT 1000,
    win_x        INTEGER,
    win_y        INTEGER,
    win_w        INTEGER,
    win_h        INTEGER,
    maximized    INTEGER NOT NULL DEFAULT 0,
    fullscreen   INTEGER NOT NULL DEFAULT 0,
    was_open     INTEGER NOT NULL DEFAULT 0,
    was_running  INTEGER NOT NULL DEFAULT 0,
    updated_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS multiview_slot (
    multiview_id INTEGER NOT NULL REFERENCES multiview(id) ON DELETE CASCADE,
    slot_index   INTEGER NOT NULL,
    nvr_id       INTEGER NOT NULL REFERENCES nvr(id) ON DELETE CASCADE,
    channel      INTEGER NOT NULL,
    camera_name  TEXT    NOT NULL DEFAULT '',
    PRIMARY KEY (multiview_id, slot_index)
);
";

        public const string ListNvr = @"
SELECT n.*,
       (SELECT COUNT(*) FROM camera c WHERE c.nvr_id = n.id) AS cam_total,
       (SELECT COUNT(*) FROM camera c WHERE c.nvr_id = n.id AND c.reg_status = 1) AS cam_ok,
       (SELECT COUNT(*) FROM camera c WHERE c.nvr_id = n.id AND c.reg_status = 2) AS cam_err
FROM nvr n
ORDER BY n.name COLLATE NOCASE, n.id";

        public const string InsertNvr = @"
INSERT INTO nvr (name, host, port, use_https, username, password_enc, max_channels, model, location, notes,
                 cam_user, cam_pass_enc, rtsp_template, substream_param, brand,
                 status, last_error, registered_at, last_checked_at)
VALUES (@name, @host, @port, @https, @user, @pass, @maxch, @model, @loc, @notes,
        @camuser, @campass, @rtsp, @substream, @brand,
        @status, @err, @reg, @checked)";

        public const string UpdateNvr = @"
UPDATE nvr SET name = @name, host = @host, port = @port, use_https = @https, username = @user,
               password_enc = @pass, max_channels = @maxch, model = @model, location = @loc, notes = @notes,
               cam_user = @camuser, cam_pass_enc = @campass, rtsp_template = @rtsp, substream_param = @substream, brand = @brand
WHERE id = @id";

        public const string SetNvrStatus = @"
UPDATE nvr SET status = @status, last_error = @err, last_checked_at = @checked WHERE id = @id";

        public const string DeleteNvr = @"DELETE FROM nvr WHERE id = @id";

        public const string ListCameras = @"
SELECT * FROM camera WHERE nvr_id = @nvr ORDER BY channel";

        /// <summary>Status dari as_getinfo (1 = normal, 2 = error koneksi). Nama/IP/lokasi manual tidak ditimpa.</summary>
        public const string UpsertCamStatus = @"
INSERT INTO camera (nvr_id, channel, reg_status, last_seen_at, scanned_at)
VALUES (@nvr, @ch, @st, @seen, @at)
ON CONFLICT (nvr_id, channel) DO UPDATE SET
    reg_status   = excluded.reg_status,
    scanned_at   = excluded.scanned_at,
    last_seen_at = CASE WHEN excluded.reg_status = 1 THEN excluded.last_seen_at ELSE camera.last_seen_at END";

        /// <summary>Kanal berstatus 0 (tidak terdaftar): hanya menandai baris yang sudah ada.</summary>
        public const string MarkUnregistered = @"
UPDATE camera SET reg_status = 0, scanned_at = @at WHERE nvr_id = @nvr AND channel = @ch";

        public const string SetCamSummary = @"
UPDATE nvr SET last_scan_at = @at, last_scan_summary = @summary, cam_info_raw = @raw WHERE id = @id";

        public const string SetCamRaw = @"UPDATE nvr SET cam_info_raw = @raw WHERE id = @id";

        public const string SaveProbe = @"
UPDATE camera SET
    codec       = CASE WHEN @codec <> '' THEN @codec ELSE codec END,
    width       = COALESCE(@w, width),
    height      = COALESCE(@h, height),
    fps         = COALESCE(@fps, fps),
    scan_detail = @detail
WHERE nvr_id = @nvr AND channel = @ch";

        public const string UpdateCameraManual = @"
UPDATE camera SET name = @name, ip_address = @ip, location = @loc, note = @note, rtsp_url = @rtsp WHERE id = @id";

        public const string AddCameraManual = @"
INSERT OR IGNORE INTO camera (nvr_id, channel, name) VALUES (@nvr, @ch, @name)";

        public const string DeleteCamera = @"DELETE FROM camera WHERE id = @id";

        public const string DeleteDisks = @"DELETE FROM nvr_disk WHERE nvr_id = @nvr";

        public const string InsertDisk = @"
INSERT OR REPLACE INTO nvr_disk (nvr_id, disk, capacity_gb, remain_gb, recording, oldest, newest, captured_at)
VALUES (@nvr, @disk, @cap, @rem, @rec, @oldest, @newest, @at)";

        public const string ListDisks = @"SELECT * FROM nvr_disk WHERE nvr_id = @nvr ORDER BY id";

        public const string UpsertMultiview = @"
INSERT INTO multiview (name, grid_size, format, decode_width, gap_ms, win_x, win_y, win_w, win_h,
                       maximized, fullscreen, was_open, was_running, updated_at)
VALUES (@name, @grid, @fmt, @dw, @gap, @x, @y, @w, @h, @max, @full, @open, @run, @at)
ON CONFLICT (name) DO UPDATE SET
    grid_size = excluded.grid_size, format = excluded.format, decode_width = excluded.decode_width,
    gap_ms = excluded.gap_ms, win_x = excluded.win_x, win_y = excluded.win_y, win_w = excluded.win_w,
    win_h = excluded.win_h, maximized = excluded.maximized, fullscreen = excluded.fullscreen,
    was_open = excluded.was_open, was_running = excluded.was_running, updated_at = excluded.updated_at";

        public const string MultiviewId = @"SELECT id FROM multiview WHERE name = @name";
        public const string DeleteMultiviewSlots = @"DELETE FROM multiview_slot WHERE multiview_id = @id";
        public const string InsertMultiviewSlot = @"
INSERT OR REPLACE INTO multiview_slot (multiview_id, slot_index, nvr_id, channel, camera_name)
VALUES (@id, @idx, @nvr, @ch, @name)";
        public const string GetMultiview = @"SELECT * FROM multiview WHERE name = @name";
        public const string ListMultiviewSlots = @"SELECT * FROM multiview_slot WHERE multiview_id = @id ORDER BY slot_index";
        public const string ListMultiviewNames = @"SELECT name FROM multiview ORDER BY name COLLATE NOCASE";
        public const string ListOpenMultiviewNames = @"SELECT name FROM multiview WHERE was_open = 1 ORDER BY id";
        public const string RenameMultiview = @"UPDATE multiview SET name = @new, updated_at = @at WHERE name = @old";
        public const string DeleteMultiview = @"DELETE FROM multiview WHERE name = @name";
    }

    /// <summary>Akses SQLite (System.Data.SQLite). Setiap operasi membuka koneksi sendiri sehingga aman dipanggil dari thread mana pun.</summary>
    public static class Db
    {
        static string _path;
        static readonly object InitLock = new object();
        static bool _inited;

        public static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "nvr.db");
                }
                return _path;
            }
        }

        internal static SQLiteConnection Open()
        {
            var c = new SQLiteConnection("Data Source=\"" + FilePath + "\";Version=3;Foreign Keys=True;Default Timeout=30;");
            c.Open();
            return c;
        }

        /// <summary>Dipicu setelah data NVR/kamera berubah (registrasi, edit, hapus). Bisa datang dari thread latar.</summary>
        public static event Action DataChanged;

        internal static void RaiseChanged()
        {
            Action h = DataChanged;
            if (h != null) { try { h(); } catch { } }
        }

        /// <summary>Paksa Init() memeriksa skema lagi (dipakai setelah file database diganti oleh import).</summary>
        internal static void ResetInit()
        {
            lock (InitLock) { _inited = false; }
        }

        public static void Init()
        {
            lock (InitLock)
            {
                if (_inited) return;
                using (SQLiteConnection c = Open())
                {
                    using (var cmd = new SQLiteCommand(Sql.Schema, c))
                    {
                        cmd.ExecuteNonQuery();
                    }
                    // database lama: tambahkan kolom yang baru (CREATE TABLE IF NOT EXISTS tidak menambah kolom)
                    EnsureColumn(c, "nvr", "cam_user", "TEXT NOT NULL DEFAULT ''");
                    EnsureColumn(c, "nvr", "cam_pass_enc", "TEXT NOT NULL DEFAULT ''");
                    EnsureColumn(c, "nvr", "rtsp_template", "TEXT NOT NULL DEFAULT '" + Rtsp.DefaultTemplate + "'");
                    EnsureColumn(c, "nvr", "substream_param", "TEXT NOT NULL DEFAULT ''");
                    EnsureColumn(c, "nvr", "brand", "TEXT NOT NULL DEFAULT 'ipro'");
                    EnsureColumn(c, "camera", "rtsp_url", "TEXT NOT NULL DEFAULT ''");
                    EnsureColumn(c, "camera", "cam_user", "TEXT NOT NULL DEFAULT ''");        // login khusus kamera (mode server)
                    EnsureColumn(c, "camera", "cam_pass_enc", "TEXT NOT NULL DEFAULT ''");
                    using (var cmd = new SQLiteCommand(AsmSql.Schema, c))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                _inited = true;
            }
        }

        static void EnsureColumn(SQLiteConnection c, string table, string column, string definition)
        {
            bool found = false;
            using (var cmd = new SQLiteCommand("PRAGMA table_info(" + table + ")", c))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    if (string.Equals(Convert.ToString(r["name"], CultureInfo.InvariantCulture), column, StringComparison.OrdinalIgnoreCase)) found = true;
                }
            }
            if (found) return;
            using (var cmd = new SQLiteCommand("ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition, c))
            {
                cmd.ExecuteNonQuery();
            }
        }

        // ---------- helpers ----------

        public static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>Waktu UTC di database -> waktu lokal untuk tampilan.</summary>
        public static string ToLocal(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            DateTime dt;
            if (DateTime.TryParseExact(iso, "yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return iso;
        }

        internal static void P(SQLiteCommand cmd, string name, object value)
        {
            cmd.Parameters.AddWithValue(name, value == null ? DBNull.Value : value);
        }

        internal static string S(SQLiteDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        internal static int I(SQLiteDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        internal static int? NI(SQLiteDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return null;
            return Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        static double? ND(SQLiteDataReader r, string col)
        {
            int i = r.GetOrdinal(col);
            if (r.IsDBNull(i)) return null;
            return Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        static object Nz(int? v)
        {
            return v.HasValue ? (object)v.Value : null;
        }

        static object Nz(double? v)
        {
            return v.HasValue ? (object)v.Value : null;
        }

        // ---------- NVR ----------

        public static List<NvrRecord> ListNvr()
        {
            Init();
            var list = new List<NvrRecord>();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.ListNvr, c))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var n = new NvrRecord();
                    n.Id = Convert.ToInt64(r["id"], CultureInfo.InvariantCulture);
                    n.Name = S(r, "name");
                    n.Host = S(r, "host");
                    n.Port = I(r, "port");
                    n.UseHttps = I(r, "use_https") != 0;
                    n.Username = S(r, "username");
                    n.PasswordEnc = S(r, "password_enc");
                    n.MaxChannels = I(r, "max_channels");
                    n.Model = S(r, "model");
                    n.Location = S(r, "location");
                    n.Notes = S(r, "notes");
                    n.Status = S(r, "status");
                    n.LastError = S(r, "last_error");
                    n.RegisteredAt = S(r, "registered_at");
                    n.LastCheckedAt = S(r, "last_checked_at");
                    n.LastScanAt = S(r, "last_scan_at");
                    n.LastScanSummary = S(r, "last_scan_summary");
                    n.CamInfoRaw = S(r, "cam_info_raw");
                    n.CamUser = S(r, "cam_user");
                    n.CamPassEnc = S(r, "cam_pass_enc");
                    n.RtspTemplate = S(r, "rtsp_template");
                    n.SubstreamParam = S(r, "substream_param");
                    n.Brand = S(r, "brand"); if (n.Brand.Length == 0) n.Brand = "ipro";
                    n.CameraTotal = I(r, "cam_total");
                    n.CameraOk = I(r, "cam_ok");
                    n.CameraErr = I(r, "cam_err");
                    list.Add(n);
                }
            }
            return list;
        }

        static void BindNvr(SQLiteCommand cmd, NvrRecord n)
        {
            P(cmd, "@name", n.Name);
            P(cmd, "@host", n.Host);
            P(cmd, "@port", n.Port);
            P(cmd, "@https", n.UseHttps ? 1 : 0);
            P(cmd, "@user", n.Username);
            P(cmd, "@pass", n.PasswordEnc);
            P(cmd, "@maxch", n.MaxChannels);
            P(cmd, "@model", n.Model);
            P(cmd, "@loc", n.Location);
            P(cmd, "@notes", n.Notes);
            P(cmd, "@camuser", n.CamUser ?? "");
            P(cmd, "@campass", n.CamPassEnc ?? "");
            P(cmd, "@rtsp", n.RtspTemplate ?? "");
            P(cmd, "@substream", n.SubstreamParam ?? "");
            P(cmd, "@brand", string.IsNullOrEmpty(n.Brand) ? "ipro" : n.Brand);
        }

        public static long InsertNvr(NvrRecord n, List<DiskRecord> disks, List<CamStatus> cams, string camRaw)
        {
            long r = InsertNvrCore(n, disks, cams, camRaw);
            RaiseChanged();
            return r;
        }

        static long InsertNvrCore(NvrRecord n, List<DiskRecord> disks, List<CamStatus> cams, string camRaw)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                long id;
                using (var cmd = new SQLiteCommand(Sql.InsertNvr, c, tx))
                {
                    BindNvr(cmd, n);
                    P(cmd, "@status", n.Status);
                    P(cmd, "@err", n.LastError);
                    P(cmd, "@reg", Now());
                    P(cmd, "@checked", n.Status.Length > 0 ? Now() : "");
                    cmd.ExecuteNonQuery();
                    id = c.LastInsertRowId;
                }
                if (disks != null) WriteDisks(c, tx, id, disks);
                if (cams != null) WriteCamStatus(c, tx, id, cams, camRaw);
                tx.Commit();
                return id;
            }
        }

        public static void UpdateNvr(NvrRecord n)
        {
            UpdateNvrCore(n);
            RaiseChanged();
        }

        static void UpdateNvrCore(NvrRecord n)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.UpdateNvr, c))
            {
                BindNvr(cmd, n);
                P(cmd, "@id", n.Id);
                cmd.ExecuteNonQuery();
            }
        }

        public static void SetNvrStatus(long id, string status, string error, List<DiskRecord> disks)
        {
            SetNvrStatusCore(id, status, error, disks);
            RaiseChanged();
        }

        static void SetNvrStatusCore(long id, string status, string error, List<DiskRecord> disks)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(Sql.SetNvrStatus, c, tx))
                {
                    P(cmd, "@status", status);
                    P(cmd, "@err", error ?? "");
                    P(cmd, "@checked", Now());
                    P(cmd, "@id", id);
                    cmd.ExecuteNonQuery();
                }
                if (disks != null && disks.Count > 0) WriteDisks(c, tx, id, disks);
                tx.Commit();
            }
        }

        static void WriteDisks(SQLiteConnection c, SQLiteTransaction tx, long nvrId, List<DiskRecord> disks)
        {
            using (var del = new SQLiteCommand(Sql.DeleteDisks, c, tx))
            {
                P(del, "@nvr", nvrId);
                del.ExecuteNonQuery();
            }
            string at = Now();
            foreach (DiskRecord d in disks)
            {
                using (var cmd = new SQLiteCommand(Sql.InsertDisk, c, tx))
                {
                    P(cmd, "@nvr", nvrId);
                    P(cmd, "@disk", d.Disk);
                    P(cmd, "@cap", Nz(d.CapacityGb));
                    P(cmd, "@rem", Nz(d.RemainGb));
                    P(cmd, "@rec", d.Recording ? 1 : 0);
                    P(cmd, "@oldest", d.Oldest ?? "");
                    P(cmd, "@newest", d.Newest ?? "");
                    P(cmd, "@at", at);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static List<DiskRecord> ListDisks(long nvrId)
        {
            Init();
            var list = new List<DiskRecord>();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.ListDisks, c))
            {
                P(cmd, "@nvr", nvrId);
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var d = new DiskRecord();
                        d.Disk = S(r, "disk");
                        d.CapacityGb = NI(r, "capacity_gb");
                        d.RemainGb = NI(r, "remain_gb");
                        d.Recording = I(r, "recording") != 0;
                        d.Oldest = S(r, "oldest");
                        d.Newest = S(r, "newest");
                        list.Add(d);
                    }
                }
            }
            return list;
        }

        public static void DeleteNvr(long id)
        {
            DeleteNvrCore(id);
            RaiseChanged();
        }

        static void DeleteNvrCore(long id)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.DeleteNvr, c))
            {
                P(cmd, "@id", id);
                cmd.ExecuteNonQuery(); // kamera dan disk ikut terhapus (ON DELETE CASCADE)
            }
        }

        // ---------- kamera ----------

        public static List<CameraRecord> ListCameras(long nvrId)
        {
            Init();
            var list = new List<CameraRecord>();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.ListCameras, c))
            {
                P(cmd, "@nvr", nvrId);
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var k = new CameraRecord();
                        k.Id = Convert.ToInt64(r["id"], CultureInfo.InvariantCulture);
                        k.NvrId = nvrId;
                        k.Channel = I(r, "channel");
                        k.Name = S(r, "name");
                        k.IpAddress = S(r, "ip_address");
                        k.Location = S(r, "location");
                        k.Note = S(r, "note");
                        k.RegStatus = I(r, "reg_status");
                        k.Codec = S(r, "codec");
                        k.Width = NI(r, "width");
                        k.Height = NI(r, "height");
                        k.Fps = ND(r, "fps");
                        k.LastSeenAt = S(r, "last_seen_at");
                        k.ScannedAt = S(r, "scanned_at");
                        k.ScanDetail = S(r, "scan_detail");
                        k.RtspUrl = S(r, "rtsp_url");
                        k.CamUser = S(r, "cam_user");
                        k.CamPassEnc = S(r, "cam_pass_enc");
                        list.Add(k);
                    }
                }
            }
            return list;
        }

        public static string StatusText(int status)
        {
            switch (status)
            {
                case 0: return "Tidak terdaftar";
                case 1: return "Terdaftar & normal";
                case 2: return "Terdaftar & error koneksi";
                default: return "Status " + status;
            }
        }

        public static string Summarize(List<CamStatus> cams)
        {
            int ok = 0, err = 0;
            foreach (CamStatus s in cams)
            {
                if (s.Status == 1) ok++;
                else if (s.Status == 2) err++;
            }
            return (ok + err) + " kamera terdaftar (" + ok + " normal, " + err + " error koneksi) dari " + cams.Count + " slot";
        }

        /// <summary>Simpan daftar kamera dari as_getinfo.cgi?FILE=2 beserta respons mentahnya.
        /// Kamera terdaftar (status 1/2) di-upsert tanpa menimpa nama/IP/lokasi manual;
        /// baris yang sudah ada tetapi kini berstatus 0 ditandai "tidak terdaftar".</summary>
        public static void SaveCameraList(long nvrId, List<CamStatus> cams, string raw)
        {
            SaveCameraListCore(nvrId, cams, raw);
            RaiseChanged();
        }

        static void SaveCameraListCore(long nvrId, List<CamStatus> cams, string raw)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                WriteCamStatus(c, tx, nvrId, cams, raw);
                tx.Commit();
            }
        }

        static void WriteCamStatus(SQLiteConnection c, SQLiteTransaction tx, long nvrId, List<CamStatus> cams, string raw)
        {
            string now = Now();
            foreach (CamStatus s in cams)
            {
                if (s.Status == 1 || s.Status == 2)
                {
                    using (var cmd = new SQLiteCommand(Sql.UpsertCamStatus, c, tx))
                    {
                        P(cmd, "@nvr", nvrId);
                        P(cmd, "@ch", s.Channel);
                        P(cmd, "@st", s.Status);
                        P(cmd, "@seen", now);
                        P(cmd, "@at", now);
                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    using (var cmd = new SQLiteCommand(Sql.MarkUnregistered, c, tx))
                    {
                        P(cmd, "@nvr", nvrId);
                        P(cmd, "@ch", s.Channel);
                        P(cmd, "@at", now);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            using (var cmd = new SQLiteCommand(Sql.SetCamSummary, c, tx))
            {
                P(cmd, "@at", now);
                P(cmd, "@summary", Summarize(cams));
                P(cmd, "@raw", raw ?? "");
                P(cmd, "@id", nvrId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Simpan respons mentah saja (mis. format respons tidak dikenali) supaya bisa diperiksa lewat UI.</summary>
        public static void SaveRawOnly(long nvrId, string raw)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.SetCamRaw, c))
            {
                P(cmd, "@raw", raw ?? "");
                P(cmd, "@id", nvrId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Simpan hasil deteksi codec/resolusi/fps (opsional) untuk kanal yang sudah ada di database.</summary>
        public static void SaveProbe(long nvrId, List<ScanResult> results)
        {
            SaveProbeCore(nvrId, results);
            RaiseChanged();
        }

        static void SaveProbeCore(long nvrId, List<ScanResult> results)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                foreach (ScanResult r in results)
                {
                    using (var cmd = new SQLiteCommand(Sql.SaveProbe, c, tx))
                    {
                        P(cmd, "@nvr", nvrId);
                        P(cmd, "@ch", r.Channel);
                        P(cmd, "@codec", r.Available ? (r.Codec ?? "") : "");
                        P(cmd, "@w", Nz(r.Width));
                        P(cmd, "@h", Nz(r.Height));
                        P(cmd, "@fps", Nz(r.Fps));
                        P(cmd, "@detail", r.Detail ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        public static void UpdateCamerasManual(List<CameraRecord> cams)
        {
            UpdateCamerasManualCore(cams);
            RaiseChanged();
        }

        static void UpdateCamerasManualCore(List<CameraRecord> cams)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                foreach (CameraRecord k in cams)
                {
                    using (var cmd = new SQLiteCommand(Sql.UpdateCameraManual, c, tx))
                    {
                        P(cmd, "@name", k.Name ?? "");
                        P(cmd, "@ip", k.IpAddress ?? "");
                        P(cmd, "@loc", k.Location ?? "");
                        P(cmd, "@note", k.Note ?? "");
                        P(cmd, "@rtsp", k.RtspUrl ?? "");
                        P(cmd, "@id", k.Id);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        /// <summary>Isi kolom detail stream (scan_detail) beberapa kamera sekaligus: id kamera -> teks.</summary>
        public static void SetScanDetails(Dictionary<long, string> details)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                foreach (KeyValuePair<long, string> kv in details)
                {
                    using (var cmd = new SQLiteCommand("UPDATE camera SET scan_detail = @d WHERE id = @id", c, tx))
                    {
                        P(cmd, "@d", kv.Value ?? "");
                        P(cmd, "@id", kv.Key);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            RaiseChanged();
        }

        public class RtspRow { public long Id; public string RtspUrl; public string Detail; }

        /// <summary>Simpan hasil deteksi RTSP: URL substream (templat {user}{pass}{ip}, kosong = pakai templat NVR) dan teks detail. RtspUrl null = jangan ubah.</summary>
        public static void SetRtspResults(List<RtspRow> rows)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                foreach (RtspRow r in rows)
                {
                    string sql = r.RtspUrl == null ? "UPDATE camera SET scan_detail = @d WHERE id = @id"
                                                   : "UPDATE camera SET scan_detail = @d, rtsp_url = @u WHERE id = @id";
                    using (var cmd = new SQLiteCommand(sql, c, tx))
                    {
                        P(cmd, "@d", r.Detail ?? "");
                        if (r.RtspUrl != null) P(cmd, "@u", r.RtspUrl);
                        P(cmd, "@id", r.Id);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            RaiseChanged();
        }

        public static void AddCameraManual(long nvrId, int channel, string name)
        {
            AddCameraManualCore(nvrId, channel, name);
            RaiseChanged();
        }

        static void AddCameraManualCore(long nvrId, int channel, string name)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.AddCameraManual, c))
            {
                P(cmd, "@nvr", nvrId);
                P(cmd, "@ch", channel);
                P(cmd, "@name", name ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        public static void DeleteCamera(long id)
        {
            DeleteCameraCore(id);
            RaiseChanged();
        }

        static void DeleteCameraCore(long id)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.DeleteCamera, c))
            {
                P(cmd, "@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        // ---------- Multiview ----------

        public static void SaveMultiview(MultiviewRecord m)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (SQLiteTransaction tx = c.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(Sql.UpsertMultiview, c, tx))
                {
                    P(cmd, "@name", m.Name);
                    P(cmd, "@grid", m.GridSize);
                    P(cmd, "@fmt", m.Format);
                    P(cmd, "@dw", m.DecodeWidth);
                    P(cmd, "@gap", m.GapMs);
                    P(cmd, "@x", m.HasWindow ? (object)m.X : null);
                    P(cmd, "@y", m.HasWindow ? (object)m.Y : null);
                    P(cmd, "@w", m.HasWindow ? (object)m.W : null);
                    P(cmd, "@h", m.HasWindow ? (object)m.H : null);
                    P(cmd, "@max", m.Maximized ? 1 : 0);
                    P(cmd, "@full", m.Fullscreen ? 1 : 0);
                    P(cmd, "@open", m.WasOpen ? 1 : 0);
                    P(cmd, "@run", m.WasRunning ? 1 : 0);
                    P(cmd, "@at", Now());
                    cmd.ExecuteNonQuery();
                }
                long id;
                using (var cmd = new SQLiteCommand(Sql.MultiviewId, c, tx))
                {
                    P(cmd, "@name", m.Name);
                    id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                using (var cmd = new SQLiteCommand(Sql.DeleteMultiviewSlots, c, tx))
                {
                    P(cmd, "@id", id);
                    cmd.ExecuteNonQuery();
                }
                foreach (MultiviewSlotRecord s in m.Slots)
                {
                    using (var cmd = new SQLiteCommand(Sql.InsertMultiviewSlot, c, tx))
                    {
                        P(cmd, "@id", id);
                        P(cmd, "@idx", s.Index);
                        P(cmd, "@nvr", s.NvrId);
                        P(cmd, "@ch", s.Channel);
                        P(cmd, "@name", s.CamName ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
                m.Id = id;
            }
        }

        /// <summary>Konfigurasi multiview beserta slot-nya; null bila belum ada.</summary>
        public static MultiviewRecord GetMultiview(string name)
        {
            Init();
            using (SQLiteConnection c = Open())
            {
                MultiviewRecord m = null;
                using (var cmd = new SQLiteCommand(Sql.GetMultiview, c))
                {
                    P(cmd, "@name", name);
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            m = new MultiviewRecord();
                            m.Id = Convert.ToInt64(r["id"], CultureInfo.InvariantCulture);
                            m.Name = S(r, "name");
                            m.GridSize = I(r, "grid_size");
                            m.Format = I(r, "format");
                            m.DecodeWidth = I(r, "decode_width");
                            m.GapMs = I(r, "gap_ms");
                            int? x = NI(r, "win_x"), y = NI(r, "win_y"), w = NI(r, "win_w"), h = NI(r, "win_h");
                            if (x.HasValue && y.HasValue && w.HasValue && h.HasValue)
                            {
                                m.HasWindow = true;
                                m.X = x.Value;
                                m.Y = y.Value;
                                m.W = w.Value;
                                m.H = h.Value;
                            }
                            m.Maximized = I(r, "maximized") == 1;
                            m.Fullscreen = I(r, "fullscreen") == 1;
                            m.WasOpen = I(r, "was_open") == 1;
                            m.WasRunning = I(r, "was_running") == 1;
                        }
                    }
                }
                if (m == null) return null;
                using (var cmd = new SQLiteCommand(Sql.ListMultiviewSlots, c))
                {
                    P(cmd, "@id", m.Id);
                    using (SQLiteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var s = new MultiviewSlotRecord();
                            s.Index = I(r, "slot_index");
                            s.NvrId = Convert.ToInt64(r["nvr_id"], CultureInfo.InvariantCulture);
                            s.Channel = I(r, "channel");
                            s.CamName = S(r, "camera_name");
                            m.Slots.Add(s);
                        }
                    }
                }
                return m;
            }
        }

        static List<string> Names(string sql)
        {
            Init();
            var list = new List<string>();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(sql, c))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read()) list.Add(S(r, "name"));
            }
            return list;
        }

        public static List<string> ListMultiviewNames()
        {
            return Names(Sql.ListMultiviewNames);
        }

        /// <summary>Multiview yang jendelanya masih terbuka saat aplikasi terakhir ditutup.</summary>
        public static List<string> ListOpenMultiviewNames()
        {
            return Names(Sql.ListOpenMultiviewNames);
        }

        public static void RenameMultiview(string oldName, string newName)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.RenameMultiview, c))
            {
                P(cmd, "@old", oldName);
                P(cmd, "@new", newName);
                P(cmd, "@at", Now());
                cmd.ExecuteNonQuery();
            }
        }

        public static void DeleteMultiview(string name)
        {
            Init();
            using (SQLiteConnection c = Open())
            using (var cmd = new SQLiteCommand(Sql.DeleteMultiview, c))
            {
                P(cmd, "@name", name);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
