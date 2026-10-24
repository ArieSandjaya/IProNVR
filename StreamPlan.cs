using System;
using System.Collections.Generic;

namespace IproNvrTool
{
    /// <summary>URL RTSP langsung ke kamera (dipakai Multiview bila NVR tidak punya substream).</summary>
    public static class Rtsp
    {
        /// <summary>Contoh untuk kamera OEM Dahua: subtype=1 = substream. Placeholder: {user} {pass} {ip}.</summary>
        public const string DefaultTemplate = "rtsp://{user}:{pass}@{ip}:554/cam/realmonitor?channel=1&subtype=1";

        /// <summary>Login RTSP kamera: login khusus kamera bila diisi, selain itu login kamera di data NVR.</summary>
        public static void Creds(NvrRecord nvr, CameraRecord cam, out string user, out string pass)
        {
            if (cam != null && !string.IsNullOrEmpty(cam.CamUser)) { user = cam.CamUser; pass = cam.CamPassword; }
            else { user = nvr.CamUser; pass = nvr.CamPassword; }
        }

        public static string Build(string template, string user, string pass, string ip)
        {
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(ip)) return "";
            string t = template.Trim();
            if (string.IsNullOrEmpty(user)) t = t.Replace("{user}:{pass}@", "");
            return t.Replace("{user}", Uri.EscapeDataString(user ?? ""))
                    .Replace("{pass}", Uri.EscapeDataString(pass ?? ""))
                    .Replace("{ip}", ip.Trim());
        }
    }

    /// <summary>Menyusun urutan sumber video untuk satu kamera di Multiview. Multiview sebaiknya memakai substream
    /// (resolusi kecil) agar CPU, jaringan, dan NVR tidak terbebani:
    ///   1. substream lewat NVR - hanya bila parameter substream NVR diisi (spec CGI i-PRO tidak mendefinisikannya),
    ///   2. RTSP substream langsung dari kamera (URL per kamera, atau template NVR + IP kamera),
    ///   3. stream utama NVR sebagai cadangan terakhir, supaya kotak tidak kosong.</summary>
    public static class StreamPlan
    {
        /// <summary>URL RTSP kamera untuk live (template/URL tersimpan, kredensial kamera). main=true: stream utama.</summary>
        public static string CameraRtspUrl(NvrRecord nvr, CameraRecord cam, bool main)
        {
            if (cam == null) return "";
            string cu, cp;
            Rtsp.Creds(nvr, cam, out cu, out cp);
            if (main && !string.IsNullOrWhiteSpace(cam.RtspMainUrl))
            {
                // URL stream utama hasil deteksi (sudah diuji dengan ffmpeg)
                string mu = cam.RtspMainUrl.Trim();
                return mu.Contains("{") ? Rtsp.Build(mu, cu, cp, cam.IpAddress) : mu;
            }
            string url = (cam.RtspUrl ?? "").Trim();
            if (url.Contains("{")) url = Rtsp.Build(url, cu, cp, cam.IpAddress);
            if (url.Length == 0) url = Rtsp.Build(nvr.RtspTemplate, cu, cp, cam.IpAddress);
            if (url.Length > 0 && main)
            {
                // substream -> stream utama: Dahua/i-PRO subtype=1 -> 0, Hikvision .../Channels/102 -> 101
                url = System.Text.RegularExpressions.Regex.Replace(url, @"subtype=1", "subtype=0", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                url = System.Text.RegularExpressions.Regex.Replace(url, @"(/Streaming/Channels/\d{1,2}0)2(?!\d)", "${1}1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            return url;
        }

        /// <summary>Live semua lewat RTSP langsung ke kamera. sub=true: substream (multi-area), false: stream utama (1 area / layar penuh).
        /// Bila URL RTSP tidak bisa disusun, stream NVR dipakai sebagai cadangan agar kotak tidak kosong.</summary>
        public static List<StreamSource> ForLive(NvrRecord nvr, CameraRecord cam, bool sub)
        {
            var list = new List<StreamSource>();
            INvrDriver drv = NvrDrivers.For(nvr);
            if (drv.BrandId != "ipro")
            {
                // merek lain: RTSP lewat NVR itu sendiri (satu login, tidak perlu IP/URL tiap kamera)
                NvrStreamSource ns = drv.LiveSource(nvr, cam != null ? cam.Channel : 1, sub);
                if (ns.Kind == NvrStreamKind.Rtsp && ns.RtspUrl.Length > 0)
                {
                    var rs = new StreamSource();
                    rs.Rtsp = true;
                    rs.Url = ns.RtspUrl;
                    rs.Label = drv.BrandName + (sub ? " sub" : " utama");
                    list.Add(rs);
                    return list;
                }
            }
            string url = CameraRtspUrl(nvr, cam, !sub);
            if (url.Length > 0)
            {
                var s = new StreamSource();
                s.Rtsp = true;
                s.Url = url;
                s.Label = sub ? "RTSP sub" : "RTSP utama";
                list.Add(s);
            }
            var fb = new StreamSource();
            fb.Label = "NVR (cadangan)";
            list.Add(fb);
            return list;
        }

        public static List<StreamSource> ForMultiview(NvrRecord nvr, CameraRecord cam)
        {
            var list = new List<StreamSource>();

            if (!string.IsNullOrWhiteSpace(nvr.SubstreamParam))
            {
                var s = new StreamSource();
                s.NvrExtra = nvr.SubstreamParam.Trim().TrimStart('&', '?');
                s.Label = "NVR sub";
                list.Add(s);
            }

            string url = "";
            if (cam != null)
            {
                string cu, cp;
                Rtsp.Creds(nvr, cam, out cu, out cp);
                url = (cam.RtspUrl ?? "").Trim();
                if (url.Contains("{")) url = Rtsp.Build(url, cu, cp, cam.IpAddress); // URL tersimpan memakai {user} {pass} {ip}
                if (url.Length == 0) url = Rtsp.Build(nvr.RtspTemplate, cu, cp, cam.IpAddress);
            }
            if (url.Length > 0)
            {
                var s = new StreamSource();
                s.Rtsp = true;
                s.Url = url;
                s.Label = "RTSP sub";
                list.Add(s);
            }

            var main = new StreamSource();
            main.Label = "NVR utama";
            list.Add(main);
            return list;
        }
    }
}
