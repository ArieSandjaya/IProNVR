using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Jendela Multiview (non-modal): tampilan 1x1 sampai 5x5, tiap kotak (slot) memuat satu kamera dari NVR mana pun
    /// di database. Tiap jendela punya konfigurasi sendiri (tabel multiview/multiview_slot di SQLite, termasuk posisi
    /// jendela/monitor), sehingga bisa dibuka banyak jendela sekaligus di beberapa monitor. Klik dua kali slot kosong = pilih
    /// kamera; klik dua kali video = jendela sendiri. Satu UID maks. 16 stream: tiap NVR otomatis memakai UID tambahan.</summary>
    public class MultiviewForm : Form
    {
        const int MaxCells = 25;
        const int MaxStreamsPerUid = 16;
        // lebar dekode yang disarankan per ukuran grid (indeks = sisi grid - 1); 0 = ukuran asli
        static readonly int[] SuggestWidth = { 0, 960, 640, 480, 384 };

        class Slot
        {
            public long NvrId;
            public int Channel;
            public string CamName = "";
            public string Label = "";
            public Panel Host;
            public LiveTile Tile;
            public NvrSession Sess;
            public bool Assigned { get { return NvrId > 0 && Channel > 0; } }
        }

        class CamItem
        {
            public CameraRecord Cam;
            public CamItem(CameraRecord c) { Cam = c; }
            public override string ToString()
            {
                return "CAM " + Cam.Channel + (string.IsNullOrEmpty(Cam.Name) ? "" : " - " + Cam.Name) + "   [" + Db.StatusText(Cam.RegStatus) + "]";
            }
        }

        readonly Slot[] slots = new Slot[MaxCells];
        readonly Dictionary<long, List<NvrSession>> sessions = new Dictionary<long, List<NvrSession>>();
        Dictionary<long, NvrRecord> nvrMap = new Dictionary<long, NvrRecord>();
        // kamera per NVR (IP, URL RTSP) untuk menyusun sumber substream; dikosongkan tiap Mulai supaya perubahan terbaru terbaca
        readonly Dictionary<long, Dictionary<int, CameraRecord>> camCache = new Dictionary<long, Dictionary<int, CameraRecord>>();
        readonly List<long> nvrComboIds = new List<long>();

        // jendela yang sedang terbuka, per nama konfigurasi
        static readonly Dictionary<string, MultiviewForm> open = new Dictionary<string, MultiviewForm>(StringComparer.OrdinalIgnoreCase);

        string cfgName;
        TableLayoutPanel grid;
        Control topPanel;
        MenuStrip menu;
        ComboBox cmbGrid, cmbFormat, cmbNvr;
        NumericUpDown numW, numGap;
        Label lblState;

        int n = 3;          // sisi grid
        int fmtIndex = 0;   // indeks LiveFormats.Items: 0 = H.264, 1 = H.265
        int decW = 640;
        int gap = 1000;
        bool wasRunning;     // dari database: stream berjalan saat terakhir ditutup
        bool userClosed;     // jendela ditutup pengguna (bukan karena aplikasi keluar)
        bool appExiting;
        bool running;
        bool suppress = true;
        bool deleted;

        // posisi jendela
        bool hasWin;
        Rectangle winRect;
        bool winMax;
        bool winFull;
        bool full;
        Rectangle fsBounds;
        FormWindowState fsState;

        public MultiviewForm(string name)
        {
            cfgName = name;
            Text = "Multiview - " + cfgName;
            KeyPreview = true;
            for (int i = 0; i < MaxCells; i++) slots[i] = new Slot();
            LoadLayout();

            if (hasWin && OnAnyScreen(winRect))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = winRect;
            }
            else
            {
                ClientSize = new Size(1100, 680);
                StartPosition = FormStartPosition.CenterScreen;
            }

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var top = Ui.Flow();
            topPanel = top;
            cmbGrid = Ui.Combo(new string[] { "1 x 1", "2 x 2", "3 x 3", "4 x 4", "5 x 5" }, n - 1, 70);
            cmbFormat = Ui.Combo(LiveFormats.Items, fmtIndex, 80);
            numW = Ui.Num(0, 1920, decW, 65);
            numGap = Ui.Num(0, 5000, gap, 65);
            cmbNvr = Ui.Combo(new string[] { "Semua NVR" }, 0, 170);
            nvrComboIds.Add(0);
            lblState = Ui.Lbl("");

            top.Controls.Add(Ui.Lbl("Tampilan:"));
            top.Controls.Add(cmbGrid);
            top.Controls.Add(Ui.Lbl("Codec NVR:"));
            top.Controls.Add(cmbFormat);
            top.Controls.Add(Ui.Lbl("Lebar dekode px (0=asli):"));
            top.Controls.Add(numW);
            top.Controls.Add(Ui.Lbl("Jeda antar kamera ms:"));
            top.Controls.Add(numGap);
            top.Controls.Add(Ui.Btn("Mulai", delegate { StartAll(); }));
            top.Controls.Add(Ui.Btn("Stop", delegate { StopAll(null); }));
            top.Controls.Add(Ui.Lbl("|"));
            top.Controls.Add(Ui.Lbl("Isi otomatis dari:"));
            top.Controls.Add(cmbNvr);
            top.Controls.Add(Ui.Btn("Isi Otomatis", delegate { AutoFill(); }));
            top.Controls.Add(Ui.Btn("Kosongkan Semua", delegate { ClearAll(); }));
            top.Controls.Add(Ui.Btn("Layar Penuh (F11)", delegate { SetFullscreen(!full); }));
            top.Controls.Add(lblState);
            root.Controls.Add(top, 0, 0);

            grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.BackColor = Color.FromArgb(8, 9, 11);
            root.Controls.Add(grid, 0, 1);
            Controls.Add(root);

            BuildMenu();
            Theme.Apply(this, true);
            LoadNvrMap();
            RelabelSlots();
            BuildGrid();

            cmbGrid.SelectedIndexChanged += delegate { OnGridChanged(); };
            cmbFormat.SelectedIndexChanged += delegate { if (!suppress) SaveLayout(); };
            numW.ValueChanged += delegate { if (!suppress) SaveLayout(); };
            numGap.ValueChanged += delegate { if (!suppress) SaveLayout(); };
            Shown += OnShownInit;
            FormClosing += delegate { if (!appExiting) userClosed = true; Shutdown(false); };
            FormClosed += delegate
            {
                MultiviewForm cur;
                if (open.TryGetValue(cfgName, out cur) && cur == this) open.Remove(cfgName);
            };
            suppress = false;

            open[cfgName] = this;
            SaveLayout(); // membuat/menandai record di database (jendela terbuka)
        }

        void OnShownInit(object sender, EventArgs e)
        {
            RefreshNvrs();
            if (winMax) WindowState = FormWindowState.Maximized;
            if (winFull) SetFullscreen(true);
            if (wasRunning)
            {
                BeginInvoke(new Action(delegate
                {
                    for (int i = 0; i < n * n; i++)
                    {
                        if (slots[i].Assigned) { StartAll(); break; }
                    }
                }));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11) { SetFullscreen(!full); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && full) { SetFullscreen(false); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // ===================== menu, layar penuh, monitor =====================

        void BuildMenu()
        {
            menu = new MenuStrip();
            var m = new ToolStripMenuItem("Multiview");
            m.DropDownItems.Add("Ganti nama...", null, delegate { RenameConfig(); });
            m.DropDownItems.Add("Hapus konfigurasi ini...", null, delegate { DeleteConfig(); });
            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.Add("Layar penuh / normal (F11)", null, delegate { SetFullscreen(!full); });
            var mon = new ToolStripMenuItem("Pindahkan ke monitor");
            mon.DropDownOpening += delegate { FillMonitorMenu(mon); };
            mon.DropDownItems.Add("(memuat...)");
            m.DropDownItems.Add(mon);
            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.Add("Tutup jendela ini", null, delegate { Close(); });
            menu.Items.Add(m);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        void FillMonitorMenu(ToolStripMenuItem mon)
        {
            mon.DropDownItems.Clear();
            Screen cur = Screen.FromControl(this);
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                Screen sc = all[i];
                var it = new ToolStripMenuItem("Monitor " + (i + 1) + " (" + sc.Bounds.Width + "x" + sc.Bounds.Height + ")" + (sc.Primary ? " - utama" : ""));
                it.Checked = sc.DeviceName == cur.DeviceName;
                it.Click += delegate { MoveToScreen(sc); };
                mon.DropDownItems.Add(it);
            }
            Theme.StyleItems(mon.DropDownItems, true); // item dinamis perlu warna teks tema gelap
        }

        static bool OnAnyScreen(Rectangle r)
        {
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle x = Rectangle.Intersect(sc.WorkingArea, r);
                if (x.Width >= 100 && x.Height >= 60) return true;
            }
            return false;
        }

        void SetFullscreen(bool on)
        {
            if (on == full) return;
            if (on)
            {
                fsState = WindowState;
                fsBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Screen scr = Screen.FromControl(this);
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = scr.Bounds;
                menu.Visible = false;
                topPanel.Visible = false;
                full = true;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                menu.Visible = true;
                topPanel.Visible = true;
                Bounds = fsBounds;
                WindowState = fsState == FormWindowState.Minimized ? FormWindowState.Normal : fsState;
                full = false;
            }
            SaveLayout();
        }

        void MoveToScreen(Screen scr)
        {
            bool wasFull = full;
            if (wasFull) SetFullscreen(false);
            FormWindowState st = WindowState;
            WindowState = FormWindowState.Normal;
            Rectangle wa = scr.WorkingArea;
            int w = Math.Min(Width, wa.Width - 40);
            int h = Math.Min(Height, wa.Height - 40);
            Bounds = new Rectangle(wa.X + 20, wa.Y + 20, w, h);
            if (st == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            if (wasFull) SetFullscreen(true);
            SaveLayout();
        }

        void RenameConfig()
        {
            string s = Ui.Ask("Ganti nama multiview", "Nama baru:", cfgName);
            if (s == null) return;
            s = Sanitize(s);
            if (s.Length == 0 || s == cfgName) return;
            bool taken = false;
            foreach (string nm in ListNames())
            {
                if (string.Equals(nm, s, StringComparison.OrdinalIgnoreCase) && !string.Equals(nm, cfgName, StringComparison.OrdinalIgnoreCase)) taken = true;
            }
            if (taken)
            {
                MessageBox.Show("Sudah ada multiview bernama '" + s + "'.");
                return;
            }
            try
            {
                Db.RenameMultiview(cfgName, s);
                open.Remove(cfgName);
                cfgName = s;
                open[cfgName] = this;
                Text = "Multiview - " + cfgName;
                SaveLayout();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal mengganti nama: " + ex.Message);
            }
        }

        void DeleteConfig()
        {
            DialogResult a = MessageBox.Show("Hapus konfigurasi multiview '" + cfgName + "' dan tutup jendela ini?",
                "Hapus multiview", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (a != DialogResult.Yes) return;
            deleted = true;
            try { Db.DeleteMultiview(cfgName); } catch (Exception ex) { Log.Write("Multiview: gagal menghapus: " + ex.Message); }
            Close();
        }

        // ===================== daftar konfigurasi (dipakai MainForm) =====================

        public static string Sanitize(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 80 ? t.Substring(0, 80).Trim() : t;
        }

        public static List<string> ListNames()
        {
            try { return Db.ListMultiviewNames(); }
            catch (Exception ex)
            {
                Log.Write("Multiview: database tidak terbaca: " + ex.Message);
                return new List<string>();
            }
        }

        /// <summary>Saat aplikasi dijalankan: buka lagi semua jendela multiview yang masih terbuka saat aplikasi terakhir ditutup
        /// (posisi, monitor, layar penuh, dan stream yang berjalan dipulihkan dari database).</summary>
        public static void RestoreOpenWindows()
        {
            List<string> names;
            try { names = Db.ListOpenMultiviewNames(); }
            catch (Exception ex)
            {
                Log.Write("Multiview: gagal memulihkan jendela: " + ex.Message);
                return;
            }
            foreach (string nm in names)
            {
                try { OpenNamed(nm); }
                catch (Exception ex) { Log.Write("Multiview '" + nm + "': " + ex.Message); }
            }
        }

        public static bool IsOpen(string name)
        {
            return open.ContainsKey(name);
        }

        /// <summary>Buka jendela multiview tersimpan (atau tampilkan yang sudah terbuka).</summary>
        public static void OpenNamed(string name)
        {
            MultiviewForm f;
            if (open.TryGetValue(name, out f))
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Activate();
                return;
            }
            new MultiviewForm(name).Show();
        }

        /// <summary>Buat multiview baru dengan konfigurasi kosong sendiri.</summary>
        public static void CreateNew()
        {
            int k = 1;
            List<string> existing = ListNames();
            while (existing.Exists(delegate (string x) { return string.Equals(x, "Multiview " + k, StringComparison.OrdinalIgnoreCase); })) k++;
            string s = Ui.Ask("Multiview baru", "Nama multiview:", "Multiview " + k);
            if (s == null) return;
            s = Sanitize(s);
            if (s.Length == 0) return;
            if (existing.Exists(delegate (string x) { return string.Equals(x, s, StringComparison.OrdinalIgnoreCase); }))
                MessageBox.Show("Sudah ada multiview bernama '" + s + "'. Dibuka saja.");
            OpenNamed(s);
        }

        public static void CloseAll()
        {
            foreach (MultiviewForm f in new List<MultiviewForm>(open.Values))
            {
                try { f.Close(); } catch { }
            }
        }

        /// <summary>Dipanggil saat aplikasi utama ditutup: simpan posisi dan hentikan semua stream (sinkron).</summary>
        public static void ShutdownAll()
        {
            foreach (MultiviewForm f in new List<MultiviewForm>(open.Values))
            {
                try { f.appExiting = true; f.Shutdown(true); } catch { }
            }
        }

        // ===================== data NVR =====================

        void LoadNvrMap()
        {
            try
            {
                var map = new Dictionary<long, NvrRecord>();
                foreach (NvrRecord r in Db.ListNvr()) map[r.Id] = r;
                nvrMap = map;
            }
            catch (Exception ex)
            {
                Log.Write("Multiview: database NVR tidak terbaca: " + ex.Message);
            }
        }

        /// <summary>Label slot memakai nama NVR dari database (file konfigurasi hanya menyimpan id NVR).</summary>
        void RelabelSlots()
        {
            for (int i = 0; i < MaxCells; i++)
            {
                NvrRecord r;
                if (slots[i].Assigned && nvrMap.TryGetValue(slots[i].NvrId, out r))
                    slots[i].Label = MakeLabel(r.Name, slots[i].Channel, slots[i].CamName);
            }
        }

        void RefreshNvrs()
        {
            LoadNvrMap();
            long keep = 0;
            int si = cmbNvr.SelectedIndex;
            if (si >= 0 && si < nvrComboIds.Count) keep = nvrComboIds[si];
            suppress = true;
            cmbNvr.Items.Clear();
            nvrComboIds.Clear();
            cmbNvr.Items.Add("Semua NVR");
            nvrComboIds.Add(0);
            foreach (NvrRecord r in nvrMap.Values)
            {
                cmbNvr.Items.Add(r.Name + " (" + r.Host + ")");
                nvrComboIds.Add(r.Id);
            }
            int sel = nvrComboIds.IndexOf(keep);
            cmbNvr.SelectedIndex = sel < 0 ? 0 : sel;
            suppress = false;
        }

        // ===================== grid =====================

        /// <summary>Bangun ulang kotak-kotak sebagai placeholder (hanya dipanggil saat tidak ada stream berjalan).</summary>
        void BuildGrid()
        {
            grid.SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in grid.Controls) old.Add(c);
            grid.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            grid.RowStyles.Clear();
            grid.ColumnStyles.Clear();
            grid.ColumnCount = n;
            grid.RowCount = n;
            for (int c = 0; c < n; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
            for (int r = 0; r < n; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / n));
            for (int i = 0; i < MaxCells; i++)
            {
                slots[i].Host = null;
                slots[i].Tile = null;
                slots[i].Sess = null;
            }
            for (int i = 0; i < n * n; i++)
            {
                Panel ph = MakePlaceholder(i);
                grid.Controls.Add(ph, i % n, i / n);
                slots[i].Host = ph;
            }
            grid.ResumeLayout();
        }

        void ReplaceHost(int i, Panel host)
        {
            Panel old = slots[i].Host;
            if (old != null)
            {
                grid.Controls.Remove(old);
                old.Dispose();
            }
            grid.Controls.Add(host, i % n, i / n);
            slots[i].Host = host;
        }

        Panel MakePlaceholder(int idx)
        {
            Slot s = slots[idx];
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Margin = new Padding(1);
            p.BackColor = Theme.Dark.Surface;
            var l = new Label();
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.ForeColor = Theme.Dark.Muted;
            l.Text = s.Assigned
                ? s.Label + "\r\n(klik dua kali untuk ganti kamera)"
                : "Slot " + (idx + 1) + "\r\nklik dua kali untuk memilih kamera";
            p.Controls.Add(l);
            ContextMenuStrip m = MakeMenu(idx);
            p.ContextMenuStrip = m;
            l.ContextMenuStrip = m;
            p.DoubleClick += delegate { Choose(idx); };
            l.DoubleClick += delegate { Choose(idx); };
            return p;
        }

        Panel MakeRunningHost(int idx, LiveTile tile)
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Margin = new Padding(1);
            host.Padding = new Padding(0);
            tile.Caption = slots[idx].Label;
            tile.FillCell();          // video memenuhi seluruh kotak grid
            tile.Dock = DockStyle.Fill;
            ContextMenuStrip m = MakeMenu(idx);
            tile.SetMenu(m);
            tile.DoubleClicked += delegate { OpenWindow(idx); };
            host.Controls.Add(tile);
            return host;
        }

        ContextMenuStrip MakeMenu(int idx)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Pilih kamera...", null, delegate { Choose(idx); });
            m.Items.Add("Kosongkan slot", null, delegate { ClearSlot(idx); });
            m.Items.Add("Buka di jendela sendiri", null, delegate { OpenWindow(idx); });
            m.Items.Add("Simpan snapshot...", null, delegate { Snapshot(idx); });
            m.Items.Add("Layar penuh / normal (F11)", null, delegate { SetFullscreen(!full); });
            Theme.StyleMenu(m, true);
            return m;
        }

        void OnGridChanged()
        {
            if (suppress) return;
            n = cmbGrid.SelectedIndex + 1;
            suppress = true;
            numW.Value = SuggestWidth[n - 1];
            suppress = false;
            if (running) StopAll(null); else BuildGrid();
            SaveLayout();
        }

        // ===================== start / stop =====================

        void StartAll()
        {
            if (running)
            {
                StopAll(delegate { StartAll(); });
                return;
            }
            LoadNvrMap();
            camCache.Clear();
            int assigned = 0;
            for (int i = 0; i < n * n; i++)
            {
                if (slots[i].Assigned) assigned++;
            }
            if (assigned == 0)
            {
                MessageBox.Show("Belum ada kamera di slot. Klik dua kali sebuah slot, atau pakai 'Isi Otomatis'.");
                return;
            }
            running = true;
            SaveLayout();
            int k = 0;
            for (int i = 0; i < n * n; i++)
            {
                if (!slots[i].Assigned) continue;
                StartSlot(i, k * (int)numGap.Value);
                k++;
            }
            lblState.Text = "Berjalan: " + k + " kamera.";
        }

        void StartSlot(int i, int delayMs)
        {
            Slot s = slots[i];
            NvrRecord nvr;
            if (!nvrMap.TryGetValue(s.NvrId, out nvr))
            {
                Log.Write("Multiview slot " + (i + 1) + ": NVR tidak ada lagi di database.");
                return;
            }
            s.Sess = SessionFor(nvr, s.Channel);
            var tile = new LiveTile(s.Channel, s.Sess, LiveFormats.FromIndex(cmbFormat.SelectedIndex), (int)numW.Value);
            // Multiview memakai substream: substream NVR (bila parameternya diisi) -> RTSP substream kamera -> stream utama NVR
            tile.Sources = StreamPlan.ForMultiview(nvr, FindCamera(nvr.Id, s.Channel));
            tile.Dock = DockStyle.Fill;
            s.Tile = tile;
            ReplaceHost(i, MakeRunningHost(i, tile));
            tile.Start(delayMs);
        }

        CameraRecord FindCamera(long nvrId, int channel)
        {
            Dictionary<int, CameraRecord> map;
            if (!camCache.TryGetValue(nvrId, out map))
            {
                map = new Dictionary<int, CameraRecord>();
                try
                {
                    foreach (CameraRecord k in Db.ListCameras(nvrId)) map[k.Channel] = k;
                }
                catch (Exception ex)
                {
                    Log.Write("Multiview: daftar kamera NVR tidak terbaca: " + ex.Message);
                }
                camCache[nvrId] = map;
            }
            CameraRecord cam;
            return map.TryGetValue(channel, out cam) ? cam : null;
        }

        /// <summary>Sesi (UID) NVR yang masih punya tempat (maks. 16 stream per UID) dan belum memakai kanal yang sama.</summary>
        NvrSession SessionFor(NvrRecord nvr, int channel)
        {
            List<NvrSession> list;
            if (!sessions.TryGetValue(nvr.Id, out list))
            {
                list = new List<NvrSession>();
                sessions[nvr.Id] = list;
            }
            foreach (NvrSession se in list)
            {
                int count = 0;
                bool sameCam = false;
                for (int i = 0; i < MaxCells; i++)
                {
                    if (slots[i].Tile != null && slots[i].Sess == se)
                    {
                        count++;
                        if (slots[i].Channel == channel) sameCam = true;
                    }
                }
                if (count < MaxStreamsPerUid && !sameCam) return se;
            }
            var ns = new NvrSession("multiview " + nvr.Name + " #" + (list.Count + 1), nvr.ToEndpoint());
            list.Add(ns);
            return ns;
        }

        void StopAll(Action after)
        {
            var tiles = new List<LiveTile>();
            var sess = new List<NvrSession>();
            for (int i = 0; i < MaxCells; i++)
            {
                if (slots[i].Tile != null)
                {
                    slots[i].Tile.Halt();
                    tiles.Add(slots[i].Tile);
                }
            }
            foreach (KeyValuePair<long, List<NvrSession>> kv in sessions) sess.AddRange(kv.Value);
            sessions.Clear();
            running = false;
            BuildGrid(); // membuang kotak lama (termasuk tile) dan memasang placeholder
            SaveLayout();
            lblState.Text = "Berhenti.";
            Task.Run(delegate
            {
                foreach (LiveTile t in tiles)
                {
                    try { t.SendStop(); } catch { }
                }
                foreach (NvrSession se in sess)
                {
                    try { se.Logout(); } catch { }
                }
            }).ContinueWith(delegate
            {
                Ui.Post(this, delegate { if (after != null) after(); });
            });
        }

        /// <summary>Hentikan stream satu slot (STOP dikirim di latar) dan kembalikan placeholder.</summary>
        void DetachTile(int i)
        {
            LiveTile t = slots[i].Tile;
            if (t == null) return;
            t.Halt();
            slots[i].Tile = null;
            slots[i].Sess = null;
            ReplaceHost(i, MakePlaceholder(i)); // membuang kotak lama
            Task.Run(delegate { try { t.SendStop(); } catch { } });
        }

        /// <summary>Hentikan semua stream dan logout. wait=true: tunggu sampai selesai (dipakai saat aplikasi ditutup);
        /// false: STOP dan logout berjalan di latar supaya jendela cepat tertutup.</summary>
        public void Shutdown(bool wait)
        {
            if (!deleted) SaveLayout();
            var tiles = new List<LiveTile>();
            var sess = new List<NvrSession>();
            for (int i = 0; i < MaxCells; i++)
            {
                if (slots[i].Tile != null)
                {
                    slots[i].Tile.Halt();
                    tiles.Add(slots[i].Tile);
                    slots[i].Tile = null;
                }
            }
            foreach (KeyValuePair<long, List<NvrSession>> kv in sessions) sess.AddRange(kv.Value);
            sessions.Clear();
            running = false;
            if (tiles.Count == 0 && sess.Count == 0) return;
            Task t = Task.Run(delegate
            {
                foreach (LiveTile lt in tiles)
                {
                    try { lt.SendStop(); } catch { }
                }
                foreach (NvrSession se in sess)
                {
                    try { se.Logout(); } catch { }
                }
            });
            if (wait)
            {
                try { t.Wait(3500); } catch { }
            }
        }

        // ===================== isi slot =====================

        void Choose(int i)
        {
            LoadNvrMap();
            var list = new List<NvrRecord>(nvrMap.Values);
            PickResult pr = PickCamera(this, list, slots[i].NvrId);
            if (pr == null) return;

            bool hadTile = slots[i].Tile != null;
            if (hadTile) DetachTile(i);
            Slot s = slots[i];
            s.NvrId = pr.NvrId;
            s.Channel = pr.Channel;
            s.CamName = pr.CamName;
            s.Label = pr.Label;
            SaveLayout();
            if (running) StartSlot(i, hadTile ? 3200 : 0); // spec: STOP dan START berikutnya >= 3 dtk
            else ReplaceHost(i, MakePlaceholder(i));
        }

        void ClearSlot(int i)
        {
            if (slots[i].Tile != null) DetachTile(i);
            Slot s = slots[i];
            s.NvrId = 0;
            s.Channel = 0;
            s.CamName = "";
            s.Label = "";
            ReplaceHost(i, MakePlaceholder(i));
            SaveLayout();
        }

        void ClearAll()
        {
            if (running)
            {
                MessageBox.Show("Tekan Stop dulu.");
                return;
            }
            for (int i = 0; i < MaxCells; i++)
            {
                slots[i].NvrId = 0;
                slots[i].Channel = 0;
                slots[i].CamName = "";
                slots[i].Label = "";
            }
            BuildGrid();
            SaveLayout();
        }

        /// <summary>Isi slot berurutan dengan kamera berstatus 'terdaftar & normal' dari NVR terpilih (atau semua NVR).</summary>
        void AutoFill()
        {
            if (running)
            {
                MessageBox.Show("Tekan Stop dulu.");
                return;
            }
            LoadNvrMap();
            int si = cmbNvr.SelectedIndex;
            long only = (si >= 0 && si < nvrComboIds.Count) ? nvrComboIds[si] : 0;
            int cap = n * n;
            int filled = 0;
            try
            {
                foreach (NvrRecord r in nvrMap.Values)
                {
                    if (only != 0 && r.Id != only) continue;
                    foreach (CameraRecord k in Db.ListCameras(r.Id))
                    {
                        if (k.RegStatus != 1) continue;
                        if (filled >= cap) break;
                        SetSlot(filled, r, k.Channel, k.Name);
                        filled++;
                    }
                    if (filled >= cap) break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal membaca daftar kamera: " + ex.Message);
                return;
            }
            if (filled == 0)
            {
                MessageBox.Show("Tidak ada kamera berstatus 'Terdaftar & normal'. Ambil daftar kamera di tab Daftar NVR dulu.");
                return;
            }
            for (int i = filled; i < MaxCells; i++)
            {
                slots[i].NvrId = 0;
                slots[i].Channel = 0;
                slots[i].CamName = "";
                slots[i].Label = "";
            }
            BuildGrid();
            SaveLayout();
            lblState.Text = filled + " slot terisi. Tekan Mulai.";
        }

        void SetSlot(int i, NvrRecord nvr, int channel, string camName)
        {
            Slot s = slots[i];
            s.NvrId = nvr.Id;
            s.Channel = channel;
            s.CamName = camName ?? "";
            s.Label = MakeLabel(nvr.Name, channel, s.CamName);
        }

        static string MakeLabel(string nvrName, int channel, string camName)
        {
            return nvrName + " - CAM " + channel + (string.IsNullOrEmpty(camName) ? "" : " - " + camName);
        }

        // ===================== aksi tile =====================

        void OpenWindow(int i)
        {
            Slot s = slots[i];
            NvrRecord nvr;
            if (!s.Assigned || !nvrMap.TryGetValue(s.NvrId, out nvr))
            {
                MessageBox.Show("Slot ini belum berisi kamera.");
                return;
            }
            var f = new CameraViewForm(nvr, s.Channel, s.CamName);
            f.FormClosed += delegate { f.Dispose(); };
            f.Show(this);
        }

        void Snapshot(int i)
        {
            LiveTile t = slots[i].Tile;
            if (t == null || t.LastJpeg == null)
            {
                MessageBox.Show("Belum ada gambar pada slot ini.");
                return;
            }
            using (var d = new SaveFileDialog())
            {
                d.Filter = "JPEG|*.jpg";
                d.FileName = "snapshot_" + slots[i].NvrId + "_cam" + slots[i].Channel + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
                if (d.ShowDialog() == DialogResult.OK) File.WriteAllBytes(d.FileName, t.LastJpeg);
            }
        }

        // ===================== pilih kamera =====================

        class PickResult
        {
            public long NvrId;
            public int Channel;
            public string CamName = "";
            public string Label = "";
        }

        static PickResult PickCamera(IWin32Window owner, List<NvrRecord> nvrs, long currentNvr)
        {
            if (nvrs.Count == 0)
            {
                MessageBox.Show("Belum ada NVR terdaftar. Daftarkan NVR di tab 'Daftar NVR' dulu.");
                return null;
            }
            using (var f = new Form())
            {
                f.Text = "Pilih kamera untuk slot";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(440, 430);

                var cmb = new ComboBox();
                cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                cmb.Left = 10;
                cmb.Top = 10;
                cmb.Width = 420;
                foreach (NvrRecord r in nvrs) cmb.Items.Add(r.Name + "  (" + r.Host + ":" + r.Port + ")");

                var lst = new ListBox();
                lst.Left = 10;
                lst.Top = 40;
                lst.Width = 420;
                lst.Height = 330;

                var chk = new CheckBox();
                chk.Text = "Tampilkan juga kanal 'tidak terdaftar'";
                chk.Left = 10;
                chk.Top = 376;
                chk.Width = 300;

                var ok = new Button();
                ok.Text = "OK";
                ok.Left = 270;
                ok.Top = 396;
                ok.Width = 75;
                var cancel = new Button();
                cancel.Text = "Batal";
                cancel.Left = 355;
                cancel.Top = 396;
                cancel.Width = 75;
                cancel.DialogResult = DialogResult.Cancel;
                f.CancelButton = cancel;

                Action fill = delegate
                {
                    lst.Items.Clear();
                    int ix = cmb.SelectedIndex;
                    if (ix < 0) return;
                    try
                    {
                        foreach (CameraRecord k in Db.ListCameras(nvrs[ix].Id))
                        {
                            if (k.RegStatus == 0 && !chk.Checked) continue;
                            lst.Items.Add(new CamItem(k));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Gagal membaca kamera: " + ex.Message);
                    }
                    if (lst.Items.Count > 0) lst.SelectedIndex = 0;
                };
                Action accept = delegate
                {
                    if (lst.SelectedItem == null)
                    {
                        MessageBox.Show("Pilih sebuah kamera.");
                        return;
                    }
                    f.DialogResult = DialogResult.OK;
                };
                cmb.SelectedIndexChanged += delegate { fill(); };
                chk.CheckedChanged += delegate { fill(); };
                lst.DoubleClick += delegate { if (lst.SelectedItem != null) accept(); };
                ok.Click += delegate { accept(); };

                f.Controls.Add(cmb);
                f.Controls.Add(lst);
                f.Controls.Add(chk);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);

                int start = 0;
                for (int i = 0; i < nvrs.Count; i++)
                {
                    if (nvrs[i].Id == currentNvr) start = i;
                }
                cmb.SelectedIndex = start;

                Theme.Apply(f, false);
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                CamItem it = (CamItem)lst.SelectedItem;
                NvrRecord nvr = nvrs[cmb.SelectedIndex];
                var pr = new PickResult();
                pr.NvrId = nvr.Id;
                pr.Channel = it.Cam.Channel;
                pr.CamName = it.Cam.Name ?? "";
                pr.Label = MakeLabel(nvr.Name, pr.Channel, pr.CamName);
                return pr;
            }
        }

        // ===================== simpan / muat layout =====================

        void SaveLayout()
        {
            if (deleted) return;
            try
            {
                var m = new MultiviewRecord();
                m.Name = cfgName;
                m.GridSize = n;
                m.Format = LiveFormats.ToStored(cmbFormat.SelectedIndex);
                m.DecodeWidth = (int)numW.Value;
                m.GapMs = (int)numGap.Value;

                Rectangle r;
                FormWindowState st;
                if (full) { r = fsBounds; st = fsState; }
                else
                {
                    st = WindowState;
                    r = st == FormWindowState.Normal ? Bounds : RestoreBounds;
                }
                m.HasWindow = r.Width >= 300 && r.Height >= 200;
                m.X = r.X;
                m.Y = r.Y;
                m.W = r.Width;
                m.H = r.Height;
                m.Maximized = st == FormWindowState.Maximized;
                m.Fullscreen = full;
                m.WasOpen = !userClosed;
                m.WasRunning = running;
                for (int i = 0; i < MaxCells; i++)
                {
                    if (!slots[i].Assigned) continue;
                    var sr = new MultiviewSlotRecord();
                    sr.Index = i;
                    sr.NvrId = slots[i].NvrId;
                    sr.Channel = slots[i].Channel;
                    sr.CamName = slots[i].CamName;
                    m.Slots.Add(sr);
                }
                Db.SaveMultiview(m);
            }
            catch (Exception ex)
            {
                Log.Write("Multiview: konfigurasi tidak tersimpan ke database: " + ex.Message);
            }
        }

        void LoadLayout()
        {
            try
            {
                MultiviewRecord m = Db.GetMultiview(cfgName);
                if (m == null) return;
                n = Math.Max(1, Math.Min(5, m.GridSize));
                fmtIndex = LiveFormats.ToIndex(m.Format);
                decW = Math.Max(0, Math.Min(1920, m.DecodeWidth));
                gap = Math.Max(0, Math.Min(5000, m.GapMs));
                if (m.HasWindow && m.W >= 300 && m.H >= 200)
                {
                    hasWin = true;
                    winRect = new Rectangle(m.X, m.Y, m.W, m.H);
                }
                winMax = m.Maximized;
                winFull = m.Fullscreen;
                wasRunning = m.WasRunning;
                foreach (MultiviewSlotRecord sr in m.Slots)
                {
                    if (sr.Index < 0 || sr.Index >= MaxCells) continue;
                    slots[sr.Index].NvrId = sr.NvrId;
                    slots[sr.Index].Channel = sr.Channel;
                    slots[sr.Index].CamName = sr.CamName ?? "";
                    slots[sr.Index].Label = "NVR#" + sr.NvrId + " - CAM " + sr.Channel + (slots[sr.Index].CamName.Length > 0 ? " - " + slots[sr.Index].CamName : "");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Multiview: konfigurasi tidak terbaca dari database: " + ex.Message);
            }
        }
    }
}
