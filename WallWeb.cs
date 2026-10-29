using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Wall web: dinding kamera lewat go2rtc + browser (Edge/Chrome). go2rtc hanya meneruskan stream RTSP (tanpa transcode),
    /// browser men-decode dengan GPU. Tidak ada proses ffmpeg per kamera dan tidak ada langkah JPEG, sehingga CPU jauh lebih ringan untuk puluhan kamera.
    /// URL RTSP (berisi password) hanya dikirim ke go2rtc lewat loopback dan tidak ditulis ke disk.</summary>
    public static class WallWeb
    {
        const int Port = 1984;
        static Process go2rtc;
        static readonly object lk = new object();

        static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool", "wall"); }
        }

        static string ToolDir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "go2rtc"); } }

        public static string FindGo2Rtc()
        {
            string cfg = AppSettings.Current.Go2RtcPath;
            if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg)) return cfg;
            string a = Path.Combine(ToolDir, "go2rtc.exe");
            if (File.Exists(a)) return a;
            a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "go2rtc.exe");
            return File.Exists(a) ? a : null;
        }

        static string FindBrowser()
        {
            string[] roots = { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("LocalAppData") };
            string[] rel = { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" };
            foreach (string r in rel)
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string p = Path.Combine(root, r);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        static bool ApiUp()
        {
            try
            {
                var rq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + "/api");
                rq.Timeout = 1500;
                using (rq.GetResponse()) return true;
            }
            catch { return false; }
        }

        static void Call(string method, string path)
        {
            var rq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + path);
            rq.Method = method;
            rq.Timeout = 5000;
            rq.ContentLength = 0;
            using (rq.GetResponse()) { }
        }

        static void EnsureServer(string exe)
        {
            lock (lk)
            {
                if (ApiUp()) return;
                Directory.CreateDirectory(DataDir);
                string www = Path.Combine(DataDir, "www");
                Directory.CreateDirectory(www);
                foreach (string f in new string[] { "video-stream.js", "video-rtc.js" })
                {
                    string src = Path.Combine(ToolDir, "www", f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(www, f), true);
                }
                string cfg = Path.Combine(DataDir, "go2rtc.yaml");
                File.WriteAllText(cfg, "api:\n  listen: \"127.0.0.1:" + Port + "\"\n  static_dir: \"" + www.Replace("\\", "/") + "\"\nrtsp:\n  listen: \"127.0.0.1:18554\"\nwebrtc:\n  listen: \":18555\"\nlog:\n  level: warn\n");
                var psi = new ProcessStartInfo(exe, "-config \"" + cfg + "\"");
                psi.WorkingDirectory = DataDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                go2rtc = Process.Start(psi);
                Application.ApplicationExit += delegate { Stop(); };
                for (int i = 0; i < 30; i++)
                {
                    System.Threading.Thread.Sleep(200);
                    if (ApiUp()) return;
                }
                throw new Exception("go2rtc tidak merespons di port " + Port + " (port dipakai program lain?).");
            }
        }

        public static void Stop()
        {
            try { if (go2rtc != null && !go2rtc.HasExited) go2rtc.Kill(); } catch { }
            go2rtc = null;
        }

        static string FirstRtsp(NvrRecord nvr, CameraRecord cam, bool sub)
        {
            foreach (StreamSource s in StreamPlan.ForLive(nvr, cam, sub))
                if (s.Rtsp && !string.IsNullOrEmpty(s.Url)) return s.Url;
            return null;
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        /// <summary>Buka dinding web untuk satu group di monitor tertentu. Mengembalikan pesan error, atau null bila berhasil. Blokir: jalankan dari thread latar.</summary>
        public static string Open(GroupRec g, Screen screen)
        {
            try
            {
                string exe = FindGo2Rtc();
                if (exe == null) return "go2rtc.exe tidak ditemukan. Letakkan di folder Tools\\go2rtc (ikut dalam paket) atau isi path-nya di Setup > Dekode & stream.";
                string browser = FindBrowser();
                if (browser == null) return "Microsoft Edge atau Google Chrome tidak ditemukan.";
                EnsureServer(exe);

                ScreenLayout lay = ScreenLayout.ById(g.Layout);
                var cells = new StringBuilder();
                int n = 0, skipped = 0;
                for (int i = 0; i < lay.Count; i++)
                {
                    Rectangle r = lay.Cells[i];
                    CamRef cr = i < g.Slots.Length ? g.Slots[i] : null;
                    string style = "grid-column:" + (r.X + 1) + "/span " + r.Width + ";grid-row:" + (r.Y + 1) + "/span " + r.Height;
                    if (cr == null) { cells.Append("<div class=\"c\" style=\"" + style + "\"></div>\n"); continue; }
                    NvrRecord nvr = Dir.Nvr(cr.NvrId);
                    CameraRecord cam = Dir.Cam(cr.NvrId, cr.Channel);
                    string us = nvr == null ? null : FirstRtsp(nvr, cam, lay.Count > 1);
                    string um = nvr == null ? null : FirstRtsp(nvr, cam, false);
                    string name = "c" + cr.NvrId + "_" + cr.Channel;
                    if (us == null) { skipped++; cells.Append("<div class=\"c\" style=\"" + style + "\"><div class=\"t\">" + Esc(Dir.Title(cr.NvrId, cr.Channel)) + " - RTSP tidak tersedia</div></div>\n"); continue; }
                    // daftar stream ke go2rtc (hanya di memori go2rtc; tersambung ke kamera baru saat ada penonton)
                    foreach (string suffix in new string[] { "_s", "_m" })
                    {
                        string url = suffix == "_s" ? us : (um ?? us);
                        try { Call("DELETE", "/api/streams?src=" + Uri.EscapeDataString(name + suffix)); } catch { }
                        Call("PUT", "/api/streams?name=" + Uri.EscapeDataString(name + suffix) + "&src=" + Uri.EscapeDataString(url));
                    }
                    n++;
                    cells.Append("<div class=\"c\" style=\"" + style + "\" data-s=\"" + name + "_s\" data-m=\"" + name + "_m\"><div class=\"t\">" + Esc(Dir.Title(cr.NvrId, cr.Channel)) + "</div></div>\n");
                }
                string html = Template.Replace("{{TITLE}}", Esc(g.Name)).Replace("{{COLS}}", lay.Cols.ToString()).Replace("{{ROWS}}", lay.Rows.ToString()).Replace("{{CELLS}}", cells.ToString());
                string file = "wall_" + g.Id + ".html";
                File.WriteAllText(Path.Combine(DataDir, "www", file), html, new UTF8Encoding(false));

                Rectangle b = screen.Bounds;
                string profile = Path.Combine(DataDir, "profile" + Array.IndexOf(Screen.AllScreens, screen));
                string args = "--app=http://127.0.0.1:" + Port + "/" + file + " --user-data-dir=\"" + profile + "\""
                    + " --window-position=" + b.X + "," + b.Y + " --window-size=" + b.Width + "," + b.Height + " --start-fullscreen"
                    + " --autoplay-policy=no-user-gesture-required --no-first-run --disable-features=Translate";
                Process.Start(browser, args);
                return n == 0 ? "Tidak ada kamera di group ini yang punya URL RTSP." : (skipped > 0 ? skipped + " kamera dilewati (tanpa URL RTSP)." : null);
            }
            catch (Exception ex)
            {
                Log.Write("Wall web: " + ex.Message);
                return ex.Message;
            }
        }

        const string Template = @"<!doctype html><html><head><meta charset=""utf-8""><title>{{TITLE}}</title>
<style>
html,body{margin:0;height:100%;background:#000;overflow:hidden;font-family:Segoe UI,Arial,sans-serif}
#g{display:grid;grid-template-columns:repeat({{COLS}},1fr);grid-template-rows:repeat({{ROWS}},1fr);gap:1px;height:100vh;background:#5f5f5f}
.c{position:relative;background:#000;overflow:hidden}
.c video-stream{display:block;width:100%;height:100%}
.c video{width:100%;height:100%;object-fit:contain;background:#000}
.t{position:absolute;left:6px;bottom:4px;color:#dcdcdc;font-size:12px;text-shadow:0 0 3px #000,0 0 3px #000;pointer-events:none;z-index:2}
.c.max{position:fixed;inset:0;z-index:9;grid-column:auto!important;grid-row:auto!important}
</style></head><body><div id=""g"">
{{CELLS}}</div>
<script type=""module"">
import './video-stream.js';
function url(n){return new URL('api/ws?src='+encodeURIComponent(n),location.href)}
function attach(c){
  var v=document.createElement('video-stream');v.mode='webrtc,mse';v.src=url(c.dataset.s);c.insertBefore(v,c.firstChild);c._v=v;
}
document.querySelectorAll('.c[data-s]').forEach(function(c){
  attach(c);
  c.addEventListener('dblclick',function(){
    var big=c.classList.toggle('max');
    c._v.remove();attach_with(c,big?c.dataset.m:c.dataset.s);
  });
});
function attach_with(c,name){var v=document.createElement('video-stream');v.mode='webrtc,mse';v.src=url(name);c.insertBefore(v,c.firstChild);c._v=v}
document.addEventListener('keydown',function(e){if(e.key=='Escape'){var m=document.querySelector('.c.max');if(m){m.classList.remove('max');m._v.remove();attach_with(m,m.dataset.s)}}});
</script></body></html>";
    }
}
