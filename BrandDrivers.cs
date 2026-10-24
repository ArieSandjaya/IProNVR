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
        public virtual NvrCaps Capabilities { get { return NvrCaps.Live | NvrCaps.Download | NvrCaps.Hdd | NvrCaps.Snapshot; } }

        public abstract RegistrationResult Test(NvrEndpoint ep);
        public abstract CameraListResult ListCameras(NvrEndpoint ep);
        public abstract List<DiskRecord> ListDisks(NvrEndpoint ep);
        protected abstract string RtspPath(int channel, bool sub);
        protected abstract string PlaybackPath(int channel, DateTime startUtc, DateTime endUtc);
        public abstract void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status);
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

        // ---------- helper HTTP ----------

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
        public override string DefaultCamTemplate { get { return "rtsp://{user}:{pass}@{ip}:554/Streaming/Channels/102"; } }

        protected override string RtspPath(int ch, bool sub) { return "/Streaming/Channels/" + (ch * 100 + (sub ? 2 : 1)); }

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
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message; }
            return res;
        }

        public override CameraListResult ListCameras(NvrEndpoint ep)
        {
            var res = new CameraListResult();
            try
            {
                // NVR: kamera IP yang terdaftar + statusnya
                res.Raw = Get(ep, "/ISAPI/ContentMgmt/InputProxy/channels/status", 20000);
                foreach (XmlNode n in Xml(res.Raw).SelectNodes("//*[local-name()='InputProxyChannelStatus']"))
                {
                    int id;
                    if (!int.TryParse(Val(n, "id"), out id)) continue;
                    var c = new CamStatus();
                    c.Channel = id;
                    c.Status = Val(n, "online").Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    res.Items.Add(c);
                }
            }
            catch (Exception) { }
            if (res.Items.Count == 0)
            {
                // DVR / kamera tunggal: turunkan dari daftar stream (id 101,102,201... -> kanal 1,2,...)
                res.Raw = Get(ep, "/ISAPI/Streaming/channels", 20000);
                var seen = new HashSet<int>();
                foreach (XmlNode n in Xml(res.Raw).SelectNodes("//*[local-name()='StreamingChannel']"))
                {
                    int id;
                    if (!int.TryParse(Val(n, "id"), out id) || id < 100) continue;
                    if (seen.Add(id / 100)) { var c = new CamStatus(); c.Channel = id / 100; c.Status = 1; res.Items.Add(c); }
                }
            }
            return res;
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

        public override void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            NvrEndpoint ep = nvr.ToEndpoint();
            try
            {
                if (status != null) status("Searching recordings...");
                List<Seg> segs = Search(ep, channel, startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
                if (segs.Count == 0) { job.NoRecording = true; job.Result = "No recording in this range."; return; }
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
                    SaveStream(req, file, job);
                    job.Files++;
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
        public override string DefaultCamTemplate { get { return Rtsp.DefaultTemplate; } }

        protected override string RtspPath(int ch, bool sub) { return "/cam/realmonitor?channel=" + ch + "&subtype=" + (sub ? 1 : 0); }

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
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message; }
            return res;
        }

        public override CameraListResult ListCameras(NvrEndpoint ep)
        {
            var res = new CameraListResult();
            var seen = new HashSet<int>();
            // NVR: kamera IP di RemoteDevice[n] (kanal = n + 1)
            res.Raw = Get(ep, "/cgi-bin/configManager.cgi?action=getConfig&name=RemoteDevice", 20000);
            foreach (Match m in Regex.Matches(res.Raw, @"RemoteDevice\[(\d+)\]\.Enable\s*=\s*(\w+)"))
            {
                int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                bool en = m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (seen.Add(ch)) { var c = new CamStatus(); c.Channel = ch; c.Status = en ? 1 : 0; res.Items.Add(c); }
            }
            if (res.Items.Count == 0)
            {
                // DVR / kamera: pakai daftar judul kanal
                res.Raw = Get(ep, "/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", 20000);
                foreach (Match m in Regex.Matches(res.Raw, @"ChannelTitle\[(\d+)\]\.Name\s*="))
                {
                    int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                    if (seen.Add(ch)) { var c = new CamStatus(); c.Channel = ch; c.Status = 1; res.Items.Add(c); }
                }
            }
            return res;
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

        public override void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            NvrEndpoint ep = nvr.ToEndpoint();
            string obj = null;
            try
            {
                if (status != null) status("Searching recordings...");
                string r = Get(ep, "/cgi-bin/mediaFileFind.cgi?action=factory.create", 15000);
                Match om = Regex.Match(r, @"result\s*=\s*(\d+)");
                if (!om.Success) throw new Exception("mediaFileFind.create gagal: " + r.Trim());
                obj = om.Groups[1].Value;
                Get(ep, "/cgi-bin/mediaFileFind.cgi?action=findFile&object=" + obj + "&condition.Channel=" + channel
                    + "&condition.StartTime=" + Uri.EscapeDataString(Ts(startLocal)) + "&condition.EndTime=" + Uri.EscapeDataString(Ts(endLocal))
                    + "&condition.Types[0]=dav", 30000);
                var files = new List<KeyValuePair<string, string>>();   // path, start
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
                if (files.Count == 0) { job.NoRecording = true; job.Result = "No recording in this range."; return; }
                string ffmpeg = FfmpegPipe.FindExe();
                bool wantMp4 = kind != null && kind.StartsWith("MP4", StringComparison.OrdinalIgnoreCase) && ffmpeg != null;
                int n = 0;
                foreach (KeyValuePair<string, string> f in files)
                {
                    if (job.Cancel) break;
                    n++;
                    if (status != null) status("Downloading " + n + "/" + files.Count + "...");
                    string stamp = Regex.Replace(f.Value, @"[^0-9]", "");
                    string dav = Path.Combine(folder, Safe(nvr.Name + "_CH" + channel + "_" + (stamp.Length > 0 ? stamp : n.ToString())) + ".dav");
                    string path = f.Key.StartsWith("/") ? f.Key : "/" + f.Key;
                    SaveStream(NvrSession.CreateRequest(ep, "/cgi-bin/RPC_Loadfile" + path, 30000), dav, job);
                    job.Files++;
                    if (wantMp4 && !job.Cancel) ConvertToMp4(ffmpeg, dav);
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

        /// <summary>.dav -> .mp4 tanpa re-encode; file .dav dihapus bila berhasil, bila gagal dibiarkan.</summary>
        internal static void ConvertToMp4(string ffmpeg, string dav)
        {
            try
            {
                string mp4 = Path.ChangeExtension(dav, ".mp4");  // sumber .dav / .mkv
                var psi = new ProcessStartInfo(ffmpeg, "-y -hide_banner -loglevel error -i \"" + dav + "\" -c copy \"" + mp4 + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return; }
                    if (p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0) File.Delete(dav);
                }
            }
            catch (Exception ex) { Log.Write("Dahua: konversi mp4 gagal: " + ex.Message); }
        }

        public override byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            return NvrSession.DownloadBytes(nvr.ToEndpoint(), "/cgi-bin/snapshot.cgi?channel=" + channel, 15000);
        }
    }

    // =====================================================================================================
    /// <summary>Axis (VAPIX). Axis tidak punya "substream" tetap: sub = resolusi kecil (640x360), main = resolusi asli kamera.
    /// Rekaman diambil dari edge storage (kartu SD / NAS) kamera atau recorder Axis. Semua endpoint belum diuji.</summary>
    public class AxisDriver : HttpBrandDriver
    {
        public override string BrandId { get { return "axis"; } }
        public override string BrandName { get { return "Axis"; } }
        public override string DefaultCamTemplate { get { return "rtsp://{user}:{pass}@{ip}:554/axis-media/media.amp?videocodec=h264&resolution=640x360"; } }

        protected override string RtspPath(int ch, bool sub)
        {
            return "/axis-media/media.amp?camera=" + ch + "&videocodec=h264" + (sub ? "&resolution=640x360" : "");
        }

        protected override string PlaybackPath(int ch, DateTime s, DateTime e)
        {
            return "/axis-media/media.amp?camera=" + ch + "&videocodec=h264";   // playback RTSP butuh recordingid: belum didukung
        }

        public override RegistrationResult Test(NvrEndpoint ep)
        {
            var res = new RegistrationResult();
            try
            {
                Get(ep, "/axis-cgi/param.cgi?action=list&group=Brand", 15000);
                res.Ok = true;
                try { res.Disks = ListDisks(ep); } catch (Exception ex) { Log.Write("Axis " + ep.Host + ": disk: " + ex.Message); }
                try
                {
                    CameraListResult cl = ListCameras(ep);
                    res.CamRaw = cl.Raw;
                    if (cl.Items.Count == 0) res.CamError = "daftar kamera kosong";
                    else res.Cameras = cl.Items;
                }
                catch (Exception ex) { res.CamError = ex.Message; }
            }
            catch (Exception ex) { res.Ok = false; res.Error = ex.Message; }
            return res;
        }

        public override CameraListResult ListCameras(NvrEndpoint ep)
        {
            var res = new CameraListResult();
            res.Raw = Get(ep, "/axis-cgi/param.cgi?action=list&group=Image", 15000);
            var seen = new HashSet<int>();
            // root.Image.I0.Enabled=yes -> kanal 1 (indeks mulai dari 0, URL mulai dari 1)
            foreach (Match m in Regex.Matches(res.Raw, @"Image\.I(\d+)\.Enabled\s*=\s*(\w+)"))
            {
                int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                bool en = m.Groups[2].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                if (seen.Add(ch)) { var c = new CamStatus(); c.Channel = ch; c.Status = en ? 1 : 0; res.Items.Add(c); }
            }
            if (res.Items.Count == 0) { var c = new CamStatus(); c.Channel = 1; c.Status = 1; res.Items.Add(c); }   // kamera tunggal
            return res;
        }

        public override List<DiskRecord> ListDisks(NvrEndpoint ep)
        {
            var list = new List<DiskRecord>();
            string text = Get(ep, "/axis-cgi/disks/list.cgi?diskid=all", 15000);
            foreach (XmlNode n in Xml(text).SelectNodes("//*[local-name()='disk']"))
            {
                var d = new DiskRecord();
                XmlAttributeCollection a = n.Attributes;
                d.Disk = a["diskid"] != null ? a["diskid"].Value : "disk";
                double tot, free;
                // ukuran dalam KB
                if (a["totalsize"] != null && double.TryParse(a["totalsize"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tot)) d.CapacityGb = (int)(tot / 1048576.0);
                if (a["freesize"] != null && double.TryParse(a["freesize"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out free)) d.RemainGb = (int)(free / 1048576.0);
                d.Recording = a["status"] != null && a["status"].Value.Equals("OK", StringComparison.OrdinalIgnoreCase);
                list.Add(d);
            }
            return list;
        }

        public override void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            NvrEndpoint ep = nvr.ToEndpoint();
            try
            {
                if (status != null) status("Searching recordings...");
                string iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
                string list = Get(ep, "/axis-cgi/record/list.cgi?recordingid=all&startTime=" + Uri.EscapeDataString(startLocal.ToUniversalTime().ToString(iso, CultureInfo.InvariantCulture))
                    + "&stopTime=" + Uri.EscapeDataString(endLocal.ToUniversalTime().ToString(iso, CultureInfo.InvariantCulture)), 30000);
                var recs = new List<KeyValuePair<string, string>>();   // id, disk
                foreach (XmlNode n in Xml(list).SelectNodes("//*[local-name()='recording']"))
                {
                    XmlAttributeCollection a = n.Attributes;
                    if (a["recordingid"] == null) continue;
                    // kamera multi-kanal: saring kanal bila atribut tersedia
                    if (a["videosourcenumber"] != null || a["source"] != null)
                    {
                        string src = a["videosourcenumber"] != null ? a["videosourcenumber"].Value : a["source"].Value;
                        int sn;
                        if (int.TryParse(src, out sn) && sn != channel && sn != channel - 1) continue;
                    }
                    recs.Add(new KeyValuePair<string, string>(a["recordingid"].Value, a["diskid"] != null ? a["diskid"].Value : "SD_DISK"));
                }
                if (recs.Count == 0) { job.NoRecording = true; job.Result = "No recording in this range."; return; }
                string ffmpeg = FfmpegPipe.FindExe();
                bool wantMp4 = kind != null && kind.StartsWith("MP4", StringComparison.OrdinalIgnoreCase) && ffmpeg != null;
                int i = 0;
                foreach (KeyValuePair<string, string> r in recs)
                {
                    if (job.Cancel) break;
                    i++;
                    if (status != null) status("Downloading " + i + "/" + recs.Count + "...");
                    string file = Path.Combine(folder, Safe(nvr.Name + "_CH" + channel + "_" + r.Key) + ".mkv");
                    SaveStream(NvrSession.CreateRequest(ep, "/axis-cgi/record/export/exportrecording.cgi?schemaversion=1&recordingid=" + Uri.EscapeDataString(r.Key)
                        + "&diskid=" + Uri.EscapeDataString(r.Value) + "&exportformat=matroska", 30000), file, job);
                    job.Files++;
                    if (wantMp4 && !job.Cancel) DahuaDriver.ConvertToMp4(ffmpeg, file);
                }
            }
            catch (Exception ex)
            {
                if (!job.Cancel) job.Result = ex.Message;
            }
        }

        public override byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            return NvrSession.DownloadBytes(nvr.ToEndpoint(), "/axis-cgi/jpg/image.cgi?camera=" + channel + (width > 0 ? "&resolution=" + width + "x" + (width * 9 / 16) : ""), 15000);
        }
    }
}
