using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Koneksi: alamat NVR, kredensial, HTTP/HTTPS, simpan pengaturan, tes login (UID), logout.</summary>
    public class ConnectionTab : UserControl
    {
        TextBox txtHost, txtUser, txtPass, txtFfmpeg;
        Label lblFfmpeg, lblGpu;
        ComboBox cmbHw;
        NumericUpDown numPort, numChannels;
        CheckBox chkHttps, chkIgnoreCert;
        Label lblResult;

        public ConnectionTab()
        {
            Dock = DockStyle.Fill;
            AppSettings s = AppSettings.Current;

            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.ColumnCount = 2;
            t.Padding = new Padding(12);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtHost = Ui.Txt(s.Host, 240);
            numPort = Ui.Num(1, 65535, s.Port, 80);
            chkHttps = new CheckBox();
            chkHttps.Text = "Pakai HTTPS (port biasanya 443)";
            chkHttps.Checked = s.UseHttps;
            chkHttps.AutoSize = true;
            chkIgnoreCert = new CheckBox();
            chkIgnoreCert.Text = "Abaikan error sertifikat (sertifikat self-signed NVR)";
            chkIgnoreCert.Checked = s.IgnoreCertErrors;
            chkIgnoreCert.AutoSize = true;
            txtUser = Ui.Txt(s.Username, 200);
            txtPass = Ui.Txt(s.Password, 200);
            txtPass.UseSystemPasswordChar = true;
            numChannels = Ui.Num(1, 256, s.MaxChannels, 80);

            AddRow(t, "Alamat NVR (IP / host)", txtHost);
            AddRow(t, "Port", numPort);
            AddRow(t, "", chkHttps);
            AddRow(t, "", chkIgnoreCert);
            AddRow(t, "User", txtUser);
            AddRow(t, "Password", txtPass);
            AddRow(t, "Jumlah kanal NVR (untuk MASK playback)", numChannels);

            txtFfmpeg = Ui.Txt(s.FfmpegPath, 300);
            lblFfmpeg = Ui.Lbl("");
            var ffRow = Ui.Flow();
            ffRow.Controls.Add(txtFfmpeg);
            ffRow.Controls.Add(Ui.Btn("...", delegate { BrowseFfmpeg(); }));
            ffRow.Controls.Add(Ui.Btn("Cek ffmpeg", delegate { CheckFfmpeg(); }));
            ffRow.Controls.Add(lblFfmpeg);
            AddRow(t, "ffmpeg.exe (untuk live H.264/H.265)", ffRow);

            cmbHw = Ui.Combo(HwAccels.Items, HwAccels.ToIndex(s.HwAccel), 260);
            lblGpu = Ui.Lbl("");
            var gpuRow = Ui.Flow();
            gpuRow.Controls.Add(cmbHw);
            gpuRow.Controls.Add(Ui.Btn("Cek & Tes GPU", delegate { CheckGpu(); }));
            gpuRow.Controls.Add(lblGpu);
            AddRow(t, "Akselerasi GPU (dekode video)", gpuRow);

            var btns = Ui.Flow();
            btns.Controls.Add(Ui.Btn("Simpan", delegate { Save(); }));
            btns.Controls.Add(Ui.Btn("Tes Login (ambil UID)", delegate { TestLogin(); }));
            btns.Controls.Add(Ui.Btn("Logout Semua Sesi", delegate { LogoutAll(); }));
            AddRow(t, "", btns);

            lblResult = new Label();
            lblResult.AutoSize = true;
            lblResult.Text = "Pengaturan disimpan di %AppData%\\IproNvrTool\\settings.xml (password terenkripsi DPAPI).";
            AddRow(t, "", lblResult);

            Controls.Add(t);
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            var l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Margin = new Padding(3, 8, 12, 3);
            t.RowCount = t.RowCount + 1;
            t.Controls.Add(l);
            t.Controls.Add(c);
        }

        void Save()
        {
            AppSettings s = AppSettings.Current;
            s.Host = txtHost.Text.Trim();
            s.Port = (int)numPort.Value;
            s.UseHttps = chkHttps.Checked;
            s.IgnoreCertErrors = chkIgnoreCert.Checked;
            s.Username = txtUser.Text.Trim();
            s.Password = txtPass.Text;
            s.MaxChannels = (int)numChannels.Value;
            s.FfmpegPath = txtFfmpeg.Text.Trim();
            s.HwAccel = HwAccels.Values[Math.Max(0, cmbHw.SelectedIndex)];
            try
            {
                s.Save();
                lblResult.Text = "Tersimpan. Sesi lama akan di-logout; login ulang otomatis saat dipakai.";
            }
            catch (Exception ex) { lblResult.Text = "Gagal menyimpan: " + ex.Message; }
            Task.Run(delegate { Sessions.LogoutAll(); });
        }

        /// <summary>Isi ulang kolom dari pengaturan aplikasi (dipanggil saat NVR aktif diganti dari Daftar NVR).</summary>
        public void Reload()
        {
            AppSettings s = AppSettings.Current;
            txtHost.Text = s.Host;
            numPort.Value = Math.Max(numPort.Minimum, Math.Min(numPort.Maximum, s.Port));
            chkHttps.Checked = s.UseHttps;
            txtUser.Text = s.Username;
            txtPass.Text = s.Password;
            numChannels.Value = Math.Max(numChannels.Minimum, Math.Min(numChannels.Maximum, s.MaxChannels));
            lblResult.Text = "Koneksi aktif diganti dari Daftar NVR: " + s.Host + ":" + s.Port;
        }

        void BrowseFfmpeg()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "ffmpeg.exe|ffmpeg.exe|Semua file|*.*";
                if (d.ShowDialog() == DialogResult.OK) txtFfmpeg.Text = d.FileName;
            }
            CheckFfmpeg();
        }

        void CheckFfmpeg()
        {
            AppSettings.Current.FfmpegPath = txtFfmpeg.Text.Trim();
            string exe = FfmpegPipe.FindExe();
            lblFfmpeg.Text = exe != null ? "terdeteksi: " + exe : "tidak ditemukan (letakkan di folder aplikasi / PATH, atau pilih manual)";
        }

        /// <summary>Tampilkan metode akselerasi yang dikenal ffmpeg.exe lalu uji dekode sungguhan dengan mode terpilih.
        /// Bila GPU gagal saat live, aplikasi otomatis kembali ke dekode CPU.</summary>
        void CheckGpu()
        {
            AppSettings.Current.FfmpegPath = txtFfmpeg.Text.Trim();
            string exe = FfmpegPipe.FindExe();
            if (exe == null) { lblGpu.Text = "ffmpeg.exe tidak ditemukan."; return; }
            string mode = HwAccels.Values[Math.Max(0, cmbHw.SelectedIndex)];
            lblGpu.Text = "memeriksa...";
            Task.Run(delegate
            {
                string list = FfmpegPipe.ListHwAccels(exe);
                string test = FfmpegPipe.TestHwAccel(exe, mode);
                string text = "Didukung build ffmpeg ini: " + (list == null ? "(gagal dibaca)" : (list.Length == 0 ? "(tidak ada)" : list))
                    + Environment.NewLine + Environment.NewLine + test;
                Log.Write("Cek GPU: " + text.Replace("\r", "").Replace("\n", " | "));
                Ui.Post(this, delegate
                {
                    lblGpu.Text = test.StartsWith("BERHASIL", StringComparison.Ordinal) ? "GPU dapat dipakai" : "lihat hasil tes";
                    MessageBox.Show(text, "Cek akselerasi GPU");
                });
            });
        }

        void TestLogin()
        {
            Save();
            lblResult.Text = "Menghubungi NVR...";
            Task.Run(delegate
            {
                try
                {
                    Sessions.Util.Logout();
                    Sessions.Util.Login();
                    string uid = Sessions.Util.Uid;
                    Ui.Post(this, delegate { lblResult.Text = "Login berhasil. UID = " + uid; });
                }
                catch (Exception ex)
                {
                    Ui.Post(this, delegate { lblResult.Text = "Login gagal: " + ex.Message; });
                    Log.Write("Login gagal: " + ex.Message);
                }
            });
        }

        void LogoutAll()
        {
            Task.Run(delegate { Sessions.LogoutAll(); });
            lblResult.Text = "Semua sesi di-logout.";
        }
    }
}
