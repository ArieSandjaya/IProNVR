using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IproNvrTool
{
    /// <summary>Alamat + kredensial satu NVR. Sesi yang dibuat tanpa endpoint mengikuti pengaturan aplikasi (tab Koneksi);
    /// sesi dengan endpoint (registrasi / scan) memakai data NVR dari database.</summary>
    public class NvrEndpoint
    {
        public string Host = "";
        public int Port = 80;
        public bool UseHttps;
        public string Username = "";
        public string Password = "";

        public string BaseUrl
        {
            get { return (UseHttps ? "https" : "http") + "://" + Host + ":" + Port; }
        }

        public static NvrEndpoint FromSettings()
        {
            AppSettings s = AppSettings.Current;
            var e = new NvrEndpoint();
            e.Host = s.Host;
            e.Port = s.Port;
            e.UseHttps = s.UseHttps;
            e.Username = s.Username;
            e.Password = s.Password;
            return e;
        }
    }

    /// <summary>Satu sesi UID pada NVR: login (dlogin.cgi), keep alive (status.cgi tiap 30 dtk),
    /// logout, dan pembuat request HTTP/HTTPS dengan digest authentication.</summary>
    public class NvrSession
    {
        readonly string _name;
        readonly NvrEndpoint _fixedEp;   // null = ikuti pengaturan aplikasi
        NvrEndpoint _ep;                 // endpoint yang dipakai sejak login (supaya logout/keep alive ke NVR yang sama)
        string _uid;
        int _gen;
        int _kaFail;
        System.Threading.Timer _timer;
        readonly object _lock = new object();

        static NvrSession()
        {
            ServicePointManager.DefaultConnectionLimit = 512;   // 48 stream + keep alive + playback + download pada satu NVR tidak boleh antre
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return AppSettings.Current.IgnoreCertErrors; };
        }

        public NvrSession(string name)
        {
            _name = name;
        }

        public NvrSession(string name, NvrEndpoint endpoint)
        {
            _name = name;
            _fixedEp = endpoint;
        }

        public string Uid { get { return _uid; } }
        /// <summary>Naik tiap login baru; dipakai Invalidate agar satu kegagalan lama tidak membuang login yang sudah baru.</summary>
        public int Generation { get { return _gen; } }

        /// <summary>Buang UID (mis. NVR restart / UID kedaluwarsa) agar perintah berikutnya login ulang; logout UID lama dicoba di latar.</summary>
        public void Invalidate(int gen)
        {
            string uid; NvrEndpoint ep;
            lock (_lock)
            {
                if (gen != _gen || _uid == null) return;
                uid = _uid; ep = _ep;
                _uid = null; _ep = null; _liveReady = false;
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
            // sengaja TIDAK logout UID lama: kamera lain yang memakai UID ini masih streaming; UID akan kedaluwarsa sendiri di NVR
            Log.Write("Sesi [" + _name + "] dibuang (UID=" + uid + "), login ulang pada perintah berikutnya.");
        }
        public bool IsLoggedIn { get { return _uid != null; } }

        NvrEndpoint Ep
        {
            get { return _ep ?? _fixedEp ?? NvrEndpoint.FromSettings(); }
        }

        bool _liveReady;

        /// <summary>Spec (diagram hlm. 9 dan 11): hdrctl.cgi?SCREEN=16X dikirim SEKALI per UID,
        /// baru kemudian push.cgi untuk tiap kamera. Memanggilnya lagi akan me-reset stream yang sedang jalan.</summary>
        public void EnsureLiveSetup()
        {
            EnsureLogin();
            lock (_lock)
            {
                if (_liveReady) return;
                string r = DownloadText(Ep, Url("hdrctl.cgi", "SCREEN=16X&PC=AS60"), 15000);
                _liveReady = true;
                r = r.Replace("\r", " ").Replace("\n", " ").Trim();
                if (r.Length > 120) r = r.Substring(0, 120);
                Log.Write("Setup live [" + _name + "] hdrctl -> " + (r.Length == 0 ? "(kosong/OK)" : r));
            }
        }

        // ---------- request helpers ----------

        public static HttpWebRequest CreateRequest(NvrEndpoint ep, string pathAndQuery, int timeoutMs)
        {
            var uri = new Uri(ep.BaseUrl + pathAndQuery);
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = 60000;
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;
            var prefix = new Uri(ep.BaseUrl + "/");
            var cache = new CredentialCache();
            cache.Add(prefix, "Digest", new NetworkCredential(ep.Username, ep.Password));
            cache.Add(prefix, "Basic", new NetworkCredential(ep.Username, ep.Password));
            req.Credentials = cache;
            return req;
        }

        public static HttpWebRequest CreateRequest(string pathAndQuery, int timeoutMs)
        {
            return CreateRequest(NvrEndpoint.FromSettings(), pathAndQuery, timeoutMs);
        }

        public static byte[] DownloadBytes(NvrEndpoint ep, string pathAndQuery, int timeoutMs)
        {
            HttpWebRequest req = CreateRequest(ep, pathAndQuery, timeoutMs);
            using (WebResponse resp = req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                st.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static byte[] DownloadBytes(string pathAndQuery, int timeoutMs)
        {
            return DownloadBytes(NvrEndpoint.FromSettings(), pathAndQuery, timeoutMs);
        }

        public static string DownloadText(NvrEndpoint ep, string pathAndQuery, int timeoutMs)
        {
            return Encoding.UTF8.GetString(DownloadBytes(ep, pathAndQuery, timeoutMs));
        }

        public static string DownloadText(string pathAndQuery, int timeoutMs)
        {
            return DownloadText(NvrEndpoint.FromSettings(), pathAndQuery, timeoutMs);
        }

        // ---------- session ----------

        public string Url(string cgi, string extra)
        {
            string u = "/cgi-bin/" + cgi + "?UID=" + _uid;
            if (!string.IsNullOrEmpty(extra)) u += "&" + extra;
            return u;
        }

        public HttpWebRequest Request(string cgi, string extra, int timeoutMs)
        {
            EnsureLogin();
            return CreateRequest(Ep, Url(cgi, extra), timeoutMs);
        }

        public string Cmd(string cgi, string extra)
        {
            EnsureLogin();
            return DownloadText(Ep, Url(cgi, extra), 15000);
        }

        public byte[] CmdBytes(string cgi, string extra, int timeoutMs)
        {
            EnsureLogin();
            return DownloadBytes(Ep, Url(cgi, extra), timeoutMs);
        }

        public void EnsureLogin()
        {
            if (_uid != null) return;
            lock (_lock)
            {
                if (_uid == null) Login();
            }
        }

        public void Login()
        {
            NvrEndpoint ep = _fixedEp ?? NvrEndpoint.FromSettings();
            string text = DownloadText(ep, "/cgi-bin/dlogin.cgi?UID=-1", 15000);
            Match m = Regex.Match(text, @"UID=(\d+)");
            if (!m.Success) throw new Exception("UID tidak ditemukan pada respons login (cek user/password).");
            _ep = ep;
            _uid = m.Groups[1].Value;
            _gen++;
            _kaFail = 0;
            _liveReady = false;
            Log.Write("Login [" + _name + "] " + ep.Host + " UID=" + _uid);
            if (_timer != null) _timer.Dispose();
            _timer = new System.Threading.Timer(KeepAliveTick, null, 30000, 30000);
        }

        void KeepAliveTick(object state)
        {
            string uid = _uid;
            NvrEndpoint ep = _ep;
            if (uid == null || ep == null) return;
            try
            {
                DownloadText(ep, "/cgi-bin/status.cgi?UID=" + uid + "&PC=AS60", 10000);
                _kaFail = 0;
            }
            catch (Exception ex)
            {
                _kaFail++;
                Log.Write("Keep alive [" + _name + "] gagal (" + _kaFail + "): " + ex.Message);
                if (_kaFail >= 3) { _kaFail = 0; Invalidate(_gen); }
            }
        }

        public void Logout()
        {
            string uid = _uid;
            NvrEndpoint ep = _ep;
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            _uid = null;
            _ep = null;
            _liveReady = false;
            if (uid == null || ep == null) return;
            try
            {
                DownloadText(ep, "/cgi-bin/logout.cgi?UID=" + uid, 5000);
                Log.Write("Logout [" + _name + "] UID=" + uid);
            }
            catch { }
        }
    }

    public static class Sessions
    {
        // Spec: satu UID tidak boleh dipakai live + playback bersamaan, dan tidak boleh
        // dua stream kamera yang sama dalam satu UID. Maka dipisah per fungsi.
        public static readonly NvrSession Live = new NvrSession("live");
        public static readonly NvrSession Rec = new NvrSession("rekam");
        public static readonly NvrSession Play = new NvrSession("playback");
        public static readonly NvrSession Util = new NvrSession("info/download");

        public static void LogoutAll()
        {
            Live.Logout();
            Rec.Logout();
            Play.Logout();
            Util.Logout();
        }
    }
}
