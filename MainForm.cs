using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    public class MainForm : Form
    {
        readonly ConnectionTab connTab;
        readonly LiveTab liveTab;
        readonly NvrRegTab regTab;
        readonly List<IShutdown> shutdowns = new List<IShutdown>();

        public MainForm()
        {
            Text = "i-PRO NVR Tool (CGI Interface v1.3R1)";
            Width = 1350;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new ModernTabControl();
            tabs.Dock = DockStyle.Fill;
            connTab = new ConnectionTab();
            liveTab = new LiveTab();
            regTab = new NvrRegTab();
            regTab.UseAsActive += OnUseNvr;
            AddTab(tabs, "Koneksi", connTab);
            AddTab(tabs, "Daftar NVR", regTab);
            AddTab(tabs, "Live & PTZ", liveTab);
            AddTab(tabs, "Playback", new PlaybackTab());
            AddTab(tabs, "Info Recorder", new InfoTab());
            AddTab(tabs, "Download", new DownloadTab());

            var root = new Panel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(10, 4, 10, 10);
            root.Controls.Add(tabs);
            Controls.Add(root);

            var menu = new MenuStrip();
            var mv = new ToolStripMenuItem("Multiview");
            mv.DropDownOpening += delegate { BuildMultiviewMenu(mv); };
            BuildMultiviewMenu(mv);
            menu.Items.Add(mv);
            Controls.Add(menu);
            MainMenuStrip = menu;

            Theme.Apply(this, false);

            FormClosing += OnClosing;
            // buka lagi jendela multiview yang masih terbuka saat aplikasi terakhir ditutup
            Shown += delegate { BeginInvoke(new Action(delegate { MultiviewForm.RestoreOpenWindows(); })); };
        }

        /// <summary>NVR dari Daftar NVR dijadikan koneksi aktif untuk tab Live/Playback/Info/Download.</summary>
        void OnUseNvr(NvrRecord n)
        {
            AppSettings s = AppSettings.Current;
            s.Host = n.Host;
            s.Port = n.Port;
            s.UseHttps = n.UseHttps;
            s.Username = n.Username;
            s.PasswordProtected = n.PasswordEnc;
            s.MaxChannels = n.MaxChannels;
            try { s.Save(); } catch { }
            Task.Run(delegate { Sessions.LogoutAll(); }); // sesi lama logout ke NVR lamanya sendiri
            connTab.Reload();

            try
            {
                var chans = new List<string>();
                foreach (CameraRecord k in Db.ListCameras(n.Id))
                {
                    if (k.RegStatus == 1 && chans.Count < 16) chans.Add(k.Channel.ToString());
                }
                if (chans.Count > 0) liveTab.SetCameras(string.Join(",", chans.ToArray()));
            }
            catch (Exception ex)
            {
                Log.Write("Daftar kamera untuk tab Live: " + ex.Message);
            }
            Log.Write("Koneksi aktif: " + n.Name + " (" + n.Host + ":" + n.Port + ")");
        }

        /// <summary>Menu Multiview: jendela baru atau buka konfigurasi tersimpan (tiap jendela punya konfigurasi sendiri).</summary>
        static void BuildMultiviewMenu(ToolStripMenuItem mv)
        {
            mv.DropDownItems.Clear();
            mv.DropDownItems.Add("Multiview Baru...", null, delegate { MultiviewForm.CreateNew(); });
            mv.DropDownItems.Add(new ToolStripSeparator());
            List<string> names = MultiviewForm.ListNames();
            if (names.Count == 0)
            {
                var none = new ToolStripMenuItem("(belum ada multiview tersimpan)");
                none.Enabled = false;
                mv.DropDownItems.Add(none);
            }
            foreach (string name in names)
            {
                string nm = name;
                mv.DropDownItems.Add(nm + (MultiviewForm.IsOpen(nm) ? "   (terbuka)" : ""), null, delegate { MultiviewForm.OpenNamed(nm); });
            }
            mv.DropDownItems.Add(new ToolStripSeparator());
            mv.DropDownItems.Add("Buka Semua yang Tersimpan", null, delegate
            {
                foreach (string nm in MultiviewForm.ListNames()) MultiviewForm.OpenNamed(nm);
            });
            mv.DropDownItems.Add("Tutup Semua Multiview", null, delegate { MultiviewForm.CloseAll(); });
            Theme.StyleItems(mv.DropDownItems, false);
        }

        void AddTab(TabControl tabs, string title, Control c)
        {
            var page = new TabPage(title);
            page.Padding = new Padding(10, 8, 10, 8); // ruang di sekeliling isi tab
            c.Dock = DockStyle.Fill;
            page.Controls.Add(c);
            tabs.TabPages.Add(page);
            IShutdown s = c as IShutdown;
            if (s != null) shutdowns.Add(s);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            try { MultiviewForm.ShutdownAll(); } catch { }
            foreach (IShutdown s in shutdowns)
            {
                try { s.Shutdown(); } catch { }
            }
            // logout UID agar slot NVR (maks. 16 UID) tidak terpakai sia-sia
            try { Task.Run(delegate { Sessions.LogoutAll(); }).Wait(4000); } catch { }
        }
    }
}
