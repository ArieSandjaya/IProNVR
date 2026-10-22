using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IproNvrServer
{
    /// <summary>Daftar kamera yang terdaftar di NVR: GET /cgi-bin/as_getinfo.cgi?FILE=2 (digest/basic auth, tanpa UID).
    /// Baris CAM_CONNECT_nnCH=s : 0 tidak terdaftar, 1 terdaftar &amp; normal, 2 terdaftar &amp; error koneksi.</summary>
    public static class NvrList
    {
        public static string Download(string host, int port, bool https, string user, string pass, string path, int timeoutMs)
        {
            string baseUrl = (https ? "https" : "http") + "://" + host + ":" + port;
            var req = (HttpWebRequest)WebRequest.Create(new Uri(baseUrl + path));
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.AllowAutoRedirect = false;
            var cache = new CredentialCache();
            var prefix = new Uri(baseUrl + "/");
            cache.Add(prefix, "Digest", new NetworkCredential(user, pass));
            cache.Add(prefix, "Basic", new NetworkCredential(user, pass));
            req.Credentials = cache;
            if (https) ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            using (WebResponse resp = req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                st.CopyTo(ms);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>Tes login ke NVR (dlogin.cgi). Mengembalikan UID, atau melempar Exception dengan pesan yang jelas.</summary>
        public static string Login(string host, int port, bool https, string user, string pass)
        {
            string t = Download(host, port, https, user, pass, "/cgi-bin/dlogin.cgi?UID=-1", 15000);
            Match m = Regex.Match(t ?? "", @"UID=(\d+)");
            if (!m.Success) throw new Exception("NVR menjawab tetapi tidak memberi UID (cek user/password NVR).");
            return m.Groups[1].Value;
        }

        public static void Logout(string host, int port, bool https, string user, string pass, string uid)
        {
            try { Download(host, port, https, user, pass, "/cgi-bin/logout.cgi?UID=" + uid, 5000); } catch { }
        }

        public static string Explain(Exception ex)
        {
            var we = ex as WebException;
            if (we != null && we.Response is HttpWebResponse)
            {
                int code = (int)((HttpWebResponse)we.Response).StatusCode;
                if (code == 401 || code == 403) return "User/password NVR ditolak (HTTP " + code + ").";
                return "NVR menjawab HTTP " + code + ".";
            }
            if (we != null && we.Status == WebExceptionStatus.Timeout) return "Tidak ada jawaban dari NVR (timeout). Cek alamat, port, dan jaringan.";
            if (we != null && we.Status == WebExceptionStatus.ConnectFailure) return "Tidak bisa terhubung ke NVR (alamat/port salah atau NVR mati).";
            return ex.Message;
        }

        /// <summary>Kanal -> status. Kosong bila respons tidak berisi baris CAM_CONNECT.</summary>
        public static SortedDictionary<int, int> Parse(string text)
        {
            var d = new SortedDictionary<int, int>();
            foreach (Match m in Regex.Matches(text ?? "", @"CAM_CONNECT_(\d+)CH\s*=\s*(\d)"))
            {
                int ch = int.Parse(m.Groups[1].Value);
                if (!d.ContainsKey(ch)) d[ch] = int.Parse(m.Groups[2].Value);
            }
            return d;
        }
    }
}
