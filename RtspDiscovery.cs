using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace IproNvrTool
{
    public class OnvifProfile
    {
        public string Token = "", Name = "", Encoding = "", Uri = "";
        public int Width, Height;
        public int Pixels { get { return Width * Height; } }
    }

    /// <summary>Klien ONVIF minimal: GetProfiles + GetStreamUri (WS-Security UsernameToken digest) untuk mengambil URL RTSP main/sub langsung dari kamera.</summary>
    public static class Onvif
    {
        const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
        const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
        const string NsSchema = "http://www.onvif.org/ver10/schema";
        static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        static string Envelope(string user, string pass, TimeSpan offset, string body)
        {
            string header = "";
            if (!string.IsNullOrEmpty(user))
            {
                var nonce = new byte[16];
                Rng.GetBytes(nonce);
                string created = (DateTime.UtcNow + offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                byte[] cb = Encoding.UTF8.GetBytes(created), pb = Encoding.UTF8.GetBytes(pass ?? "");
                var all = new byte[nonce.Length + cb.Length + pb.Length];
                Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
                Buffer.BlockCopy(cb, 0, all, nonce.Length, cb.Length);
                Buffer.BlockCopy(pb, 0, all, nonce.Length + cb.Length, pb.Length);
                string digest;
                using (var sha = SHA1.Create()) digest = Convert.ToBase64String(sha.ComputeHash(all));
                header = "<s:Header><Security s:mustUnderstand=\"1\" xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">"
                    + "<UsernameToken><Username>" + Esc(user) + "</Username>"
                    + "<Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" + digest + "</Password>"
                    + "<Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" + Convert.ToBase64String(nonce) + "</Nonce>"
                    + "<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" + created + "</Created>"
                    + "</UsernameToken></Security></s:Header>";
            }
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" + header + "<s:Body>" + body + "</s:Body></s:Envelope>";
        }

        static string Post(string url, string xml, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/soap+xml; charset=utf-8";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            byte[] b = Encoding.UTF8.GetBytes(xml);
            using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null)
                {
                    string t = "";
                    try { using (var sr = new StreamReader(hr.GetResponseStream())) t = sr.ReadToEnd(); } catch { }
                    Match m = Regex.Match(t, @"<[^>]*Text[^>]*>([^<]+)<");
                    throw new Exception("HTTP " + (int)hr.StatusCode + (m.Success ? ": " + m.Groups[1].Value : ""));
                }
                throw;
            }
        }

        static XmlDocument Load(string xml)
        {
            var d = new XmlDocument();
            d.LoadXml(xml);
            return d;
        }

        static string Txt(XmlNode n, string local)
        {
            XmlNode x = n.SelectSingleNode(".//*[local-name()='" + local + "']");
            return x == null ? "" : x.InnerText.Trim();
        }

        /// <summary>Selisih jam kamera terhadap PC (WS-Security menolak waktu yang melenceng). Nol bila gagal.</summary>
        static TimeSpan ClockOffset(string devUrl)
        {
            try
            {
                string r = Post(devUrl, Envelope(null, null, TimeSpan.Zero, "<GetSystemDateAndTime xmlns=\"" + NsDevice + "\"/>"), 4000);
                XmlDocument d = Load(r);
                XmlNode u = d.SelectSingleNode("//*[local-name()='UTCDateTime']");
                if (u == null) return TimeSpan.Zero;
                int h = int.Parse(Txt(u, "Hour")), mi = int.Parse(Txt(u, "Minute")), se = int.Parse(Txt(u, "Second"));
                int y = int.Parse(Txt(u, "Year")), mo = int.Parse(Txt(u, "Month")), da = int.Parse(Txt(u, "Day"));
                return new DateTime(y, mo, da, h, mi, se, DateTimeKind.Utc) - DateTime.UtcNow;
            }
            catch { return TimeSpan.Zero; }
        }

        /// <summary>Profil stream + URL RTSP dari kamera. Null + error bila ONVIF tidak tersedia / login ditolak.</summary>
        public static List<OnvifProfile> GetProfiles(string ip, string user, string pass, out string error)
        {
            error = null;
            string[] devUrls = { "http://" + ip + "/onvif/device_service", "http://" + ip + ":8080/onvif/device_service" };
            string lastErr = "ONVIF tidak menjawab";
            foreach (string dev in devUrls)
            {
                try
                {
                    TimeSpan off = ClockOffset(dev);
                    string mediaUrl = null;
                    try
                    {
                        string caps = Post(dev, Envelope(user, pass, off, "<GetCapabilities xmlns=\"" + NsDevice + "\"><Category>Media</Category></GetCapabilities>"), 6000);
                        XmlNode xa = Load(caps).SelectSingleNode("//*[local-name()='Media']/*[local-name()='XAddr']");
                        if (xa != null)
                        {
                            // alamat dari kamera bisa berupa IP internal: pakai host:port yang kita pakai
                            var u = new Uri(xa.InnerText.Trim());
                            var b = new Uri(dev);
                            mediaUrl = b.Scheme + "://" + b.Authority + u.PathAndQuery;
                        }
                    }
                    catch (Exception ex) { lastErr = ex.Message; if (ex.Message.StartsWith("HTTP 401")) { error = "login ONVIF ditolak (cek user/password kamera)"; return null; } }
                    var tries = new List<string>();
                    if (mediaUrl != null) tries.Add(mediaUrl);
                    var bu = new Uri(dev);
                    tries.Add(bu.Scheme + "://" + bu.Authority + "/onvif/media_service");
                    tries.Add(bu.Scheme + "://" + bu.Authority + "/onvif/Media");
                    foreach (string media in tries)
                    {
                        try
                        {
                            string pr = Post(media, Envelope(user, pass, off, "<GetProfiles xmlns=\"" + NsMedia + "\"/>"), 8000);
                            XmlNodeList nodes = Load(pr).SelectNodes("//*[local-name()='Profiles']");
                            var list = new List<OnvifProfile>();
                            foreach (XmlNode n in nodes)
                            {
                                var p = new OnvifProfile();
                                p.Token = n.Attributes["token"] != null ? n.Attributes["token"].Value : "";
                                p.Name = Txt(n, "Name");
                                p.Encoding = Txt(n, "Encoding");
                                int w, h;
                                int.TryParse(Txt(n, "Width"), out w); int.TryParse(Txt(n, "Height"), out h);
                                p.Width = w; p.Height = h;
                                if (p.Token.Length > 0) list.Add(p);
                            }
                            if (list.Count == 0) { lastErr = "tidak ada profil stream"; continue; }
                            foreach (OnvifProfile p in list)
                            {
                                try
                                {
                                    string body = "<GetStreamUri xmlns=\"" + NsMedia + "\"><StreamSetup><Stream xmlns=\"" + NsSchema + "\">RTP-Unicast</Stream>"
                                        + "<Transport xmlns=\"" + NsSchema + "\"><Protocol>RTSP</Protocol></Transport></StreamSetup><ProfileToken>" + Esc(p.Token) + "</ProfileToken></GetStreamUri>";
                                    string su = Post(media, Envelope(user, pass, off, body), 8000);
                                    XmlNode un = Load(su).SelectSingleNode("//*[local-name()='Uri']");
                                    if (un != null) p.Uri = un.InnerText.Trim();
                                }
                                catch (Exception ex) { lastErr = ex.Message; }
                            }
                            list.RemoveAll(delegate(OnvifProfile p) { return p.Uri.Length == 0; });
                            if (list.Count > 0) return list;
                        }
                        catch (Exception ex) { lastErr = ex.Message; }
                    }
                }
                catch (Exception ex) { lastErr = ex.Message; }
            }
            error = lastErr;
            return null;
        }

        /// <summary>URL dari kamera -> templat: host diganti {ip}, kredensial {user}:{pass}; port & path dipertahankan.</summary>
        public static string ToTemplate(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u) || u.Scheme != "rtsp") return null;
            string port = u.IsDefaultPort ? "" : ":" + u.Port;
            if (u.IsDefaultPort) port = ":554";
            return "rtsp://{user}:{pass}@{ip}" + port + u.PathAndQuery;
        }
    }

    public class DiscoveryResult
    {
        public string Method = "";
        public string SubTemplate;      // templat dengan {user} {pass} {ip}
        public string MainTemplate;
        public StreamInfo Sub, Main;
        public string Note = "";

        public string Summary()
        {
            if (SubTemplate == null) return "RTSP tidak ditemukan: " + Note;
            var sb = new StringBuilder();
            if (Main != null) sb.Append("Main " + Main + " | ");
            bool same = Main != null && Sub.Width * Sub.Height >= Main.Width * Main.Height;
            sb.Append((same ? "Sub = main " : "Sub ") + Sub);
            sb.Append("  [" + Method + "]");
            return sb.ToString();
        }
    }

    /// <summary>Cari URL RTSP kamera otomatis: ONVIF (paling pasti) -> templat NVR -> daftar pola merk. Tiap kandidat diuji dengan ffmpeg; yang pertama berhasil dipakai.</summary>
    public static class RtspDiscovery
    {
        class Cand
        {
            public string Method, Sub, Main;
            public Cand(string method, string sub, string main) { Method = method; Sub = sub; Main = main; }
        }

        static List<Cand> Patterns()
        {
            string h = "rtsp://{user}:{pass}@{ip}:554";
            var l = new List<Cand>();
            l.Add(new Cand("pola Dahua/OEM", h + "/cam/realmonitor?channel=1&subtype=1", h + "/cam/realmonitor?channel=1&subtype=0"));
            l.Add(new Cand("pola Hikvision", h + "/Streaming/Channels/102", h + "/Streaming/Channels/101"));
            l.Add(new Cand("pola Axis (H.264)", h + "/axis-media/media.amp?videocodec=h264&resolution=640x360", h + "/axis-media/media.amp?videocodec=h264"));
            l.Add(new Cand("pola Axis lama (MPEG-4)", h + "/mpeg4/media.amp?resolution=640x480", h + "/mpeg4/media.amp"));
            l.Add(new Cand("pola Panasonic/i-PRO", h + "/MediaInput/h264/stream_2", h + "/MediaInput/h264/stream_1"));
            l.Add(new Cand("pola Panasonic/i-PRO (tunggal)", h + "/MediaInput/h264", null));
            l.Add(new Cand("pola Uniview", h + "/media/video2", h + "/media/video1"));
            l.Add(new Cand("pola generik stream1/2", h + "/stream2", h + "/stream1"));
            l.Add(new Cand("pola generik live", h + "/live/ch1", h + "/live/ch0"));
            l.Add(new Cand("pola generik /1", h + "/1", null));
            return l;
        }

        static bool PortOpen(string ip, int port, int ms)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(ip, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(ms) && c.Connected;
                    try { c.Close(); } catch { }
                    return ok;
                }
            }
            catch { return false; }
        }

        static string Url(string template, string user, string pass, string ip) { return Rtsp.Build(template, user, pass, ip); }

        public static DiscoveryResult Discover(string exe, string ip, string user, string pass, string nvrTemplate, CancellationToken ct)
        {
            var res = new DiscoveryResult();
            if (string.IsNullOrWhiteSpace(ip)) { res.Note = "IP kamera kosong"; return res; }
            ip = ip.Trim();
            var cands = new List<Cand>();
            string note = "";

            // 1. ONVIF
            string oerr;
            List<OnvifProfile> prof = Onvif.GetProfiles(ip, user, pass, out oerr);
            if (prof != null && prof.Count > 0)
            {
                prof.Sort(delegate(OnvifProfile a, OnvifProfile b) { return b.Pixels.CompareTo(a.Pixels); });
                OnvifProfile main = prof[0], sub = prof[prof.Count - 1];
                string st = Onvif.ToTemplate(sub.Uri), mt = Onvif.ToTemplate(main.Uri);
                if (st != null) cands.Add(new Cand("ONVIF", st, prof.Count > 1 ? mt : null));
            }
            else note = "ONVIF: " + oerr;

            // 2. templat NVR
            if (!string.IsNullOrWhiteSpace(nvrTemplate))
            {
                string main = Regex.IsMatch(nvrTemplate, "subtype=1", RegexOptions.IgnoreCase) ? Regex.Replace(nvrTemplate, "subtype=1", "subtype=0", RegexOptions.IgnoreCase) : null;
                cands.Add(new Cand("templat NVR", nvrTemplate.Trim(), main));
            }

            // 3. pola merk (hanya bila port RTSP standar terbuka)
            bool rtspOpen = PortOpen(ip, 554, 3000);
            if (rtspOpen) cands.AddRange(Patterns());
            else note += (note.Length > 0 ? "; " : "") + "port 554 tertutup/tidak terjangkau";

            foreach (Cand c in cands)
            {
                if (ct.IsCancellationRequested) { res.Note = "dibatalkan"; return res; }
                string err;
                StreamInfo si = StreamProbe.Probe(exe, Url(c.Sub, user, pass, ip), out err, 9000);
                if (si == null) { note += (note.Length > 0 ? "; " : "") + c.Method + ": " + Short(err); continue; }
                res.Method = c.Method;
                res.SubTemplate = c.Sub;
                res.Sub = si;
                if (c.Main != null)
                {
                    string e2;
                    StreamInfo mi = StreamProbe.Probe(exe, Url(c.Main, user, pass, ip), out e2, 9000);
                    if (mi != null) { res.Main = mi; res.MainTemplate = c.Main; }
                }
                return res;
            }
            res.Note = note.Length > 0 ? note : "tidak ada kandidat";
            return res;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "gagal";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
    }
}
