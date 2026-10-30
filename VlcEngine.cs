using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace IproNvrTool
{
    /// <summary>Dekoder live berbasis LibVLC (paket NuGet LibVLCSharp + VideoLAN.LibVLC.Windows): dekode di dalam proses (GPU: D3D11/DXVA2)
    /// dan gambar digambar langsung ke jendela tile - tanpa proses ffmpeg, pipe, JPEG, maupun bitmap .NET.
    /// LibVLCSharp dipanggil lewat reflection/dynamic: bila library tidak ada atau gagal dimuat, aplikasi otomatis memakai ffmpeg.</summary>
    public static class Vlc
    {
        static readonly object gate = new object();
        static bool tried;
        static bool broken;
        static string error;
        static object lib;
        internal static Type TLib, TMedia, TPlayer, TFrom;

        public static string Error { get { return error; } }

        public static bool Available
        {
            get { Init(); return lib != null && !broken; }
        }

        /// <summary>Dipanggil bila VLC gagal secara fatal (mis. tidak bisa membuka player): seluruh aplikasi kembali ke ffmpeg.</summary>
        public static void MarkBroken(string why) { broken = true; error = why; Log.Write("LibVLC dimatikan, memakai ffmpeg: " + why); }

        internal static object Lib { get { Init(); return lib; } }

        static string HwOption()
        {
            switch ((AppSettings.Current.HwAccel ?? "auto").ToLowerInvariant())
            {
                case "none": return "none";
                case "dxva2": return "dxva2";
                case "d3d11va": return "d3d11va";
                case "cuda": return "d3d11va";
                case "qsv": return "d3d11va";
                default: return "any";
            }
        }

        static void Init()
        {
            lock (gate)
            {
                if (tried) return;
                tried = true;
                try
                {
                    Assembly asm = Assembly.Load("LibVLCSharp");
                    Type core = asm.GetType("LibVLCSharp.Shared.Core", true);
                    TLib = asm.GetType("LibVLCSharp.Shared.LibVLC", true);
                    TMedia = asm.GetType("LibVLCSharp.Shared.Media", true);
                    TPlayer = asm.GetType("LibVLCSharp.Shared.MediaPlayer", true);
                    TFrom = asm.GetType("LibVLCSharp.Shared.FromType", false);

                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libvlc");
                    dir = Path.Combine(dir, Environment.Is64BitProcess ? "win-x64" : "win-x86");
                    MethodInfo init = core.GetMethod("Initialize", new Type[] { typeof(string) });
                    if (init == null) throw new Exception("Core.Initialize tidak ditemukan");
                    init.Invoke(null, new object[] { Directory.Exists(dir) ? dir : null });

                    string hw = HwOption();
                    var opts = new System.Collections.Generic.List<string>();
                    opts.Add("--no-audio");
                    opts.Add("--no-video-title-show");
                    opts.Add("--no-osd");
                    opts.Add("--no-stats");
                    opts.Add("--no-snapshot-preview");
                    opts.Add("--no-sub-autodetect-file");
                    opts.Add("--no-mouse-events");
                    opts.Add("--no-keyboard-events");
                    opts.Add("--quiet");
                    opts.Add("--drop-late-frames");
                    opts.Add("--skip-frames");
                    opts.Add("--rtsp-tcp");
                    opts.Add("--network-caching=300");
                    opts.Add("--live-caching=300");
                    opts.Add("--clock-jitter=0");
                    opts.Add("--avcodec-hw=" + hw);
                    if (hw == "none") opts.Add("--avcodec-threads=1");
                    lib = Activator.CreateInstance(TLib, new object[] { opts.ToArray() });
                    Log.Write("LibVLC siap (hw=" + hw + ", " + (Environment.Is64BitProcess ? "x64" : "x86") + ").");
                }
                catch (Exception ex)
                {
                    Exception e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    error = e.GetType().Name + ": " + e.Message;
                    lib = null;
                    Log.Write("LibVLC tidak bisa dipakai (" + error + "), memakai ffmpeg.");
                }
            }
        }

        /// <summary>Status di folder aplikasi untuk ditampilkan di Setup.</summary>
        public static string Describe()
        {
            return Available ? "LibVLC aktif" : "LibVLC tidak tersedia" + (error != null ? " (" + error + ")" : "");
        }

        // ---------- Win32: jendela video VLC dibuat tembus-klik supaya klik/drag/scroll tetap sampai ke kontrol tile ----------

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool enable);

        internal static void ClickThrough(IntPtr parent)
        {
            try
            {
                EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) { EnableWindow(h, false); return true; }, IntPtr.Zero);
            }
            catch { }
        }
    }

    /// <summary>Satu player LibVLC yang menggambar ke handle jendela (satu per tile).</summary>
    public class VlcPlayer : IDisposable
    {
        dynamic mp;
        dynamic media;
        IntPtr hwnd;
        bool disposed;
        readonly object gate = new object();

        public static VlcPlayer Open(IntPtr hwnd, string url)
        {
            var v = new VlcPlayer();
            v.hwnd = hwnd;
            try
            {
                object lib = Vlc.Lib;
                string[] mo = new string[] { ":rtsp-tcp", ":network-caching=300", ":no-audio" };
                object m;
                if (Vlc.TFrom != null)
                {
                    object loc = Enum.Parse(Vlc.TFrom, "FromLocation");
                    m = Activator.CreateInstance(Vlc.TMedia, new object[] { lib, url, loc, mo });
                }
                else m = Activator.CreateInstance(Vlc.TMedia, new object[] { lib, new Uri(url), mo });
                v.media = m;
                v.mp = Activator.CreateInstance(Vlc.TPlayer, new object[] { lib });
                try { v.mp.EnableMouseInput = false; } catch { }
                try { v.mp.EnableKeyInput = false; } catch { }
                try { v.mp.Mute = true; } catch { }
                v.mp.Hwnd = hwnd;
                v.mp.Play(v.media);
                return v;
            }
            catch
            {
                v.Dispose();
                throw;
            }
        }

        /// <summary>Opening / Buffering / Playing / Paused / Stopped / Ended / Error.</summary>
        public string State
        {
            get
            {
                lock (gate)
                {
                    if (disposed) return "Stopped";
                    try { object o = mp.State; return o == null ? "" : o.ToString(); } catch { return "Error"; }
                }
            }
        }

        /// <summary>Posisi pemutaran (ms); bertambah selama video berjalan.</summary>
        public long Time
        {
            get
            {
                lock (gate)
                {
                    if (disposed) return 0;
                    try { long t = mp.Time; return t; } catch { return 0; }
                }
            }
        }

        public void ClickThrough() { Vlc.ClickThrough(hwnd); }

        /// <summary>Simpan gambar saat ini ke file PNG; false bila gagal.</summary>
        public bool Snapshot(string path)
        {
            lock (gate)
            {
                if (disposed) return false;
                try { bool ok = mp.TakeSnapshot(0u, path, 0u, 0u); return ok; } catch { return false; }
            }
        }

        /// <summary>Lepas dari jendela (panggil dari UI thread sebelum kontrol dihancurkan).</summary>
        public void Detach()
        {
            lock (gate)
            {
                if (disposed) return;
                try { mp.Hwnd = IntPtr.Zero; } catch { }
            }
        }

        public void Dispose()
        {
            dynamic p, m;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                p = mp; m = media; mp = null; media = null;
            }
            try { if (p != null) p.Hwnd = IntPtr.Zero; } catch { }
            try { if (p != null) p.Stop(); } catch { }
            try { if (p != null) p.Dispose(); } catch { }
            try { if (m != null) m.Dispose(); } catch { }
        }
    }
}
