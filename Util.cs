using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace IproNvrTool
{
    public interface IShutdown
    {
        void Shutdown();
    }

    /// <summary>Log aplikasi: ditulis ke file harian %AppData%\IproNvrTool\logs\yyyy-MM-dd.log (tanpa tampilan di form).
    /// Komponen lain tetap bisa berlangganan event Line. File lebih dari 14 hari dihapus otomatis.</summary>
    public static class Log
    {
        const int KeepDays = 14;
        const long MaxFileBytes = 20L * 1024 * 1024;
        static readonly object FileLock = new object();
        static string _dir;
        static bool _cleaned;

        public static event Action<string> Line;

        /// <summary>Folder file log.</summary>
        public static string Directory
        {
            get
            {
                if (_dir == null) _dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool", "logs");
                return _dir;
            }
        }

        public static void Write(string msg)
        {
            DateTime now = DateTime.Now;
            WriteFile(now, msg);
            Action<string> h = Line;
            if (h != null) h(now.ToString("HH:mm:ss") + "  " + msg);
        }

        static void WriteFile(DateTime now, string msg)
        {
            try
            {
                lock (FileLock)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    if (!_cleaned)
                    {
                        _cleaned = true;
                        Cleanup(now);
                    }
                    string path = System.IO.Path.Combine(Directory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > MaxFileBytes) return; // batas ukuran per hari
                    System.IO.File.AppendAllText(path,
                        now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
            catch { } // gagal menulis log tidak boleh mengganggu aplikasi
        }

        static void Cleanup(DateTime now)
        {
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(Directory, "*.log"))
                {
                    if ((now - System.IO.File.GetLastWriteTime(f)).TotalDays > KeepDays) System.IO.File.Delete(f);
                }
            }
            catch { }
        }
    }

    public static class Timef
    {
        /// <summary>Format yymmddhhmmssff (ff=00), waktu UTC, sesuai spec.</summary>
        public static string Format(DateTime utc)
        {
            return utc.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + "00";
        }
    }

    /// <summary>Antrian satu thread; menjaga urutan perintah (mis. PTZ gerak lalu stop)
    /// dan jarak minimal antar perintah (spec: kontrol kamera >= 0,2 detik).</summary>
    public class SerialWorker
    {
        readonly BlockingCollection<Action> _q = new BlockingCollection<Action>();
        readonly int _minGapMs;

        public SerialWorker(int minGapMs)
        {
            _minGapMs = minGapMs;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        public int Pending { get { return _q.Count; } }

        public void Post(Action a)
        {
            try { _q.Add(a); } catch (InvalidOperationException) { }
        }

        /// <summary>Hentikan thread pekerja (setelah antrian terakhir selesai).</summary>
        public void Close()
        {
            try { _q.CompleteAdding(); } catch { }
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            long last = -100000;
            foreach (Action a in _q.GetConsumingEnumerable())
            {
                long wait = _minGapMs - (sw.ElapsedMilliseconds - last);
                if (wait > 0) Thread.Sleep((int)wait);
                try { a(); }
                catch (Exception ex) { Log.Write("Error: " + ex.Message); }
                last = sw.ElapsedMilliseconds;
            }
        }
    }

    public static class Ui
    {
        public static Label Lbl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(6, 8, 2, 0);
            return l;
        }

        public static Button Btn(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(3);
            if (click != null) b.Click += click;
            return b;
        }

        public static TextBox Txt(string text, int width)
        {
            var t = new TextBox();
            t.Text = text;
            t.Width = width;
            t.Margin = new Padding(3, 5, 3, 3);
            return t;
        }

        public static NumericUpDown Num(int min, int max, int value, int width)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = width;
            n.Margin = new Padding(3, 5, 3, 3);
            return n;
        }

        public static ComboBox Combo(string[] items, int selected, int width)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            c.SelectedIndex = selected;
            c.Width = width;
            c.Margin = new Padding(3, 5, 3, 3);
            return c;
        }

        public static FlowLayoutPanel Flow()
        {
            var f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.Dock = DockStyle.Fill;
            f.WrapContents = true;
            return f;
        }

        /// <summary>Jalankan di thread UI dengan aman (abaikan jika control sudah ditutup).</summary>
        public static void Post(Control c, Action a)
        {
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            try
            {
                if (c.InvokeRequired) c.BeginInvoke(a);
                else a();
            }
            catch (InvalidOperationException) { } // termasuk ObjectDisposedException
        }

        public static Image DecodeImage(byte[] jpeg)
        {
            using (var ms = new System.IO.MemoryStream(jpeg))
            using (var tmp = Image.FromStream(ms))
            {
                return new Bitmap(tmp);
            }
        }

        /// <summary>Dialog kecil satu isian teks; null bila dibatalkan.</summary>
        public static string Ask(string title, string label, string def)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(330, 105);
                var l = new Label();
                l.Text = label;
                l.Left = 10;
                l.Top = 12;
                l.Width = 310;
                var t = new TextBox();
                t.Left = 10;
                t.Top = 36;
                t.Width = 305;
                t.Text = def;
                var ok = new Button();
                ok.Text = "OK";
                ok.Left = 160;
                ok.Top = 68;
                ok.Width = 75;
                ok.DialogResult = DialogResult.OK;
                var cancel = new Button();
                cancel.Text = "Batal";
                cancel.Left = 240;
                cancel.Top = 68;
                cancel.Width = 75;
                cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.Add(l);
                f.Controls.Add(t);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                Theme.Apply(f, false);
                return f.ShowDialog() == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        public static string BrowseFolder(string start)
        {
            using (var d = new FolderBrowserDialog())
            {
                if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start)) d.SelectedPath = start;
                if (d.ShowDialog() == DialogResult.OK) return d.SelectedPath;
            }
            return null;
        }
    }
}
