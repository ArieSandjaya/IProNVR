using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace IproNvrTool
{
    public class DownloadJob
    {
        public volatile bool Cancel;
        public HttpWebRequest Req;
        public int Files;
        public long Bytes;
        public string Result = "";
        public bool NoRecording;   // ada chunk tanpa rekaman (bukan kegagalan: chunk berikutnya tetap dicoba)

        public void Abort()
        {
            Cancel = true;
            try { if (Req != null) Req.Abort(); } catch { }
        }
    }

    /// <summary>Download rekaman lewat httpdl.cgi (maks. 1 jam per permintaan: rentang lebih panjang dipecah otomatis).</summary>
    public static class NvrDownloader
    {
        static string FileNameOf(string header)
        {
            if (string.IsNullOrEmpty(header)) return "";
            Match m = Regex.Match(header, "\"([^\"]*)\"");
            string name;
            if (m.Success) name = m.Groups[1].Value;
            else
            {
                int i = header.IndexOf('=');
                name = (i >= 0 ? header.Substring(i + 1) : header).Trim().Trim('"');
            }
            return Path.GetFileName(name);
        }

        /// <summary>Ekstensi file hasil sesuai format yang dipilih.</summary>
        public static string ExtOf(string kind)
        {
            return kind != null && kind.StartsWith("MP4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".n3r";
        }

        static void Finish(ref FileStream cur, ref string curPath, string finalName, string folder, DownloadJob job, string kind)
        {
            if (cur == null) return;
            cur.Dispose();
            cur = null;
            string dest = Path.Combine(folder, Path.ChangeExtension(finalName, ExtOf(kind)));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(curPath, dest);
            job.Files++;
            Log.Write("Download selesai: " + dest);
            curPath = null;
        }

        /// <summary>kind: MP4, MP4VO, N3, N3VO. Waktu masukan = waktu lokal PC; dikonversi sesuai pengaturan DownloadTimeUtc.</summary>
        public static void Run(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            Directory.CreateDirectory(folder);
            bool utc = AppSettings.Current.DownloadTimeUtc;
            var sess = new NvrSession("download " + nvr.Name, nvr.ToEndpoint());
            try
            {
                sess.EnsureLogin();
                Pass(sess, channel, startLocal, endLocal, kind, folder, job, status, utc);
                if (!job.Cancel && job.Result.Length == 0 && job.Files == 0 && job.Bytes == 0 && job.NoRecording)
                {
                    // spec tidak menyebut zona waktu STARTTIME/ENDTIME: bila "tidak ada rekaman", coba sekali lagi dengan zona waktu sebaliknya
                    job.NoRecording = false;
                    if (status != null) status("Tidak ada rekaman, mencoba zona waktu " + (utc ? "lokal" : "UTC") + "...");
                    Log.Write("Download: tidak ada rekaman dengan waktu " + (utc ? "UTC" : "lokal") + ", mencoba " + (utc ? "lokal" : "UTC") + ".");
                    Pass(sess, channel, startLocal, endLocal, kind, folder, job, status, !utc);
                    if (job.Files > 0 || job.Bytes > 0)
                    {
                        AppSettings.Current.DownloadTimeUtc = !utc;
                        try { AppSettings.Current.Save(); } catch { }
                        Log.Write("Download: zona waktu yang cocok = " + (!utc ? "UTC" : "lokal") + " (disimpan di pengaturan).");
                    }
                }
            }
            catch (Exception ex)
            {
                if (job.Result.Length == 0) job.Result = "Error: " + ex.Message;
                Log.Write("Download: " + ex.Message);
            }
            finally
            {
                try { sess.Logout(); } catch { }
            }
            if (job.Cancel) job.Result = "Dibatalkan.";
            else if (job.Result.Length == 0 && job.Files == 0 && job.Bytes == 0 && job.NoRecording) job.Result = "Tidak ada rekaman pada rentang waktu itu (sudah dicoba waktu UTC dan lokal).";
            else if (job.Result.Length == 0) job.Result = "Selesai: " + job.Files + " file, " + (job.Bytes / 1024) + " KB -> " + folder;
            if (status != null) status(job.Result);
        }

        static void Pass(NvrSession sess, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status, bool utc)
        {
            DateTime s = utc ? startLocal.ToUniversalTime() : startLocal;
            DateTime e = utc ? endLocal.ToUniversalTime() : endLocal;
            DateTime a = new DateTime(s.Year, s.Month, s.Day, s.Hour, s.Minute, 0, s.Kind); // detik awal selalu 00 (spec); cegah chunk saling tumpang
            while (a < e && !job.Cancel)
            {
                DateTime b = a.AddMinutes(60);
                if (b > e) b = e;
                string st = a.ToString("yyMMddHHmm", CultureInfo.InvariantCulture) + "00";
                string en = b.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
                Log.Write("Download: CAM " + channel + " " + st + " - " + en + " " + kind + (utc ? " (UTC)" : " (lokal)"));
                if (!Chunk(sess, st, en, kind, channel, folder, job, status)) break;
                a = b;
            }
        }

        static bool Chunk(NvrSession sess, string st, string en, string kind, int cam, string folder, DownloadJob job, Action<string> status)
        {
            if (status != null) status("Meminta data...");
            job.Req = sess.Request("httpdl.cgi", "STARTTIME=" + st + "&ENDTIME=" + en + "&KIND=" + kind + "&CAM=" + cam + "&PC=AS60", 60000);
            job.Req.ReadWriteTimeout = 120000; // NVR berhenti kirim data: jangan menggantung selamanya
            FileStream cur = null;
            string curPath = null;
            long lastGot = 0;
            try
            {
                using (WebResponse resp = job.Req.GetResponse())
                using (Stream stream = resp.GetResponseStream())
                {
                    var mr = new MultipartReader(stream);
                    MultipartPart p;
                    while (!job.Cancel && (p = mr.ReadPartHeaders()) != null)
                    {
                        string temp = FileNameOf(p.H("X-Temp-Filename"));
                        string prev = FileNameOf(p.H("X-Prev-Filename"));
                        string stt = p.H("X-RecData-Satus");
                        if (stt.Length == 0) stt = p.H("X-RecData-Status");
                        if (stt == "1") { job.NoRecording = true; return !job.Cancel; }
                        if (stt == "2") { job.Result = "Codec tidak kompatibel (MP4 tidak bisa untuk JPEG). Pilih N3R."; return false; }
                        if (stt == "3") { job.Result = "NVR sibuk (terlalu banyak proses bersamaan). Coba lagi nanti."; return false; }

                        if (prev.Length > 0 && temp.Length > 0) Finish(ref cur, ref curPath, prev, folder, job, kind);
                        if (temp.Length > 0 && cur == null)
                        {
                            curPath = Path.Combine(folder, temp);
                            cur = new FileStream(curPath, FileMode.Create, FileAccess.Write);
                        }
                        // isi dialirkan langsung ke file (tidak ditampung di memori: rekaman 5 menit bisa puluhan MB)
                        long got = mr.CopyBody(p, cur != null ? (Stream)cur : Stream.Null, delegate(int n)
                        {
                            if (cur == null) return;
                            job.Bytes += n;
                            if (status != null) status("Mengunduh... " + (job.Bytes / 1024) + " KB");
                        });
                        if (got < 0) break;   // koneksi terputus: file sementara disimpan oleh blok finally
                        if (prev.Length > 0 && temp.Length == 0) Finish(ref cur, ref curPath, prev, folder, job, kind);
                        lastGot = got;
                    }
                    // stream selesai normal tapi file terakhir belum diberi nama akhir oleh NVR: namai sekarang (jangan tertinggal .tmp)
                    if (cur != null && !job.Cancel && lastGot >= 0)
                        Finish(ref cur, ref curPath, Path.GetFileName(curPath), folder, job, kind);
                }
            }
            catch (Exception ex)
            {
                if (!job.Cancel) { job.Result = "Error: " + ex.Message; Log.Write("Download: " + ex.Message); return false; }
            }
            finally
            {
                if (cur != null)
                {
                    cur.Dispose();
                    Log.Write("Download tidak lengkap, file sementara: " + curPath);
                }
            }
            return !job.Cancel;
        }
    }
}
