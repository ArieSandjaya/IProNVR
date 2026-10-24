using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Daftar NVR: registrasi banyak NVR (tes login + info HDD + daftar kamera dari as_getinfo.cgi?FILE=2)
    /// dan penyimpanan semuanya di SQLite (%AppData%\IproNvrTool\nvr.db).</summary>
    public class NvrRegTab : UserControl
    {
        const int ProbeGapMs = 3000; // spec: jeda >= 3 dtk antar perintah (deteksi stream opsional)

        /// <summary>Dipicu saat pengguna menekan "Pakai Sebagai Koneksi Aktif".</summary>
        public event Action<NvrRecord> UseAsActive;

        TextBox txtName, txtHost, txtUser, txtPass, txtModel, txtLocation, txtNotes, txtInfo;
        TextBox txtCamUser, txtCamPass, txtRtspTpl, txtSubParam;
        NumericUpDown numPort, numMaxCh;
        CheckBox chkHttps, chkSkipTest;
        DataGridView gridNvr, gridCam;
        ProgressBar prog;
        Label lblProg;
        Button btnCancel;
        FlowLayoutPanel actions;

        List<NvrRecord> nvrs = new List<NvrRecord>();
        NvrRecord current;
        bool loading;
        bool loaded;
        bool busy;
        System.Threading.CancellationTokenSource cts;

        public NvrRegTab()
        {
            Dock = DockStyle.Fill;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // form
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));   // grid NVR
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // progress
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));   // grid kamera
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // tombol kamera

            root.Controls.Add(BuildForm(), 0, 0);

            gridNvr = MakeGrid(true);
            gridNvr.MultiSelect = false;
            gridNvr.Columns.Add("id", "Id");
            gridNvr.Columns[0].Visible = false;
            gridNvr.Columns.Add("nama", "Nama NVR");
            gridNvr.Columns.Add("alamat", "Alamat");
            gridNvr.Columns.Add("status", "Status");
            gridNvr.Columns.Add("kamera", "Kamera (normal / error / tercatat)");
            gridNvr.Columns.Add("dicek", "Terakhir dites");
            gridNvr.Columns.Add("scan", "Terakhir ambil daftar kamera");
            gridNvr.Columns.Add("hasil", "Ringkasan");
            gridNvr.SelectionChanged += delegate { if (!loading) SyncSelection(); };
            root.Controls.Add(gridNvr, 0, 1);

            var pf = Ui.Flow();
            prog = new ProgressBar();
            prog.Width = 320;
            prog.Margin = new Padding(6, 8, 6, 3);
            lblProg = Ui.Lbl("");
            btnCancel = Ui.Btn("Batalkan proses", delegate { if (cts != null) cts.Cancel(); });
            btnCancel.Enabled = false;
            pf.Controls.Add(Ui.Lbl("Daftar kamera NVR terpilih:"));
            pf.Controls.Add(prog);
            pf.Controls.Add(btnCancel);
            pf.Controls.Add(lblProg);
            root.Controls.Add(pf, 0, 2);

            gridCam = MakeGrid(false);
            gridCam.MultiSelect = true;
            gridCam.Columns.Add("id", "Id");
            gridCam.Columns[0].Visible = false;
            AddCamCol("kanal", "Kanal", true, 50);
            AddCamCol("nama", "Nama kamera", false, 150);
            AddCamCol("ip", "IP kamera", false, 110);
            AddCamCol("lokasi", "Lokasi", false, 130);
            AddCamCol("catatan", "Catatan", false, 150);
            AddCamCol("status", "Status di NVR", true, 150);
            AddCamCol("codec", "Codec", true, 60);
            AddCamCol("res", "Resolusi", true, 80);
            AddCamCol("fps", "FPS", true, 45);
            AddCamCol("seen", "Terakhir normal", true, 110);
            AddCamCol("detail", "Hasil deteksi stream", true, 180);
            AddCamCol("rtsp", "RTSP substream (opsional, menimpa template)", false, 260);
            // klik dua kali = dialog konfigurasi kamera; edit sel lewat F2 supaya tidak bentrok dengan klik dua kali
            gridCam.EditMode = DataGridViewEditMode.EditOnF2;
            gridCam.CellDoubleClick += OnCamDoubleClick;
            root.Controls.Add(gridCam, 0, 3);

            var cf = Ui.Flow();
            cf.Controls.Add(Ui.Btn("Simpan perubahan kamera", delegate { SaveCameras(); }));
            cf.Controls.Add(Ui.Btn("Tambah kanal manual...", delegate { AddCamera(); }));
            cf.Controls.Add(Ui.Btn("Hapus kamera terpilih", delegate { DeleteCameras(); }));
            cf.Controls.Add(Ui.Lbl("Klik dua kali baris kamera = buka detail konfigurasi (ada tombol Lihat video). Isi Nama/IP/Lokasi/Catatan: pilih sel, tekan F2 (tidak ditimpa saat ambil ulang daftar)."));
            root.Controls.Add(cf, 0, 4);

            Controls.Add(root);
            if (ServerLink.Enabled) LockForServer();

            VisibleChanged += delegate
            {
                if (Visible && !loaded)
                {
                    loaded = true;
                    LoadAll();
                }
            };
        }

        /// <summary>Mode server: registrasi NVR/kamera dilakukan di server; tab ini hanya untuk melihat dan mengetes koneksi.</summary>
        void LockForServer()
        {
            var banner = new Panel();
            banner.Dock = DockStyle.Top;
            banner.Height = Asm.S(40);
            banner.BackColor = Color.FromArgb(60, 48, 20);
            var lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.UseMnemonic = false;
            lbl.ForeColor = Color.FromArgb(255, 214, 120);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Padding = new Padding(Asm.S(10), 0, 0, 0);
            lbl.Text = "Mode server aktif: NVR, kamera, dan URL RTSP diatur di server (" + AppSettings.Current.ServerUrl + "). Tab ini hanya untuk melihat dan tes koneksi.";
            var open = new Button();
            open.Text = "Buka halaman admin";
            open.Dock = DockStyle.Right;
            open.Width = Asm.S(150);
            open.Click += delegate { try { System.Diagnostics.Process.Start(AppSettings.Current.ServerUrl); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            banner.Controls.Add(lbl);
            banner.Controls.Add(open);
            Controls.Add(banner);
            banner.SendToBack();
            string[] allow = { "Tes Koneksi & Sinkron", "Lihat Respons Mentah", "Batalkan proses" };
            LockControls(this, allow);
            gridCam.ReadOnly = true;
        }

        static void LockControls(Control parent, string[] allow)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button)
                {
                    if (Array.IndexOf(allow, c.Text) < 0 && c.Text != "Buka halaman admin") c.Enabled = false;
                }
                else if (c is TextBox) ((TextBox)c).ReadOnly = true;
                else if (c is NumericUpDown || c is CheckBox) c.Enabled = false;
                if (c.HasChildren) LockControls(c, allow);
            }
        }

        void AddCamCol(string name, string title, bool readOnly, int width)
        {
            int i = gridCam.Columns.Add(name, title);
            gridCam.Columns[i].ReadOnly = readOnly;
            gridCam.Columns[i].Width = width;
            gridCam.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        }

        static DataGridView MakeGrid(bool readOnly)
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.ReadOnly = readOnly;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            return g;
        }

        // ===================== form =====================

        Control BuildForm()
        {
            var grp = new GroupBox();
            grp.Text = "Registrasi NVR - database: " + Db.FilePath;
            grp.AutoSize = true;
            grp.Dock = DockStyle.Fill;

            var f = Ui.Flow();
            txtName = Ui.Txt("", 150);
            txtHost = Ui.Txt("", 140);
            numPort = Ui.Num(1, 65535, 80, 65);
            chkHttps = new CheckBox();
            chkHttps.Text = "HTTPS";
            chkHttps.AutoSize = true;
            chkHttps.Margin = new Padding(6, 8, 3, 3);
            txtUser = Ui.Txt("admin", 90);
            txtPass = Ui.Txt("", 100);
            txtPass.UseSystemPasswordChar = true;
            numMaxCh = Ui.Num(1, 256, 16, 60);
            txtModel = Ui.Txt("", 100);
            txtLocation = Ui.Txt("", 130);
            txtNotes = Ui.Txt("", 180);
            chkSkipTest = new CheckBox();
            chkSkipTest.Text = "Simpan tanpa tes login";
            chkSkipTest.AutoSize = true;
            chkSkipTest.Margin = new Padding(6, 8, 3, 3);

            f.Controls.Add(Ui.Lbl("Nama NVR"));
            f.Controls.Add(txtName);
            f.Controls.Add(Ui.Lbl("Alamat (IP/host)"));
            f.Controls.Add(txtHost);
            f.Controls.Add(Ui.Lbl("Port"));
            f.Controls.Add(numPort);
            f.Controls.Add(chkHttps);
            f.Controls.Add(Ui.Lbl("User"));
            f.Controls.Add(txtUser);
            f.Controls.Add(Ui.Lbl("Password"));
            f.Controls.Add(txtPass);
            f.Controls.Add(Ui.Lbl("Jumlah kanal"));
            f.Controls.Add(numMaxCh);
            f.Controls.Add(Ui.Lbl("Model"));
            f.Controls.Add(txtModel);
            f.Controls.Add(Ui.Lbl("Lokasi"));
            f.Controls.Add(txtLocation);
            f.Controls.Add(Ui.Lbl("Catatan"));
            f.Controls.Add(txtNotes);
            f.Controls.Add(chkSkipTest);

            // ---- sumber substream untuk Multiview ----
            txtCamUser = Ui.Txt("", 90);
            txtCamPass = Ui.Txt("", 100);
            txtCamPass.UseSystemPasswordChar = true;
            txtRtspTpl = Ui.Txt(Rtsp.DefaultTemplate, 420);
            txtSubParam = Ui.Txt("", 110);
            var f2 = Ui.Flow();
            f2.Controls.Add(Ui.Lbl("Multiview/substream - user kamera"));
            f2.Controls.Add(txtCamUser);
            f2.Controls.Add(Ui.Lbl("Password kamera"));
            f2.Controls.Add(txtCamPass);
            f2.Controls.Add(Ui.Lbl("Template RTSP ({user} {pass} {ip})"));
            f2.Controls.Add(txtRtspTpl);
            f2.Controls.Add(Ui.Lbl("Param substream NVR (opsional)"));
            f2.Controls.Add(txtSubParam);

            actions = Ui.Flow();
            actions.Controls.Add(Ui.Btn("Registrasi Baru", delegate { NewNvr(); }));
            actions.Controls.Add(Ui.Btn("Simpan / Registrasi", delegate { Save(); }));
            actions.Controls.Add(Ui.Btn("Tes Koneksi & Sinkron", delegate { TestSelected(); }));
            actions.Controls.Add(Ui.Btn("Hapus NVR", delegate { DeleteNvr(); }));
            actions.Controls.Add(Ui.Btn("Pakai Sebagai Koneksi Aktif", delegate { Activate(); }));
            actions.Controls.Add(Ui.Lbl("|"));
            actions.Controls.Add(Ui.Btn("Ambil Daftar Kamera", delegate { FetchSelected(); }));
            actions.Controls.Add(Ui.Btn("Ambil Semua NVR", delegate { FetchAll(); }));
            actions.Controls.Add(Ui.Btn("Deteksi Codec/Resolusi (opsional)", delegate { ProbeSelected(); }));
            actions.Controls.Add(Ui.Btn("Deteksi Main/Sub + URL RTSP otomatis", delegate { ProbeSubstreams(); }));
            actions.Controls.Add(Ui.Btn("Lihat Respons Mentah", delegate { ShowRaw(); }));

            txtInfo = new TextBox();
            txtInfo.Multiline = true;
            txtInfo.ReadOnly = true;
            txtInfo.ScrollBars = ScrollBars.Vertical;
            txtInfo.Dock = DockStyle.Fill;
            txtInfo.Height = 54;

            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.AutoSize = true;
            t.ColumnCount = 1;
            t.RowCount = 4;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            t.Controls.Add(f, 0, 0);
            t.Controls.Add(f2, 0, 1);
            t.Controls.Add(actions, 0, 2);
            t.Controls.Add(txtInfo, 0, 3);
            grp.Controls.Add(t);
            return grp;
        }

        // ===================== data =====================

        void LoadAll()
        {
            try
            {
                RefreshNvrGrid(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Database SQLite tidak bisa dibuka:\n" + ex.Message
                    + "\n\nPastikan paket NuGet System.Data.SQLite.Core ter-restore dan folder x86/x64 (SQLite.Interop.dll) ada di samping aplikasi.",
                    "Daftar NVR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void RefreshNvrGrid(long selectId)
        {
            loading = true;
            try
            {
                nvrs = Db.ListNvr();
                gridNvr.Rows.Clear();
                int sel = -1;
                for (int i = 0; i < nvrs.Count; i++)
                {
                    NvrRecord n = nvrs[i];
                    int ri = gridNvr.Rows.Add(n.Id, n.Name, n.Host + ":" + n.Port + (n.UseHttps ? " (https)" : ""),
                        n.Status.Length == 0 ? "belum dites" : n.Status, n.CameraOk + " / " + n.CameraErr + " / " + n.CameraTotal,
                        Db.ToLocal(n.LastCheckedAt), Db.ToLocal(n.LastScanAt), n.LastScanSummary);
                    if (n.Id == selectId) sel = ri;
                }
                gridNvr.ClearSelection();
                if (sel >= 0)
                {
                    gridNvr.Rows[sel].Selected = true;
                    gridNvr.CurrentCell = gridNvr.Rows[sel].Cells[1];
                }
            }
            finally
            {
                loading = false;
            }
            SyncSelection();
        }

        void SyncSelection()
        {
            current = null;
            if (gridNvr.SelectedRows.Count > 0)
            {
                long id = Convert.ToInt64(gridNvr.SelectedRows[0].Cells[0].Value);
                current = nvrs.Find(x => x.Id == id);
            }
            FillForm(current);
            RefreshCameraGrid();
        }

        void FillForm(NvrRecord n)
        {
            if (n == null)
            {
                txtName.Text = "";
                txtHost.Text = "";
                numPort.Value = 80;
                chkHttps.Checked = false;
                txtUser.Text = "admin";
                txtPass.Text = "";
                numMaxCh.Value = 16;
                txtModel.Text = "";
                txtLocation.Text = "";
                txtNotes.Text = "";
                txtCamUser.Text = "";
                txtCamPass.Text = "";
                txtRtspTpl.Text = Rtsp.DefaultTemplate;
                txtSubParam.Text = "";
                txtInfo.Text = "NVR baru: isi data lalu tekan 'Simpan / Registrasi' (akan dites login dulu).";
                return;
            }
            txtName.Text = n.Name;
            txtHost.Text = n.Host;
            numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, n.Port));
            chkHttps.Checked = n.UseHttps;
            txtUser.Text = n.Username;
            txtPass.Text = "";
            numMaxCh.Value = Math.Max(numMaxCh.Minimum, Math.Min(numMaxCh.Maximum, n.MaxChannels));
            txtModel.Text = n.Model;
            txtLocation.Text = n.Location;
            txtNotes.Text = n.Notes;
            txtCamUser.Text = n.CamUser;
            txtCamPass.Text = "";
            txtRtspTpl.Text = string.IsNullOrEmpty(n.RtspTemplate) ? Rtsp.DefaultTemplate : n.RtspTemplate;
            txtSubParam.Text = n.SubstreamParam;

            var sb = new StringBuilder();
            sb.Append("Status: ").Append(n.Status.Length == 0 ? "belum dites" : n.Status);
            if (n.LastCheckedAt.Length > 0) sb.Append(" (dites ").Append(Db.ToLocal(n.LastCheckedAt)).Append(")");
            sb.Append("   |   Registrasi: ").Append(Db.ToLocal(n.RegisteredAt));
            sb.Append("   |   Password tersimpan terenkripsi - kosongkan kolom Password jika tidak diganti.");
            if (n.LastError.Length > 0) sb.Append("\r\nError terakhir: ").Append(n.LastError);
            try
            {
                foreach (DiskRecord d in Db.ListDisks(n.Id))
                {
                    sb.Append("\r\nHDD ").Append(d.Disk).Append(": ")
                      .Append(d.CapacityGb.HasValue ? d.CapacityGb + " GB" : "?")
                      .Append(" (sisa ").Append(d.RemainGb.HasValue ? d.RemainGb + " GB" : "?").Append(")")
                      .Append(d.Recording ? " [merekam]" : "");
                    if (d.Oldest.Length > 0) sb.Append("  rekaman ").Append(d.Oldest).Append(" s/d ").Append(d.Newest);
                }
            }
            catch { }
            txtInfo.Text = sb.ToString();
        }

        void RefreshCameraGrid()
        {
            gridCam.Rows.Clear();
            if (current == null) return;
            try
            {
                foreach (CameraRecord k in Db.ListCameras(current.Id))
                {
                    string res = (k.Width.HasValue && k.Height.HasValue) ? k.Width + "x" + k.Height : "";
                    string fps = k.Fps.HasValue ? k.Fps.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "";
                    int ri = gridCam.Rows.Add(k.Id, k.Channel, k.Name, k.IpAddress, k.Location, k.Note, Db.StatusText(k.RegStatus),
                        k.Codec, res, fps, Db.ToLocal(k.LastSeenAt), k.ScanDetail, k.RtspUrl);
                    if (k.RegStatus == 2) gridCam.Rows[ri].DefaultCellStyle.BackColor = Color.MistyRose;
                    else if (k.RegStatus == 0) gridCam.Rows[ri].DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Daftar kamera: " + ex.Message);
            }
        }

        // ===================== busy / progress =====================

        void SetBusy(bool b, string text)
        {
            busy = b;
            actions.Enabled = !b;
            gridNvr.Enabled = !b;
            gridCam.Enabled = !b;
            lblProg.Text = text;
            if (!b)
            {
                prog.Value = 0;
                btnCancel.Enabled = false;
            }
        }

        void Progress(string nvrName, int done, int total, string msg)
        {
            Ui.Post(this, delegate
            {
                prog.Maximum = Math.Max(1, total);
                prog.Value = Math.Max(0, Math.Min(done, prog.Maximum));
                lblProg.Text = nvrName + ": " + msg;
            });
        }

        static string Friendly(string error)
        {
            if (string.IsNullOrEmpty(error)) return "";
            if (error.IndexOf("401", StringComparison.Ordinal) >= 0)
                return "User atau password salah (401 Unauthorized).\n" + error;
            if (error.IndexOf("remote name", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "NVR tidak dapat dihubungi: periksa IP/port, HTTP/HTTPS, dan jaringan.\n" + error;
            return error;
        }

        // ===================== aksi NVR =====================

        void NewNvr()
        {
            loading = true;
            gridNvr.ClearSelection();
            loading = false;
            current = null;
            FillForm(null);
            RefreshCameraGrid();
            txtName.Focus();
        }

        void Save()
        {
            if (busy) return;
            string name = txtName.Text.Trim();
            string host = txtHost.Text.Trim();
            if (name.Length == 0 || host.Length == 0)
            {
                MessageBox.Show("Nama NVR dan alamat (IP/host) wajib diisi.");
                return;
            }
            bool isNew = current == null;
            var n = new NvrRecord();
            n.Id = isNew ? 0 : current.Id;
            n.Name = name;
            n.Host = host;
            n.Port = (int)numPort.Value;
            n.UseHttps = chkHttps.Checked;
            n.Username = txtUser.Text.Trim();
            n.PasswordEnc = txtPass.Text.Length > 0 ? Secret.Protect(txtPass.Text) : (isNew ? "" : current.PasswordEnc);
            n.MaxChannels = (int)numMaxCh.Value;
            n.Model = txtModel.Text.Trim();
            n.Location = txtLocation.Text.Trim();
            n.Notes = txtNotes.Text.Trim();
            n.CamUser = txtCamUser.Text.Trim();
            n.CamPassEnc = txtCamPass.Text.Length > 0 ? Secret.Protect(txtCamPass.Text) : (isNew ? "" : current.CamPassEnc);
            n.RtspTemplate = txtRtspTpl.Text.Trim();
            n.SubstreamParam = txtSubParam.Text.Trim();
            n.Status = isNew ? "" : current.Status;
            n.LastError = isNew ? "" : current.LastError;

            if (chkSkipTest.Checked)
            {
                Persist(n, isNew, null, "", "", null, "");
                return;
            }

            SetBusy(true, "Tes login ke " + host + "...");
            NvrEndpoint ep = n.ToEndpoint();
            Task.Run(() => NvrDrivers.For(n).Test(ep)).ContinueWith(t => Ui.Post(this, delegate
            {
                SetBusy(false, "");
                RegistrationResult res = t.Result;
                if (!res.Ok)
                {
                    DialogResult a = MessageBox.Show("Tes login gagal:\n" + Friendly(res.Error) + "\n\nTetap simpan NVR ini?",
                        "Registrasi NVR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (a != DialogResult.Yes) return;
                    Persist(n, isNew, null, "GAGAL", res.Error, null, "");
                }
                else
                {
                    Persist(n, isNew, res.Disks, "OK", "", res.Cameras, res.CamRaw);
                    if (res.CamError.Length > 0)
                        MessageBox.Show("NVR tersimpan, tetapi daftar kamera belum terambil:\n" + res.CamError
                            + "\n\nCoba tombol 'Ambil Daftar Kamera' atau 'Lihat Respons Mentah'.", "Registrasi NVR");
                }
            }));
        }

        void Persist(NvrRecord n, bool isNew, List<DiskRecord> disks, string status, string error, List<CamStatus> cams, string camRaw)
        {
            try
            {
                n.Status = status;
                n.LastError = error ?? "";
                long id;
                if (isNew)
                {
                    id = Db.InsertNvr(n, disks, cams, camRaw);
                }
                else
                {
                    Db.UpdateNvr(n);
                    id = n.Id;
                    if (status.Length > 0) Db.SetNvrStatus(id, status, error, disks);
                    if (cams != null) Db.SaveCameraList(id, cams, camRaw);
                }
                Log.Write("NVR '" + n.Name + "' (" + n.Host + ":" + n.Port + ") tersimpan, status: " + (status.Length == 0 ? "belum dites" : status));
                RefreshNvrGrid(id);
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0)
                    MessageBox.Show("NVR dengan alamat " + n.Host + ":" + n.Port + " sudah terdaftar.");
                else
                    MessageBox.Show("Gagal menyimpan ke database:\n" + ex.Message);
            }
        }

        void TestSelected()
        {
            if (busy) return;
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            NvrRecord n = current;
            SetBusy(true, "Tes login ke " + n.Host + "...");
            NvrEndpoint ep = n.ToEndpoint();
            Task.Run(() => NvrDrivers.For(n).Test(ep)).ContinueWith(t => Ui.Post(this, delegate
            {
                SetBusy(false, "");
                RegistrationResult res = t.Result;
                try
                {
                    Db.SetNvrStatus(n.Id, res.Ok ? "OK" : "GAGAL", res.Ok ? "" : res.Error, res.Ok ? res.Disks : null);
                    if (res.Ok && res.Cameras != null) Db.SaveCameraList(n.Id, res.Cameras, res.CamRaw);
                }
                catch (Exception ex) { Log.Write("Simpan status NVR: " + ex.Message); }
                RefreshNvrGrid(n.Id);
                lblProg.Text = res.Ok
                    ? "Login berhasil (UID " + res.Uid + ")" + (res.Cameras != null ? "; " + Db.Summarize(res.Cameras) : "; daftar kamera gagal: " + res.CamError)
                    : "Login gagal: " + res.Error;
            }));
        }

        void DeleteNvr()
        {
            if (busy) return;
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            NvrRecord n = current;
            DialogResult a = MessageBox.Show("Hapus NVR '" + n.Name + "' beserta " + n.CameraTotal + " kamera dan info HDD-nya dari database?",
                "Hapus NVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (a != DialogResult.Yes) return;
            try
            {
                Db.DeleteNvr(n.Id);
                Log.Write("NVR '" + n.Name + "' dihapus dari database.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menghapus: " + ex.Message);
            }
            RefreshNvrGrid(0);
        }

        void Activate()
        {
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            Action<NvrRecord> h = UseAsActive;
            if (h != null) h(current);
            lblProg.Text = "Koneksi aktif sekarang: " + current.Name + " (" + current.Host + ")";
        }

        // ===================== daftar kamera dari NVR =====================

        void FetchSelected()
        {
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            RunFetch(new List<NvrRecord> { current });
        }

        void FetchAll()
        {
            if (nvrs.Count == 0) { MessageBox.Show("Belum ada NVR terdaftar."); return; }
            RunFetch(new List<NvrRecord>(nvrs));
        }

        /// <summary>Ambil daftar kamera (as_getinfo.cgi?FILE=2) untuk satu atau banyak NVR secara berurutan.</summary>
        void RunFetch(List<NvrRecord> targets)
        {
            if (busy || targets.Count == 0) return;
            long selectId = current != null ? current.Id : 0;
            SetBusy(true, "Mengambil daftar kamera...");
            Task.Run(() =>
            {
                var summaries = new List<string>();
                for (int i = 0; i < targets.Count; i++)
                {
                    NvrRecord n = targets[i];
                    Progress(n.Name, i, targets.Count, "mengambil daftar kamera...");
                    string error = null;
                    string summary = "";
                    try
                    {
                        CameraListResult cl = NvrDrivers.For(n).ListCameras(n.ToEndpoint());
                        if (cl.Items.Count == 0)
                        {
                            Db.SetNvrStatus(n.Id, "OK", "", null);
                            error = "respons tidak berisi CAM_CONNECT_nnCH (lihat 'Lihat Respons Mentah')";
                            Db.SaveRawOnly(n.Id, cl.Raw);
                        }
                        else
                        {
                            Db.SaveCameraList(n.Id, cl.Items, cl.Raw);
                            Db.SetNvrStatus(n.Id, "OK", "", null);
                            summary = Db.Summarize(cl.Items);
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        try { Db.SetNvrStatus(n.Id, "GAGAL", ex.Message, null); } catch { }
                    }
                    summaries.Add(n.Name + ": " + (error != null ? "GAGAL - " + error : summary));
                }
                return summaries;
            }).ContinueWith(task => Ui.Post(this, delegate
            {
                SetBusy(false, "");
                RefreshNvrGrid(selectId);
                string text = task.IsFaulted ? "Gagal: " + task.Exception.GetBaseException().Message : string.Join(" | ", task.Result.ToArray());
                lblProg.Text = text;
                Log.Write("Daftar kamera: " + text);
            }));
        }

        void ShowRaw()
        {
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            string raw = current.CamInfoRaw;
            ShowText("Respons as_getinfo.cgi?FILE=2 - " + current.Name,
                raw.Length == 0 ? "(belum ada; tekan 'Ambil Daftar Kamera')" : raw);
        }

        // ===================== deteksi stream (opsional) =====================

        void ProbeSelected()
        {
            if (busy) return;
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            var channels = new List<int>();
            long nvrId = current.Id;
            NvrEndpoint ep = current.ToEndpoint();
            try
            {
                foreach (CameraRecord k in Db.ListCameras(nvrId))
                {
                    if (k.RegStatus == 1) channels.Add(k.Channel);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal membaca daftar kamera: " + ex.Message);
                return;
            }
            if (channels.Count == 0)
            {
                MessageBox.Show("Belum ada kamera berstatus 'Terdaftar & normal'. Ambil daftar kamera dulu.");
                return;
            }
            DialogResult a = MessageBox.Show("Membuka live singkat pada " + channels.Count + " kamera untuk membaca codec/resolusi/fps (jeda "
                + (ProbeGapMs / 1000) + " dtk per perintah, resolusi butuh ffmpeg.exe). Lanjutkan?",
                "Deteksi stream", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (a != DialogResult.Yes) return;

            cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken ct = cts.Token;
            SetBusy(true, "Deteksi stream dimulai...");
            btnCancel.Enabled = true;
            string name = current.Name;

            Task.Run(() =>
            {
                var results = new List<ScanResult>();
                string error = null;
                bool canceled = false;
                try
                {
                    NvrScanner.ProbeStreams(ep, channels, ProbeGapMs, results,
                        delegate (int done, int total, string msg) { Progress(name, done, total, msg); }, ct);
                }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    if (results.Count > 0) Db.SaveProbe(nvrId, results);
                }
                catch (Exception ex) { error = (error == null ? "" : error + "; ") + "database: " + ex.Message; }
                int found = 0;
                foreach (ScanResult r in results)
                {
                    if (r.Available) found++;
                }
                return name + ": " + found + " dari " + channels.Count + " kamera punya stream terdeteksi"
                    + (canceled ? " (dibatalkan)" : "") + (error != null ? "; error: " + error : "");
            }).ContinueWith(task => Ui.Post(this, delegate
            {
                SetBusy(false, "");
                RefreshNvrGrid(nvrId);
                string text = task.IsFaulted ? "Deteksi gagal: " + task.Exception.GetBaseException().Message : task.Result;
                lblProg.Text = text;
                Log.Write("Deteksi stream: " + text);
            }));
        }

        /// <summary>Deteksi otomatis URL RTSP tiap kamera (ONVIF -> templat NVR -> pola merk), diuji dengan ffmpeg; hasil disimpan per kamera.</summary>
        void ProbeSubstreams()
        {
            if (busy) return;
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            string exe = FfmpegPipe.FindExe();
            if (exe == null) { MessageBox.Show("Butuh ffmpeg.exe (folder aplikasi, PATH, atau isi path di Setup > Device > Koneksi)."); return; }
            NvrRecord nvr = current;
            List<CameraRecord> cams;
            try { cams = Db.ListCameras(nvr.Id); }
            catch (Exception ex) { MessageBox.Show("Gagal membaca daftar kamera: " + ex.Message); return; }
            var todo = new List<CameraRecord>();
            foreach (CameraRecord k in cams) if (!string.IsNullOrWhiteSpace(k.IpAddress)) todo.Add(k);
            if (todo.Count == 0)
            {
                MessageBox.Show("Tidak ada kamera dengan IP. Isi kolom 'IP kamera' dulu (pilih sel, tekan F2), lalu klik Simpan perubahan kamera.");
                return;
            }
            if (string.IsNullOrEmpty(nvr.CamUser))
                if (MessageBox.Show("User kamera di data NVR masih kosong; kamera yang butuh login akan gagal. Lanjutkan?", "Deteksi RTSP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            bool hasExisting = false;
            foreach (CameraRecord k in todo) if (!string.IsNullOrWhiteSpace(k.RtspUrl)) hasExisting = true;
            bool overwrite = false;
            if (hasExisting)
            {
                DialogResult o = MessageBox.Show("Sebagian kamera sudah punya URL RTSP.\nYa = timpa dengan hasil deteksi.\nTidak = biarkan URL yang sudah ada (hanya kamera kosong yang diisi).",
                    "Deteksi RTSP", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (o == DialogResult.Cancel) return;
                overwrite = o == DialogResult.Yes;
            }
            if (MessageBox.Show("Mendeteksi URL RTSP " + todo.Count + " kamera (ONVIF, templat NVR, lalu pola merk) dan menguji tiap kandidat. Bisa beberapa menit. Lanjutkan?",
                "Deteksi RTSP", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            cts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken ct = cts.Token;
            SetBusy(true, "Deteksi RTSP dimulai...");
            btnCancel.Enabled = true;
            long nvrId = nvr.Id;
            string name = nvr.Name;
            string tpl = nvr.RtspTemplate, cu = nvr.CamUser, cp = nvr.CamPassword;

            Task.Run(() =>
            {
                var rows = new List<Db.RtspRow>();
                int found = 0, done = 0;
                var gate = new object();
                try
                {
                    var opt = new ParallelOptions();
                    opt.MaxDegreeOfParallelism = 4;
                    opt.CancellationToken = ct;
                    Parallel.ForEach(todo, opt, delegate(CameraRecord k)
                    {
                        DiscoveryResult r;
                        string ku = string.IsNullOrEmpty(k.CamUser) ? cu : k.CamUser, kp = string.IsNullOrEmpty(k.CamUser) ? cp : k.CamPassword;
                        try { r = RtspDiscovery.Discover(exe, k.IpAddress, ku, kp, tpl, ct); }
                        catch (Exception ex) { r = new DiscoveryResult(); r.Note = ex.Message; }
                        var row = new Db.RtspRow();
                        row.Id = k.Id;
                        row.Detail = r.Summary();
                        bool hasOwn = !string.IsNullOrWhiteSpace(k.RtspUrl);
                        if (r.SubTemplate != null && (!hasOwn || overwrite))
                        {
                            // sama dengan hasil templat NVR -> kosongkan (pakai templat), supaya tidak menyimpan duplikat
                            string viaTpl = Rtsp.Build(tpl, cu, cp, k.IpAddress);
                            string built = Rtsp.Build(r.SubTemplate, cu, cp, k.IpAddress);
                            row.RtspUrl = (viaTpl.Length > 0 && viaTpl == built) ? "" : r.SubTemplate;
                            row.RtspMainUrl = r.MainTemplate ?? "";      // main yang lolos uji ffmpeg; kosong = turunkan dari sub
                        }
                        lock (gate)
                        {
                            rows.Add(row);
                            done++;
                            if (r.SubTemplate != null) found++;
                        }
                        Progress(name, done, todo.Count, "CAM " + k.Channel + ": " + row.Detail);
                    });
                }
                catch (OperationCanceledException) { }
                string err = null;
                try { if (rows.Count > 0) Db.SetRtspResults(rows); }
                catch (Exception ex) { err = "database: " + ex.Message; }
                return name + ": URL RTSP ditemukan untuk " + found + " dari " + done + " kamera"
                    + (ct.IsCancellationRequested ? " (dibatalkan)" : "") + (err != null ? "; " + err : "");
            }).ContinueWith(task => Ui.Post(this, delegate
            {
                SetBusy(false, "");
                RefreshNvrGrid(nvrId);
                RefreshCameraGrid();
                string text = task.IsFaulted ? "Deteksi gagal: " + task.Exception.GetBaseException().Message : task.Result;
                lblProg.Text = text;
                Log.Write("Deteksi RTSP: " + text);
            }));
        }

        // ===================== aksi kamera =====================

        void OnCamDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || current == null) return;
            DataGridViewRow row = gridCam.Rows[e.RowIndex];
            int ch;
            if (!int.TryParse(Convert.ToString(row.Cells[1].Value), out ch)) return;
            long id;
            if (!long.TryParse(Convert.ToString(row.Cells[0].Value), out id)) return;
            CameraRecord cam = null;
            try
            {
                foreach (CameraRecord k in Db.ListCameras(current.Id)) if (k.Id == id) { cam = k; break; }
            }
            catch (Exception ex) { MessageBox.Show("Gagal membaca data kamera: " + ex.Message); return; }
            if (cam == null) return;
            using (var d = new CameraConfigDialog(current, cam))
            {
                if (d.ShowDialog(this.FindForm()) == DialogResult.OK) RefreshCameraGrid();
            }
        }

        void SaveCameras()
        {
            if (current == null) return;
            gridCam.EndEdit();
            var list = new List<CameraRecord>();
            foreach (DataGridViewRow row in gridCam.Rows)
            {
                var k = new CameraRecord();
                k.Id = Convert.ToInt64(row.Cells[0].Value);
                k.Name = Convert.ToString(row.Cells[2].Value) ?? "";
                k.IpAddress = Convert.ToString(row.Cells[3].Value) ?? "";
                k.Location = Convert.ToString(row.Cells[4].Value) ?? "";
                k.Note = Convert.ToString(row.Cells[5].Value) ?? "";
                k.RtspUrl = (Convert.ToString(row.Cells[12].Value) ?? "").Trim();
                list.Add(k);
            }
            try
            {
                Db.UpdateCamerasManual(list);
                lblProg.Text = list.Count + " kamera disimpan.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menyimpan: " + ex.Message);
            }
        }

        void AddCamera()
        {
            if (current == null) { MessageBox.Show("Pilih NVR di daftar dulu."); return; }
            string s = Ask("Tambah kamera manual", "Nomor kanal di NVR:", "");
            if (s == null) return;
            int ch;
            if (!int.TryParse(s, out ch) || ch < 1 || ch > 256)
            {
                MessageBox.Show("Nomor kanal tidak valid (1-256).");
                return;
            }
            try
            {
                Db.AddCameraManual(current.Id, ch, "");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menambah: " + ex.Message);
                return;
            }
            RefreshNvrGrid(current.Id);
        }

        void DeleteCameras()
        {
            if (current == null || gridCam.SelectedRows.Count == 0)
            {
                MessageBox.Show("Pilih satu atau lebih baris kamera dulu.");
                return;
            }
            DialogResult a = MessageBox.Show("Hapus " + gridCam.SelectedRows.Count + " kamera terpilih dari database?",
                "Hapus kamera", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (a != DialogResult.Yes) return;
            try
            {
                foreach (DataGridViewRow row in gridCam.SelectedRows)
                    Db.DeleteCamera(Convert.ToInt64(row.Cells[0].Value));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menghapus: " + ex.Message);
            }
            RefreshNvrGrid(current.Id);
        }

        static void ShowText(string title, string text)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(720, 520);
                var t = new TextBox();
                t.Multiline = true;
                t.ReadOnly = true;
                t.ScrollBars = ScrollBars.Both;
                t.WordWrap = false;
                t.Dock = DockStyle.Fill;
                t.Font = new Font(FontFamily.GenericMonospace, 9f);
                t.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                t.Select(0, 0);
                f.Controls.Add(t);
                Theme.Apply(f, false);
                f.ShowDialog();
            }
        }

        static string Ask(string title, string label, string def)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
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
    }
}
