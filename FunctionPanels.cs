using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Pohon perangkat: folder -> kamera (atau NVR -> kamera bila belum ada folder). Seret kamera ke area gambar.</summary>
    public class DeviceTreePanel : Panel
    {
        readonly TreeView tv = new TreeView();
        string filter = "";
        public event Action<CamRef> CameraActivated;
        public event Action<CamRef> CameraSelected;

        public DeviceTreePanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            tv.Dock = DockStyle.Fill;
            tv.BackColor = Asm.Panel;
            tv.ForeColor = Asm.Text;
            tv.BorderStyle = BorderStyle.None;
            tv.Font = Asm.F(9f, false);
            tv.ShowLines = false;
            tv.HideSelection = false;
            tv.ItemHeight = Asm.S(22);
            tv.Tag = "nostyle";
            tv.ItemDrag += OnItemDrag;
            tv.NodeMouseDoubleClick += delegate(object s, TreeNodeMouseClickEventArgs e)
            {
                CamRef r = e.Node.Tag as CamRef;
                if (r != null && CameraActivated != null) CameraActivated(r);
            };
            tv.AfterSelect += delegate(object s, TreeViewEventArgs e)
            {
                CamRef r = e.Node.Tag as CamRef;
                if (r != null && CameraSelected != null) CameraSelected(r);
            };
            Controls.Add(tv);
            Rebuild();
            Dir.Changed += OnDirChanged;
            Dir.StatusChanged += OnDirChanged;
        }

        int _rebuildPending;
        /// <summary>Dir.StatusChanged bisa datang puluhan kali per detik (75 kamera): gabungkan jadi satu rebuild tiap ~1,5 dtk.</summary>
        void OnDirChanged()
        {
            if (System.Threading.Interlocked.Exchange(ref _rebuildPending, 1) != 0) return;
            try
            {
                if (!IsHandleCreated || IsDisposed) { _rebuildPending = 0; return; }
                BeginInvoke((Action)delegate
                {
                    var t = new System.Windows.Forms.Timer();
                    t.Interval = 1500;
                    t.Tick += delegate
                    {
                        t.Stop(); t.Dispose();
                        _rebuildPending = 0;
                        if (!IsDisposed) Rebuild();
                    };
                    t.Start();
                });
            }
            catch { _rebuildPending = 0; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Dir.Changed -= OnDirChanged; Dir.StatusChanged -= OnDirChanged; }
            base.Dispose(disposing);
        }

        void OnItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode n = e.Item as TreeNode;
            CamRef r = n == null ? null : n.Tag as CamRef;
            if (r == null) return;
            if (!AsmApp.Can(Perm.Live)) return;
            tv.DoDragDrop("asmcam:" + r.NvrId + ":" + r.Channel, DragDropEffects.Copy);
        }

        bool Match(long nvrId, int ch)
        {
            if (filter.Length == 0) return true;
            return Dir.Title(nvrId, ch).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        TreeNode CamNode(long nvrId, int ch)
        {
            var n = new TreeNode(Dir.CamName(nvrId, ch));
            n.Tag = new CamRef(nvrId, ch);
            int st = Dir.Status(nvrId, ch);
            n.ForeColor = st == 2 ? Asm.Danger : (st == 0 ? Asm.Muted : Asm.Text);
            if (st == 2) n.Text += "  (error)";
            else if (Dir.IsRecording(nvrId, ch)) n.Text += "  \u25CF";
            return n;
        }

        public bool Filter(string text)
        {
            filter = text ?? "";
            return Rebuild();
        }

        /// <summary>Bangun ulang pohon; true bila ada kamera yang cocok dengan filter.</summary>
        public bool Rebuild()
        {
            var open = new HashSet<string>();
            foreach (TreeNode n in tv.Nodes) if (n.IsExpanded) open.Add(n.Text);
            bool any = false;
            string selKey = null;
            CamRef selRef = tv.SelectedNode == null ? null : tv.SelectedNode.Tag as CamRef;
            if (selRef != null) selKey = selRef.NvrId + ":" + selRef.Channel;
            TreeNode topNode = tv.TopNode;
            string topText = topNode == null ? null : topNode.Text;
            tv.BeginUpdate();
            try
            {
            tv.Nodes.Clear();
            List<FolderRec> folders;
            try { folders = AsmDb.ListFolders(); }
            catch { folders = new List<FolderRec>(); }
            if (folders.Count > 0)
            {
                foreach (FolderRec f in folders)
                {
                    var fn = new TreeNode(f.Name);
                    foreach (CamRef r in f.Cams)
                    {
                        if (Dir.Nvr(r.NvrId) == null || !Match(r.NvrId, r.Channel)) continue;
                        TreeNode cn = CamNode(r.NvrId, r.Channel);
                        cn.Text = Dir.Title(r.NvrId, r.Channel) + (Dir.Status(r.NvrId, r.Channel) == 2 ? "  (error)" : (Dir.IsRecording(r.NvrId, r.Channel) ? "  \u25CF" : ""));
                        fn.Nodes.Add(cn);
                        any = true;
                    }
                    if (fn.Nodes.Count > 0 || filter.Length == 0) tv.Nodes.Add(fn);
                }
            }
            // NVR (semua) -> kamera. Bila sudah ada folder, hanya kamera yang belum masuk folder manapun yang ditampilkan di sini,
            // sehingga kamera dari NVR ke-2, ke-3, dst tidak hilang.
            var inFolder = new HashSet<string>();
            foreach (FolderRec f in folders) foreach (CamRef r in f.Cams) inFolder.Add(r.NvrId + ":" + r.Channel);
            foreach (NvrRecord nvr in Dir.Nvrs)
            {
                var nn = new TreeNode(nvr.Name);
                foreach (CameraRecord c in Dir.Registered(nvr.Id))
                {
                    if (inFolder.Contains(nvr.Id + ":" + c.Channel)) continue;
                    if (!Match(nvr.Id, c.Channel)) continue;
                    nn.Nodes.Add(CamNode(nvr.Id, c.Channel));
                    any = true;
                }
                if (nn.Nodes.Count > 0 || (filter.Length == 0 && folders.Count == 0)) tv.Nodes.Add(nn);
            }
            if (filter.Length > 0) tv.ExpandAll();
            else foreach (TreeNode n in tv.Nodes) if (open.Count == 0 || open.Contains(n.Text)) n.Expand();
            // pulihkan pilihan & posisi gulir agar daftar tidak "melompat" tiap pembaruan status
            if (selKey != null)
            {
                foreach (TreeNode n in tv.Nodes) foreach (TreeNode c in n.Nodes)
                {
                    CamRef cr = c.Tag as CamRef;
                    if (cr != null && (cr.NvrId + ":" + cr.Channel) == selKey) { tv.SelectedNode = c; break; }
                }
            }
            if (topText != null)
                foreach (TreeNode n in tv.Nodes) if (n.Text == topText) { try { tv.TopNode = n; } catch { } break; }
            }
            finally { tv.EndUpdate(); }
            return any;
        }
    }

    /// <summary>Daftar group (pola layar + kamera per area).</summary>
    public class GroupPanel : Panel
    {
        readonly ListBox lb = new ListBox();
        List<GroupRec> groups = new List<GroupRec>();
        string filter = "";
        public event Action<GroupRec> GroupChosen;

        public GroupPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            lb.Dock = DockStyle.Fill;
            lb.BackColor = Asm.Panel;
            lb.ForeColor = Asm.Text;
            lb.BorderStyle = BorderStyle.None;
            lb.Font = Asm.F(9f, false);
            lb.ItemHeight = Asm.S(28);
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.Tag = "nostyle";
            lb.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Asm.BarOpen : Asm.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
                // ikon = gambar kecil pola layar group ini
                int iw = Asm.S(30), ih = Asm.S(20);
                Rectangle ir = new Rectangle(e.Bounds.X + Asm.S(6), e.Bounds.Y + (e.Bounds.Height - ih) / 2, iw, ih);
                if (e.Index < shown.Count)
                {
                    Bitmap pv = LayoutIcon(shown[e.Index].Layout, iw, ih);
                    if (pv != null) e.Graphics.DrawImage(pv, ir);
                }
                Rectangle tr = new Rectangle(ir.Right + Asm.S(8), e.Bounds.Y, e.Bounds.Width - ir.Right - Asm.S(8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(lb.Items[e.Index]), lb.Font, tr, Asm.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            lb.DoubleClick += delegate
            {
                int i = lb.SelectedIndex;
                if (i >= 0 && i < shown.Count && GroupChosen != null) GroupChosen(shown[i]);
            };
            Controls.Add(lb);
            Reload();
        }

        readonly List<GroupRec> shown = new List<GroupRec>();
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();

        Bitmap LayoutIcon(string layoutId, int w, int h)
        {
            Bitmap b;
            if (icons.TryGetValue(layoutId, out b)) return b;
            try { b = ScreenLayout.ById(layoutId).Preview(w, h); } catch { b = null; }
            icons[layoutId] = b;
            return b;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (Bitmap b in icons.Values) if (b != null) b.Dispose();
            icons.Clear();
            base.Dispose(disposing);
        }

        public void Reload()
        {
            try { groups = AsmDb.ListGroups(); }
            catch { groups = new List<GroupRec>(); }
            Apply();
        }

        void Apply()
        {
            shown.Clear();
            lb.Items.Clear();
            foreach (GroupRec g in groups)
            {
                if (g.Owner.Length > 0 && g.Owner != AsmApp.UserKey) continue;
                if (filter.Length > 0 && g.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(g);
                lb.Items.Add(g.Name + "   (" + g.Layout + ")");
            }
        }

        public bool Filter(string t)
        {
            filter = t ?? "";
            Apply();
            return shown.Count > 0;
        }

        public GroupRec Current { get { int i = lb.SelectedIndex; return i >= 0 && i < shown.Count ? shown[i] : null; } }
    }

    /// <summary>Daftar sequence dengan tombol Start/Stop.</summary>
    public class SequencePanel : Panel
    {
        readonly ListBox lb = new ListBox();
        List<SeqRec> seqs = new List<SeqRec>();
        readonly List<SeqRec> shown = new List<SeqRec>();
        string filter = "";
        public event Action<SeqRec> StartRequested;
        public event Action StopRequested;

        public SequencePanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            lb.Dock = DockStyle.Fill;
            lb.BackColor = Asm.Panel;
            lb.ForeColor = Asm.Text;
            lb.BorderStyle = BorderStyle.None;
            lb.Font = Asm.F(9f, false);
            lb.Tag = "nostyle";
            lb.DoubleClick += delegate { StartCurrent(); };

            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Bottom;
            row.Height = Asm.S(36);
            row.Padding = new Padding(Asm.S(4));
            row.Tag = "nostyle";
            var b1 = new AsmButton(); b1.Icon = AsmIcon.Play; b1.Label = "Start"; b1.Width = Asm.S(80); b1.Click += delegate { StartCurrent(); };
            var b2 = new AsmButton(); b2.Icon = AsmIcon.Stop; b2.Label = "Stop"; b2.Width = Asm.S(80); b2.Click += delegate { if (StopRequested != null) StopRequested(); };
            row.Controls.Add(b1);
            row.Controls.Add(b2);
            Controls.Add(lb);
            Controls.Add(row);
            Reload();
        }

        void StartCurrent()
        {
            int i = lb.SelectedIndex;
            if (i >= 0 && i < shown.Count && StartRequested != null) StartRequested(shown[i]);
        }

        public void Reload()
        {
            try { seqs = AsmDb.ListSequences(); }
            catch { seqs = new List<SeqRec>(); }
            Apply();
        }

        void Apply()
        {
            shown.Clear();
            lb.Items.Clear();
            foreach (SeqRec s in seqs)
            {
                if (filter.Length > 0 && s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(s);
                lb.Items.Add(s.Name + "   (" + s.Steps.Count + " langkah)");
            }
        }

        public bool Filter(string t)
        {
            filter = t ?? "";
            Apply();
            return shown.Count > 0;
        }
    }

    /// <summary>Quick playback: pilih tanggal/jam lalu putar kamera terpilih di Operation window.</summary>
    public class QuickPlaybackPanel : Panel
    {
        readonly DarkCalendar cal = new DarkCalendar();
        readonly ComboBox cmbH = new ComboBox();
        readonly ComboBox cmbM = new ComboBox();
        readonly Label lblColon = new Label();
        readonly Label lblCam = new Label();
        readonly CheckBox chkSd = new CheckBox();
        readonly AsmButton btnDate = new AsmButton();
        readonly AsmButton btnLast = new AsmButton();
        public Func<CamRef> CameraSource;
        public event Action<CamRef, DateTime> PlayRequested;

        public QuickPlaybackPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Padding = new Padding(Asm.S(8));
            bool h24 = AppSettings.Current.Time24h;
            for (int h = 0; h < 24; h++)
                cmbH.Items.Add(h24 ? h.ToString("00") : (h < 12 ? "AM" : "PM") + (h % 12 == 0 ? 12 : h % 12));
            for (int m = 0; m < 60; m++) cmbM.Items.Add(m.ToString("00"));
            foreach (ComboBox c in new[] { cmbH, cmbM })
            {
                c.DropDownStyle = ComboBoxStyle.DropDownList;
                c.FlatStyle = FlatStyle.Flat;
                c.BackColor = Color.FromArgb(18, 22, 31);
                c.ForeColor = Asm.Text;
                c.Font = Asm.F(12f, false);
                c.MaxDropDownItems = 12;
                c.Tag = "nostyle";
            }
            DateTime t = DateTime.Now;
            cal.Value = t.Date;
            cmbH.SelectedIndex = t.Hour;
            cmbM.SelectedIndex = t.Minute;
            lblColon.Text = ":"; lblColon.ForeColor = Asm.Text; lblColon.Font = Asm.F(13f, true);
            lblColon.TextAlign = ContentAlignment.MiddleCenter; lblColon.Tag = "nostyle";
            lblCam.ForeColor = Asm.Muted; lblCam.Tag = "nostyle"; lblCam.TextAlign = ContentAlignment.MiddleCenter;
            lblCam.Text = "Camera: (select an image area)";
            lblCam.AutoEllipsis = true;

            btnDate.Label = "Go to date"; btnLast.Label = "Go to last"; btnDate.Font = Asm.F(12f, false); btnLast.Font = Asm.F(12f, false);
            btnDate.Click += delegate { Go(Selected()); };
            btnLast.Click += delegate { Go(DateTime.Now.AddMinutes(-5)); };

            chkSd.Text = "SD memory rec."; chkSd.Enabled = false; chkSd.ForeColor = Asm.Muted; chkSd.Font = Asm.F(12f, false); chkSd.Tag = "nostyle";
            Controls.Add(chkSd);
            Controls.Add(cal); Controls.Add(cmbH); Controls.Add(cmbM); Controls.Add(lblColon);
            Controls.Add(lblCam); Controls.Add(btnDate); Controls.Add(btnLast);
        }

        DateTime Selected()
        {
            return cal.Value.Date.AddHours(Math.Max(0, cmbH.SelectedIndex)).AddMinutes(Math.Max(0, cmbM.SelectedIndex));
        }

        void Go(DateTime at)
        {
            CamRef r = CameraSource == null ? null : CameraSource();
            if (r == null) { MessageBox.Show("Select an image area that contains a camera first.", "Quick playback"); return; }
            if (PlayRequested != null) PlayRequested(r, at);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int cx = ClientSize.Width / 2;
            // posisi mengikuti ASM300 (sidebar 374 px): kalender 265 px di tengah, jam/menit, SD memory rec., dua tombol
            cal.SetBounds(cx - Asm.S(132), Asm.S(5), Asm.S(265), Asm.S(237));
            int y = Asm.S(254), ch = Asm.S(27);
            cmbH.SetBounds(cx - Asm.S(173), y, Asm.S(158), ch);
            lblColon.SetBounds(cx - Asm.S(15), y, Asm.S(30), ch);
            cmbM.SetBounds(cx + Asm.S(16), y, Asm.S(157), ch);
            chkSd.SetBounds(cx - Asm.S(70), Asm.S(292), Asm.S(150), Asm.S(24));
            int bw = Asm.S(133), bh = Asm.S(37);
            btnDate.SetBounds(cx - Asm.S(160), Asm.S(328), bw, bh);
            btnLast.SetBounds(cx + Asm.S(28), Asm.S(328), bw, bh);
            lblCam.SetBounds(Asm.S(10), Asm.S(378), ClientSize.Width - Asm.S(20), Asm.S(22));
        }

        public void RefreshCamera()
        {
            CamRef r = CameraSource == null ? null : CameraSource();
            lblCam.Text = r == null ? "Camera: (select an image area)" : "Camera: " + Dir.Title(r.NvrId, r.Channel);
        }
    }

    /// <summary>Download rekaman kamera terpilih (httpdl.cgi), termasuk Quick download.</summary>
    public class DownloadPanel : Panel
    {
        readonly DateTimePicker dtS = new DateTimePicker();
        readonly DateTimePicker dtE = new DateTimePicker();
        readonly ComboBox cmbKind = new ComboBox();
        readonly Label lblCam = new Label();
        readonly Label lblStatus = new Label();
        readonly AsmButton btnGo = new AsmButton();
        readonly AsmButton btnCancel = new AsmButton();
        DownloadJob job;
        public Func<CamRef> CameraSource;

        public DownloadPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Padding = new Padding(Asm.S(8));
            lblCam.Dock = DockStyle.Top; lblCam.Height = Asm.S(36); lblCam.ForeColor = Asm.Text; lblCam.Tag = "nostyle";
            lblCam.Text = "Kamera: (pilih area gambar)";
            dtS.Dock = DockStyle.Top; dtS.Format = DateTimePickerFormat.Custom; dtS.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtE.Dock = DockStyle.Top; dtE.Format = DateTimePickerFormat.Custom; dtE.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtS.Value = DateTime.Now.AddMinutes(-10);
            dtE.Value = DateTime.Now;
            cmbKind.Dock = DockStyle.Top;
            cmbKind.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbKind.Items.AddRange(new object[] { "N3R (data asli)", "MP4", "N3R tanpa audio", "MP4 tanpa audio" });
            cmbKind.SelectedIndex = 0;
            var ls = Lab("Mulai:"); var le = Lab("Selesai:"); var lk = Lab("Format:");
            lblStatus.Dock = DockStyle.Fill; lblStatus.ForeColor = Asm.Muted; lblStatus.Tag = "nostyle";
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top; row.Height = Asm.S(42); row.Tag = "nostyle"; row.Padding = new Padding(0, Asm.S(6), 0, 0);
            btnGo.Icon = AsmIcon.Download; btnGo.Label = "Download"; btnGo.Width = Asm.S(110);
            btnGo.Click += delegate { StartFromUi(); };
            btnCancel.Icon = AsmIcon.Close; btnCancel.Label = "Batal"; btnCancel.Width = Asm.S(80); btnCancel.Enabled = false;
            btnCancel.Click += delegate { if (job != null) job.Abort(); };
            row.Controls.Add(btnGo);
            row.Controls.Add(btnCancel);
            // lokasi simpan
            var fp = new Panel(); fp.Dock = DockStyle.Top; fp.Height = Asm.S(28); fp.Tag = "nostyle";
            var bb = new Button(); bb.Text = "..."; bb.Dock = DockStyle.Right; bb.Width = Asm.S(34); bb.Tag = "nostyle";
            bb.FlatStyle = FlatStyle.Flat; bb.UseVisualStyleBackColor = false; bb.BackColor = Asm.Panel2; bb.ForeColor = Asm.Text; bb.FlatAppearance.BorderColor = Asm.BtnBorder;
            bb.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "Select folder to save downloads";
                    d.SelectedPath = txtFolder.Text;
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { txtFolder.Text = d.SelectedPath; SaveFolder(); }
                }
            };
            txtFolder.Dock = DockStyle.Fill;
            txtFolder.Text = AppSettings.Current.ResolveDownloadFolder().Length > 0 && string.IsNullOrWhiteSpace(AppSettings.Current.QdFolder)
                ? AppSettings.Current.ResolveDownloadFolder() : AppSettings.Current.QdFolder;
            txtFolder.Leave += delegate { SaveFolder(); };
            fp.Controls.Add(txtFolder); fp.Controls.Add(bb);
            var lf = Lab("Save to:");
            Controls.Add(lblStatus);
            Controls.Add(row);
            Controls.Add(fp); Controls.Add(lf);
            Controls.Add(cmbKind); Controls.Add(lk);
            Controls.Add(dtE); Controls.Add(le);
            Controls.Add(dtS); Controls.Add(ls);
            Controls.Add(lblCam);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && job != null) job.Abort();
            base.Dispose(disposing);
        }

        static Label Lab(string t)
        {
            var l = new Label();
            l.Text = t; l.ForeColor = Asm.Muted; l.Dock = DockStyle.Top; l.Height = Asm.S(20); l.Tag = "nostyle";
            return l;
        }

        public void RefreshCamera()
        {
            CamRef r = CameraSource == null ? null : CameraSource();
            lblCam.Text = r == null ? "Kamera: (pilih area gambar)" : "Kamera: " + Dir.Title(r.NvrId, r.Channel);
        }

        readonly TextBox txtFolder = new TextBox();

        void SaveFolder()
        {
            AppSettings.Current.QdFolder = txtFolder.Text.Trim();
            try { AppSettings.Current.Save(); } catch { }
        }

        string KindCode(int i)
        {
            switch (i) { case 1: return "MP4"; case 2: return "N3VO"; case 3: return "MP4VO"; default: return "N3"; }
        }

        void StartFromUi()
        {
            if (!AsmApp.Can(Perm.Download)) { MessageBox.Show("Tidak ada hak download."); return; }
            CamRef r = CameraSource == null ? null : CameraSource();
            if (r == null) { MessageBox.Show("Pilih dulu area gambar yang berisi kamera.", "Download"); return; }
            if (dtE.Value <= dtS.Value) { MessageBox.Show("Waktu selesai harus setelah waktu mulai."); return; }
            Start(r, dtS.Value, dtE.Value, KindCode(cmbKind.SelectedIndex));
        }

        /// <summary>Quick download: mulai (waktu playback - QdPreSec) selama QdMinutes menit dengan format di Setup.</summary>
        public void Quick(CamRef r, DateTime atLocal, bool live)
        {
            if (!AsmApp.Can(Perm.Download)) { MessageBox.Show("Tidak ada hak download."); return; }
            AppSettings s = AppSettings.Current;
            DateTime a, b;
            if (live)
            {
                // dari tampilan live: ambil rekaman beberapa menit TERAKHIR (rentang tidak boleh melewati sekarang)
                b = DateTime.Now.AddSeconds(-5);
                a = b.AddMinutes(-Math.Max(1, s.QdMinutes));
            }
            else
            {
                a = atLocal.AddSeconds(-Math.Max(0, s.QdPreSec));
                b = a.AddMinutes(Math.Max(1, s.QdMinutes));
                if (b > DateTime.Now) b = DateTime.Now.AddSeconds(-5);
            }
            dtS.Value = a;
            dtE.Value = b;
            Start(r, a, b, s.QdMp4 ? "MP4" : "N3");
        }

        void Start(CamRef r, DateTime a, DateTime b, string kind)
        {
            if (job != null && !btnGo.Enabled) return;
            NvrRecord nvr = Dir.Nvr(r.NvrId);
            if (nvr == null) return;
            SaveFolder();
            string folder = AppSettings.Current.QdFolder;
            if (string.IsNullOrWhiteSpace(folder)) folder = AppSettings.Current.ResolveDownloadFolder();
            var j = new DownloadJob();
            job = j;
            btnGo.Enabled = false;
            btnCancel.Enabled = true;
            int ch = r.Channel;
            Task.Run(delegate
            {
                try
                {
                    NvrDrivers.For(nvr).Download(nvr, ch, a, b, kind, folder, j, delegate(string t) { Ui.Post(this, delegate { lblStatus.Text = t; }); });
                }
                catch (Exception ex)
                {
                    Ui.Post(this, delegate { lblStatus.Text = "Error: " + ex.Message; });
                }
                finally
                {
                    Ui.Post(this, delegate { btnGo.Enabled = true; btnCancel.Enabled = false; });
                }
            });
        }
    }

    /// <summary>Log alarm / device error / network error (Events).</summary>
    public class LogPanel : Panel
    {
        readonly ListView lv = new ListView();
        readonly string kind;
        public event Action<LogEntry> EntryActivated;

        public LogPanel(string kind)
        {
            this.kind = kind;
            Tag = "nostyle";
            BackColor = Asm.Panel;
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.BackColor = Asm.Panel;
            lv.ForeColor = Asm.Text;
            lv.BorderStyle = BorderStyle.None;
            lv.Font = Asm.F(8.5f, false);
            lv.Tag = "nostyle";
            lv.Columns.Add("Waktu", Asm.S(110));
            lv.Columns.Add("Sumber", Asm.S(70));
            lv.Columns.Add("Keterangan", Asm.S(200));
            lv.DoubleClick += delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                LogEntry e = lv.SelectedItems[0].Tag as LogEntry;
                if (e != null && EntryActivated != null) EntryActivated(e);
            };
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Bottom; row.Height = Asm.S(34); row.Tag = "nostyle"; row.Padding = new Padding(Asm.S(4));
            var clr = new AsmButton(); clr.Label = "Hapus log"; clr.Width = Asm.S(100);
            clr.Click += delegate { IproNvrTool.Events.Clear(kind); Reload(); };
            row.Controls.Add(clr);
            Controls.Add(lv);
            Controls.Add(row);
            Reload();
            IproNvrTool.Events.Added += OnAdded;
        }

        void OnAdded(string k, LogEntry e)
        {
            if (k != kind) return;
            if (System.Threading.Interlocked.Exchange(ref _reloadPending, 1) != 0) return;
            try
            {
                if (!IsHandleCreated || IsDisposed) { _reloadPending = 0; return; }
                BeginInvoke((Action)delegate { _reloadPending = 0; if (!IsDisposed) Reload(); });
            }
            catch { _reloadPending = 0; }
        }
        int _reloadPending;

        protected override void Dispose(bool disposing)
        {
            if (disposing) IproNvrTool.Events.Added -= OnAdded;
            base.Dispose(disposing);
        }

        public void Reload()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            List<LogEntry> l = IproNvrTool.Events.Get(kind);
            for (int i = l.Count - 1; i >= 0; i--)
            {
                LogEntry e = l[i];
                var it = new ListViewItem(Asm.FormatTime(e.Time));
                it.SubItems.Add(e.Source);
                it.SubItems.Add(e.Text);
                it.Tag = e;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
        }
    }
}
