using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Pengaman operasi 24x7: satu instance, supervisor (restart otomatis bila crash/hang), keep-awake, batas memori, restart harian, auto-start Windows.</summary>
    public static class AppLifecycle
    {
        public const int ExitRestart = 3;
        public static bool IsChild;
        static Mutex _mutex;
        static volatile int _uiBeat = Environment.TickCount;
        static System.Windows.Forms.Timer _beatTimer;
        static Thread _watchThread;
        static DateTime _started = DateTime.Now;
        static DateTime _lastDailyCheck = DateTime.Now;

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint f);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

        public static bool AcquireSingleInstance()
        {
            bool created;
            try { _mutex = new Mutex(true, "IproNvrTool_SingleInstance_" + Environment.UserName, out created); }
            catch { return true; }
            return created;
        }

        public static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { Log.Write("ERROR UI tidak tertangani: " + e.Exception); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { Log.Write("FATAL tidak tertangani: " + e.ExceptionObject); } catch { }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate(object s, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
            {
                try { Log.Write("Task error: " + e.Exception.GetBaseException().Message); } catch { }
                e.SetObserved();
            };
        }

        public static void ApplyKeepAwake()
        {
            try
            {
                SetThreadExecutionState(AppSettings.Current.KeepAwake
                    ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
                    : ES_CONTINUOUS);
            }
            catch { }
        }

        /// <summary>Dipanggil dari thread UI setelah form utama dibuat: detak jantung UI + pengawas di thread terpisah.</summary>
        public static void StartMonitors()
        {
            ApplyKeepAwake();
            _beatTimer = new System.Windows.Forms.Timer();
            _beatTimer.Interval = 2000;
            _beatTimer.Tick += delegate { _uiBeat = Environment.TickCount; };
            _beatTimer.Start();
            _watchThread = new Thread(WatchLoop);
            _watchThread.IsBackground = true;
            _watchThread.Name = "AppWatch";
            _watchThread.Start();
        }

        static void WatchLoop()
        {
            int memHigh = 0;
            while (true)
            {
                Thread.Sleep(10000);
                try
                {
                    AppSettings s = AppSettings.Current;
                    // UI macet > 2 menit -> matikan paksa agar supervisor menghidupkan ulang
                    if (unchecked(Environment.TickCount - _uiBeat) > 120000)
                    {
                        Log.Write("WATCHDOG: UI tidak merespons > 120 dtk, proses dimatikan untuk restart.");
                        Kill();
                    }
                    // batas memori (3 kali berturut-turut agar lonjakan sesaat tidak memicu)
                    if (s.MemLimitMb > 0)
                    {
                        long mb = Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
                        if (mb > s.MemLimitMb) memHigh++; else memHigh = 0;
                        if (memHigh >= 3)
                        {
                            Log.Write("WATCHDOG: memori " + mb + " MB melebihi batas " + s.MemLimitMb + " MB, restart terjadwal.");
                            RequestRestart();
                        }
                    }
                    // restart harian
                    if (s.DailyRestart && !string.IsNullOrEmpty(s.DailyRestartTime))
                    {
                        DateTime now = DateTime.Now;
                        TimeSpan t;
                        if (TimeSpan.TryParse(s.DailyRestartTime, out t))
                        {
                            DateTime at = now.Date + t;
                            if (_lastDailyCheck < at && now >= at && (now - _started).TotalMinutes > 5)
                            {
                                Log.Write("Restart harian terjadwal (" + s.DailyRestartTime + ").");
                                RequestRestart();
                            }
                        }
                        _lastDailyCheck = now;
                    }
                }
                catch { }
            }
        }

        static void Kill()
        {
            try { Process.GetCurrentProcess().Kill(); } catch { Environment.Exit(9); }
        }

        /// <summary>Restart aplikasi: logout sesi NVR, lalu keluar dengan kode 3 (supervisor menghidupkan lagi) atau jalankan proses baru.</summary>
        public static void RequestRestart()
        {
            try { Pool.LogoutAll(); Sessions.LogoutAll(); } catch { }
            try { AppSettings.Current.Save(); } catch { }
            if (IsChild) { Environment.Exit(ExitRestart); return; }
            try
            {
                if (_mutex != null) { try { _mutex.ReleaseMutex(); } catch { } }
                Process.Start(Application.ExecutablePath);
            }
            catch { }
            Environment.Exit(0);
        }

        /// <summary>Induk: jalankan aplikasi sebagai proses anak; hidupkan lagi bila crash/hang/restart terjadwal, berhenti bila keluar normal (kode 0).</summary>
        public static int Supervise(string[] args)
        {
            int quick = 0;
            while (true)
            {
                DateTime t0 = DateTime.Now;
                int code;
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--child " + string.Join(" ", args));
                    psi.UseShellExecute = false;
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        code = p.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    try { Log.Write("Supervisor gagal menjalankan aplikasi: " + ex.Message); } catch { }
                    return 1;
                }
                if (code == 0) return 0;
                try { Log.Write("Supervisor: aplikasi berhenti dengan kode " + code + ", dijalankan ulang."); } catch { }
                // crash berulang cepat -> tunggu lebih lama agar tidak memenuhi CPU/log
                quick = (DateTime.Now - t0).TotalSeconds < 30 ? quick + 1 : 0;
                Thread.Sleep(Math.Min(60, 3 + quick * 5) * 1000);
            }
        }

        static DateTime _cpuAt = DateTime.MinValue;
        static TimeSpan _cpuPrev;
        static string _loadText = "";

        /// <summary>Ringkasan beban: stream aktif, CPU total (aplikasi + ffmpeg), memori total. Dihitung paling sering tiap 3 dtk.</summary>
        public static string LoadText()
        {
            DateTime now = DateTime.Now;
            if ((now - _cpuAt).TotalSeconds < 3) return _loadText;
            try
            {
                TimeSpan cpu = TimeSpan.Zero; long mem = 0;
                Process me = Process.GetCurrentProcess();
                cpu += me.TotalProcessorTime; mem += me.WorkingSet64;
                foreach (Process f in Process.GetProcessesByName("ffmpeg"))
                {
                    try { cpu += f.TotalProcessorTime; mem += f.WorkingSet64; } catch { }
                    f.Dispose();
                }
                double pct = 0;
                if (_cpuAt != DateTime.MinValue)
                    pct = (cpu - _cpuPrev).TotalMilliseconds / ((now - _cpuAt).TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                _cpuPrev = cpu; _cpuAt = now;
                _loadText = "Stream " + LiveTile.ActiveStreams + "  CPU " + Math.Max(0, Math.Min(100, pct)).ToString("0") + "%  RAM " + (mem / (1024 * 1024)) + " MB";
            }
            catch { }
            return _loadText;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("IproNvrTool", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("IproNvrTool", false);
                }
            }
            catch (Exception ex) { Log.Write("Auto-start: " + ex.Message); }
        }
    }
}
