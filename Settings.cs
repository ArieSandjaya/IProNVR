using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace IproNvrTool
{
    /// <summary>Pengaturan aplikasi, disimpan di %AppData%\IproNvrTool\settings.xml.
    /// Password dienkripsi dengan DPAPI (hanya bisa dibuka oleh user Windows yang sama).</summary>
    public class AppSettings
    {
        public string Host = "192.168.1.100";
        public int Port = 80;
        public bool UseHttps = false;
        public bool IgnoreCertErrors = true;
        public string Username = "admin";
        public string PasswordProtected = "";
        public int MaxChannels = 16;          // jumlah digit MASK saat playback (jumlah kamera yang didukung NVR)
        public string LastCameras = "1,2,3,4";
        public string RecordFolder = "";
        public string DownloadFolder = "";
        public int PlaybackFetchMs = 1000;    // jeda ambil data playback tampilan ASM (spec NVR menyarankan >= 3000; naikkan bila NVR menolak)
        public int PlaybackIntervalMs = 3000; // spec: kirim perintah minimal tiap 3 detik
        public string FfmpegPath = "";        // untuk dekode live H.264/H.265 (kosong = cari di folder aplikasi / PATH)
        public int LiveFormat = 2;            // 2 H.264, 3 H.265 (nilai lain, mis. dari versi lama, dianggap H.264)
        public string HwAccel = "auto";       // akselerasi dekode GPU ffmpeg: auto, d3d11va, dxva2, cuda, qsv, none
        public int DecodeWidth = 640;         // lebar hasil dekode (px); 0 = ukuran asli

        // ---- tampilan ala ASM300 (Setup > System, Default screen, Event) ----
        // ---- operasi 24x7 ----
        public bool Supervise = true;           // proses induk menghidupkan ulang aplikasi bila crash/hang
        public bool KeepAwake = true;           // cegah PC tidur / layar mati
        public int MemLimitMb = 3000;           // restart terjadwal bila memori melewati batas (0 = mati)
        public bool DailyRestart = false;       // restart harian untuk membersihkan memori
        public string DailyRestartTime = "04:00";
        public bool AutoStart = false;          // jalankan saat login Windows
        public int AsmMagnification = 100;      // 75/100/125/150/175/200 (%)
        public string DateFormat = "yyyy/MM/dd";
        public bool Time24h = true;
        public string OpLayout = "16";          // pola layar Operation window
        public string LiveLayout = "4";         // pola layar Live window bila tidak memakai group
        public int OpMaxAreas = 16;             // jumlah area maksimum Operation window
        public int LiveMaxAreas = 16;           // jumlah area maksimum Live window
        public bool ResumePlayback = true;      // lanjut playback saat ganti kamera
        public int AutoLogoutMin = 0;           // 0 = mati
        public int QdPreSec = 5;                // quick download: mundur sekian detik
        public int QdMinutes = 10;              // quick download: lama (menit)
        public bool QdMp4 = false;              // false = n3r priority, true = mp4 priority
        public string QdFolder = "";
        public bool SetupTips = true;           // keterangan item setup (tooltip)
        public bool SeparateTab = true;         // tab boleh dipisah jadi jendela
        public bool DownloadTimeUtc = true;     // STARTTIME/ENDTIME download dianggap UTC (false = waktu lokal PC)
        public bool PanelHandle = true;         // tombol buka/tutup function panel
        public bool ShowIconNames = true;       // nama ikon kamera di map
        public bool ConfirmBeforeLive = false;  // konfirmasi sebelum membuka Live/Map window
        public bool AutoDecodeWidth = true;     // lebar dekode otomatis menurut jumlah area
        public string LayoutShortcuts = "1,4,9,16,25"; // 5 ikon pola layar di toolbar
        public bool UseSubstreamMulti = true;   // multi-screen memakai substream (urutan StreamPlan)
        public int LiveWindowsOnStart = 0;      // jumlah Live window yang dibuka saat start (0-4)
        public bool MapOnStart = false;
        public string OpStartGroup = "";        // group/sequence yang dijalankan di Operation window saat start
        public string OpStartSequence = "";
        public string LiveStartItems = "";      // "1=g:Nama;2=s:Nama" per Live window
        public bool RequireLogin = false;       // tampilkan login saat aplikasi mulai
        public bool EventPopup = true;
        public bool EventBuzzer = true;
        public int EventPollSec = 30;           // 0 = mati
        public bool ErrLogPoll = true;
        public int LogKeepDays = 14;
        public bool ShowCamNames = true;        // judul kamera di area gambar

        [XmlIgnore]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(PasswordProtected)) return "";
                try
                {
                    byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(raw);
                }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { PasswordProtected = ""; return; }
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                PasswordProtected = Convert.ToBase64String(enc);
            }
        }

        static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.xml");
            }
        }

        public static AppSettings Current = Load();

        static AppSettings Load()
        {
            // berkas utama, lalu cadangan (.bak) bila berkas utama rusak (mis. listrik mati saat menyimpan)
            foreach (string p in new string[] { FilePath, FilePath + ".bak" })
            {
                try
                {
                    if (File.Exists(p))
                    {
                        var ser = new XmlSerializer(typeof(AppSettings));
                        using (var fs = File.OpenRead(p))
                            return (AppSettings)ser.Deserialize(fs);
                    }
                }
                catch { }
            }
            return new AppSettings();
        }

        static readonly object SaveLock = new object();

        public void Save()
        {
            lock (SaveLock)
            {
                string path = FilePath;
                string tmp = path + ".tmp";
                var ser = new XmlSerializer(typeof(AppSettings));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ser.Serialize(fs, this);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
                else File.Move(tmp, path);
            }
        }

        public string ResolveRecordFolder()
        {
            if (!string.IsNullOrEmpty(RecordFolder)) return RecordFolder;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IproNvrTool", "Rekaman");
        }

        public string ResolveDownloadFolder()
        {
            if (!string.IsNullOrEmpty(DownloadFolder)) return DownloadFolder;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IproNvrTool", "Download");
        }
    }
}
