using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace IproNvrTool
{
    /// <summary>Hasil baca satu stream RTSP kamera.</summary>
    public class StreamInfo
    {
        public string Codec = "";
        public int Width, Height;
        public double Fps;
        public override string ToString()
        {
            return Codec + " " + Width + "x" + Height + (Fps > 0 ? " " + Fps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "fps" : "");
        }
    }

    /// <summary>Deteksi main stream / substream kamera lewat RTSP: membuka URL dengan ffmpeg lalu membaca codec, resolusi dan fps.</summary>
    public static class StreamProbe
    {
        static readonly Regex VideoLine = new Regex(@"Video:\s*(\w+).*?,\s*(\d{2,5})x(\d{2,5})(?:.*?,\s*([\d\.]+)\s*fps)?", RegexOptions.Compiled);

        /// <summary>URL main stream dari URL substream gaya Dahua (subtype=1 -> subtype=0); null bila pola tidak dikenal.</summary>
        public static string MainFromSub(string subUrl)
        {
            if (string.IsNullOrEmpty(subUrl)) return null;
            if (Regex.IsMatch(subUrl, @"subtype=1", RegexOptions.IgnoreCase))
                return Regex.Replace(subUrl, @"subtype=1", "subtype=0", RegexOptions.IgnoreCase);
            return null;
        }

        /// <summary>Blokir hingga ~10 detik. Null bila tidak tersambung / tidak ada video.</summary>
        public static StreamInfo Probe(string exe, string url, out string error, int timeoutMs = 12000)
        {
            error = null;
            var psi = new ProcessStartInfo(exe, "-hide_banner -rtsp_transport tcp -probesize 500000 -analyzeduration 2000000 -i \"" + url.Replace("\"", "%22") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                var sb = new StringBuilder();
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null && sb.Length < 8000) sb.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                p.OutputDataReceived += delegate { };
                p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                else p.WaitForExit();
                string text = sb.ToString();
                Match m = VideoLine.Match(text);
                if (!m.Success)
                {
                    string last = "";
                    foreach (string line in text.Split('\n')) if (line.Trim().Length > 0) last = line.Trim();
                    error = last.Length > 0 ? last : "tidak ada respons";
                    return null;
                }
                var si = new StreamInfo();
                si.Codec = m.Groups[1].Value;
                si.Width = int.Parse(m.Groups[2].Value);
                si.Height = int.Parse(m.Groups[3].Value);
                if (m.Groups[4].Success)
                {
                    double f;
                    if (double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) si.Fps = f;
                }
                return si;
            }
        }
    }
}
