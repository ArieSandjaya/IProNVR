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
            else if (Dir.IsRecording(nvrId, ch)) n.Text += "  \u25CF R";
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
                        cn.Text = Dir.Title(r.NvrId, r.Channel) + (Dir.Status(r.NvrId, r.Channel) == 2 ? "  (error)" : (Dir.IsRecording(r.NvrId, r.Channel) ? "  \u25CF R" : ""));
                        fn.Nodes.Add(cn);
                        any = true;
                    }
                    if (fn.Nodes.Count > 0 || filter.Length == 0) tv.Nodes.Add(fn);
                }
            }
            else
            {
                foreach (NvrRecord nvr in Dir.Nvrs)
                {
                    var nn = new TreeNode(nvr.Name);
                    foreach (CameraRecord c in Dir.Registered(nvr.Id))
                    {
                        if (!Match(nvr.Id, c.Channel)) continue;
                        nn.Nodes.Add(CamNode(nvr.Id, c.Channel));
                        any = true;
                    }
                    if (nn.Nodes.Count > 0 || filter.Length == 0) tv.Nodes.Add(nn);
                }
            }
            if (filter.Length > 0) tv.ExpandAll();
            else foreach (TreeNode n in tv.Nodes) if (open.Count == 0 || open.Contains(n.Text)) n.Expand();
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
            lb.ItemHeight = Asm.S(22);
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.Tag = "nostyle";
            lb.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Asm.BarOpen : Asm.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(lb.Items[e.Index]), lb.Font, e.Bounds, Asm.Text,
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
                lb.Items.Add(g.Name + "   [" + g.Layout + "]");
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
        readonly DateTimePicker dtp = new DateTimePicker();
        readonly Label lblCam = new Label();
        public Func<CamRef> CameraSource;
        public event Action<CamRef, DateTime> PlayRequested;

        public QuickPlaybackPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Padding = new Padding(Asm.S(8));
            lblCam.Dock = DockStyle.Top;
            lblCam.Height = Asm.S(40);
            lblCam.ForeColor = Asm.Text;
            lblCam.Tag = "nostyle";
            lblCam.Text = "Kamera: (pilih area gambar)";
            var l2 = new Label();
            l2.Text = "Tanggal & jam mulai:";
            l2.ForeColor = Asm.Muted;
            l2.Dock = DockStyle.Top;
            l2.Height = Asm.S(22);
            l2.Tag = "nostyle";
            dtp.Dock = DockStyle.Top;
            dtp.Format = DateTimePickerFormat.Custom;
            dtp.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtp.Value = DateTime.Now.AddMinutes(-10);
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Top;
            row.Height = Asm.S(40);
            row.Tag = "nostyle";
            row.Padding = new Padding(0, Asm.S(6), 0, 0);
            var play = new AsmButton(); play.Icon = AsmIcon.Play; play.Label = "Putar"; play.Width = Asm.S(90);
            play.Click += delegate
            {
                CamRef r = CameraSource == null ? null : CameraSource();
                if (r == null) { MessageBox.Show("Pilih dulu area gambar yang berisi kamera.", "Quick playback"); return; }
                if (PlayRequested != null) PlayRequested(r, dtp.Value);
            };
            var m5 = new AsmButton(); m5.Label = "-5 mnt"; m5.Width = Asm.S(66); m5.Click += delegate { dtp.Value = DateTime.Now.AddMinutes(-5); };
            var m60 = new AsmButton(); m60.Label = "-1 jam"; m60.Width = Asm.S(66); m60.Click += delegate { dtp.Value = DateTime.Now.AddHours(-1); };
            row.Controls.Add(play);
            row.Controls.Add(m5);
            row.Controls.Add(m60);
            Controls.Add(row);
            Controls.Add(dtp);
            Controls.Add(l2);
            Controls.Add(lblCam);
        }

        public void RefreshCamera()
        {
            CamRef r = CameraSource == null ? null : CameraSource();
            lblCam.Text = r == null ? "Kamera: (pilih area gambar)" : "Kamera: " + Dir.Title(r.NvrId, r.Channel);
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
            Controls.Add(lblStatus);
            Controls.Add(row);
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
        public void Quick(CamRef r, DateTime atLocal)
        {
            if (!AsmApp.Can(Perm.Download)) { MessageBox.Show("Tidak ada hak download."); return; }
            AppSettings s = AppSettings.Current;
            DateTime a = atLocal.AddSeconds(-Math.Max(0, s.QdPreSec));
            DateTime b = a.AddMinutes(Math.Max(1, s.QdMinutes));
            dtS.Value = a;
            dtE.Value = b;
            Start(r, a, b, s.QdMp4 ? "MP4" : "N3");
        }

        void Start(CamRef r, DateTime a, DateTime b, string kind)
        {
            if (job != null && !btnGo.Enabled) return;
            NvrRecord nvr = Dir.Nvr(r.NvrId);
            if (nvr == null) return;
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
                    NvrDownloader.Run(nvr, ch, a, b, kind, folder, j, delegate(string t) { Ui.Post(this, delegate { lblStatus.Text = t; }); });
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
