using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Download: httpdl.cgi - MP4 (dengan/tanpa audio), N3R (dengan/tanpa audio), dan LIST (N3N).
    /// Maks. 1 jam per permintaan; file yang terpecah ditangani lewat header X-Temp-Filename / X-Prev-Filename.</summary>
    public class DownloadTab : UserControl, IShutdown
    {
        NumericUpDown numCam;
        DateTimePicker dtpStart, dtpEnd;
        ComboBox cmbKind, cmbZone;
        TextBox txtFolder;
        Label lblStatus;
        Button btnGo;
        HttpWebRequest req;
        volatile bool cancel;
        volatile bool running;

        public DownloadTab()
        {
            Dock = DockStyle.Fill;

            var f = Ui.Flow();
            f.Dock = DockStyle.Top;
            numCam = Ui.Num(1, 256, 1, 55);
            dtpStart = MakePicker(DateTime.Now.AddMinutes(-30));
            dtpEnd = MakePicker(DateTime.Now.AddMinutes(-10));
            cmbKind = Ui.Combo(new string[]
            {
                "MP4 (dengan audio)", "MP4VO (tanpa audio)", "N3 (N3R + N3A, proprietary)", "N3VO (N3R tanpa audio)", "LIST (info N3N)"
            }, 1, 190);
            cmbZone = Ui.Combo(new string[] { "UTC", "Waktu lokal PC" }, 0, 110);
            txtFolder = Ui.Txt(AppSettings.Current.ResolveDownloadFolder(), 320);
            btnGo = Ui.Btn("Download", delegate { Go(); });
            lblStatus = Ui.Lbl("Siap. Rentang maksimal 1 jam; detik awal selalu 00.");

            f.Controls.Add(Ui.Lbl("Kamera"));
            f.Controls.Add(numCam);
            f.Controls.Add(Ui.Lbl("Mulai"));
            f.Controls.Add(dtpStart);
            f.Controls.Add(Ui.Lbl("Selesai"));
            f.Controls.Add(dtpEnd);
            f.Controls.Add(Ui.Lbl("Waktu input dianggap"));
            f.Controls.Add(cmbZone);
            f.Controls.Add(Ui.Lbl("Jenis"));
            f.Controls.Add(cmbKind);
            f.Controls.Add(Ui.Lbl("Simpan ke"));
            f.Controls.Add(txtFolder);
            f.Controls.Add(Ui.Btn("...", delegate
            {
                string d = Ui.BrowseFolder(txtFolder.Text);
                if (d != null) txtFolder.Text = d;
            }));
            f.Controls.Add(btnGo);
            f.Controls.Add(Ui.Btn("Batal", delegate { Cancel(); }));

            var note = new Label();
            note.AutoSize = true;
            note.Dock = DockStyle.Top;
            note.Padding = new Padding(8);
            note.Text = "Catatan: spec tidak menyebut zona waktu STARTTIME/ENDTIME untuk download (playback memakai UTC). "
                + "Jika hasil tidak sesuai, coba ganti pilihan zona waktu. N3R/N3A butuh viewer i-PRO.";

            var status = new Panel();
            status.Dock = DockStyle.Top;
            status.Height = 70;
            lblStatus.AutoSize = false;   // pesan hasil bisa panjang (alasan + lokasi file): biarkan membungkus
            lblStatus.Dock = DockStyle.Fill;
            status.Controls.Add(lblStatus);

            Controls.Add(status);
            Controls.Add(note);
            Controls.Add(f);
        }

        static DateTimePicker MakePicker(DateTime v)
        {
            var d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            d.Width = 160;
            d.Margin = new Padding(3, 5, 3, 3);
            d.Value = v;
            return d;
        }

        void SetStatus(string s)
        {
            Ui.Post(this, delegate { lblStatus.Text = s; });
        }

        void Cancel()
        {
            cancel = true;
            try { if (req != null) req.Abort(); } catch { }
        }

        void Go()
        {
            if (running) return;
            DateTime s = dtpStart.Value;
            DateTime e = dtpEnd.Value;
            if (e <= s) { MessageBox.Show("Waktu selesai harus setelah waktu mulai."); return; }
            if ((e - s).TotalMinutes > 60.0) { MessageBox.Show("Rentang maksimal 1 jam per download (spec)."); return; }
            if (cmbZone.SelectedIndex == 1) { s = s.ToUniversalTime(); e = e.ToUniversalTime(); }

            string[] kinds = { "MP4", "MP4VO", "N3", "N3VO", "LIST" };
            string kind = kinds[cmbKind.SelectedIndex];
            int cam = (int)numCam.Value;
            string folder = txtFolder.Text;
            AppSettings.Current.DownloadFolder = folder;
            try { AppSettings.Current.Save(); } catch { }

            string st = s.ToString("yyMMddHHmm", CultureInfo.InvariantCulture) + "00";
            string en = e.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);

            cancel = false;
            running = true;
            btnGo.Enabled = false;
            Task.Run(delegate
            {
                try { Run(st, en, kind, cam, folder); }
                catch (Exception ex)
                {
                    if (!cancel) { Log.Write("Download: " + ex.Message); SetStatus("Error: " + ex.Message); }
                    else SetStatus("Dibatalkan.");
                }
                finally
                {
                    running = false;
                    Ui.Post(this, delegate { btnGo.Enabled = true; });
                }
            });
        }

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
            return Path.GetFileName(name); // cegah path traversal dari server
        }

        static bool Finish(ref FileStream cur, ref string curPath, string finalName, string folder)
        {
            if (cur == null) return false;
            cur.Dispose();
            cur = null;
            string dest = Path.Combine(folder, finalName);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(curPath, dest);
            Log.Write("Download selesai: " + dest);
            curPath = null;
            return true;
        }

        void Run(string st, string en, string kind, int cam, string folder)
        {
            Directory.CreateDirectory(folder);
            SetStatus("Meminta data...");
            req = Sessions.Util.Request("httpdl.cgi",
                "STARTTIME=" + st + "&ENDTIME=" + en + "&KIND=" + kind + "&CAM=" + cam + "&PC=AS60", 60000);
            req.ReadWriteTimeout = 120000;

            // Salinan respon apa adanya: untuk LIST selalu disimpan utuh (isinya tidak dirinci di spec);
            // untuk jenis lain hanya bagian awal, dan hanya disimpan bila tidak ada file yang dihasilkan.
            bool isList = kind == "LIST";
            string rawPath = Path.Combine(folder, "respon_" + kind + "_cam" + cam + "_" + st + ".bin");

            FileStream cur = null;
            string curPath = null;
            long total = 0, received = 0;
            int files = 0;
            string reason = null, parseError = null, type = "";
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream net = resp.GetResponseStream())
                using (var raw = new FileStream(rawPath, FileMode.Create, FileAccess.Write))
                using (var stream = new CaptureStream(net, raw, isList ? long.MaxValue : 64 * 1024))
                {
                    type = "HTTP " + (int)resp.StatusCode + ", Content-Type: " + (string.IsNullOrEmpty(resp.ContentType) ? "(kosong)" : resp.ContentType);
                    Log.Write("Download " + kind + " CAM " + cam + " " + st + " - " + en + " -> " + type);
                    try
                    {
                        var mr = new MultipartReader(stream);
                        MultipartPart p;
                        while (!cancel && (p = mr.ReadPart()) != null)
                        {
                            string temp = FileNameOf(p.H("X-Temp-Filename"));
                            string prev = FileNameOf(p.H("X-Prev-Filename"));
                            string status = p.H("X-RecData-Satus");
                            if (status.Length == 0) status = p.H("X-RecData-Status");

                            if (status == "1") reason = "Tidak ada rekaman pada rentang waktu itu. Coba ganti pilihan \"Waktu input dianggap\" (UTC / lokal) atau pilih jam yang pasti ada rekamannya.";
                            if (status == "2") reason = "Codec tidak kompatibel (MP4 tidak bisa untuk JPEG). Pilih N3.";
                            if (status == "3") reason = "NVR sedang sibuk (terlalu banyak proses bersamaan). Coba lagi nanti.";
                            if (reason != null) break;

                            if (prev.Length > 0 && temp.Length > 0 && Finish(ref cur, ref curPath, prev, folder)) files++;
                            if (temp.Length > 0 && cur == null)
                            {
                                curPath = Path.Combine(folder, temp);
                                cur = new FileStream(curPath, FileMode.Create, FileAccess.Write);
                            }
                            if (cur != null && p.Body.Length > 0)
                            {
                                cur.Write(p.Body, 0, p.Body.Length);
                                total += p.Body.Length;
                                SetStatus("Mengunduh... " + (total / 1024) + " KB");
                            }
                            if (prev.Length > 0 && temp.Length == 0 && Finish(ref cur, ref curPath, prev, folder)) files++;
                        }
                    }
                    catch (Exception ex)
                    {
                        parseError = ex.Message;
                        Log.Write("Download: " + ex.Message);
                    }
                    if (isList && !cancel) stream.Drain();   // ambil sisa respon walaupun pembacaan multipart berhenti lebih awal
                    received = stream.Received;
                }
            }
            finally
            {
                if (cur != null)
                {
                    cur.Dispose();
                    Log.Write("Download tidak lengkap, file sementara: " + curPath);
                }
            }
            if (cancel) { SetStatus("Dibatalkan."); return; }

            bool keepRaw = received > 0 && (isList || (files == 0 && total == 0));
            if (!keepRaw) { try { File.Delete(rawPath); } catch { } }

            string msg;
            if (reason != null) msg = reason;
            else if (files == 0 && curPath == null) msg = "Tidak ada file yang dihasilkan. " + type + ", diterima " + received + " byte.";
            else msg = files > 0 ? "Selesai: " + files + " file, " + (total / 1024) + " KB -> " + folder : "Download belum selesai.";
            if (curPath != null) msg += " File sementara (belum lengkap): " + curPath;
            if (parseError != null) msg += " Kesalahan: " + parseError;
            if (keepRaw) msg += "\nRespon asli NVR disimpan: " + rawPath;
            Log.Write("Download: " + msg);
            SetStatus(msg);
        }

        public void Shutdown()
        {
            Cancel();
        }
    }
}
