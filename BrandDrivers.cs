using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IproNvrTool
{
    /// <summary>Dasar driver NVR berbasis HTTP + RTSP lewat NVR itu sendiri (Hikvision, Dahua). Definisi endpoint: Knowledge/*.json.
    /// Semua endpoint masih "unverified" - belum diuji ke perangkat nyata.</summary>
    public abstract class HttpBrandDriver : INvrDriver
    {
        public abstract string BrandId { get; }
        public abstract string BrandName { get; }
        public abstract string DefaultCamTemplate { get; }
        public virtual NvrCaps Capabilities { get { return NvrCaps.Live | NvrCaps.Playback | NvrCaps.Download | NvrCaps.Hdd | NvrCaps.Snapshot; } }

        public abstract RegistrationResult Test(NvrEndpoint ep);
        public abstract CameraListResult ListCameras(NvrEndpoint ep);
        public abstract List<DiskRecord> ListDisks(NvrEndpoint ep);
        protected abstract string RtspPath(int channel, bool sub);
        protected abstract string PlaybackPath(int channel, DateTime startUtc, DateTime endUtc);
        protected abstract void DoDownload(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status);

        public void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            try { DoDownload(nvr, channel, startLocal, endLocal, kind, folder, job, status); }
            catch (Exception ex) { if (job.Result.Length == 0) job.Result = "Error: " + ex.Message; Log.Write("Download " + BrandName + ": " + ex.Message); }
            if (job.Cancel) job.Result = "Dibatalkan.";
            else if (job.Result.Length == 0 || (job.Files > 0 && job.Result.StartsWith("No recording")))
                job.Result = "Selesai: " + job.Files + " file, " + (job.Bytes / 1024) + " KB -> " + folder + (job.Warn.Length > 0 ? "  (" + job.Warn.Trim() + ")" : "");
            Log.Write("Download " + BrandName + " " + nvr.Name + " CH" + channel + ": " + job.Result);
            if (status != null) status(job.Result);
        }

        /// <summary>Hasil unduhan mentah -> .mp4 (tanpa re-encode bila bisa). Urutan: salin video + audio AAC, salin video tanpa audio, encode ulang H.264. true bila mp4 jadi.</summary>
        protected static bool ConvertToMp4(string ffmpeg, string src, string mp4, bool videoOnly, out string err)
        {
            err = "";
            string[] opts = videoOnly
                ? new string[] { "-c:v copy -an", "-c:v libx264 -preset veryfast -an" }
                : new string[] { "-c:v copy -c:a aac", "-c:v copy -an", "-c:v libx264 -preset veryfast -c:a aac" };
            foreach (string o in opts)
            {
                try
                {
                    try { if (File.Exists(mp4)) File.Delete(mp4); } catch { }
                    var psi = new ProcessStartInfo(ffmpeg, "-y -hide_banner -loglevel error -i \"" + src + "\" " + o + " \"" + mp4 + "\"");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true;
                    using (Process p = Process.Start(psi))
                    {
                        string e = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } err = "timeout"; continue; }
                        if (p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 1024) return true;
                        err = e.Trim();
                    }
                }
                catch (Exception ex) { err = ex.Message; }
            }
            try { if (File.Exists(mp4)) File.Delete(mp4); } catch { }
            return false;
        }

        protected static bool WantMp4(string kind) { return kind != null && kind.StartsWith("MP4", StringComparison.OrdinalIgnoreCase); }
        protected static bool VideoOnly(string kind) { return kind != null && kind.EndsWith("VO", StringComparison.OrdinalIgnoreCase); }
        public abstract byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width);

        protected static string RtspBase(NvrRecord nvr, int rtspPort)
        {
            return "rtsp://" + Uri.EscapeDataString(nvr.Username) + ":" + Uri.EscapeDataString(nvr.Password) + "@" + nvr.Host + ":" + rtspPort;
        }

        protected virtual int RtspPort { get { return 554; } }

        public NvrStreamSource LiveSource(NvrRecord nvr, int channel, bool subStream)
        {
            var s = new NvrStreamSource();
            s.Kind = NvrStreamKind.Rtsp;
            s.RtspUrl = RtspBase(nvr, RtspPort) + RtspPath(channel, subStream);
            return s;
        }

        public NvrStreamSource PlaybackSource(NvrRecord nvr, int channel, DateTime startUtc, DateTime endUtc)
        {
            var s = new NvrStreamSource();
            s.Kind = NvrStreamKind.Rtsp;
            s.RtspUrl = RtspBase(nvr, RtspPort) + PlaybackPath(channel, startUtc, endUtc);
            return s;
        }

        /// <summary>Cadangan download: ffmpeg menyalin stream RTSP playback ke .mp4 (berjalan real-time: 10 menit rekaman = sekitar 10 menit). Tanpa CGI pencarian.</summary>
        protected void RtspDownload(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string folder, DownloadJob job, Action<string> status)
        {
            string ffmpeg = FfmpegPipe.FindExe();
            if (ffmpeg == null) { job.Result = "ffmpeg.exe tidak ditemukan (dibutuhkan untuk download lewat RTSP)."; return; }
            DateTime s = startLocal.ToUniversalTime(), e = endLocal.ToUniversalTime();
            int total = (int)Math.Max(1, (e - s).TotalSeconds);
            string url = PlaybackSource(nvr, channel, s, e).RtspUrl;
            string file = Path.Combine(folder, Safe(nvr.Name + "_CH" + channel + "_" + startLocal.ToString("yyyyMMdd_HHmmss")) + ".mp4");
            var psi = new ProcessStartInfo(ffmpeg, "-y -hide_banner -loglevel error -rtsp_transport tcp -i \"" + url.Replace("\"", "%22") + "\" -t " + total + " -c:v copy -an \"" + file + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true;
            var err = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs a) { if (a.Data != null && err.Length < 1500) err.AppendLine(a.Data); };
                p.BeginErrorReadLine();
                DateTime t0 = DateTime.UtcNow;
                while (!p.WaitForExit(1000))
                {
                    double el = (DateTime.UtcNow - t0).TotalSeconds;
                    if (job.Cancel || el > total + 180) { try { p.Kill(); } catch { } break; }
                    long len = 0; try { len = new FileInfo(file).Length; } catch { }
                    job.Bytes = len;
                    if (status != null) status("Downloading via RTSP... " + (int)Math.Min(100, el * 100 / total) + "%");
                }
            }
            long size = 0; try { size = File.Exists(file) ? new FileInfo(file).Length : 0; } catch { }
            if (size > 1024) { job.Files++; job.Bytes = size; }
            else
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                if (!job.Cancel) { job.NoRecording = true; job.Result = "Tidak ada rekaman / playback RTSP ditolak. " + err.ToString().Trim(); }
            }
        }

        // ---------- helper HTTP ----------

        /// <summary>Daftar kamera lewat logika bersama (BrandCgi, dipakai juga oleh server).</summary>
        protected static CameraListResult BrandCamList(string brand, NvrEndpoint ep)
        {
            var res = new CameraListResult();
            string raw;
            SortedDictionary<int, int> d = BrandCgi.Cameras(brand, delegate(string path) { return NvrSession.DownloadText(ep, path, 20000); }, out raw);
            res.Raw = raw;
            foreach (KeyValuePair<int, int> kv in d) { var c = new CamStatus(); c.Channel = kv.Key; c.Status = kv.Value; res.Items.Add(c); }
            return res;
        }

        protected static string Get(NvrEndpoint ep, string path, int timeoutMs)
        {
            return NvrSession.DownloadText(ep, path, timeoutMs);
        }

        protected static string Post(NvrEndpoint ep, string path, string body, string contentType, int timeoutMs)
        {
            HttpWebRequest req = NvrSession.CreateRequest(ep, path, timeoutMs);
            req.Method = "POST";
            req.ContentType = contentType;
            byte[] b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (WebResponse resp = req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (var sr = new StreamReader(rs, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        protected static void SaveStream(HttpWebRequest req, string file, DownloadJob job)
        {
            job.Req = req;
            using (WebResponse resp = req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (FileStream fs = File.Create(file))
            {
                var buf = new byte[65536];
                int n;
                while (!job.Cancel && (n = st.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    job.Bytes += n;
                }
            }
        }

        protected static XmlDocument Xml(string text)
        {
            var d = new XmlDocument();
            d.LoadXml(text);
            return d;
        }

        protected static string Val(XmlNode n, string local)
        {
            XmlNode x = n.SelectSingleNode(".//*[local-name()='" + local + "']");
            return x == null ? "" : x.InnerText.Trim();
        }

        protected static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }

    // =====================================================================================================
    public class HikvisionDriver : HttpBrandDriver
    {
        public override string BrandId { get { return "hikvision"; } }
        public override string BrandName { get { return "Hikvision"; } }
        public override string DefaultCamTemplate { get { return BrandCgi.DefaultCamTemplate(BrandCgi.Hikvision); } }

        protected override string RtspPath(int ch, bool sub) { return BrandCgi.ViaRtspPath(BrandId, ch, sub); }

        protected override string PlaybackPath(int ch, DateTime s, DateTime e)
        {
            return "/Streaming/tracks/" + (ch * 100 + 1) + "?starttime=" + Iso(s) + "&endtime=" + Iso(e);
        }

        static string Iso(DateTime utc) { return utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture); }

        public override RegistrationResult Test(NvrEndpoint ep)
        {
            var res = new RegistrationResult();
            try
            {
                Get(ep, "/ISAPI/System/deviceInfo", 15000);
                res.Ok = true;
                try { res.Disks = ListDisks(ep); } catch (Exception ex) { Log.Write("Hikvision " + ep.Host + ": HDD: " + ex.Message); }
                try
                {
                    CameraListResult cl = ListCameras(ep);
                    res.CamRaw = cl.Raw;
                    if (cl.Items.Count == 0) res.CamError = "daftar kamera kosong";
                    else res.Cameras = cl.Items;
                }
                catch (Exception ex) { res.CamError = ex.Message; }
            }
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message + BrandCgi.PortHint(BrandId, ep.Port); }
            return res;
        }

        public override CameraListResult ListCameras(NvrEndpoint ep)
        {
            return BrandCamList(BrandId, ep);
        }

        public override List<DiskRecord> ListDisks(NvrEndpoint ep)
        {
            var list = new List<DiskRecord>();
            string text = Get(ep, "/ISAPI/ContentMgmt/Storage", 15000);
            foreach (XmlNode n in Xml(text).SelectNodes("//*[local-name()='hdd']"))
            {
                var d = new DiskRecord();
                d.Disk = Val(n, "hddName");
                if (d.Disk.Length == 0) d.Disk = "HDD " + Val(n, "id");
                double cap, free;
                if (double.TryParse(Val(n, "capacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out cap)) d.CapacityGb = (int)(cap / 1024);   // MB -> GB
                if (double.TryParse(Val(n, "freeSpace"), NumberStyles.Float, CultureInfo.InvariantCulture, out free)) d.RemainGb = (int)(free / 1024);
                string st = Val(n, "status");
                d.Recording = st.Equals("ok", StringComparison.OrdinalIgnoreCase) || st.Equals("normal", StringComparison.OrdinalIgnoreCase);
                list.Add(d);
            }
            return list;
        }

        class Seg { public string Uri = ""; public DateTime Start; }

        List<Seg> Search(NvrEndpoint ep, int ch, DateTime fromUtc, DateTime toUtc)
        {
            var all = new List<Seg>();
            int pos = 0;
            string sid = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
            for (int page = 0; page < 200; page++)
            {
                string body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><CMSearchDescription><searchID>" + sid + "</searchID>"
                    + "<trackIDList><trackID>" + (ch * 100 + 1) + "</trackID></trackIDList>"
                    + "<timeSpanList><timeSpan><startTime>" + fromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "</startTime>"
                    + "<endTime>" + toUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "</endTime></timeSpan></timeSpanList>"
                    + "<maxResults>100</maxResults><searchResultPostion>" + pos + "</searchResultPostion>"
                    + "<metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList></CMSearchDescription>";
                XmlDocument d = Xml(Post(ep, "/ISAPI/ContentMgmt/search", body, "application/xml", 30000));
                int got = 0;
                foreach (XmlNode m in d.SelectNodes("//*[local-name()='searchMatchItem']"))
                {
                    var s = new Seg();
                    s.Uri = Val(m, "playbackURI").Replace("&amp;", "&");
                    DateTime t;
                    if (DateTime.TryParse(Val(m, "startTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) s.Start = t;
                    if (s.Uri.Length > 0) { all.Add(s); got++; }
                }
                string more = Val(d.DocumentElement, "responseStatusStrg");
                if (got == 0 || !more.Equals("MORE", StringComparison.OrdinalIgnoreCase)) break;
                pos += got;
            }
            return all;
        }

        protected override void DoDownload(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            NvrEndpoint ep = nvr.ToEndpoint();
            try
            {
                if (status != null) status("Searching recordings...");
                List<Seg> segs = null;
                try { segs = Search(ep, channel, startLocal.ToUniversalTime(), endLocal.ToUniversalTime()); }
                catch (Exception ex0) { Log.Write("Hikvision search gagal, pakai RTSP: " + ex0.Message); }
                if (segs == null || segs.Count == 0)
                {
                    if (status != null) status("Mencoba download lewat RTSP...");
                    RtspDownload(nvr, channel, startLocal, endLocal, folder, job, status);
                    return;
                }
                int i = 0;
                foreach (Seg s in segs)
                {
                    if (job.Cancel) break;
                    i++;
                    if (status != null) status("Downloading " + i + "/" + segs.Count + "...");
                    string file = Path.Combine(folder, Safe(nvr.Name + "_CH" + channel + "_" + s.Start.ToLocalTime().ToString("yyyyMMdd_HHmmss")) + ".mp4");
                    string body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><downloadRequest version=\"1.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\"><playbackURI>"
                        + System.Security.SecurityElement.Escape(s.Uri) + "</playbackURI></downloadRequest>";
                    HttpWebRequest req = NvrSession.CreateRequest(ep, "/ISAPI/ContentMgmt/download", 30000);
                    req.Method = "POST";
                    req.ContentType = "application/xml";
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = b.Length;
                    using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
                    string ffm = WantMp4(kind) ? FfmpegPipe.FindExe() : null;
                    string raw = ffm != null ? Path.ChangeExtension(file, ".ps") : file;
                    long before = job.Bytes;
                    SaveStream(req, raw, job);
                    if (job.Bytes - before < 1024) { try { File.Delete(raw); } catch { } job.Warn += "file " + i + " kosong (ditolak perangkat). "; continue; }
                    job.Files++;
                    if (ffm != null && !job.Cancel)
                    {
                        if (status != null) status("Konversi ke mp4 " + i + "/" + segs.Count + "...");
                        string err;
                        if (ConvertToMp4(ffm, raw, file, VideoOnly(kind), out err)) { try { File.Delete(raw); } catch { } }
                        else job.Warn += "konversi mp4 gagal (file .ps disimpan): " + (err.Length > 120 ? err.Substring(0, 120) : err) + " ";
                    }
                    else if (WantMp4(kind) && i == 1) job.Warn += "ffmpeg.exe tidak ditemukan: file mentah dari perangkat, belum dikonversi. ";
                }
            }
            catch (Exception ex)
            {
                if (!job.Cancel) job.Result = ex.Message;
            }
        }

        public override byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            return NvrSession.DownloadBytes(nvr.ToEndpoint(), "/ISAPI/Streaming/channels/" + (channel * 100 + 1) + "/picture", 15000);
        }
    }

    // =====================================================================================================
    public class DahuaDriver : HttpBrandDriver
    {
        public override string BrandId { get { return "dahua"; } }
        public override string BrandName { get { return "Dahua"; } }
        public override string DefaultCamTemplate { get { return BrandCgi.DefaultCamTemplate(BrandCgi.Dahua); } }

        protected override string RtspPath(int ch, bool sub) { return BrandCgi.ViaRtspPath(BrandId, ch, sub); }

        protected override string PlaybackPath(int ch, DateTime s, DateTime e)
        {
            // waktu playback RTSP Dahua memakai jam lokal perangkat; diasumsikan sama dengan zona waktu PC
            return "/cam/playback?channel=" + ch + "&starttime=" + s.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture)
                + "&endtime=" + e.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture);
        }

        static string Ts(DateTime local) { return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        public override RegistrationResult Test(NvrEndpoint ep)
        {
            var res = new RegistrationResult();
            try
            {
                Get(ep, "/cgi-bin/magicBox.cgi?action=getSystemInfo", 15000);
                res.Ok = true;
                try { res.Disks = ListDisks(ep); } catch (Exception ex) { Log.Write("Dahua " + ep.Host + ": HDD: " + ex.Message); }
                try
                {
                    CameraListResult cl = ListCameras(ep);
                    res.CamRaw = cl.Raw;
                    if (cl.Items.Count == 0) res.CamError = "daftar kamera kosong";
                    else res.Cameras = cl.Items;
                }
                catch (Exception ex) { res.CamError = ex.Message; }
            }
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message + BrandCgi.PortHint(BrandId, ep.Port); }
            return res;
        }

        public override CameraListResult ListCameras(NvrEndpoint ep)
        {
            return BrandCamList(BrandId, ep);
        }

        public override List<DiskRecord> ListDisks(NvrEndpoint ep)
        {
            var list = new List<DiskRecord>();
            string text = Get(ep, "/cgi-bin/storageDevice.cgi?action=getDeviceAllInfo", 15000);
            var rows = new Dictionary<string, Dictionary<string, string>>();
            foreach (Match m in Regex.Matches(text, @"info\[(\d+)\]\.Detail\[(\d+)\]\.(\w+)\s*=\s*([^\r\n]*)"))
            {
                string key = m.Groups[1].Value + "." + m.Groups[2].Value;
                Dictionary<string, string> d;
                if (!rows.TryGetValue(key, out d)) { d = new Dictionary<string, string>(); rows[key] = d; }
                d[m.Groups[3].Value] = m.Groups[4].Value.Trim();
            }
            foreach (KeyValuePair<string, Dictionary<string, string>> kv in rows)
            {
                var r = new DiskRecord();
                string v;
                r.Disk = kv.Value.TryGetValue("Name", out v) ? v : "HDD " + kv.Key;
                double tot, used;
                if (kv.Value.TryGetValue("TotalBytes", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out tot))
                {
                    r.CapacityGb = (int)(tot / 1073741824.0);
                    if (kv.Value.TryGetValue("UsedBytes", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out used))
                        r.RemainGb = (int)((tot - used) / 1073741824.0);
                }
                r.Recording = !(kv.Value.TryGetValue("IsError", out v) && v.Equals("true", StringComparison.OrdinalIgnoreCase));
                list.Add(r);
            }
            return list;
        }

        static string Describe(Exception ex)
        {
            var we = ex as WebException;
            if (we != null && we.Response != null)
            {
                try
                {
                    var hr = (HttpWebResponse)we.Response;
                    string body = "";
                    using (var sr = new StreamReader(hr.GetResponseStream())) { body = sr.ReadToEnd(); }
                    body = body.Replace("\r", " ").Replace("\n", " ").Trim();
                    return "HTTP " + (int)hr.StatusCode + (body.Length > 0 ? " " + (body.Length > 100 ? body.Substring(0, 100) : body) : "");
                }
                catch { }
            }
            return ex.Message;
        }

        static string Md5Up(string t)
        {
            using (var m = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = m.ComputeHash(Encoding.UTF8.GetBytes(t));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>Login sesi RPC2 (cara web UI Dahua; sebagian firmware XVR menolak Digest untuk unduh file). Mengembalikan id sesi atau null.</summary>
        string RpcSession(NvrEndpoint ep, out string err)
        {
            err = "";
            try
            {
                var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                string r1 = Post(ep, "/RPC2_Login", "{\"method\":\"global.login\",\"params\":{\"userName\":\"" + ep.Username.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"password\":\"\",\"clientType\":\"Web3.0\"},\"id\":1}", "application/json", 15000);
                var d1 = js.DeserializeObject(r1) as Dictionary<string, object>;
                if (d1 == null || !d1.ContainsKey("session")) { err = "RPC2: tanpa sesi"; return null; }
                string sid = Convert.ToString(d1["session"]);
                var pr = d1.ContainsKey("params") ? d1["params"] as Dictionary<string, object> : null;
                string realm = pr != null && pr.ContainsKey("realm") ? Convert.ToString(pr["realm"]) : "";
                string rnd = pr != null && pr.ContainsKey("random") ? Convert.ToString(pr["random"]) : "";
                string a1 = Md5Up(ep.Username + ":" + realm + ":" + ep.Password);
                string h = Md5Up(ep.Username + ":" + rnd + ":" + a1);
                string body = "{\"method\":\"global.login\",\"session\":\"" + sid + "\",\"params\":{\"userName\":\"" + ep.Username.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"password\":\"" + h
                    + "\",\"clientType\":\"Web3.0\",\"authorityType\":\"Default\",\"passwordType\":\"Default\"},\"id\":2}";
                string r2 = Post(ep, "/RPC2_Login", body, "application/json", 15000);
                if (r2.IndexOf("\"result\":true", StringComparison.OrdinalIgnoreCase) < 0 && r2.IndexOf("\"result\": true", StringComparison.OrdinalIgnoreCase) < 0) { err = "RPC2 login ditolak: " + (r2.Length > 100 ? r2.Substring(0, 100) : r2); return null; }
                return sid;
            }
            catch (Exception ex) { err = "RPC2: " + Describe(ex); return null; }
        }

        /// <summary>Unduh satu file .dav. Urutan cara: RPC_Loadfile (Digest) -> RPC_Loadfile dengan sesi RPC2 -> loadfile.cgi per waktu. diag berisi alasan tiap cara yang gagal.</summary>
        bool FetchFile(NvrEndpoint ep, string path, string startStamp, string dav, DownloadJob job, out string diag)
        {
            diag = "";
            long before = job.Bytes;
            try
            {
                SaveStream(NvrSession.CreateRequest(ep, "/cgi-bin/RPC_Loadfile" + path, 30000), dav, job);
                if (job.Bytes - before >= 1024) return true;
                diag += "Digest: data kosong; ";
            }
            catch (Exception ex) { if (job.Cancel) return false; diag += "Digest: " + Describe(ex) + "; "; }
            job.Bytes = before;

            string err;
            string sid = RpcSession(ep, out err);
            if (sid != null)
            {
                try
                {
                    HttpWebRequest rq = NvrSession.CreateRequest(ep, "/RPC_Loadfile" + path, 30000);
                    rq.Headers["Cookie"] = "DhWebClientSessionID=" + sid;
                    SaveStream(rq, dav, job);
                    if (job.Bytes - before >= 1024) return true;
                    diag += "Sesi RPC2: data kosong; ";
                }
                catch (Exception ex) { if (job.Cancel) return false; diag += "Sesi RPC2: " + Describe(ex) + "; "; }
                job.Bytes = before;
            }
            else diag += err + "; ";
            return false;
        }

        protected override void DoDownload(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            NvrEndpoint ep = nvr.ToEndpoint();
            string obj = null;
            try
            {
                if (status != null) status("Searching recordings...");
                var files = new List<KeyValuePair<string, string>>();   // path, start
                string searchErr = null;
                try
                {
                    string r = Get(ep, "/cgi-bin/mediaFileFind.cgi?action=factory.create", 15000);
                    Match om = Regex.Match(r, @"result\s*=\s*(\d+)");
                    if (!om.Success) throw new Exception("mediaFileFind.create gagal: " + r.Trim());
                    obj = om.Groups[1].Value;
                    string fr = Get(ep, "/cgi-bin/mediaFileFind.cgi?action=findFile&object=" + obj + "&condition.Channel=" + channel
                        + "&condition.StartTime=" + Uri.EscapeDataString(Ts(startLocal)) + "&condition.EndTime=" + Uri.EscapeDataString(Ts(endLocal))
                        + "&condition.Types[0]=dav", 30000);
                    if (fr.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 && fr.IndexOf("OK", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new Exception("findFile: " + fr.Trim());
                    for (int page = 0; page < 200; page++)
                {
                    string t = Get(ep, "/cgi-bin/mediaFileFind.cgi?action=findNextFile&object=" + obj + "&count=100", 30000);
                    Match fm = Regex.Match(t, @"found\s*=\s*(\d+)");
                    int found = fm.Success ? int.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    if (found == 0) break;
                    for (int i = 0; i < found; i++)
                    {
                        Match p = Regex.Match(t, @"items\[" + i + @"\]\.FilePath\s*=\s*([^\r\n]+)");
                        Match st = Regex.Match(t, @"items\[" + i + @"\]\.StartTime\s*=\s*([^\r\n]+)");
                        if (p.Success) files.Add(new KeyValuePair<string, string>(p.Groups[1].Value.Trim(), st.Success ? st.Groups[1].Value.Trim() : ""));
                    }
                    }
                }
                catch (Exception ex0) { searchErr = ex0.Message; Log.Write("Dahua search gagal, pakai RTSP: " + ex0.Message); }
                if (files.Count == 0)
                {
                    if (status != null) status("Mencoba download lewat RTSP...");
                    RtspDownload(nvr, channel, startLocal, endLocal, folder, job, status);
                    if (job.Files == 0 && searchErr != null && job.Result.Length > 0) job.Result += " (pencarian: " + searchErr + ")";
                    return;
                }
                string ffmpeg = FfmpegPipe.FindExe();
                bool wantMp4 = WantMp4(kind);
                if (wantMp4 && ffmpeg == null) job.Warn += "ffmpeg.exe tidak ditemukan: file disimpan sebagai .dav, bukan .mp4. ";
                int n = 0;
                foreach (KeyValuePair<string, string> f in files)
                {
                    if (job.Cancel) break;
                    n++;
                    if (status != null) status("Downloading " + n + "/" + files.Count + "...");
                    string stamp = Regex.Replace(f.Value, @"[^0-9]", "");
                    string dav = Path.Combine(folder, Safe(nvr.Name + "_CH" + channel + "_" + (stamp.Length > 0 ? stamp : n.ToString())) + ".dav");
                    string path = f.Key.StartsWith("/") ? f.Key : "/" + f.Key;
                    string diag;
                    if (!FetchFile(ep, path, f.Value, dav, job, out diag))
                    {
                        try { File.Delete(dav); } catch { }
                        job.Warn += "file " + n + " ditolak DVR [" + diag + "] ";
                        Log.Write("Dahua download ditolak: " + diag);
                        continue;
                    }
                    job.Files++;
                    if (wantMp4 && ffmpeg != null && !job.Cancel)
                    {
                        if (status != null) status("Konversi ke mp4 " + n + "/" + files.Count + "...");
                        string err;
                        if (ConvertToMp4(ffmpeg, dav, Path.ChangeExtension(dav, ".mp4"), VideoOnly(kind), out err)) { try { File.Delete(dav); } catch { } }
                        else job.Warn += "konversi mp4 gagal (file .dav disimpan): " + (err.Length > 120 ? err.Substring(0, 120) : err) + " ";
                    }
                }
                if (job.Files == 0 && !job.Cancel && job.Result.Length == 0)
                {
                    // file ditemukan tetapi semua ditolak: terakhir coba lewat RTSP playback
                    string why = job.Warn;
                    job.Warn = "";
                    if (status != null) status("Unduh file ditolak XVR, mencoba lewat RTSP...");
                    RtspDownload(nvr, channel, startLocal, endLocal, folder, job, status);
                    if (job.Files == 0) job.Result = "Download ditolak XVR. " + why + (job.Result.Length > 0 ? " | RTSP: " + job.Result : "");
                    else job.Warn = "file asli ditolak, memakai RTSP (real-time). ";
                }
            }
            catch (Exception ex)
            {
                if (!job.Cancel) job.Result = ex.Message;
            }
            finally
            {
                if (obj != null)
                {
                    try { Get(ep, "/cgi-bin/mediaFileFind.cgi?action=close&object=" + obj, 8000); } catch { }
                    try { Get(ep, "/cgi-bin/mediaFileFind.cgi?action=destroy&object=" + obj, 8000); } catch { }
                }
            }
        }

        public override byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            return NvrSession.DownloadBytes(nvr.ToEndpoint(), "/cgi-bin/snapshot.cgi?channel=" + channel, 15000);
        }
    }
}
