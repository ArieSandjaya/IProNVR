using System;
using System.Windows.Forms;

namespace IproNvrTool
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool child = false;
            foreach (string a in args)
                if (string.Equals(a, "--child", StringComparison.OrdinalIgnoreCase)) child = true;
            AppLifecycle.IsChild = child;

            if (!child)
            {
                if (!AppLifecycle.AcquireSingleInstance())
                {
                    MessageBox.Show("Aplikasi sudah berjalan.", "i-PRO NVR Tool");
                    return;
                }
                bool sup = false;
                try { sup = AppSettings.Current.Supervise; } catch { }
                bool cl = false;
                foreach (string a in args) if (string.Equals(a, "--classic", StringComparison.OrdinalIgnoreCase)) cl = true;
                if (sup && !cl) { Environment.Exit(AppLifecycle.Supervise(args)); return; }
            }
            AppLifecycle.InstallCrashHandlers();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();

            bool classic = false;
            foreach (string a in args)
                if (string.Equals(a, "--classic", StringComparison.OrdinalIgnoreCase)) classic = true;

            if (classic)
            {
                Application.Run(new MainForm());
                return;
            }

            try
            {
                Db.Init();
                ServerLink.Start();     // mode server: tarik konfigurasi terbaru sebelum Dir membaca database
                Dir.Init();
                Events.HookNetworkLog();
                AppSettings s = AppSettings.Current;
                if (s.RequireLogin && AsmDb.HasAdmin())
                {
                    using (var lf = new LoginForm())
                    {
                        if (lf.ShowDialog() != DialogResult.OK) return;
                        AsmApp.User = lf.User;
                    }
                }
                EventMonitor.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal memulai tampilan ASM300: " + ex.Message + "\n\nMembuka tampilan klasik.", "i-PRO NVR Tool");
                Application.Run(new MainForm());
                return;
            }
            var main = new AsmMainForm();
            AppLifecycle.StartMonitors();
            Application.Run(main);
        }
    }
}
