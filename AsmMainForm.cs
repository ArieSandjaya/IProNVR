using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    public class LoginForm : Form
    {
        readonly TextBox txtName = new TextBox();
        readonly TextBox txtPass = new TextBox();
        public UserRec User;

        public LoginForm()
        {
            Text = "Login";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Asm.S(320), Asm.S(170));
            BackColor = Asm.Panel;
            var l1 = new Label(); l1.Text = "Nama user"; l1.Left = Asm.S(16); l1.Top = Asm.S(16); l1.AutoSize = true; l1.ForeColor = Asm.Text;
            txtName.Left = Asm.S(16); txtName.Top = Asm.S(38); txtName.Width = Asm.S(286);
            var l2 = new Label(); l2.Text = "Password"; l2.Left = Asm.S(16); l2.Top = Asm.S(68); l2.AutoSize = true; l2.ForeColor = Asm.Text;
            txtPass.Left = Asm.S(16); txtPass.Top = Asm.S(90); txtPass.Width = Asm.S(286); txtPass.UseSystemPasswordChar = true;
            var ok = new Button(); ok.Text = "Login"; ok.Left = Asm.S(16); ok.Top = Asm.S(124); ok.Width = Asm.S(110);
            ok.Click += delegate { Try(); };
            var cancel = new Button(); cancel.Text = "Keluar"; cancel.Left = Asm.S(140); cancel.Top = Asm.S(124); cancel.Width = Asm.S(110);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.AddRange(new Control[] { l1, txtName, l2, txtPass, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.ApplyTo(this, true);
        }

        void Try()
        {
            UserRec u = null;
            try { u = AsmDb.Login(txtName.Text.Trim(), txtPass.Text); }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            if (u == null) { MessageBox.Show("Nama user atau password salah.", "Login"); txtPass.Text = ""; return; }
            User = u;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>Popup notifikasi kecil di pojok kanan bawah layar.</summary>
    public class NotifyForm : Form
    {
        static readonly List<NotifyForm> open = new List<NotifyForm>();

        public NotifyForm(string kind, LogEntry e)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(Asm.S(340), Asm.S(92));
            bool alarm = kind == IproNvrTool.Events.Alarm;
            BackColor = alarm ? Asm.Danger : Asm.Warn;
            var t = new Label();
            t.Dock = DockStyle.Fill;
            t.ForeColor = Color.Black;
            t.Padding = new Padding(Asm.S(10), Asm.S(6), Asm.S(30), 0);
            t.Font = Asm.F(9f, false);
            t.Text = (alarm ? "ALARM" : (kind == IproNvrTool.Events.Device ? "DEVICE ERROR" : "NETWORK ERROR")) + "   " + Asm.FormatTime(e.Time) + "\r\n" + e.Source + "\r\n" + e.Text;
            var x = new AsmButton(AsmIcon.Close, "Tutup", delegate { Close(); });
            x.Size = new Size(Asm.S(24), Asm.S(24));
            x.Location = new Point(Width - Asm.S(28), Asm.S(4));
            x.Borderless = true;
            x.IconColor = Color.Black;
            Controls.Add(x);
            Controls.Add(t);
            x.BringToFront();
            t.Click += delegate { Close(); };
        }

        public static void Show(string kind, LogEntry e)
        {
            var f = new NotifyForm(kind, e);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int n = open.Count;
            f.Location = new Point(wa.Right - f.Width - 8, wa.Bottom - (f.Height + 6) * (n + 1) - 6);
            open.Add(f);
            f.FormClosed += delegate
            {
                open.Remove(f);
                if (open.Count == 0) Buzzer.Stop();
            };
            f.Show();
            Buzzer.Start();
            f.Opacity = 0.96;
            if (open.Count > 6) open[0].Close();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public static void CloseAll()
        {
            foreach (NotifyForm f in new List<NotifyForm>(open)) f.Close();
            Buzzer.Stop();
        }
    }

    /// <summary>Jendela utama bergaya ASM300: bar judul dengan ikon Live/Map/Search/Setting + tab, menu ≡, dan jendela di tab.</summary>
    public class AsmMainForm : Form
    {
        readonly Panel titleBar = new Panel();
        readonly Panel host = new Panel();
        readonly AsmTabStrip strip = new AsmTabStrip();
        readonly Label lblUser = new Label();
        readonly Dictionary<string, Control> views = new Dictionary<string, Control>();
        readonly Dictionary<string, Form> detached = new Dictionary<string, Form>();
        OperationView op;
        readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer();
        DateTime lastInput = DateTime.Now;
        bool fullScreen;
        FormBorderStyle oldStyle;
        bool loggingOut;

        class InputFilter : IMessageFilter
        {
            readonly AsmMainForm f;
            public InputFilter(AsmMainForm f) { this.f = f; }
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == 0x200 || m.Msg == 0x201 || m.Msg == 0x100 || m.Msg == 0x20A) f.lastInput = DateTime.Now;
                return false;
            }
        }

        public AsmMainForm()
        {
            Text = "i-PRO NVR Tool - ASM300 style";
            Width = 1480;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Asm.Bg;
            KeyPreview = true;
            UiSmooth.Attach(this);

            titleBar.Dock = DockStyle.Top;
            titleBar.Height = Asm.S(40);
            titleBar.BackColor = Asm.Bar;
            titleBar.Tag = "nostyle";
            host.Dock = DockStyle.Fill;
            host.BackColor = Asm.Bg;
            host.Tag = "nostyle";
            Controls.Add(host);
            Controls.Add(titleBar);

            strip.Dock = DockStyle.Fill;
            strip.TabSelected += delegate(string k) { ShowView(k); };
            strip.TabClosed += delegate(string k) { CloseView(k); };
            strip.TabDetach += delegate(string k) { Detach(k); };

            var menuBtn = new AsmButton(AsmIcon.Menu, "Menu", null);
            menuBtn.Dock = DockStyle.Right; menuBtn.Width = Asm.S(44); menuBtn.Borderless = true;
            menuBtn.Click += delegate { ShowMenu(menuBtn); };
            lblUser.Dock = DockStyle.Right;
            lblUser.Width = Asm.S(130);
            lblUser.ForeColor = Asm.Text;
            lblUser.TextAlign = ContentAlignment.MiddleRight;
            lblUser.Tag = "nostyle";

            var iconRow = new FlowLayoutPanel();
            iconRow.Dock = DockStyle.Left;
            iconRow.Width = Asm.S(4 * 44 + 14);
            iconRow.Padding = new Padding(Asm.S(4), Asm.S(4), 0, 0);
            iconRow.Tag = "nostyle";
            iconRow.Controls.Add(Icon(AsmIcon.Live, "Live window", "live"));
            iconRow.Controls.Add(Icon(AsmIcon.Map, "Map window", "map"));
            iconRow.Controls.Add(Icon(AsmIcon.Search, "Search window", "search"));
            iconRow.Controls.Add(Icon(AsmIcon.Setting, "Setup window", "setup"));

            titleBar.Controls.Add(strip);
            titleBar.Controls.Add(lblUser);
            titleBar.Controls.Add(menuBtn);
            titleBar.Controls.Add(iconRow);

            op = new OperationView();
            AddView("operation", "Operation", op, false);

            AsmApp.ShowCamera = delegate(NvrRecord nvr, CameraRecord cam)
            {
                OnUi(delegate { Activate("operation"); op.ShowCamera(nvr, cam); });
            };
            AsmApp.PlayCameraAt = delegate(NvrRecord nvr, CameraRecord cam, DateTime utc)
            {
                OnUi(delegate { Activate("operation"); op.PlayCameraAt(new CamRef(nvr.Id, cam.Channel), utc); });
            };
            AsmApp.OpenWindow = delegate(string k) { OnUi(delegate { OpenWindow(k); }); };
            SetupView.SettingsApplied += delegate { OnUi(delegate { op.RefreshFromSettings(); UpdateUser(); }); };
            IproNvrTool.Events.Added += OnEvent;

            idleTimer.Interval = 15000;
            idleTimer.Tick += delegate { CheckIdle(); };
            idleTimer.Start();
            Application.AddMessageFilter(new InputFilter(this));

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F11) ToggleFull();
            };
            FormClosing += OnClosing;
            Shown += delegate { BeginInvoke((Action)OnShown); };
            UpdateUser();
        }

        void OnUi(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        AsmButton Icon(AsmIcon ic, string tip, string key)
        {
            var b = new AsmButton(ic, tip, delegate { OpenWindow(key); });
            b.Size = new Size(Asm.S(38), Asm.S(32));
            return b;
        }

        void UpdateUser()
        {
            lblUser.Text = AsmApp.User == null ? "" : AsmApp.User.Name + "  ";
        }

        // ---------- tab / view ----------

        void AddView(string key, string title, Control view, bool closable)
        {
            views[key] = view;
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            host.Controls.Add(view);
            var t = new AsmTab();
            t.Key = key; t.Title = title; t.Closable = closable; t.Detachable = closable && AppSettings.Current.SeparateTab;
            strip.Add(t);
            strip.Select(key);
            ShowView(key);
        }

        void ShowView(string key)
        {
            foreach (KeyValuePair<string, Control> kv in views)
                if (kv.Value.Parent == host) kv.Value.Visible = kv.Key == key;
            Control v;
            if (views.TryGetValue(key, out v) && v.Parent == host) v.BringToFront();
        }

        void Activate(string key)
        {
            if (detached.ContainsKey(key)) { detached[key].Activate(); return; }
            if (!views.ContainsKey(key)) return;
            strip.Select(key);
            ShowView(key);
        }

        void CloseView(string key)
        {
            Control v;
            if (!views.TryGetValue(key, out v) || key == "operation") return;
            strip.Remove(key);
            views.Remove(key);
            host.Controls.Remove(v);
            v.Dispose();
            strip.Select("operation");
            ShowView("operation");
        }

        void Detach(string key)
        {
            Control v;
            if (!views.TryGetValue(key, out v) || key == "operation") return;
            string title = "";
            foreach (AsmTab t in strip.Tabs) if (t.Key == key) title = t.Title;
            strip.Remove(key);
            host.Controls.Remove(v);
            var f = new Form();
            f.Text = title + " - i-PRO NVR Tool";
            f.Size = new Size(1200, 760);
            f.BackColor = Asm.Bg;
            f.StartPosition = FormStartPosition.CenterScreen;
            v.Visible = true;
            f.Controls.Add(v);
            detached[key] = f;
            f.FormClosed += delegate
            {
                detached.Remove(key);
                views.Remove(key);
                v.Dispose();
            };
            f.Show();
            strip.Select("operation");
            ShowView("operation");
        }

        public void OpenWindow(string key)
        {
            AppSettings s = AppSettings.Current;
            if (key == "operation") { Activate("operation"); return; }
            if (key == "live")
            {
                if (!AsmApp.Can(Perm.Live)) { MessageBox.Show("Tidak ada hak Live."); return; }
                for (int i = 1; i <= 4; i++)
                {
                    string k = "live" + i;
                    if (views.ContainsKey(k)) continue;
                    if (s.ConfirmBeforeLive && MessageBox.Show("Buka Live window?", "Live", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    var lv = new LiveView(i);
                    AddView(k, "Live " + i, lv, true);
                    lv.ApplyStartup();
                    return;
                }
                MessageBox.Show("Live window maksimum 4.");
                return;
            }
            if (views.ContainsKey(key) || detached.ContainsKey(key)) { Activate(key); return; }
            if (key == "map")
            {
                if (s.ConfirmBeforeLive && MessageBox.Show("Buka Map window?", "Map", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                AddView("map", "Map", new MapView(), true);
            }
            else if (key == "search")
            {
                if (!AsmApp.Can(Perm.Playback)) { MessageBox.Show("Tidak ada hak playback."); return; }
                AddView("search", "Search", new SearchView(), true);
            }
            else if (key == "setup")
            {
                if (!AsmApp.Can(Perm.Setup)) { MessageBox.Show("Tidak ada hak Setup."); return; }
                AddView("setup", "Setup", new SetupView(), true);
            }
        }

        // ---------- menu ----------

        void ShowMenu(Control anchor)
        {
            ContextMenuStrip m = Asm.Menu();
            ToolStripMenuItem mag = new ToolStripMenuItem("Display magnification");
            foreach (int pct in new int[] { 75, 100, 125, 150, 175, 200 })
            {
                int p = pct;
                ToolStripMenuItem it = Asm.Sub(mag, p + "%", delegate
                {
                    AppSettings.Current.AsmMagnification = p;
                    try { AppSettings.Current.Save(); } catch { }
                    MessageBox.Show("Ukuran tampilan " + p + "% berlaku setelah aplikasi dibuka ulang.");
                });
                it.Checked = AppSettings.Current.AsmMagnification == p;
            }
            m.Items.Add(mag);
            Asm.Item(m, "Display setups...", delegate { OpenWindow("setup"); });
            if (ServerLink.Enabled)
                Asm.Item(m, "Periksa pembaruan dari server", delegate
                {
                    System.Threading.Tasks.Task.Run(delegate { bool ch; string r = ServerLink.CheckNow(out ch); return new KeyValuePair<bool, string>(ch, r); })
                        .ContinueWith(t => OnUi(delegate
                        {
                            if (t.IsFaulted) { MessageBox.Show(t.Exception.GetBaseException().Message, "Server konfigurasi"); return; }
                            if (t.Result.Key) op.RefreshFromSettings();
                            MessageBox.Show(t.Result.Value, "Server konfigurasi");
                        }));
                });
            Asm.Item(m, fullScreen ? "Keluar layar penuh (F11)" : "Full screen (F11)", delegate { ToggleFull(); });
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mv = new ToolStripMenuItem("Multiview (jendela terpisah)");
            Asm.Sub(mv, "Multiview baru...", delegate { MultiviewForm.CreateNew(); });
            foreach (string nm in MultiviewForm.ListNames())
            {
                string n = nm;
                Asm.Sub(mv, n + (MultiviewForm.IsOpen(n) ? "   (terbuka)" : ""), delegate { MultiviewForm.OpenNamed(n); });
            }
            Asm.Sub(mv, "Tutup semua multiview", delegate { MultiviewForm.CloseAll(); });
            m.Items.Add(mv);
            Asm.Item(m, "Tampilan klasik (alat lama)", delegate
            {
                var f = new MainForm();
                f.Show();
            });
            m.Items.Add(new ToolStripSeparator());
            Asm.Item(m, "Tutup semua notifikasi", delegate { NotifyForm.CloseAll(); });
            Asm.Item(m, "Version", delegate
            {
                MessageBox.Show("i-PRO NVR Tool - tampilan bergaya ASM300 (v7.80)\nBackend: CGI Interface i-PRO v1.3R1\nFitur ASM300 yang tidak punya CGI (pencarian event REC/VMD/wajah, heat map, decoder, license, dewarp fisheye) tidak tersedia.", "Version");
            });
            Asm.Item(m, "Help", delegate
            {
                string readme = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
                try { if (System.IO.File.Exists(readme)) System.Diagnostics.Process.Start(readme); else MessageBox.Show("README.md tidak ditemukan."); }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem lo = Asm.Item(m, "Logout", delegate { Logout(); });
            lo.Enabled = AsmApp.User != null;
            Asm.Item(m, "Exit", delegate { Close(); });
            Asm.StyleMenu(m);
            m.Show(anchor, new Point(anchor.Width - m.Width, anchor.Height));
        }

        void ToggleFull()
        {
            if (!fullScreen)
            {
                oldStyle = FormBorderStyle;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
                fullScreen = true;
            }
            else
            {
                FormBorderStyle = oldStyle;
                WindowState = FormWindowState.Normal;
                fullScreen = false;
            }
        }

        // ---------- notifikasi & login ----------

        void OnEvent(string kind, LogEntry e)
        {
            // error (device/jaringan) hanya dicatat di log & penghitung ERR di status bar; popup hanya untuk alarm
            if (kind != IproNvrTool.Events.Alarm) return;
            if (!AppSettings.Current.EventPopup) { OnUi(delegate { Buzzer.Start(); }); return; }
            OnUi(delegate { NotifyForm.Show(kind, e); });
        }

        void CheckIdle()
        {
            int min = AppSettings.Current.AutoLogoutMin;
            if (min <= 0 || AsmApp.User == null || loggingOut) return;
            if ((DateTime.Now - lastInput).TotalMinutes >= min) Logout();
        }

        void Logout()
        {
            if (loggingOut) return;
            loggingOut = true;
            try
            {
                op.StopSequence();
                op.ExitPlayback();
                AsmApp.User = null;
                UpdateUser();
                if (!AppSettings.Current.RequireLogin) return;
                Hide();
                NotifyForm.CloseAll();
                using (var lf = new LoginForm())
                {
                    if (lf.ShowDialog() == DialogResult.OK)
                    {
                        AsmApp.User = lf.User;
                        lastInput = DateTime.Now;
                        UpdateUser();
                        Show();
                    }
                    else
                    {
                        Close();
                    }
                }
            }
            finally { loggingOut = false; }
        }

        // ---------- mulai / tutup ----------

        void OnShown()
        {
            AppSettings s = AppSettings.Current;
            op.ApplyStartup();
            for (int i = 0; i < s.LiveWindowsOnStart && i < 4; i++) OpenWindow("live");
            if (s.MapOnStart) OpenWindow("map");
            Activate("operation");
            BeginInvoke(new Action(delegate { try { MultiviewForm.RestoreOpenWindows(); } catch { } }));
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            IproNvrTool.Events.Added -= OnEvent;
            idleTimer.Stop();
            NotifyForm.CloseAll();
            try { EventMonitor.Stop(); } catch { }
            try { MultiviewForm.ShutdownAll(); } catch { }
            foreach (Form f in new List<Form>(detached.Values)) { try { f.Close(); } catch { } }
            try { op.Shutdown(); } catch { }
            foreach (Control v in new List<Control>(views.Values))
            {
                LiveView lv = v as LiveView;
                if (lv != null) { try { lv.Shutdown(); } catch { } }
            }
            try { Task.Run(delegate { Pool.LogoutAll(); Sessions.LogoutAll(); }).Wait(5000); } catch { }
        }
    }
}
