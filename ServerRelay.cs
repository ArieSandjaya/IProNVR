using System;
using System.Collections.Generic;

namespace IproNvrTool
{
    /// <summary>Relay RTSP di IPRO NVR Server (MediaMTX): server menarik kamera satu kali dan membagikannya ke semua client, sehingga kamera/NVR tidak
    /// terbebani koneksi ganda. Info (port + login baca-saja) diambil dari server setelah sinkron; nama path dihitung sama dengan server.</summary>
    public static class ServerRelay
    {
        static volatile bool ready;
        static int port;
        static string user = "", pass = "";
        static string host = "";
        static int lastTick;

        public static bool Ready { get { return ready; } }
        public static string Status = "belum diperiksa";

        /// <summary>Dipanggil ServerLink setelah sinkron (dibatasi sekali per 30 dtk).</summary>
        public static void Refresh(Func<string, object> get)
        {
            if (unchecked(Environment.TickCount - lastTick) < 30000 && lastTick != 0) return;
            lastTick = Environment.TickCount;
            try
            {
                var d = get("/api/relay") as Dictionary<string, object>;
                if (d == null) { ready = false; Status = "server tidak menjawab"; return; }
                bool en = d.ContainsKey("enabled") && Convert.ToBoolean(d["enabled"]);
                if (!en) { ready = false; Status = d.ContainsKey("status") ? Convert.ToString(d["status"]) : "relay tidak aktif"; return; }
                port = Convert.ToInt32(d["port"]);
                user = Convert.ToString(d["user"]); pass = Convert.ToString(d["pass"]);
                string u = AppSettings.Current.ServerUrl ?? "";
                host = new Uri(u.Trim().Contains("://") ? u.Trim() : "http://" + u.Trim()).Host;
                ready = port > 0 && user.Length > 0 && host.Length > 0;
                Status = ready ? "relay aktif (" + host + ":" + port + ", " + Convert.ToString(d["paths"]) + " path)" : "info relay tidak lengkap";
            }
            catch (Exception ex) { ready = false; Status = "gagal: " + ex.Message; }
        }

        public static string PathName(string nvrHost, int nvrPort, int channel, bool main)
        {
            return "n" + System.Text.RegularExpressions.Regex.Replace((nvrHost ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]", "-") + "-" + nvrPort + "-" + channel + (main ? "_m" : "_s");
        }

        public static string Url(NvrRecord nvr, int channel, bool main)
        {
            if (!ready || nvr == null) return null;
            return "rtsp://" + Uri.EscapeDataString(user) + ":" + Uri.EscapeDataString(pass) + "@" + host + ":" + port + "/" + PathName(nvr.Host, nvr.Port, channel, main);
        }
    }
}
