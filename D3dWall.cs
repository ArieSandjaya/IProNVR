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
        static readonly Dictionary<string, Form> open = new Dictionary<string, Form>();
        /// <summary>true saat aplikasi keluar: jendela yang tertutup tetap tercatat supaya dipulihkan pada start berikutnya.</summary>
        public static volatile bool Exiting;

        static readonly SerialWorker PtzWorker = new SerialWorker(150);

        static string Key(GroupRec g, Screen s) { return g.Id + "|" + s.DeviceName; }

        static void Remember(GroupRec g, Screen s, bool add)
        {
            try
            {
                var list = AppSettings.Current.D3dOpen;
                lock (list)
                {
                    list.RemoveAll(delegate(D3dOpenRec r) { return r.GroupId == g.Id && r.Screen == s.DeviceName; });
                    if (add) list.Add(new D3dOpenRec { GroupId = g.Id, GroupName = g.Name, Screen = s.DeviceName });
                }
                AppSettings.Current.Save();
            }
            catch (Exception ex) { try { Log.Write("Wall Direct3D: gagal menyimpan kondisi: " + ex.Message); } catch { } }
        }

        public static void CloseAll()
        {
            Exiting = true;
            List<Form> l;
            lock (open) l = new List<Form>(open.Values);
            foreach (Form f in l) { try { f.Close(); } catch { } }
        }

        /// <summary>Buka kembali Wall Direct3D yang terbuka saat aplikasi terakhir ditutup (group + monitor yang sama).</summary>
        public static void RestoreOpen()
        {
            List<D3dOpenRec> recs;
            var src = AppSettings.Current.D3dOpen;
            lock (src) recs = new List<D3dOpenRec>(src);
            foreach (D3dOpenRec r in recs)
            {
                try
                {
                    Screen sc = null;
                    foreach (Screen s in Screen.AllScreens) if (s.DeviceName == r.Screen) { sc = s; break; }
                    if (sc == null) { Log.Write("Wall Direct3D: monitor " + r.Screen + " tidak ada, '" + r.GroupName + "' tidak dipulihkan (tetap tercatat)"); continue; }
                    GroupRec g = AsmDb.GetGroup(r.GroupId);
                    if (g == null) { Log.Write("Wall Direct3D: group '" + r.GroupName + "' tidak ditemukan"); continue; }
                    string err = Open(g, sc);
                    if (err != null) Log.Write("Wall Direct3D (pulihkan '" + g.Name + "'): " + err.Replace("\n", " "));
                }
                catch (Exception ex) { Log.Write("Wall Direct3D (pulihkan): " + ex.Message); }
            }
        }

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
            Form ex0;
            lock (open) { if (open.TryGetValue(Key(g, screen), out ex0) && !ex0.IsDisposed) { try { ex0.Activate(); } catch { } return null; } }
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
            Action<int, int, int, int> ptz = delegate(int i, int pan, int tilt, int zoom)
            {
                if (i < 0 || i >= recNvr.Count || !AsmApp.Can(Perm.Camera)) return;
                NvrRecord nv = Dir.Nvr(recNvr[i]); int ch = recCh[i];
                if (nv == null) return;
                PtzWorker.Post(delegate
                {
                    NvrSession ss = Pool.Control(nv);
                    ss.EnsureLogin();
                    ss.Cmd("directctrl.cgi", "CH=" + ch + "&PAN=" + pan + "&TILT=" + tilt + "&ZOOM=" + zoom + "&PC=AS60");
                });
            };
            Func<int, bool> isRec = delegate(int i) { return i >= 0 && i < recNvr.Count && Dir.IsRecording(recNvr[i], recCh[i]); };
            Form f;
            try
            {
                var s = AppSettings.Current;
                f = (Form)entry.GetMethod("Open").Invoke(null, new object[] {
                    "Wall Direct3D - " + g.Name, urls.ToArray(), mains.ToArray(), names.ToArray(), rects.ToArray(),
                    screen.Bounds, screen.DeviceName, dir, Math.Max(1, s.D3dMaxStreams), s.D3dFps <= 0 ? 15 : s.D3dFps, log, isRec, ptz });
            }
            catch (Exception ex)
            {
                Exception e = Inner(ex);
                Log.Write("Wall Direct3D: " + e);
                return "Wall Direct3D gagal dibuka: " + e.GetType().Name + ": " + e.Message;
            }
            f.Show();
            string key = Key(g, screen);
            lock (open) open[key] = f;
            Remember(g, screen, true);
            f.FormClosed += delegate
            {
                lock (open) open.Remove(key);
                if (!Exiting) Remember(g, screen, false);   // ditutup pengguna (Esc): jangan dipulihkan lagi
            };
            return null;
        }

        static Exception Inner(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
