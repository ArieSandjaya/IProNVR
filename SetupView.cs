using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Dialog pilih kamera (NVR -> kamera).</summary>
    public class CameraPicker : Form
    {
        readonly TreeView tv = new TreeView();
        public CamRef Result;

        public CameraPicker()
        {
            Text = "Pilih kamera";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Asm.S(360), Asm.S(480));
            tv.Dock = DockStyle.Fill;
            foreach (NvrRecord n in Dir.Nvrs)
            {
                var nn = new TreeNode(n.Name);
                foreach (CameraRecord c in Dir.Registered(n.Id))
                {
                    var cn = new TreeNode(c.Channel + " : " + Dir.CamName(n.Id, c.Channel));
                    cn.Tag = new CamRef(n.Id, c.Channel);
                    nn.Nodes.Add(cn);
                }
                tv.Nodes.Add(nn);
            }
            tv.ExpandAll();
            tv.NodeMouseDoubleClick += delegate(object s, TreeNodeMouseClickEventArgs e) { Pick(e.Node); };
            var ok = new Button();
            ok.Text = "OK";
            ok.Dock = DockStyle.Bottom;
            ok.Height = Asm.S(32);
            ok.Click += delegate { Pick(tv.SelectedNode); };
            Controls.Add(tv);
            Controls.Add(ok);
            Theme.ApplyTo(this, true);
        }

        void Pick(TreeNode n)
        {
            if (n == null) return;
            CamRef r = n.Tag as CamRef;
            if (r == null) return;
            Result = r;
            DialogResult = DialogResult.OK;
        }

        public static CamRef Choose(IWin32Window owner)
        {
            using (var f = new CameraPicker())
            {
                return f.ShowDialog(owner) == DialogResult.OK ? f.Result : null;
            }
        }
    }

    /// <summary>Halaman pengaturan berbaris label+kontrol dengan tombol Set (menyimpan semua sekaligus).</summary>
    public class SettingsPage : Panel
    {
        readonly TableLayoutPanel table = new TableLayoutPanel();
        readonly List<Action> appliers = new List<Action>();
        readonly Label lblMsg = new Label();
        public event Action Applied;

        public SettingsPage()
        {
            Dock = DockStyle.Fill;
            Tag = "nostyle";
            BackColor = Asm.Panel2;
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.Padding = new Padding(Asm.S(12));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Asm.S(300)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.Tag = "nostyle";
            table.BackColor = Asm.Panel2;
            var scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.Tag = "nostyle";
            scroll.Controls.Add(table);

            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = Asm.S(44);
            bottom.Tag = "nostyle";
            bottom.BackColor = Asm.Panel;
            var set = new AsmButton();
            set.Label = "Set";
            set.Icon = AsmIcon.Setting;
            set.Width = Asm.S(110);
            set.Height = Asm.S(30);
            set.Location = new Point(Asm.S(12), Asm.S(7));
            set.Click += delegate { Apply(); };
            lblMsg.Location = new Point(Asm.S(140), Asm.S(12));
            lblMsg.AutoSize = true;
            lblMsg.ForeColor = Asm.Muted;
            lblMsg.Tag = "nostyle";
            bottom.Controls.Add(set);
            bottom.Controls.Add(lblMsg);
            Controls.Add(scroll);
            Controls.Add(bottom);
        }

        void Row(string label, Control c, string tip)
        {
            var l = new Label();
            l.Text = label;
            l.ForeColor = Asm.Text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 8, 8, 4);
            l.Tag = "nostyle";
            c.Margin = new Padding(0, 4, 0, 4);
            if (tip != null && AppSettings.Current.SetupTips) new ToolTip().SetToolTip(c, tip);
            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(l, 0, r);
            table.Controls.Add(c, 1, r);
        }

        public void Heading(string text)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Asm.Accent;
            l.Font = Asm.F(9.5f, true);
            l.AutoSize = true;
            l.Margin = new Padding(0, 14, 0, 2);
            l.Tag = "nostyle";
            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(l, 0, r);
            table.SetColumnSpan(l, 2);
        }

        public void Check(string label, bool value, Action<bool> set, string tip)
        {
            var c = new CheckBox();
            c.Checked = value;
            c.AutoSize = true;
            c.ForeColor = Asm.Text;
            Row(label, c, tip);
            appliers.Add(delegate { set(c.Checked); });
        }

        public void Num(string label, int min, int max, int value, Action<int> set, string tip)
        {
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = Asm.S(90);
            Row(label, n, tip);
            appliers.Add(delegate { set((int)n.Value); });
        }

        public void Text(string label, string value, Action<string> set, string tip)
        {
            var t = new TextBox();
            t.Text = value ?? "";
            t.Width = Asm.S(320);
            Row(label, t, tip);
            appliers.Add(delegate { set(t.Text.Trim()); });
        }

        public void Combo(string label, string[] items, string value, Action<string> set, string tip)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            int i = Array.IndexOf(items, value);
            c.SelectedIndex = i >= 0 ? i : 0;
            c.Width = Asm.S(200);
            Row(label, c, tip);
            appliers.Add(delegate { if (c.SelectedItem != null) set((string)c.SelectedItem); });
        }

        public void Button(string label, string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Click += click;
            Row(label, b, null);
        }

        public void Apply()
        {
            foreach (Action a in appliers) a();
            try { AppSettings.Current.Save(); lblMsg.Text = "Tersimpan."; }
            catch (Exception ex) { lblMsg.Text = "Gagal menyimpan: " + ex.Message; }
            if (Applied != null) Applied();
        }
    }

    /// <summary>Setup window: System, Device setting, User mng., Folder, Group, Sequence, Default screen, Map, Event, Maintenance.</summary>
    public class SetupView : UserControl
    {
        public static event Action SettingsApplied;
        readonly AsmTabControl tabs = new AsmTabControl();

        public SetupView()
        {
            Dock = DockStyle.Fill;
            BackColor = Asm.Panel;
            Tag = "nostyle";
            tabs.Dock = DockStyle.Fill;
            tabs.ItemSize = new Size(Asm.S(120), Asm.S(30));
            Controls.Add(tabs);
            AddPage("System", BuildSystem());
            AddPage("Device setting", BuildDevice());
            AddPage("User mng.", BuildUsers());
            AddPage("Folder", BuildFolder());
            AddPage("Group", BuildGroup());
            AddPage("Sequence", BuildSequence());
            AddPage("Default screen", BuildDefaultScreen());
            AddPage("Map", BuildMap());
            AddPage("Event", BuildEvent());
            AddPage("Maintenance", BuildMaintenance());
        }

        void AddPage(string title, Control c)
        {
            var p = new TabPage(title);
            p.BackColor = Asm.Panel2;
            c.Dock = DockStyle.Fill;
            p.Controls.Add(c);
            tabs.TabPages.Add(p);
        }

        static void Notify()
        {
            if (SettingsApplied != null) SettingsApplied();
        }

        // ================= System =================

        Control BuildSystem()
        {
            AppSettings s = AppSettings.Current;
            var p = new SettingsPage();
            p.Heading("Tampilan");
            p.Combo("Display magnification (%)", new string[] { "75", "100", "125", "150", "175", "200" }, s.AsmMagnification.ToString(),
                delegate(string v) { s.AsmMagnification = int.Parse(v); }, "Berlaku setelah aplikasi dibuka ulang.");
            p.Combo("Format tanggal", new string[] { "yyyy/MM/dd", "dd/MM/yyyy", "MM/dd/yyyy" }, s.DateFormat, delegate(string v) { s.DateFormat = v; }, null);
            p.Check("Jam 24 jam", s.Time24h, delegate(bool v) { s.Time24h = v; }, null);
            p.Check("Tampilkan nama kamera di area gambar", s.ShowCamNames, delegate(bool v) { s.ShowCamNames = v; }, null);
            p.Check("Tombol buka/tutup function panel", s.PanelHandle, delegate(bool v) { s.PanelHandle = v; }, null);
            p.Check("Keterangan item setup (tooltip)", s.SetupTips, delegate(bool v) { s.SetupTips = v; }, null);
            p.Check("Tab boleh dipisah jadi jendela (klik kanan tab)", s.SeparateTab, delegate(bool v) { s.SeparateTab = v; }, null);
            p.Heading("Operasi");
            p.Check("Konfirmasi sebelum membuka Live/Map window", s.ConfirmBeforeLive, delegate(bool v) { s.ConfirmBeforeLive = v; }, null);
            p.Num("Auto logout (menit, 0 = mati)", 0, 600, s.AutoLogoutMin, delegate(int v) { s.AutoLogoutMin = v; }, "Hanya berlaku bila login dipakai.");
            p.Check("Tampilkan login saat aplikasi dimulai", s.RequireLogin, delegate(bool v)
            {
                if (v && !AsmDb.HasAdmin())
                {
                    MessageBox.Show("Buat dulu user admin di tab 'User mng.'. Login tidak diaktifkan.");
                    return;
                }
                s.RequireLogin = v;
            }, "Aktifkan setelah user dibuat.");
            p.Heading("Operasi 24x7");
            p.Check("Supervisor: hidupkan ulang otomatis bila aplikasi crash/hang", s.Supervise, delegate(bool v) { s.Supervise = v; }, "Berlaku setelah aplikasi dibuka ulang.");
            p.Check("Cegah PC tidur / layar mati", s.KeepAwake, delegate(bool v) { s.KeepAwake = v; AppLifecycle.ApplyKeepAwake(); }, null);
            p.Check("Jalankan otomatis saat login Windows", s.AutoStart, delegate(bool v) { s.AutoStart = v; AppLifecycle.SetAutoStart(v); }, "Gunakan akun Windows yang auto-login di PC wall.");
            p.Num("Restart bila memori melebihi (MB, 0 = mati)", 0, 32000, s.MemLimitMb, delegate(int v) { s.MemLimitMb = v; }, null);
            p.Check("Restart harian terjadwal", s.DailyRestart, delegate(bool v) { s.DailyRestart = v; }, null);
            p.Text("Jam restart harian (HH:mm)", s.DailyRestartTime, delegate(string v) { s.DailyRestartTime = v; }, null);
            p.Num("Interval ambil data playback (ms)", 300, 10000, s.PlaybackFetchMs, delegate(int v) { s.PlaybackFetchMs = v; }, "Lebih kecil = playback lebih halus tetapi beban NVR lebih besar. Spec NVR menyarankan minimal 3000; naikkan bila NVR menolak/menutup koneksi.");
            p.Heading("Server konfigurasi (client-server)");
            p.Check("Pakai server: NVR, kamera, group, sequence, dan user diambil dari server", s.ServerMode, delegate(bool v) { s.ServerMode = v; },
                "Registrasi NVR/kamera dilakukan di halaman admin server. Konfigurasi diambil saat aplikasi dibuka dan saat Anda memilih menu 'Periksa pembaruan dari server'. Saat pertama kali, database lokal dicadangkan otomatis lalu NVR/kamera/group lokal diganti data server. Tetap jalan dengan data terakhir bila server mati.");
            p.Text("Alamat server", s.ServerUrl, delegate(string v) { s.ServerUrl = v; }, "Contoh: http://192.168.1.5:8470");
            p.Text("Akun sinkron: user", s.ServerUser, delegate(string v) { s.ServerUser = v; }, "Akun server untuk tarik data otomatis (cukup operator / hanya baca).");
            p.Text("Akun sinkron: password", Secret.Unprotect(s.ServerPassEnc), delegate(string v) { s.ServerPassEnc = Secret.Protect(v); }, null);
            p.Button("Tes koneksi", "Tes koneksi dan sinkron sekarang", delegate
            {
                p.Apply();
                System.Threading.Tasks.Task.Run(delegate { string r = ServerLink.TestAndSync(); return r; })
                    .ContinueWith(t => Ui.Post(this, delegate { MessageBox.Show(t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result, "Server konfigurasi"); }));
            });
            p.Heading("Download");
            p.Check("Waktu download dianggap UTC (matikan = waktu lokal PC)", s.DownloadTimeUtc, delegate(bool v) { s.DownloadTimeUtc = v; }, "Spec NVR memakai UTC.");
            p.Num("Quick download: mundur (detik)", 0, 3600, s.QdPreSec, delegate(int v) { s.QdPreSec = v; }, null);
            p.Num("Quick download: durasi (menit)", 1, 60, s.QdMinutes, delegate(int v) { s.QdMinutes = v; }, "Maksimal 60 menit per permintaan (spec).");
            p.Check("Quick download: MP4 (matikan = N3R)", s.QdMp4, delegate(bool v) { s.QdMp4 = v; }, null);
            p.Text("Folder tujuan download (kosong = default)", s.QdFolder, delegate(string v) { s.QdFolder = v; }, null);
            p.Applied += Notify;
            return p;
        }

        // ================= Device =================

        Control BuildDevice()
        {
            var inner = new AsmTabControl();
            inner.Dock = DockStyle.Fill;
            var a = new TabPage("Initial setup (daftar NVR & kamera)");
            var b = new TabPage("Koneksi, ffmpeg & GPU");
            a.BackColor = Asm.Panel2; b.BackColor = Asm.Panel2;
            inner.ItemSize = new Size(Asm.S(240), Asm.S(28));
            var reg = new NvrRegTab();
            reg.Dock = DockStyle.Fill;
            a.Controls.Add(reg);
            var conn = new ConnectionTab();
            conn.Dock = DockStyle.Fill;
            b.Controls.Add(conn);
            inner.TabPages.Add(a);
            inner.TabPages.Add(b);
            Theme.ApplyTo(reg, true);
            Theme.ApplyTo(conn, true);
            return inner;
        }

        // ================= helper daftar kiri + editor kanan =================

        static Panel Split(out ListBox lb, out Panel right, int leftWidth)
        {
            var root = new Panel();
            root.Dock = DockStyle.Fill;
            root.Tag = "nostyle";
            root.BackColor = Asm.Panel2;
            lb = new ListBox();
            lb.Dock = DockStyle.Left;
            lb.Width = Asm.S(leftWidth);
            lb.BackColor = Asm.Panel;
            lb.ForeColor = Asm.Text;
            lb.BorderStyle = BorderStyle.None;
            lb.Font = Asm.F(9f, false);
            lb.Tag = "nostyle";
            right = new Panel();
            right.Dock = DockStyle.Fill;
            right.Padding = new Padding(Asm.S(10));
            right.Tag = "nostyle";
            right.BackColor = Asm.Panel2;
            root.Controls.Add(right);
            root.Controls.Add(lb);
            return root;
        }

        static Label Lab(string t, DockStyle d)
        {
            var l = new Label();
            l.Text = t;
            l.ForeColor = Asm.Muted;
            l.Dock = d;
            l.Height = Asm.S(22);
            l.Tag = "nostyle";
            return l;
        }

        static FlowLayoutPanel Buttons(params AsmButton[] b)
        {
            var f = new FlowLayoutPanel();
            f.Dock = DockStyle.Bottom;
            f.Height = Asm.S(40);
            f.Tag = "nostyle";
            foreach (AsmButton x in b) { x.Height = Asm.S(30); f.Controls.Add(x); }
            return f;
        }

        static AsmButton B(string text, int w, EventHandler click)
        {
            var b = new AsmButton();
            b.Label = text;
            b.Width = Asm.S(w);
            b.Click += click;
            b.Margin = new Padding(0, 4, 6, 0);
            return b;
        }

        // ================= User =================

        Control BuildUsers()
        {
            ListBox lb; Panel right;
            Panel root = Split(out lb, out right, 200);
            List<UserRec> users = new List<UserRec>();
            var txtName = new TextBox(); txtName.Dock = DockStyle.Top;
            var txtPass = new TextBox(); txtPass.Dock = DockStyle.Top; txtPass.UseSystemPasswordChar = true;
            var cmbLevel = new ComboBox(); cmbLevel.Dock = DockStyle.Top; cmbLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLevel.Items.AddRange(new object[] { "0 (administrator)", "1", "2", "3", "4", "5" });
            cmbLevel.SelectedIndex = 5;
            var chkGroup = new CheckBox(); chkGroup.Text = "Boleh membuat group sendiri"; chkGroup.Dock = DockStyle.Top; chkGroup.Checked = true; chkGroup.ForeColor = Asm.Text;
            var clb = new CheckedListBox(); clb.Dock = DockStyle.Fill; clb.CheckOnClick = true;
            string[] pn = { "Live", "Camera control", "Playback", "Download", "Still / Print", "Setup" };
            Perm[] pv = { Perm.Live, Perm.Camera, Perm.Playback, Perm.Download, Perm.StillPrint, Perm.Setup };
            foreach (string s in pn) clb.Items.Add(s);
            var cmbPermLevel = new ComboBox(); cmbPermLevel.Dock = DockStyle.Top; cmbPermLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPermLevel.Items.AddRange(new object[] { "0", "1", "2", "3", "4", "5" });
            cmbPermLevel.SelectedIndex = 1;
            Action loadPerm = delegate
            {
                Perm cur;
                try { cur = AsmDb.GetPerm(cmbPermLevel.SelectedIndex); } catch { cur = Perm.Live; }
                for (int i = 0; i < pv.Length; i++) clb.SetItemChecked(i, (cur & pv[i]) == pv[i]);
            };
            cmbPermLevel.SelectedIndexChanged += delegate { loadPerm(); };
            loadPerm();

            Action reload = delegate
            {
                try { users = AsmDb.ListUsers(); } catch { users = new List<UserRec>(); }
                lb.Items.Clear();
                foreach (UserRec u in users) lb.Items.Add(u.Name + (u.IsAdmin ? "   (admin)" : "   (level " + u.Level + ")"));
            };
            reload();
            UserRec editing = null;
            lb.SelectedIndexChanged += delegate
            {
                int i = lb.SelectedIndex;
                editing = i >= 0 && i < users.Count ? users[i] : null;
                if (editing == null) return;
                txtName.Text = editing.Name;
                txtPass.Text = "";
                cmbLevel.SelectedIndex = editing.IsAdmin ? 0 : Math.Max(1, Math.Min(5, editing.Level));
                chkGroup.Checked = editing.UseGroup;
            };

            var bar = Buttons(
                B("Baru", 70, delegate { editing = null; lb.ClearSelected(); txtName.Text = ""; txtPass.Text = ""; cmbLevel.SelectedIndex = 5; }),
                B("Simpan user", 110, delegate
                {
                    if (txtName.Text.Trim().Length == 0) { MessageBox.Show("Isi nama user."); return; }
                    string pw = txtPass.Text;
                    if (editing == null || pw.Length > 0)
                    {
                        string err = AsmDb.CheckPassword(pw);
                        if (err != null) { MessageBox.Show(err); return; }
                    }
                    var u = editing ?? new UserRec();
                    u.Name = txtName.Text.Trim();
                    int lv = cmbLevel.SelectedIndex;
                    u.Level = lv;
                    if (editing == null) u.IsAdmin = lv == 0;
                    u.UseGroup = chkGroup.Checked;
                    try { AsmDb.SaveUser(u, editing == null ? pw : (pw.Length > 0 ? pw : null)); }
                    catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                    reload();
                }),
                B("Hapus", 70, delegate
                {
                    if (editing == null || editing.IsAdmin) { MessageBox.Show("User admin tidak bisa dihapus."); return; }
                    if (MessageBox.Show("Hapus user " + editing.Name + "?", "User", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    AsmDb.DeleteUser(editing.Id);
                    editing = null;
                    reload();
                }));
            var permBar = Buttons(B("Simpan hak level", 130, delegate
            {
                Perm p = Perm.None;
                for (int i = 0; i < pv.Length; i++) if (clb.GetItemChecked(i)) p |= pv[i];
                AsmDb.SetPerm(cmbPermLevel.SelectedIndex, p);
                MessageBox.Show("Hak level " + cmbPermLevel.SelectedIndex + " tersimpan.");
            }));

            right.Controls.Add(clb);
            right.Controls.Add(permBar);
            right.Controls.Add(cmbPermLevel);
            right.Controls.Add(Lab("Hak akses per level (admin selalu penuh):", DockStyle.Top));
            right.Controls.Add(bar);
            right.Controls.Add(chkGroup);
            right.Controls.Add(cmbLevel);
            right.Controls.Add(Lab("Level:", DockStyle.Top));
            right.Controls.Add(txtPass);
            right.Controls.Add(Lab("Password (8-32, min. 3 jenis karakter; kosong = tidak diubah):", DockStyle.Top));
            right.Controls.Add(txtName);
            right.Controls.Add(Lab("Nama user:", DockStyle.Top));
            return root;
        }

        // ================= Folder =================

        Control BuildFolder()
        {
            ListBox lb; Panel right;
            Panel root = Split(out lb, out right, 220);
            List<FolderRec> folders = new List<FolderRec>();
            var txt = new TextBox(); txt.Dock = DockStyle.Top;
            var clb = new CheckedListBox(); clb.Dock = DockStyle.Fill; clb.CheckOnClick = true;
            var refs = new List<CamRef>();
            Action fillCams = delegate
            {
                clb.Items.Clear(); refs.Clear();
                foreach (NvrRecord n in Dir.Nvrs)
                    foreach (CameraRecord c in Dir.Registered(n.Id))
                    { refs.Add(new CamRef(n.Id, c.Channel)); clb.Items.Add(Dir.Title(n.Id, c.Channel)); }
            };
            fillCams();
            FolderRec cur = null;
            Action reload = delegate
            {
                try { folders = AsmDb.ListFolders(); } catch { folders = new List<FolderRec>(); }
                lb.Items.Clear();
                foreach (FolderRec f in folders) lb.Items.Add(f.Name);
            };
            reload();
            lb.SelectedIndexChanged += delegate
            {
                int i = lb.SelectedIndex;
                cur = i >= 0 && i < folders.Count ? folders[i] : null;
                if (cur == null) return;
                txt.Text = cur.Name;
                for (int k = 0; k < refs.Count; k++)
                {
                    bool on = false;
                    foreach (CamRef c in cur.Cams) if (c.Key == refs[k].Key) on = true;
                    clb.SetItemChecked(k, on);
                }
            };
            var bar = Buttons(
                B("Baru", 70, delegate { cur = null; lb.ClearSelected(); txt.Text = ""; fillCams(); }),
                B("Simpan", 80, delegate
                {
                    if (txt.Text.Trim().Length == 0) { MessageBox.Show("Isi nama folder."); return; }
                    var f = cur ?? new FolderRec();
                    f.Name = txt.Text.Trim();
                    f.Cams = new List<CamRef>();
                    for (int k = 0; k < refs.Count; k++) if (clb.GetItemChecked(k)) f.Cams.Add(refs[k]);
                    AsmDb.SaveFolder(f);
                    reload();
                }),
                B("Hapus", 70, delegate
                {
                    if (cur == null) return;
                    if (MessageBox.Show("Hapus folder " + cur.Name + "?", "Folder", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    AsmDb.DeleteFolder(cur.Id); cur = null; reload();
                }),
                B("Naik", 60, delegate { MoveFolder(lb, folders, -1, reload); }),
                B("Turun", 60, delegate { MoveFolder(lb, folders, 1, reload); }));
            right.Controls.Add(clb);
            right.Controls.Add(bar);
            right.Controls.Add(Lab("Kamera dalam folder:", DockStyle.Top));
            right.Controls.Add(txt);
            right.Controls.Add(Lab("Nama folder (tampil di pohon Device):", DockStyle.Top));
            dirHandler = delegate { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)fillCams); } catch { } };
            Dir.Changed += dirHandler;
            return root;
        }

        Action dirHandler;

        protected override void Dispose(bool disposing)
        {
            if (disposing && dirHandler != null) { Dir.Changed -= dirHandler; dirHandler = null; }
            base.Dispose(disposing);
        }

        static void MoveFolder(ListBox lb, List<FolderRec> list, int d, Action reload)
        {
            int i = lb.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= list.Count) return;
            FolderRec t = list[i]; list[i] = list[j]; list[j] = t;
            var ids = new List<long>();
            foreach (FolderRec f in list) ids.Add(f.Id);
            AsmDb.ReorderFolders(ids);
            reload();
            lb.SelectedIndex = j;
        }

        // ================= Group =================

        Control BuildGroup()
        {
            ListBox lb; Panel right;
            Panel root = Split(out lb, out right, 220);
            List<GroupRec> groups = new List<GroupRec>();
            var txt = new TextBox(); txt.Dock = DockStyle.Top;
            var cmbLay = new ComboBox(); cmbLay.Dock = DockStyle.Top; cmbLay.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (ScreenLayout l in ScreenLayout.All) cmbLay.Items.Add(l.Id);
            cmbLay.SelectedItem = "4";
            var lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.HideSelection = false;
            lv.Columns.Add("Area", Asm.S(60)); lv.Columns.Add("Kamera", Asm.S(300));
            var slots = new List<CamRef>();
            Action drawSlots = delegate
            {
                ScreenLayout l = ScreenLayout.ById((string)cmbLay.SelectedItem);
                while (slots.Count < l.Count) slots.Add(null);
                while (slots.Count > l.Count) slots.RemoveAt(slots.Count - 1);
                lv.Items.Clear();
                for (int i = 0; i < slots.Count; i++)
                {
                    var it = new ListViewItem((i + 1).ToString());
                    it.SubItems.Add(slots[i] == null ? "" : Dir.Title(slots[i].NvrId, slots[i].Channel));
                    lv.Items.Add(it);
                }
            };
            cmbLay.SelectedIndexChanged += delegate { drawSlots(); };
            drawSlots();
            GroupRec cur = null;
            Action reload = delegate
            {
                try { groups = AsmDb.ListGroups(); } catch { groups = new List<GroupRec>(); }
                lb.Items.Clear();
                foreach (GroupRec g in groups) lb.Items.Add(g.Name + (g.Owner.Length > 0 ? "   (" + g.Owner + ")" : ""));
            };
            reload();
            lb.SelectedIndexChanged += delegate
            {
                int i = lb.SelectedIndex;
                cur = i >= 0 && i < groups.Count ? groups[i] : null;
                if (cur == null) return;
                txt.Text = cur.Name;
                cmbLay.SelectedItem = ScreenLayout.Exists(cur.Layout) ? cur.Layout : "4";
                slots.Clear();
                foreach (CamRef r in cur.Slots) slots.Add(r);
                drawSlots();
            };
            Action pick = delegate
            {
                if (lv.SelectedIndices.Count == 0) return;
                int i = lv.SelectedIndices[0];
                CamRef r = CameraPicker.Choose(this);
                if (r != null) { slots[i] = r; drawSlots(); lv.Items[i].Selected = true; }
            };
            lv.DoubleClick += delegate { pick(); };
            var bar = Buttons(
                B("Baru", 70, delegate { cur = null; lb.ClearSelected(); txt.Text = ""; slots.Clear(); drawSlots(); }),
                B("Pilih kamera...", 120, delegate { pick(); }),
                B("Kosongkan area", 120, delegate { if (lv.SelectedIndices.Count > 0) { slots[lv.SelectedIndices[0]] = null; drawSlots(); } }),
                B("Simpan", 80, delegate
                {
                    if (txt.Text.Trim().Length == 0) { MessageBox.Show("Isi nama group."); return; }
                    var g = cur ?? new GroupRec();
                    g.Name = txt.Text.Trim();
                    g.Layout = (string)cmbLay.SelectedItem;
                    g.Slots = slots.ToArray();
                    AsmDb.SaveGroup(g);
                    reload();
                }),
                B("Hapus", 70, delegate
                {
                    if (cur == null) return;
                    if (MessageBox.Show("Hapus group " + cur.Name + "?", "Group", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    AsmDb.DeleteGroup(cur.Id); cur = null; reload();
                }));
            right.Controls.Add(lv);
            right.Controls.Add(bar);
            right.Controls.Add(Lab("Area (klik ganda = pilih kamera):", DockStyle.Top));
            right.Controls.Add(cmbLay);
            right.Controls.Add(Lab("Pola layar:", DockStyle.Top));
            right.Controls.Add(txt);
            right.Controls.Add(Lab("Nama group:", DockStyle.Top));
            return root;
        }

        // ================= Sequence =================

        Control BuildSequence()
        {
            ListBox lb; Panel right;
            Panel root = Split(out lb, out right, 220);
            List<SeqRec> seqs = new List<SeqRec>();
            var txt = new TextBox(); txt.Dock = DockStyle.Top;
            var lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.HideSelection = false;
            lv.Columns.Add("No", Asm.S(40)); lv.Columns.Add("Group", Asm.S(240)); lv.Columns.Add("Detik", Asm.S(70));
            var steps = new List<SeqStep>();
            Action draw = delegate
            {
                lv.Items.Clear();
                for (int i = 0; i < steps.Count; i++)
                {
                    var it = new ListViewItem((i + 1).ToString());
                    it.SubItems.Add(steps[i].GroupName);
                    it.SubItems.Add(steps[i].Seconds.ToString());
                    lv.Items.Add(it);
                }
            };
            var cmbG = new ComboBox(); cmbG.DropDownStyle = ComboBoxStyle.DropDownList; cmbG.Width = Asm.S(220);
            var gl = new List<GroupRec>();
            Action fillG = delegate
            {
                try { gl = AsmDb.ListGroups(); } catch { gl = new List<GroupRec>(); }
                cmbG.Items.Clear();
                foreach (GroupRec g in gl) cmbG.Items.Add(g.Name);
                if (cmbG.Items.Count > 0) cmbG.SelectedIndex = 0;
            };
            fillG();
            var numSec = new NumericUpDown(); numSec.Minimum = 1; numSec.Maximum = 3600; numSec.Value = 10; numSec.Width = Asm.S(70);
            SeqRec cur = null;
            Action reload = delegate
            {
                try { seqs = AsmDb.ListSequences(); } catch { seqs = new List<SeqRec>(); }
                lb.Items.Clear();
                foreach (SeqRec s in seqs) lb.Items.Add(s.Name);
            };
            reload();
            lb.SelectedIndexChanged += delegate
            {
                int i = lb.SelectedIndex;
                cur = i >= 0 && i < seqs.Count ? seqs[i] : null;
                if (cur == null) return;
                txt.Text = cur.Name;
                steps = new List<SeqStep>(cur.Steps);
                draw();
            };
            var addRow = new FlowLayoutPanel(); addRow.Dock = DockStyle.Bottom; addRow.Height = Asm.S(36); addRow.Tag = "nostyle";
            addRow.Controls.Add(cmbG);
            addRow.Controls.Add(numSec);
            addRow.Controls.Add(B("Tambah langkah", 120, delegate
            {
                if (cmbG.SelectedIndex < 0) return;
                var st = new SeqStep();
                st.GroupId = gl[cmbG.SelectedIndex].Id;
                st.GroupName = gl[cmbG.SelectedIndex].Name;
                st.Seconds = (int)numSec.Value;
                steps.Add(st);
                draw();
            }));
            addRow.Controls.Add(B("Hapus", 70, delegate { if (lv.SelectedIndices.Count > 0) { steps.RemoveAt(lv.SelectedIndices[0]); draw(); } }));
            addRow.Controls.Add(B("Naik", 60, delegate { MoveStep(lv, steps, -1, draw); }));
            addRow.Controls.Add(B("Turun", 60, delegate { MoveStep(lv, steps, 1, draw); }));
            var bar = Buttons(
                B("Baru", 70, delegate { cur = null; lb.ClearSelected(); txt.Text = ""; steps.Clear(); draw(); fillG(); }),
                B("Simpan", 80, delegate
                {
                    if (txt.Text.Trim().Length == 0) { MessageBox.Show("Isi nama sequence."); return; }
                    var s = cur ?? new SeqRec();
                    s.Name = txt.Text.Trim();
                    s.Steps = new List<SeqStep>(steps);
                    AsmDb.SaveSequence(s);
                    reload();
                }),
                B("Hapus", 70, delegate
                {
                    if (cur == null) return;
                    if (MessageBox.Show("Hapus sequence " + cur.Name + "?", "Sequence", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    AsmDb.DeleteSequence(cur.Id); cur = null; reload();
                }),
                B("Muat ulang group", 130, delegate { fillG(); }));
            right.Controls.Add(lv);
            right.Controls.Add(addRow);
            right.Controls.Add(bar);
            right.Controls.Add(Lab("Langkah (group berpindah tiap N detik, berulang):", DockStyle.Top));
            right.Controls.Add(txt);
            right.Controls.Add(Lab("Nama sequence:", DockStyle.Top));
            return root;
        }

        static void MoveStep(ListView lv, List<SeqStep> steps, int d, Action draw)
        {
            if (lv.SelectedIndices.Count == 0) return;
            int i = lv.SelectedIndices[0], j = i + d;
            if (j < 0 || j >= steps.Count) return;
            SeqStep t = steps[i]; steps[i] = steps[j]; steps[j] = t;
            draw();
            lv.Items[j].Selected = true;
        }

        // ================= Default screen =================

        Control BuildDefaultScreen()
        {
            AppSettings s = AppSettings.Current;
            var p = new SettingsPage();
            var lays = new List<string>();
            foreach (ScreenLayout l in ScreenLayout.All) lays.Add(l.Id);
            string[] la = lays.ToArray();
            var gnames = new List<string>(); gnames.Add("");
            var snames = new List<string>(); snames.Add("");
            try
            {
                foreach (GroupRec g in AsmDb.ListGroups()) gnames.Add(g.Name);
                foreach (SeqRec q in AsmDb.ListSequences()) snames.Add(q.Name);
            }
            catch { }
            p.Heading("Operation window");
            p.Combo("Pola layar awal", la, s.OpLayout, delegate(string v) { s.OpLayout = v; }, null);
            p.Num("Jumlah area maksimum", 1, 16, s.OpMaxAreas, delegate(int v) { s.OpMaxAreas = v; }, "Pola layar dengan area lebih banyak disembunyikan dari menu.");
            p.Text("Ikon pola layar di toolbar (pisah koma)", s.LayoutShortcuts, delegate(string v) { s.LayoutShortcuts = v; }, "Contoh: 1,4,9,16,25");
            p.Combo("Group saat mulai", gnames.ToArray(), s.OpStartGroup ?? "", delegate(string v) { s.OpStartGroup = v; }, null);
            p.Combo("Sequence saat mulai", snames.ToArray(), s.OpStartSequence ?? "", delegate(string v) { s.OpStartSequence = v; }, "Dipakai bila Group kosong.");
            p.Check("Lanjutkan playback saat ganti kamera", s.ResumePlayback, delegate(bool v) { s.ResumePlayback = v; }, null);
            p.Heading("Live window");
            p.Combo("Pola layar awal", la, s.LiveLayout, delegate(string v) { s.LiveLayout = v; }, null);
            p.Num("Jumlah area maksimum", 1, 16, s.LiveMaxAreas, delegate(int v) { s.LiveMaxAreas = v; }, null);
            p.Num("Jumlah Live window saat mulai (0-4)", 0, 4, s.LiveWindowsOnStart, delegate(int v) { s.LiveWindowsOnStart = v; }, null);
            p.Text("Isi Live window saat mulai", s.LiveStartItems, delegate(string v) { s.LiveStartItems = v; }, "Format: 1=g:NamaGroup;2=s:NamaSequence");
            p.Check("Buka Map window saat mulai", s.MapOnStart, delegate(bool v) { s.MapOnStart = v; }, null);
            p.Check("Nama ikon kamera di map", s.ShowIconNames, delegate(bool v) { s.ShowIconNames = v; }, null);
            p.Heading("Dekode & stream");
            p.Check("Lebar dekode otomatis menurut jumlah area", s.AutoDecodeWidth, delegate(bool v) { s.AutoDecodeWidth = v; }, null);
            p.Num("Lebar dekode manual (px)", 0, 3840, s.DecodeWidth, delegate(int v) { s.DecodeWidth = v; }, "Dipakai bila otomatis dimatikan. 0 = ukuran asli.");
            p.Check("Multi-area memakai substream", s.UseSubstreamMulti, delegate(bool v) { s.UseSubstreamMulti = v; }, "Urutan: substream NVR -> RTSP substream kamera -> stream utama.");
            p.Applied += Notify;
            return p;
        }

        // ================= Map =================

        Control BuildMap()
        {
            ListBox lb; Panel right;
            Panel root = Split(out lb, out right, 200);
            List<MapRec> maps = new List<MapRec>();
            MapRec cur = null;
            var txt = new TextBox(); txt.Dock = DockStyle.Top;
            var canvas = new MapCanvas(); canvas.Dock = DockStyle.Fill; canvas.EditMode = true;
            var tree = new DeviceTreePanel(); tree.Dock = DockStyle.Left; tree.Width = Asm.S(220);
            Action reload = delegate
            {
                try { maps = AsmDb.ListMaps(true); } catch { maps = new List<MapRec>(); }
                lb.Items.Clear();
                foreach (MapRec m in maps) lb.Items.Add(m.Name);
            };
            reload();
            lb.SelectedIndexChanged += delegate
            {
                int i = lb.SelectedIndex;
                cur = i >= 0 && i < maps.Count ? maps[i] : null;
                if (cur == null) return;
                txt.Text = cur.Name;
                canvas.Map = cur;
            };
            var bar = Buttons(
                B("Baru", 70, delegate
                {
                    cur = new MapRec();
                    cur.Name = "Map baru";
                    lb.ClearSelected();
                    txt.Text = cur.Name;
                    canvas.Map = cur;
                }),
                B("Pilih gambar...", 120, delegate
                {
                    if (cur == null) return;
                    using (var d = new OpenFileDialog())
                    {
                        d.Filter = "Gambar|*.jpg;*.jpeg;*.png;*.bmp";
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        try
                        {
                            byte[] b = File.ReadAllBytes(d.FileName);
                            if (b.Length > 8 * 1024 * 1024) { MessageBox.Show("Gambar terlalu besar (maks. 8 MB)."); return; }
                            cur.Image = b;
                            canvas.Map = cur;
                        }
                        catch (Exception ex) { MessageBox.Show(ex.Message); }
                    }
                }),
                B("Ikon map...", 100, delegate
                {
                    if (cur == null) return;
                    ContextMenuStrip m = Asm.Menu();
                    foreach (MapRec t in maps)
                    {
                        if (t.Id == cur.Id) continue;
                        MapRec tt = t;
                        Asm.Item(m, tt.Name, delegate { canvas.AddIcon(1, 0, 0, tt.Id, new Point(canvas.Width / 2, canvas.Height / 2)); });
                    }
                    if (m.Items.Count == 0) { MessageBox.Show("Belum ada map lain (simpan dulu map tujuan)."); return; }
                    m.Show(Cursor.Position);
                }),
                B("Hapus ikon", 90, delegate { canvas.RemoveSelected(); }),
                B("Simpan", 80, delegate
                {
                    if (cur == null) return;
                    if (txt.Text.Trim().Length == 0) { MessageBox.Show("Isi nama map."); return; }
                    cur.Name = txt.Text.Trim();
                    try { AsmDb.SaveMap(cur); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                    reload();
                }),
                B("Hapus map", 90, delegate
                {
                    if (cur == null || cur.Id == 0) return;
                    if (MessageBox.Show("Hapus map " + cur.Name + "?", "Map", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    AsmDb.DeleteMap(cur.Id); cur = null; canvas.Map = null; reload();
                }));
            right.Controls.Add(canvas);
            right.Controls.Add(tree);
            right.Controls.Add(bar);
            right.Controls.Add(Lab("Seret kamera dari pohon ke map; seret ikon untuk memindah. Klik ikon lalu 'Hapus ikon'.", DockStyle.Top));
            right.Controls.Add(txt);
            right.Controls.Add(Lab("Nama map:", DockStyle.Top));
            return root;
        }

        // ================= Event =================

        Control BuildEvent()
        {
            AppSettings s = AppSettings.Current;
            var p = new SettingsPage();
            p.Check("Popup notifikasi saat ada event", s.EventPopup, delegate(bool v) { s.EventPopup = v; }, null);
            p.Check("Buzzer saat ada notifikasi", s.EventBuzzer, delegate(bool v) { s.EventBuzzer = v; }, "Berbunyi sampai notifikasi ditutup.");
            p.Num("Interval cek status NVR (detik, 0 = mati)", 0, 3600, s.EventPollSec, delegate(int v) { s.EventPollSec = v; }, "as_getinfo.cgi: status koneksi kamera.");
            p.Check("Ambil error log NVR (loglist.cgi)", s.ErrLogPoll, delegate(bool v) { s.ErrLogPoll = v; }, null);
            p.Num("Simpan log (hari)", 1, 365, s.LogKeepDays, delegate(int v) { s.LogKeepDays = v; }, null);
            p.Applied += delegate { EventMonitor.Poke(); Notify(); };
            return p;
        }

        // ================= Export / Import database =================

        void ExportDb()
        {
            DialogResult r = MessageBox.Show(
                "Sertakan password NVR/kamera di file export?\n\nYa = dienkripsi dengan passphrase yang Anda isi (diperlukan saat import).\n" +
                "Tidak = password dikosongkan; setelah import isi ulang password NVR.",
                "Export database", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            string pass = null;
            if (r == DialogResult.Yes)
            {
                pass = PassphraseDialog.Ask(this, "Passphrase export", "Isi passphrase untuk mengenkripsi password NVR/kamera di file ini. Simpan baik-baik; tanpa passphrase password tidak bisa dipulihkan.", true);
                if (pass == null) return;
            }
            using (var d = new SaveFileDialog())
            {
                d.Filter = "Database i-PRO NVR Tool (*.iprodb)|*.iprodb";
                d.FileName = "iprotool_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".iprodb";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    DbTransfer.Export(d.FileName, pass);
                    MessageBox.Show("Database diexport ke:\n" + d.FileName, "Export database");
                }
                catch (Exception ex) { MessageBox.Show("Export gagal: " + ex.Message, "Export database"); }
                finally { Cursor = Cursors.Default; }
            }
        }

        void ImportDb()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "Database i-PRO NVR Tool (*.iprodb;*.db)|*.iprodb;*.db|Semua berkas|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                bool needs;
                string err = DbTransfer.Inspect(d.FileName, out needs);
                if (err != null) { MessageBox.Show(err, "Import database"); return; }
                if (MessageBox.Show("Import akan MENGGANTI seluruh data saat ini (NVR, kamera, folder, group, sequence, map, user, fisheye) dengan isi berkas.\n" +
                    "Database saat ini dicadangkan otomatis, dan aplikasi akan dimulai ulang.\n\nLanjutkan?", "Import database",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                string pass = null;
                if (needs)
                {
                    pass = PassphraseDialog.Ask(this, "Passphrase import", "Berkas ini berisi password NVR/kamera terenkripsi. Isi passphrase saat export.", false);
                    if (pass == null) return;
                }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    string backup = DbTransfer.Import(d.FileName, pass);
                    MessageBox.Show("Import selesai." + (backup.Length > 0 ? "\nCadangan database lama:\n" + backup : "") + "\n\nAplikasi dimulai ulang.", "Import database");
                    AppLifecycle.RequestRestart();
                }
                catch (Exception ex) { MessageBox.Show("Import gagal (data lama tidak diubah): " + ex.Message, "Import database"); }
                finally { Cursor = Cursors.Default; }
            }
        }

        // ================= Maintenance =================

        Control BuildMaintenance()
        {
            var p = new SettingsPage();
            p.Heading("Pemeliharaan");
            p.Button("Hapus log di panel", "Hapus Alarm / Device / Network log", delegate
            {
                IproNvrTool.Events.Clear(IproNvrTool.Events.Alarm);
                IproNvrTool.Events.Clear(IproNvrTool.Events.Device);
                IproNvrTool.Events.Clear(IproNvrTool.Events.Net);
            });
            p.Button("Muat ulang daftar NVR/kamera", "Reload dari database", delegate { Dir.Reload(); });
            p.Button("Logout semua sesi NVR", "Logout UID", delegate
            {
                System.Threading.Tasks.Task.Run(delegate { Pool.LogoutAll(); Sessions.LogoutAll(); });
            });
            p.Button("Cek status NVR sekarang", "Poll sekarang", delegate { EventMonitor.Poke(); });
            p.Button("Folder log aplikasi", "Buka folder", delegate
            {
                try { System.Diagnostics.Process.Start(Log.Directory); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            p.Heading("Database (NVR, kamera, folder, group, sequence, map, user, fisheye)");
            p.Button("Export database", "Export ke berkas...", delegate { ExportDb(); });
            p.Button("Import database", "Import dari berkas...", delegate { ImportDb(); });
            p.Button("Folder data (database)", "Buka folder", delegate
            {
                try { System.Diagnostics.Process.Start(Path.GetDirectoryName(Db.FilePath)); } catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            return p;
        }
    }
}
