using System;

// RtspDiscovery.cs dan StreamProbe.cs dipakai bersama dengan aplikasi client (file yang sama, di-link dari folder induk).
// Hanya Rtsp.Build yang dibutuhkan dari StreamPlan.cs (yang bergantung pada kelas client lain), jadi disediakan di sini.
namespace IproNvrTool
{
    public static class Rtsp
    {
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
}
