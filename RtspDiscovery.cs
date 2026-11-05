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

    /// <summary>Kamera Axis: baca daftar resolusi, format video, dan stream profile lewat VAPIX (param.cgi), lalu susun URL RTSP axis-media.</summary>
    public static class AxisVapix
    {
        public class Info
        {
            public List<string> Resolutions = new List<string>();   // "1920x1080", terbesar dulu
            public List<string> Codecs = new List<string>();         // h264 / h265 / mjpeg
            public List<string> Profiles = new List<string>();       // nama stream profile yang dibuat di kamera
        }

        static string Get(string ip, string user, string pass, string query, int timeoutMs)
        {
            try
            {
                var uri = new Uri("http://" + ip + "/axis-cgi/param.cgi?action=list&" + query);
                var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(uri);
                rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs;
                rq.Proxy = null;
                var cc = new System.Net.CredentialCache();
                cc.Add(uri, "Digest", new System.Net.NetworkCredential(user ?? "", pass ?? ""));
                cc.Add(uri, "Basic", new System.Net.NetworkCredential(user ?? "", pass ?? ""));
                rq.Credentials = cc;
                using (var rs = (System.Net.HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>Nilai parameter (akhiran nama, mis. "Properties.Image.Resolution"); null bila tidak ada.</summary>
        static string Val(string text, string suffix)
        {
            foreach (string raw in text.Split('\n'))
            {
                string ln = raw.Trim();
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                string k = ln.Substring(0, eq);
                if (k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return ln.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>null bila bukan kamera Axis (atau login ditolak / port 80 tertutup).</summary>
        public static Info Detect(string ip, string user, string pass)
        {
            string t = Get(ip, user, pass, "group=Brand,Properties.Image", 4000);
            if (string.IsNullOrEmpty(t)) return null;
            string brand = Val(t, "Brand.Brand");
            if (brand == null || brand.IndexOf("axis", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var info = new Info();
            string res = Val(t, "Properties.Image.Resolution");
            if (res != null)
                foreach (string r in res.Split(','))
                {
                    string x = r.Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(x, @"^\d+x\d+$") && !info.Resolutions.Contains(x)) info.Resolutions.Add(x);
                }
            info.Resolutions.Sort(delegate(string a, string b) { return Px(b).CompareTo(Px(a)); });
            string fmt = Val(t, "Properties.Image.Format");
            if (fmt != null) foreach (string f in fmt.Split(',')) { string x = f.Trim().ToLowerInvariant(); if (x.Length > 0) info.Codecs.Add(x); }
            string sp = Get(ip, user, pass, "group=StreamProfile", 4000);
            if (!string.IsNullOrEmpty(sp))
                foreach (string raw in sp.Split('\n'))
                {
                    string ln = raw.Trim();
                    int eq = ln.IndexOf('=');
                    if (eq <= 0 || !ln.Substring(0, eq).EndsWith(".Name", StringComparison.OrdinalIgnoreCase)) continue;
                    string n = ln.Substring(eq + 1).Trim();
                    if (n.Length > 0 && !info.Profiles.Contains(n)) info.Profiles.Add(n);
                }
            return info;
        }

        static long Px(string wh)
        {
            string[] a = wh.Split('x');
            long w, h;
            return a.Length == 2 && long.TryParse(a[0], out w) && long.TryParse(a[1], out h) ? w * h : 0;
        }

        /// <summary>Susun templat RTSP: [0] = sub (terkecil dengan lebar &gt;= 320), [1] = main (terbesar), sisanya stream tengah + stream profile.</summary>
        public static List<string> Templates(Info info)
        {
            string h = "rtsp://{user}:{pass}@{ip}:554/axis-media/media.amp";
            var codecs = new List<string>();
            if (info.Codecs.Contains("h264") || info.Codecs.Count == 0) codecs.Add("h264");
            if (info.Codecs.Contains("h265")) codecs.Add("h265");
            if (codecs.Count == 0) codecs.Add("h264");
            var l = new List<string>();
            if (info.Resolutions.Count == 0)
            {
                foreach (string cd in codecs) l.Add(h + "?videocodec=" + cd);
                return AddProfiles(l, info, h);
            }
            string sub = info.Resolutions[info.Resolutions.Count - 1];
            for (int i = info.Resolutions.Count - 1; i >= 0; i--)
            {
                string w = info.Resolutions[i].Split('x')[0];
                int wi; if (int.TryParse(w, out wi) && wi >= 320) { sub = info.Resolutions[i]; break; }
            }
            string main = info.Resolutions[0];
            // urutan: sub dan main codec pertama dulu (dipakai sebagai kandidat sub/main), lalu semua resolusi lain dan codec lain
            foreach (string cd in codecs)
            {
                l.Add(h + "?videocodec=" + cd + "&resolution=" + sub);
                if (main != sub) l.Add(h + "?videocodec=" + cd + "&resolution=" + main);
            }
            foreach (string cd in codecs)
                foreach (string r in info.Resolutions)
                {
                    if (r == sub || r == main || l.Count >= 14) continue;
                    l.Add(h + "?videocodec=" + cd + "&resolution=" + r);
                }
            return AddProfiles(l, info, h);
        }

        static List<string> AddProfiles(List<string> l, Info info, string h)
        {
            foreach (string p in info.Profiles) if (l.Count < 20) l.Add(h + "?streamprofile=" + Uri.EscapeDataString(p));
            return l;
        }
    }

    public class DiscoveryResult
    {
        public string Method = "";
        public string SubTemplate;      // templat dengan {user} {pass} {ip}
        public string MainTemplate;
        public StreamInfo Sub, Main;
        public string Note = "";
        /// <summary>Semua stream yang terbaca dari kamera (terbesar dulu): templat + info.</summary>
        public List<KeyValuePair<string, StreamInfo>> All = new List<KeyValuePair<string, StreamInfo>>();

        /// <summary>Satu baris per stream: "templat<TAB>info" (disimpan di kolom rtsp_streams server).</summary>
        public string StreamsText()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, StreamInfo> kv in All)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append('\t').Append(kv.Value == null ? "" : kv.Value.ToString());
            }
            return sb.ToString();
        }

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
            public List<string> Extra = new List<string>();   // stream lain (mis. stream ketiga) ikut diuji agar yang terkecil/terbesar bisa dipilih
            public Cand(string method, string sub, string main) { Method = method; Sub = sub; Main = main; }
            public Cand Ex(string u) { Extra.Add(u); return this; }
        }

        static List<Cand> Patterns()
        {
            string h = "rtsp://{user}:{pass}@{ip}:554";
            var l = new List<Cand>();
            l.Add(new Cand("pola Dahua/OEM", h + "/cam/realmonitor?channel=1&subtype=1", h + "/cam/realmonitor?channel=1&subtype=0").Ex(h + "/cam/realmonitor?channel=1&subtype=2").Ex(h + "/cam/realmonitor?channel=1&subtype=3"));
            l.Add(new Cand("pola Hikvision", h + "/Streaming/Channels/102", h + "/Streaming/Channels/101").Ex(h + "/Streaming/Channels/103").Ex(h + "/Streaming/Channels/104"));
            l.Add(new Cand("pola Axis", h + "/axis-media/media.amp?videocodec=h264&resolution=640x360", h + "/axis-media/media.amp?videocodec=h264")
                .Ex(h + "/axis-media/media.amp?videocodec=h264&resolution=1280x720").Ex(h + "/axis-media/media.amp?videocodec=h265&resolution=640x360").Ex(h + "/axis-media/media.amp?videocodec=h265"));
            l.Add(new Cand("pola Axis lama (MPEG-4)", h + "/mpeg4/media.amp?resolution=640x480", h + "/mpeg4/media.amp"));
            l.Add(new Cand("pola Panasonic/i-PRO", h + "/MediaInput/h264/stream_2", h + "/MediaInput/h264/stream_1")
                .Ex(h + "/MediaInput/h264/stream_3").Ex(h + "/MediaInput/h264/stream_4").Ex(h + "/MediaInput/h265/stream_1").Ex(h + "/MediaInput/h265/stream_2").Ex(h + "/MediaInput/h265/stream_3").Ex(h + "/MediaInput/h265/stream_4"));
            l.Add(new Cand("pola Panasonic/i-PRO (tunggal)", h + "/MediaInput/h264", null).Ex(h + "/MediaInput/h265"));
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

            // 0. Axis (VAPIX): daftar resolusi + stream profile langsung dari kamera
            AxisVapix.Info ax = AxisVapix.Detect(ip, user, pass);
            if (ax != null)
            {
                List<string> at = AxisVapix.Templates(ax);
                if (at.Count > 0)
                {
                    var ac = new Cand("Axis VAPIX", at[0], at.Count > 1 ? at[1] : null);
                    for (int ai = 2; ai < at.Count; ai++) ac.Ex(at[ai]);
                    cands.Add(ac);
                }
            }

            // 1. ONVIF
            string oerr;
            List<OnvifProfile> prof = Onvif.GetProfiles(ip, user, pass, out oerr);
            if (prof != null && prof.Count > 0)
            {
                prof.Sort(delegate(OnvifProfile a, OnvifProfile b) { return b.Pixels.CompareTo(a.Pixels); });
                OnvifProfile main = prof[0], sub = prof[prof.Count - 1];
                string st = Onvif.ToTemplate(sub.Uri), mt = Onvif.ToTemplate(main.Uri);
                if (st != null)
                {
                    var oc = new Cand("ONVIF", st, prof.Count > 1 ? mt : null);
                    for (int pi = 1; pi < prof.Count - 1; pi++) { string xt = Onvif.ToTemplate(prof[pi].Uri); if (xt != null) oc.Ex(xt); }   // profil di antara main dan sub ikut dicatat
                    cands.Add(oc);
                }
            }
            else note = "ONVIF: " + oerr;

            // 2. templat NVR
            if (!string.IsNullOrWhiteSpace(nvrTemplate))
            {
                string t0 = nvrTemplate.Trim(), main = null;
                Cand tc;
                if (Regex.IsMatch(t0, "subtype=1", RegexOptions.IgnoreCase))
                {
                    main = Regex.Replace(t0, "subtype=1", "subtype=0", RegexOptions.IgnoreCase);
                    tc = new Cand("templat NVR", t0, main).Ex(Regex.Replace(t0, "subtype=1", "subtype=2", RegexOptions.IgnoreCase)).Ex(Regex.Replace(t0, "subtype=1", "subtype=3", RegexOptions.IgnoreCase));
                }
                else if (Regex.IsMatch(t0, @"/Streaming/Channels/(\d)02\b", RegexOptions.IgnoreCase))
                {
                    main = Regex.Replace(t0, @"/Streaming/Channels/(\d)02\b", "/Streaming/Channels/$101", RegexOptions.IgnoreCase);
                    tc = new Cand("templat NVR", t0, main).Ex(Regex.Replace(t0, @"/Streaming/Channels/(\d)02\b", "/Streaming/Channels/$103", RegexOptions.IgnoreCase));
                }
                else tc = new Cand("templat NVR", t0, null);
                cands.Add(tc);
            }

            // 3. pola merk (hanya bila port RTSP standar terbuka)
            bool rtspOpen = PortOpen(ip, 554, 3000);
            if (rtspOpen) cands.AddRange(Patterns());
            else note += (note.Length > 0 ? "; " : "") + "port 554 tertutup/tidak terjangkau";

            // Kumpulkan SEMUA stream yang terbaca (H.264 dan H.265, semua resolusi/profil), tanpa duplikat.
            // Sumber pasti (ONVIF, VAPIX, templat NVR): semuanya diuji dan digabung. Pola merk: berhenti di pola pertama yang berhasil
            // (tetapi semua variannya diuji). Sub = piksel terkecil, Main = terbesar.
            var got = new List<KeyValuePair<string, StreamInfo>>();
            var seen = new HashSet<string>();
            var methods = new List<string>();
            foreach (Cand c in cands)
            {
                if (ct.IsCancellationRequested) { res.Note = "dibatalkan"; return res; }
                bool sure = !c.Method.StartsWith("pola", StringComparison.Ordinal);
                var urls = new List<string>();
                urls.Add(c.Sub);
                if (c.Main != null) urls.Add(c.Main);
                urls.AddRange(c.Extra);
                int okCount = 0;
                string firstErr = null;
                for (int k = 0; k < urls.Count; k++)
                {
                    if (ct.IsCancellationRequested) break;
                    string full = Url(urls[k], user, pass, ip);
                    if (seen.Contains(full)) { okCount++; continue; }
                    string err;
                    StreamInfo si = StreamProbe.Probe(exe, full, out err, 9000);
                    if (si == null)
                    {
                        if (firstErr == null) firstErr = err;
                        if (k == 0 && !sure) break;     // pola merk: sub tidak ada -> pola ini bukan milik kamera ini
                        continue;
                    }
                    seen.Add(full);
                    okCount++;
                    got.Add(new KeyValuePair<string, StreamInfo>(urls[k], si));
                }
                if (okCount == 0) { note += (note.Length > 0 ? "; " : "") + c.Method + ": " + Short(firstErr); continue; }
                if (!methods.Contains(c.Method)) methods.Add(c.Method);
                if (!sure) break;   // pola merk pertama yang cocok sudah cukup
            }
            if (got.Count > 0)
            {
                res.Method = string.Join(" + ", methods.ToArray());
                Rank(got, res);
                return res;
            }
            res.Note = note.Length > 0 ? note : "tidak ada kandidat";
            return res;
        }

        /// <summary>Sub = stream berpiksel terkecil, Main = terbesar (bila ada dua atau lebih resolusi berbeda); tanpa konfigurasi manual.</summary>
        public static void Rank(List<KeyValuePair<string, StreamInfo>> got, DiscoveryResult res)
        {
            res.All = new List<KeyValuePair<string, StreamInfo>>(got);
            res.All.Sort(delegate(KeyValuePair<string, StreamInfo> a, KeyValuePair<string, StreamInfo> b)
            { return ((long)b.Value.Width * b.Value.Height).CompareTo((long)a.Value.Width * a.Value.Height); });
            KeyValuePair<string, StreamInfo> lo = got[0], hi = got[0];
            foreach (KeyValuePair<string, StreamInfo> kv in got)
            {
                long p = (long)kv.Value.Width * kv.Value.Height;
                if (p < (long)lo.Value.Width * lo.Value.Height) lo = kv;
                if (p > (long)hi.Value.Width * hi.Value.Height) hi = kv;
            }
            res.SubTemplate = lo.Key; res.Sub = lo.Value;
            bool distinct = (long)hi.Value.Width * hi.Value.Height > (long)lo.Value.Width * lo.Value.Height;
            if (distinct) { res.MainTemplate = hi.Key; res.Main = hi.Value; }
            else if (got.Count > 1) { res.MainTemplate = got[1].Key; res.Main = got[1].Value; }   // semua sama besar: tetap catat main
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "gagal";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
    }
}
