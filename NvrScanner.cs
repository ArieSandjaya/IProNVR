using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace IproNvrTool
{
    public class RegistrationResult
    {
        public bool Ok;
        public string Error = "";
        public string Uid = "";
        public List<DiskRecord> Disks = new List<DiskRecord>();
        public List<CamStatus> Cameras;      // null jika daftar kamera gagal diambil
        public string CamRaw = "";
        public string CamError = "";
    }

    public class CameraListResult
    {
        public List<CamStatus> Items = new List<CamStatus>();
        public string Raw = "";
    }

    /// <summary>Parser respons recordedtime.cgi (spec bab 5.2.1) menjadi daftar HDD.</summary>
    public static class HddParser
    {
        static int? Gb(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            int v;
            if (i > 0 && int.TryParse(s.Substring(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        public static List<DiskRecord> Parse(string text)
        {
            var list = new List<DiskRecord>();
            bool header = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (!header)
                {
                    if (line.StartsWith("Disk,", StringComparison.OrdinalIgnoreCase)) header = true;
                    continue;
                }
                string[] p = line.Split(',');
                if (p.Length < 3 || p[0].Trim().Length == 0) continue;
                string cap = p[1].Trim();
                if (cap.Length == 0) continue; // slot kosong
                var d = new DiskRecord();
                d.Disk = p[0].Trim();
                d.Recording = cap.IndexOf("(R)", StringComparison.OrdinalIgnoreCase) >= 0;
                d.CapacityGb = Gb(cap);
                d.RemainGb = Gb(p[2].Trim());
                if (p.Length > 5 && p[3].Trim().Length > 0) d.Oldest = (p[3].Trim() + " " + p[5].Trim()).Trim();
                if (p.Length > 8 && p[6].Trim().Length > 0) d.Newest = (p[6].Trim() + " " + p[8].Trim()).Trim();
                list.Add(d);
            }
            return list;
        }
    }

    /// <summary>Recorder information (bab 7.1) yang ditanam NVR di header ekstensi RTP.</summary>
    public static class RecInfo
    {
        static int BE16(byte[] d, int p)
        {
            return (d[p] << 8) | d[p + 1];
        }

        public static Dictionary<int, byte[]> FromRtp(byte[] d)
        {
            var r = new Dictionary<int, byte[]>();
            if (d == null || d.Length < 16 || (d[0] & 0x10) == 0) return r;
            int cc = d[0] & 0x0F;
            int off = 12 + cc * 4;
            if (d.Length < off + 4) return r;
            int words = BE16(d, off + 2);
            int p = off + 4;
            int end = Math.Min(d.Length, p + words * 4);
            while (p + 4 <= end)
            {
                int id = BE16(d, p);
                int len = BE16(d, p + 2);
                if (len < 4 || p + len > end) break;
                var data = new byte[len - 4];
                Buffer.BlockCopy(d, p + 4, data, 0, len - 4);
                r[id] = data;
                p += len;
            }
            return r;
        }

        /// <summary>0x0009 Framerate info: nilai dalam 10 x gambar/detik.</summary>
        public static double? Fps(Dictionary<int, byte[]> info)
        {
            byte[] v;
            if (info.TryGetValue(0x0009, out v) && v.Length >= 2)
            {
                int n = BE16(v, 0);
                if (n > 0 && n <= 2000) return n / 10.0;
            }
            return null;
        }
    }

    /// <summary>Registrasi (tes login + info HDD + daftar kamera) dan deteksi stream opsional sebuah NVR.</summary>
    public static class NvrScanner
    {
        /// <summary>Daftar kamera + status koneksi: GET /cgi-bin/as_getinfo.cgi?FILE=2 (tanpa UID, cukup digest/basic auth).
        /// Baris yang dicari: CAM_CONNECT_nnCH=s  (0 tidak terdaftar, 1 terdaftar &amp; normal, 2 terdaftar &amp; error koneksi).</summary>
        public static CameraListResult FetchCameraList(NvrEndpoint ep)
        {
            string text = NvrSession.DownloadText(ep, "/cgi-bin/as_getinfo.cgi?FILE=2", 20000);
            return ParseCameraList(text);
        }

        public static CameraListResult ParseCameraList(string text)
        {
            var res = new CameraListResult();
            res.Raw = text ?? "";
            var seen = new HashSet<int>();
            foreach (Match m in Regex.Matches(res.Raw, @"CAM_CONNECT_(\d+)CH\s*=\s*(\d)"))
            {
                int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int st = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (seen.Add(ch))
                {
                    var s = new CamStatus();
                    s.Channel = ch;
                    s.Status = st;
                    res.Items.Add(s);
                }
            }
            return res;
        }

        public static RegistrationResult TestNvr(NvrEndpoint ep)
        {
            var res = new RegistrationResult();
            var s = new NvrSession("registrasi", ep);
            try
            {
                s.Login();
                res.Uid = s.Uid;
                res.Ok = true;
                try
                {
                    string text;
                    try
                    {
                        text = NvrSession.DownloadText(ep, "/cgi-bin/recordedtime.cgi", 15000);
                    }
                    catch (Exception)
                    {
                        // penulisan URL di dokumen spec: /cgi-bin//cgi-bin/recordedtime.cgi
                        text = NvrSession.DownloadText(ep, "/cgi-bin//cgi-bin/recordedtime.cgi", 15000);
                    }
                    res.Disks = HddParser.Parse(text);
                }
                catch (Exception ex)
                {
                    Log.Write("Registrasi " + ep.Host + ": info HDD tidak terbaca: " + ex.Message);
                }
                try
                {
                    CameraListResult cl = FetchCameraList(ep);
                    res.CamRaw = cl.Raw;
                    if (cl.Items.Count == 0)
                    {
                        string head = cl.Raw.Length > 200 ? cl.Raw.Substring(0, 200) : cl.Raw;
                        res.CamError = "respons as_getinfo tidak berisi CAM_CONNECT_nnCH (awal respons: " + head.Replace("\r", " ").Replace("\n", " ") + ")";
                    }
                    else
                    {
                        res.Cameras = cl.Items;
                    }
                }
                catch (Exception ex)
                {
                    res.CamError = ex.Message;
                    Log.Write("Registrasi " + ep.Host + ": daftar kamera gagal diambil: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = ex.Message;
            }
            finally
            {
                s.Logout();
            }
            return res;
        }

        static void WaitGap(int ms, CancellationToken ct)
        {
            int waited = 0;
            while (waited < ms)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(100);
                waited += 100;
            }
        }

        /// <summary>Opsional: deteksi codec/resolusi/fps dengan membuka live singkat pada kanal yang diberikan
        /// (biasanya hanya kamera terdaftar &amp; normal). Hasil diisi bertahap ke 'results' sehingga tetap bisa
        /// disimpan sebagian jika dibatalkan (OperationCanceledException).</summary>
        public static void ProbeStreams(NvrEndpoint ep, List<int> channels, int gapMs, List<ScanResult> results,
            Action<int, int, string> progress, CancellationToken ct)
        {
            var sess = new NvrSession("deteksi " + ep.Host, ep);
            try
            {
                sess.EnsureLogin();
                sess.EnsureLiveSetup();
                string preferred = null;
                int total = channels.Count;
                for (int i = 0; i < total; i++)
                {
                    int ch = channels[i];
                    ct.ThrowIfCancellationRequested();
                    if (progress != null) progress(i, total, "Kanal " + ch + "...");

                    var order = new List<string>();
                    if (preferred != null) order.Add(preferred);
                    foreach (string c in new string[] { "H264", "H265" })
                    {
                        if (c != preferred) order.Add(c);
                    }

                    var r = new ScanResult();
                    r.Channel = ch;
                    string tried = "";
                    foreach (string comp in order)
                    {
                        WaitGap(gapMs, ct);
                        tried += (tried.Length > 0 ? "," : "") + comp;
                        if (Attempt(sess, ch, comp, r, ct))
                        {
                            r.Available = true;
                            preferred = r.Codec.Length > 0 ? r.Codec : comp;
                            break;
                        }
                    }
                    if (!r.Available) r.Detail = "tidak ada stream (dicoba: " + tried + ")";
                    results.Add(r);
                    if (progress != null)
                        progress(i + 1, total, "Kanal " + ch + (r.Available ? ": " + r.Codec : ": tidak ada stream"));
                }
            }
            finally
            {
                sess.Logout();
            }
        }

        class Dim
        {
            public volatile int W;
            public volatile int H;
        }

        /// <summary>Satu percobaan push.cgi START untuk satu kanal + satu format. True jika ada data yang diterima.</summary>
        static bool Attempt(NvrSession s, int ch, string comp, ScanResult r, CancellationToken ct)
        {
            HttpWebRequest req = null;
            FfmpegPipe ff = null;
            int parts = 0;
            var dim = new Dim();
            try
            {
                RtpDepacketizer dep = null;
                string exe = FfmpegPipe.FindExe();
                if (exe != null)
                {
                    ff = new FfmpegPipe(exe, comp == "H265", 0, "scan CAM " + ch, delegate (byte[] jpg)
                    {
                        if (dim.W > 0) return;
                        try
                        {
                            using (Image img = Ui.DecodeImage(jpg))
                            {
                                dim.H = img.Height;
                                dim.W = img.Width;
                            }
                        }
                        catch { }
                    });
                    dep = new RtpDepacketizer(comp == "H265", ff.Input);
                }

                req = s.Request("push.cgi", "CAM=" + ch + "&CMD=START&COMP=" + comp + "&INTERNETMODE=ON", 8000);
                req.ReadWriteTimeout = 5000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var st = resp.GetResponseStream())
                {
                    var mr = new MultipartReader(st);
                    var sw = Stopwatch.StartNew();
                    int budget = ff != null ? 7000 : 3500;
                    try
                    {
                        MultipartPart p;
                        while (sw.ElapsedMilliseconds < budget && (p = mr.ReadPart()) != null)
                        {
                            ct.ThrowIfCancellationRequested();
                            parts++;
                            {
                                Dictionary<int, byte[]> info = RecInfo.FromRtp(p.Body);
                                int pt = p.Body.Length > 1 ? (p.Body[1] & 0x7F) : 0;
                                if (pt == 98) r.Codec = "H264";
                                else if (pt == 101) r.Codec = "H265";
                                else if (r.Codec.Length == 0) r.Codec = comp;
                                double? fps = RecInfo.Fps(info);
                                if (fps.HasValue) r.Fps = fps;

                                if (dep != null)
                                {
                                    dep.Feed(p.Body, p.Body.Length);
                                    if (RtpDepacketizer.Marker(p.Body)) ff.Input.Flush();
                                    if (dim.W > 0) break;
                                }
                                else if (parts >= 5)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        // timeout/penutupan setelah beberapa bagian diterima tetap dianggap berhasil
                    }
                }

                if (parts == 0) return false;
                if (dim.W > 0)
                {
                    r.Width = dim.W;
                    r.Height = dim.H;
                }
                r.Detail = comp + " HTTP, " + parts + " bagian" + (ff == null ? " (resolusi: ffmpeg.exe tidak ada)" : "");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("Scan CAM " + ch + " " + comp + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (ff != null) ff.Dispose();
                try { if (req != null) req.Abort(); } catch { }
                try { s.Cmd("push.cgi", "CAM=" + ch + "&CMD=STOP&COMP=" + comp); } catch { }
            }
        }
    }
}
