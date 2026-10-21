using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace IproNvrServer
{
    public class ServerConfig
    {
        public int Port = 8470;
        public string DataDir = "";     // kosong = folder "data" di samping IproNvrServer.exe
        public int TokenDays = 30;
        public string FfmpegPath = "";   // kosong = ffmpeg.exe di samping server atau di PATH

        public static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static ServerConfig Load()
        {
            string p = Path.Combine(Dir, "server.json");
            var js = new JavaScriptSerializer();
            ServerConfig c = new ServerConfig();
            if (File.Exists(p))
            {
                try { c = js.Deserialize<ServerConfig>(File.ReadAllText(p)); } catch (Exception ex) { Console.WriteLine("server.json rusak: " + ex.Message); }
            }
            else
            {
                try { File.WriteAllText(p, js.Serialize(c)); } catch { }
            }
            if (string.IsNullOrWhiteSpace(c.DataDir)) c.DataDir = Path.Combine(Dir, "data");
            if (c.TokenDays < 1) c.TokenDays = 30;
            return c;
        }
    }

    public static class Log
    {
        static readonly object lk = new object();
        static string dir;
        public static void Init(string d) { dir = Path.Combine(d, "logs"); try { Directory.CreateDirectory(dir); } catch { } }
        public static void Write(string s)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
            lock (lk)
            {
                try { Console.WriteLine(line); } catch { }
                try { if (dir != null) File.AppendAllText(Path.Combine(dir, "server-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine); } catch { }
            }
        }
    }

    class ServerService : ServiceBase
    {
        Host host;
        public ServerService() { ServiceName = Program.SvcName; }
        protected override void OnStart(string[] args) { host = new Host(); host.Start(); }
        protected override void OnStop() { if (host != null) host.Stop(); }
    }

    class Host
    {
        Store store;
        ApiServer api;
        public void Start()
        {
            ServerConfig cfg = ServerConfig.Load();
            Log.Init(cfg.DataDir);
            store = new Store(cfg.DataDir);
            if (!store.HasAdmin())
            {
                string pass = Program.RandomPass();
                store.SetAdmin("admin", pass);
                string note = "User admin awal dibuat. Nama: admin  Password: " + pass + "  (segera ganti: IproNvrServer setpass admin PASSWORDBARU)";
                Log.Write(note);
                try { File.WriteAllText(Path.Combine(cfg.DataDir, "admin-awal.txt"), note + Environment.NewLine); } catch { }
            }
            api = new ApiServer(store, cfg);
            api.Start();
        }
        public void Stop() { if (api != null) api.Stop(); Log.Write("Server berhenti."); }
    }

    static class Program
    {
        public const string SvcName = "IproNvrServer";

        public static string RandomPass()
        {
            const string abc = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var b = new byte[12];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(abc[x % abc.Length]);
            return sb.ToString();
        }

        static int Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process p = Process.Start(psi))
            {
                Console.WriteLine(p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string exe = Assembly.GetExecutingAssembly().Location;
            ServerConfig cfg = ServerConfig.Load();
            switch (cmd)
            {
                case "--service":
                    ServiceBase.Run(new ServerService());
                    return 0;
                case "install":
                    Run("netsh", "http add urlacl url=http://+:" + cfg.Port + "/ user=Everyone");
                    Run("sc", "create " + SvcName + " binPath= \"\\\"" + exe + "\\\" --service\" start= auto DisplayName= \"IPRO NVR Server\"");
                    Run("sc", "description " + SvcName + " \"Server konfigurasi IPRO NVR (registrasi NVR/kamera, user, group)\"");
                    Run("sc", "failure " + SvcName + " reset= 86400 actions= restart/5000/restart/5000/restart/30000");
                    Run("netsh", "advfirewall firewall add rule name=\"IPRO NVR Server\" dir=in action=allow protocol=TCP localport=" + cfg.Port);
                    Run("sc", "start " + SvcName);
                    return 0;
                case "uninstall":
                    Run("sc", "stop " + SvcName);
                    Run("sc", "delete " + SvcName);
                    Run("netsh", "http delete urlacl url=http://+:" + cfg.Port + "/");
                    Run("netsh", "advfirewall firewall delete rule name=\"IPRO NVR Server\"");
                    return 0;
                case "setpass":
                    if (args.Length < 3) { Console.WriteLine("Pemakaian: IproNvrServer setpass NAMA PASSWORDBARU"); return 1; }
                    {
                        Log.Init(cfg.DataDir);
                        var st = new Store(cfg.DataDir);
                        var body = new Dictionary<string, object>();
                        body["name"] = args[1]; body["password"] = args[2];
                        st.Upsert(Schema.ByRoute("users"), body);
                        Console.WriteLine("Password " + args[1] + " diubah.");
                        return 0;
                    }
                case "help":
                case "--help":
                case "-h":
                case "/?":
                    Console.WriteLine("IproNvrServer [perintah]\n  (kosong)   jalankan di konsol (Ctrl+C untuk berhenti)\n  install    pasang sebagai Windows Service (jalankan sebagai Administrator)\n  uninstall  hapus service\n  setpass NAMA PASSWORD  ubah password user\nPengaturan: server.json di samping exe (Port, DataDir, TokenDays). Halaman admin: http://SERVER:PORT/");
                    return 0;
            }
            var h = new Host();
            h.Start();
            var quit = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
            Console.WriteLine("Tekan Ctrl+C untuk berhenti.");
            quit.WaitOne();
            h.Stop();
            return 0;
        }
    }
}
