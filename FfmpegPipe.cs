using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace IproNvrTool
{
    /// <summary>Pilihan akselerasi dekode GPU untuk ffmpeg (-hwaccel). Nilai tersimpan di AppSettings.HwAccel.</summary>
    public static class HwAccels
    {
        public static readonly string[] Values = new string[] { "auto", "d3d11va", "dxva2", "cuda", "qsv", "none" };
        public static readonly string[] Items = new string[]
        {
            "Otomatis (GPU bila tersedia)",
            "GPU - Direct3D 11 (NVIDIA / AMD / Intel)",
            "GPU - DXVA2",
            "GPU - NVIDIA CUDA (NVDEC)",
            "GPU - Intel Quick Sync",
            "CPU saja (tanpa GPU)"
        };

        public static int ToIndex(string value)
        {
            int i = Array.IndexOf(Values, (value ?? "").Trim().ToLowerInvariant());
            return i < 0 ? 0 : i;
        }

        /// <summary>Argumen ffmpeg sebelum -i. Kosong bila CPU saja. codecForQsv: "h264"/"hevc" bila diketahui (mode pipe).</summary>
        public static string Args(string mode, string codecForQsv)
        {
            string m = Values[ToIndex(mode)];
            if (m == "none") return "";
            string a = "-hwaccel " + m + " ";
            if (m == "qsv" && !string.IsNullOrEmpty(codecForQsv)) a += "-c:v " + codecForQsv + "_qsv ";
            return a;
        }
    }

    /// <summary>Dekoder H.264 / H.265 memakai ffmpeg.exe lewat pipe:
    /// stream Annex-B ditulis ke stdin, gambar JPEG (MJPEG) dibaca dari stdout.
    /// ffmpeg.exe tidak ikut dibundel; letakkan di folder aplikasi, di PATH, atau isi path-nya di tab Koneksi.</summary>
    public class FfmpegPipe : IDisposable
    {
        public static string FindExe()
        {
            string cfg = AppSettings.Current.FfmpegPath;
            if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg)) return cfg;

            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    string f = Path.Combine(d, "ffmpeg.exe");
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Dekode satu potongan stream Annex-B (mis. satu GOP playback) menjadi daftar JPEG. Proses ffmpeg sekali pakai:
        /// stdin ditutup setelah data ditulis sehingga semua gambar (termasuk yang terakhir) keluar. Blokir; panggil dari thread latar.</summary>
        public static List<byte[]> DecodeOnce(string exe, bool h265, int width, byte[] data, out string error)
        {
            error = null;
            var result = new List<byte[]>();
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -nostats ");
            sb.Append(h265 ? "-f hevc " : "-f h264 ");
            sb.Append("-i pipe:0 -an ");
            if (width > 0) sb.Append("-vf scale=" + width + ":-2:flags=fast_bilinear ");
            sb.Append(VsyncArg());
            sb.Append("-pix_fmt yuvj420p -q:v 4 -f image2pipe -vcodec mjpeg pipe:1");
            var psi = new ProcessStartInfo(exe, sb.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null && err.Length < 2000) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                var writer = new Thread(delegate()
                {
                    try
                    {
                        p.StandardInput.BaseStream.Write(data, 0, data.Length);
                        p.StandardInput.BaseStream.Flush();
                    }
                    catch { }
                    try { p.StandardInput.Close(); } catch { }
                });
                writer.IsBackground = true;
                writer.Start();

                var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                Stream o = p.StandardOutput.BaseStream;
                int n;
                while ((n = o.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } }
                writer.Join(2000);

                byte[] acc = ms.ToArray();
                int pos = 0;
                while (true)
                {
                    int soi = IndexOf(acc, 0xFF, 0xD8, pos, acc.Length);
                    if (soi < 0) break;
                    int eoi = IndexOf(acc, 0xFF, 0xD9, soi + 2, acc.Length);
                    if (eoi < 0) break;
                    var jpg = new byte[eoi + 2 - soi];
                    Buffer.BlockCopy(acc, soi, jpg, 0, jpg.Length);
                    result.Add(jpg);
                    pos = eoi + 2;
                }
                if (result.Count == 0) error = err.Length > 0 ? err.ToString().Trim() : "ffmpeg tidak menghasilkan gambar";
            }
            return result;
        }

        readonly Process _p;
        readonly Stream _in;
        readonly Action<byte[]> _onJpeg;
        readonly string _name;
        volatile bool _closed;
        volatile bool _hwError;

        /// <summary>True bila ffmpeg melaporkan masalah akselerasi GPU (driver/perangkat tidak mendukung).</summary>
        public bool HwError { get { return _hwError; } }

        /// <summary>Tulis stream Annex-B ke sini (ter-buffer; panggil Flush() setelah tiap gambar). Null pada mode RTSP.</summary>
        public Stream Input { get { return _in; } }

        /// <summary>True bila proses ffmpeg sudah berhenti (stream RTSP terputus / gagal tersambung).</summary>
        public bool HasExited
        {
            get
            {
                try { return _p.HasExited; }
                catch { return true; }
            }
        }

        /// <summary>Mode pipe: stream Annex-B H.264/H.265 dari NVR ditulis ke Input.</summary>
        public FfmpegPipe(string exe, bool h265, int width, string name, Action<byte[]> onJpeg, bool hw = false)
            : this(exe, PipeArgs(h265, width, hw), true, name, onJpeg)
        {
        }

        /// <summary>Mode RTSP: ffmpeg membuka sendiri URL RTSP kamera (TCP) dan mendekodenya; codec dideteksi otomatis.</summary>
        public static FfmpegPipe ForRtsp(string exe, string url, int width, string name, Action<byte[]> onJpeg, bool hw = false)
        {
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -nostats -fflags nobuffer -flags low_delay ");
            if (hw) sb.Append(HwAccels.Args(AppSettings.Current.HwAccel, null));
            sb.Append("-rtsp_transport tcp -probesize 200000 -analyzeduration 1000000 ");
            sb.Append("-i \"" + url.Replace("\"", "%22") + "\" -an ");
            if (width > 0) sb.Append("-vf scale=" + width + ":-2:flags=fast_bilinear ");
            sb.Append(VsyncArg());
            sb.Append("-pix_fmt yuvj420p -q:v 5 -flush_packets 1 -f image2pipe -vcodec mjpeg pipe:1");
            return new FfmpegPipe(exe, sb.ToString(), false, name, onJpeg);
        }

        static string _vsync;
        /// <summary>Opsi agar ffmpeg tidak menduplikasi/membuang gambar (penyebab video patah-patah): "-fps_mode passthrough" (ffmpeg >= 5.1) atau "-vsync 0".</summary>
        static string VsyncArg()
        {
            if (_vsync != null) return _vsync;
            string r = "-vsync 0 ";
            try
            {
                string exe = FindExe();
                if (exe != null)
                {
                    var psi = new ProcessStartInfo(exe, "-version");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                    using (Process p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadLine() ?? "";
                        p.WaitForExit(3000);
                        var m = System.Text.RegularExpressions.Regex.Match(o, @"version\s+[nN]?(\d+)\.(\d+)");
                        if (m.Success)
                        {
                            int maj = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
                            if (maj > 5 || (maj == 5 && min >= 1)) r = "-fps_mode passthrough ";
                        }
                        else if (o.IndexOf("git-", StringComparison.OrdinalIgnoreCase) >= 0 || o.IndexOf("N-", StringComparison.Ordinal) >= 0)
                            r = "-fps_mode passthrough "; // build git/nightly: terbaru
                    }
                }
            }
            catch { }
            _vsync = r;
            return r;
        }

        static string PipeArgs(bool h265, int width, bool hw)
        {
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -nostats -flags low_delay ");
            if (hw) sb.Append(HwAccels.Args(AppSettings.Current.HwAccel, h265 ? "hevc" : "h264"));
            sb.Append("-probesize 65536 -analyzeduration 0 ");
            sb.Append(h265 ? "-f hevc " : "-f h264 ");
            sb.Append("-i pipe:0 -an ");
            if (width > 0) sb.Append("-vf scale=" + width + ":-2:flags=fast_bilinear ");
            sb.Append(VsyncArg());
            sb.Append("-pix_fmt yuvj420p -q:v 5 -flush_packets 1 -f image2pipe -vcodec mjpeg pipe:1");
            return sb.ToString();
        }

        FfmpegPipe(string exe, string args, bool useStdin, string name, Action<byte[]> onJpeg)
        {
            _name = name;
            _onJpeg = onJpeg;

            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = useStdin;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            _p = Process.Start(psi);
            if (useStdin) _in = new BufferedStream(_p.StandardInput.BaseStream, 64 * 1024);

            var t1 = new Thread(ReadFrames);
            t1.IsBackground = true;
            t1.Start();
            var t2 = new Thread(ReadErrors);
            t2.IsBackground = true;
            t2.Start();
        }

        static int IndexOf(byte[] d, byte a, byte b, int from, int len)
        {
            for (int i = from; i + 1 < len; i++)
            {
                if (d[i] == a && d[i + 1] == b) return i;
            }
            return -1;
        }

        void ReadFrames()
        {
            try
            {
                Stream s = _p.StandardOutput.BaseStream;
                var buf = new byte[128 * 1024];
                var acc = new byte[512 * 1024];
                int len = 0;
                while (!_closed)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (len + n > acc.Length) Array.Resize(ref acc, Math.Max(acc.Length * 2, len + n));
                    Buffer.BlockCopy(buf, 0, acc, len, n);
                    len += n;

                    int pos = 0;
                    while (true)
                    {
                        int soi = IndexOf(acc, 0xFF, 0xD8, pos, len);
                        if (soi < 0) { pos = Math.Max(pos, len - 1); break; }
                        int eoi = IndexOf(acc, 0xFF, 0xD9, soi + 2, len);
                        if (eoi < 0) { pos = soi; break; }
                        var jpg = new byte[eoi + 2 - soi];
                        Buffer.BlockCopy(acc, soi, jpg, 0, jpg.Length);
                        pos = eoi + 2;
                        if (!_closed) _onJpeg(jpg);
                    }
                    if (pos > 0)
                    {
                        Buffer.BlockCopy(acc, pos, acc, 0, len - pos);
                        len -= pos;
                    }
                }
            }
            catch { }
        }

        static bool IsHwMessage(string line)
        {
            string l = line.ToLowerInvariant();
            return l.Contains("hwaccel") || l.Contains("hardware") || l.Contains("cuda") || l.Contains("cuvid")
                || l.Contains("d3d11") || l.Contains("dxva") || l.Contains("qsv") || l.Contains("device creation")
                || l.Contains("no device") || l.Contains("nvdec");
        }

        /// <summary>Metode akselerasi yang dikenal ffmpeg.exe ini (hasil "ffmpeg -hwaccels"), mis. "cuda, dxva2, d3d11va"; null bila gagal.</summary>
        public static string ListHwAccels(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-hide_banner -hwaccels");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var names = new System.Collections.Generic.List<string>();
                    foreach (string line in output.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.EndsWith(":", StringComparison.Ordinal)) continue;
                        names.Add(t);
                    }
                    return string.Join(", ", names.ToArray());
                }
            }
            catch { return null; }
        }

        static int Exec(string exe, string args, int timeoutMs, StringBuilder output)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } }
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return -999;
                }
                p.WaitForExit(); // pastikan semua keluaran terbaca
                return p.ExitCode;
            }
        }

        /// <summary>Tes sungguhan akselerasi GPU: membuat video H.264 kecil (butuh libx264 di ffmpeg ini) lalu
        /// mendekodenya dengan mode terpilih. Mengembalikan teks ringkas hasilnya.</summary>
        public static string TestHwAccel(string exe, string mode)
        {
            string m = HwAccels.Values[HwAccels.ToIndex(mode)];
            if (m == "none") return "Mode 'CPU saja' dipilih - tidak ada yang diuji.";
            string tmp = Path.Combine(Path.GetTempPath(), "ipro_gputest_" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                var mk = new StringBuilder();
                int rc = Exec(exe, "-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=10 -t 1 -c:v libx264 -pix_fmt yuv420p \"" + tmp + "\"", 30000, mk);
                if (rc != 0 || !File.Exists(tmp))
                    return "Tes dilewati: ffmpeg ini tidak bisa membuat video uji H.264 (butuh libx264). Pakai build ffmpeg 'full'/'gyan'.";

                var o = new StringBuilder();
                rc = Exec(exe, "-hide_banner -loglevel verbose " + HwAccels.Args(mode, "h264") + "-i \"" + tmp + "\" -f null -", 30000, o);
                string text = o.ToString();
                bool failed = rc != 0
                    || text.IndexOf("Failed setup", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Device creation failed", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Failed to set value", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("No device available", StringComparison.OrdinalIgnoreCase) >= 0;
                bool used = text.IndexOf("hwaccel type", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Created hwaccel", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Using auto hwaccel", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("hardware accelerated", StringComparison.OrdinalIgnoreCase) >= 0;

                var sb = new StringBuilder();
                if (failed) sb.AppendLine("GAGAL - GPU/driver tidak bisa dipakai dengan mode '" + m + "'. Coba mode lain, perbarui driver GPU, atau pilih CPU saja. (Saat live, aplikasi otomatis kembali ke CPU bila GPU gagal.)");
                else if (used) sb.AppendLine("BERHASIL - dekode H.264 memakai GPU (mode '" + m + "').");
                else sb.AppendLine("ffmpeg berjalan, tetapi tidak ada bukti GPU dipakai (kemungkinan kembali ke CPU).");
                int shown = 0;
                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    if (IsHwMessage(t))
                    {
                        sb.AppendLine("  " + (t.Length > 150 ? t.Substring(0, 150) : t));
                        if (++shown >= 4) break;
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "Tes gagal dijalankan: " + ex.Message;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        void ReadErrors()
        {
            try
            {
                string line;
                int count = 0;
                while ((line = _p.StandardError.ReadLine()) != null)
                {
                    if (_closed) continue;
                    if (IsHwMessage(line)) _hwError = true;
                    if (count++ < 25) Log.Write("ffmpeg " + _name + ": " + line);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _p.Kill(); } catch { }
            try { if (_in != null) _in.Close(); } catch { }
            try { _p.Dispose(); } catch { }
        }
    }
}
