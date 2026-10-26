using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace IproNvrTool
{
    /// <summary>Logika CGI per merek yang tidak bergantung pada kelas client: dipakai bersama aplikasi client (driver)
    /// dan server konfigurasi (file ini di-link dari folder induk). 'get' = fungsi GET (path -> teks respons) dengan
    /// digest auth ke NVR; pemanggil yang mengurus alamat dan kredensial. Endpoint masih belum diuji ke perangkat nyata.</summary>
    public static class BrandCgi
    {
        public const string Ipro = "ipro", Hikvision = "hikvision", Dahua = "dahua";

        public static string Norm(string brand)
        {
            string b = (brand ?? "").Trim().ToLowerInvariant();
            return b == Hikvision || b == Dahua ? b : Ipro;
        }

        public static string Name(string brand)
        {
            switch (Norm(brand)) { case Hikvision: return "Hikvision"; case Dahua: return "Dahua"; default: return "i-PRO (Panasonic)"; }
        }

        /// <summary>Template RTSP substream kamera (placeholder {user} {pass} {ip}) bawaan merek.</summary>
        public static string DefaultCamTemplate(string brand)
        {
            if (Norm(brand) == Hikvision) return "rtsp://{user}:{pass}@{ip}:554/Streaming/Channels/102";
            return "rtsp://{user}:{pass}@{ip}:554/cam/realmonitor?channel=1&subtype=1";
        }

        /// <summary>Path RTSP lewat perangkat (NVR/DVR) itu sendiri: tanpa IP kamera. sub = substream.</summary>
        public static string ViaRtspPath(string brand, int ch, bool sub)
        {
            if (Norm(brand) == Hikvision) return "/Streaming/Channels/" + (ch * 100 + (sub ? 2 : 1));
            return "/cam/realmonitor?channel=" + ch + "&subtype=" + (sub ? 1 : 0);
        }

        /// <summary>Petunjuk bila port yang diisi kemungkinan bukan port HTTP (mis. 37777 = port SDK Dahua di Smart PSS). Kosong bila tidak relevan.</summary>
        public static string PortHint(string brand, int port)
        {
            if (port == 37777 || port == 37778) return " Port " + port + " adalah port SDK Dahua (Smart PSS), bukan HTTP. Isi port HTTP perangkat (biasanya 80; lihat Network > Port di perangkat).";
            if (port == 554) return " Port 554 adalah RTSP, bukan HTTP. Isi port HTTP perangkat (biasanya 80).";
            if (port == 8000 && Norm(brand) == Hikvision) return " Port 8000 adalah port SDK Hikvision, bukan HTTP. Isi port HTTP (biasanya 80).";
            return "";
        }

        /// <summary>Tes login ke NVR merek non-i-PRO: satu GET yang harus berhasil (digest). Melempar exception bila gagal.</summary>
        public static void Login(string brand, Func<string, string> get)
        {
            if (Norm(brand) == Hikvision) get("/ISAPI/System/deviceInfo");
            else if (Norm(brand) == Dahua) get("/cgi-bin/magicBox.cgi?action=getSystemInfo");
            else throw new NotSupportedException("Login i-PRO memakai dlogin.cgi (UID).");
        }

        /// <summary>Kanal -> status (0 tidak terdaftar, 1 normal, 2 error koneksi). raw = respons mentah terakhir (untuk pesan galat).</summary>
        public static SortedDictionary<int, int> Cameras(string brand, Func<string, string> get, out string raw)
        {
            var d = new SortedDictionary<int, int>();
            raw = "";
            if (Norm(brand) == Hikvision)
            {
                try
                {
                    raw = get("/ISAPI/ContentMgmt/InputProxy/channels/status");
                    foreach (XmlNode n in Doc(raw).SelectNodes("//*[local-name()='InputProxyChannelStatus']"))
                    {
                        int id;
                        if (!int.TryParse(Val(n, "id"), out id) || d.ContainsKey(id)) continue;
                        d[id] = Val(n, "online").Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    }
                }
                catch (Exception) { }
                if (d.Count == 0)
                {
                    // DVR / kamera tunggal: id stream 101,102,201,... -> kanal 1,2,...
                    raw = get("/ISAPI/Streaming/channels");
                    foreach (XmlNode n in Doc(raw).SelectNodes("//*[local-name()='StreamingChannel']"))
                    {
                        int id;
                        if (int.TryParse(Val(n, "id"), out id) && id >= 100 && !d.ContainsKey(id / 100)) d[id / 100] = 1;
                    }
                }
            }
            else if (Norm(brand) == Dahua)
            {
                // DVR / XVR / HCVR (kanal analog atau hibrid): semua kanal dari ChannelTitle dianggap terdaftar
                string type = "";
                try { type = get("/cgi-bin/magicBox.cgi?action=getDeviceType"); } catch (Exception) { }
                if (Regex.IsMatch(type, @"DVR|XVR|HCVR|HCVI|HDCVI", RegexOptions.IgnoreCase))
                {
                    raw = get("/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle");
                    foreach (Match m in Regex.Matches(raw, @"ChannelTitle\[(\d+)\]\.Name\s*="))
                    {
                        int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                        if (!d.ContainsKey(ch)) d[ch] = 1;
                    }
                    if (d.Count > 0) return d;
                }
                raw = get("/cgi-bin/configManager.cgi?action=getConfig&name=RemoteDevice");
                foreach (Match m in Regex.Matches(raw, @"RemoteDevice\[(\d+)\]\.Enable\s*=\s*(\w+)"))
                {
                    int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                    if (!d.ContainsKey(ch)) d[ch] = m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }
                if (d.Count == 0)
                {
                    raw = get("/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle");
                    foreach (Match m in Regex.Matches(raw, @"ChannelTitle\[(\d+)\]\.Name\s*="))
                    {
                        int ch = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                        if (!d.ContainsKey(ch)) d[ch] = 1;
                    }
                }
            }
            else throw new NotSupportedException("Daftar kamera i-PRO memakai as_getinfo.cgi.");
            return d;
        }

        static XmlDocument Doc(string text) { var x = new XmlDocument(); x.LoadXml(text); return x; }

        static string Val(XmlNode n, string local)
        {
            XmlNode x = n.SelectSingleNode(".//*[local-name()='" + local + "']");
            return x == null ? "" : x.InnerText.Trim();
        }
    }
}
