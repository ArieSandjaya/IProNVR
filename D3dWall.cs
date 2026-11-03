using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>
    /// Wall Direct3D (uji coba): dekode GPU D3D11VA + render D3D11 di dalam proses (modul IproD3D.dll, dimuat lewat reflection).
    /// Satu jendela per layar; semua kamera group digambar di satu swapchain. Bila modul/DLL ffmpeg tidak ada, hanya muncul pesan.
    /// </summary>
    public static class D3dWall
    {
        public const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip";

        static string FindDir()
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            var c = new List<string>();
            string s = AppSettings.Current.D3dFfmpegDir;
            if (!string.IsNullOrEmpty(s)) { c.Add(s); c.Add(Path.Combine(s, "bin")); }
            c.Add(Path.Combine(b, "ffmpeg9"));
            c.Add(Path.Combine(b, "ffmpeg9", "bin"));
            c.Add(Path.Combine(b, "ffmpeg-shared"));
            c.Add(Path.Combine(b, "ffmpeg-shared", "bin"));
            c.Add(b);
            foreach (string d in c)
                if (File.Exists(Path.Combine(d, "avcodec-63.dll"))) return d;
            return null;
        }

        /// <summary>Buka Wall Direct3D untuk satu group di satu layar. Mengembalikan pesan galat atau null.</summary>
        public static string Open(GroupRec g, Screen screen)
        {
            string dir = FindDir();
            if (dir == null)
                return "DLL ffmpeg 9 (shared) tidak ditemukan.\n\nUnduh: " + DownloadUrl +
                       "\nLalu salin semua file .dll dari folder bin di dalam zip ke folder 'ffmpeg9' di samping IproNvrTool.exe," +
                       "\natau isi foldernya di Setup > Dekode & stream > 'Folder DLL ffmpeg 9'.";
            string mod = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IproD3D.dll");
            if (!File.Exists(mod)) return "IproD3D.dll tidak ada di folder aplikasi (proyek IproD3D belum ikut dibangun).";

            Type entry;
            try { entry = Assembly.LoadFrom(mod).GetType("IproD3D.Entry", true); }
            catch (Exception ex) { return "Modul Direct3D gagal dimuat: " + Inner(ex).Message; }

            string probe;
            try { probe = (string)entry.GetMethod("Probe").Invoke(null, new object[] { dir }); }
            catch (Exception ex) { probe = Inner(ex).Message; }
            if (probe != null) return "DLL ffmpeg gagal dimuat dari " + dir + "\n" + probe;

            ScreenLayout lay = ScreenLayout.ById(g.Layout);
            var recNvr = new List<long>(); var recCh = new List<int>();
            var urls = new List<string>(); var mains = new List<string>(); var names = new List<string>(); var rects = new List<float>();
            for (int i = 0; i < lay.Count && i < g.Slots.Length; i++)
            {
                CamRef cr = g.Slots[i];
                if (cr == null) continue;
                NvrRecord nvr = Dir.Nvr(cr.NvrId);
                CameraRecord cam = Dir.Cam(cr.NvrId, cr.Channel);
                if (nvr == null) continue;
                string sub = MosaicWall.FirstRtsp(nvr, cam, true);
                if (sub == null) continue;
                string main = MosaicWall.FirstRtsp(nvr, cam, false);
                Rectangle r = lay.Cells[i];
                recNvr.Add(cr.NvrId); recCh.Add(cr.Channel);
                urls.Add(sub); mains.Add(main); names.Add(Dir.Title(cr.NvrId, cr.Channel));
                rects.Add((float)r.X / lay.Cols); rects.Add((float)r.Y / lay.Rows);
                rects.Add((float)r.Width / lay.Cols); rects.Add((float)r.Height / lay.Rows);
            }
            if (urls.Count == 0) return "Tidak ada kamera di group ini yang punya URL RTSP.";

            Action<string> log = delegate(string m) { try { Log.Write(m); } catch { } };
            Func<int, bool> isRec = delegate(int i) { return i >= 0 && i < recNvr.Count && Dir.IsRecording(recNvr[i], recCh[i]); };
            Form f;
            try
            {
                var s = AppSettings.Current;
                f = (Form)entry.GetMethod("Open").Invoke(null, new object[] {
                    "Wall Direct3D - " + g.Name, urls.ToArray(), mains.ToArray(), names.ToArray(), rects.ToArray(),
                    screen.Bounds, screen.DeviceName, dir, Math.Max(1, s.D3dMaxStreams), s.D3dFps <= 0 ? 15 : s.D3dFps, log, isRec });
            }
            catch (Exception ex)
            {
                Exception e = Inner(ex);
                Log.Write("Wall Direct3D: " + e);
                return "Wall Direct3D gagal dibuka: " + e.GetType().Name + ": " + e.Message;
            }
            f.Show();
            return null;
        }

        static Exception Inner(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
