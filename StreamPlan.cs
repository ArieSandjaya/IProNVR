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
