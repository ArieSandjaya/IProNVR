using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IproNvrTool;

namespace IproNvrServer
{
    /// <summary>Satu perangkat hasil pemindaian wizard "Tambah perangkat".</summary>
    class WizItem
    {
        public string Ip = "", Ports = "", User = "", Pass = "", Method = "", Summary = "", Sub = "", Main = "", Streams = "", Codec = "";
        public int W, H;
        public bool Ok, Exists;
    }

    class WizJob
    {
        public string Id, State = "running", Mode = "", By = "";
        public int Total, Done, Found;
        public readonly List<WizItem> Items = new List<WizItem>();
        public CancellationTokenSource Cts = new CancellationTokenSource();
    }

    /// <summary>Wizard pendaftaran kamera ala "Add Hardware" XProtect: metode Cepat (subnet server), Rentang IP, Manual;
    /// beberapa login dicoba bergantian; hasil dipilih lalu didaftarkan ke NVR.</summary>
    public class WizardManager
    {
        readonly Store store;
        readonly DiscoveryManager disc;
        readonly object lk = new object();
        readonly Dictionary<string, WizJob> jobs = new Dictionary<string, WizJob>();
        int seq;

        public WizardManager(Store s, DiscoveryManager d) { store = s; disc = d; }

        static string S(Dictionary<string, object> d, string k)
        {
            object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o) : "";
        }

        static uint ToU(IPAddress a) { byte[] b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        static string FromU(uint u) { return ((u >> 24) & 255) + "." + ((u >> 16) & 255) + "." + ((u >> 8) & 255) + "." + (u & 255); }

        /// <summary>Subnet /24 dari tiap kartu jaringan server yang aktif.</summary>
        public static List<string> LocalSubnets()
        {
            var res = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                        string p = ip.Substring(0, ip.LastIndexOf('.'));
                        string r = p + ".1-" + p + ".254";
                        if (!res.Contains(r)) res.Add(r);
                    }
                }
            }
            catch { }
            return res;
        }

        /// <summary>Urai "a.b.c.d-e", "a.b.c.d-a.b.c.e", "a.b.c.d/24" atau IP tunggal (dipisah koma/baris) menjadi daftar IP (maks 1024).</summary>
        public static List<string> ParseRange(string text, out string error)
        {
            error = null;
            var res = new List<string>();
            var seen = new HashSet<string>();
            foreach (string part0 in (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = part0.Trim();
                uint a, b;
                IPAddress ip;
                try
                {
                    int slash = part.IndexOf('/');
                    int dash = part.IndexOf('-');
                    if (slash > 0)
                    {
                        int bits = int.Parse(part.Substring(slash + 1));
                        if (!IPAddress.TryParse(part.Substring(0, slash), out ip) || bits < 22 || bits > 32) { error = "Rentang tidak valid: " + part + " (CIDR minimal /22)"; return null; }
                        uint mask = bits == 32 ? 0xFFFFFFFF : ~(0xFFFFFFFFu >> bits);
                        a = ToU(ip) & mask; b = a | ~mask;
                        if (bits < 31) { a++; b--; }
                    }
                    else if (dash > 0)
                    {
                        string l = part.Substring(0, dash), r = part.Substring(dash + 1);
                        if (!IPAddress.TryParse(l, out ip)) { error = "IP tidak valid: " + l; return null; }
                        a = ToU(ip);
                        IPAddress ip2;
                        if (r.IndexOf('.') < 0) { int last; if (!int.TryParse(r, out last) || last < 0 || last > 255) { error = "Rentang tidak valid: " + part; return null; } b = (a & 0xFFFFFF00u) | (uint)last; }
                        else { if (!IPAddress.TryParse(r, out ip2)) { error = "IP tidak valid: " + r; return null; } b = ToU(ip2); }
                    }
                    else
                    {
                        if (!IPAddress.TryParse(part, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) { error = "IP tidak valid: " + part; return null; }
                        a = b = ToU(ip);
                    }
                }
                catch { error = "Rentang tidak valid: " + part; return null; }
                if (b < a) { error = "Rentang terbalik: " + part; return null; }
                for (uint u = a; u <= b; u++)
                {
                    if (res.Count >= 1024) { error = "Terlalu banyak alamat (maksimal 1024 per pemindaian)."; return null; }
                    string s = FromU(u);
                    if (seen.Add(s)) res.Add(s);
                }
            }
            if (res.Count == 0) error = "Tidak ada alamat IP untuk dipindai.";
            return res.Count == 0 ? null : res;
        }

        /// <summary>Port yang terbuka dari daftar (dicoba bersamaan, batas waktu singkat).</summary>
        static string OpenPorts(string ip, int[] ports, int timeoutMs)
        {
            var cl = new TcpClient[ports.Length];
            var ar = new IAsyncResult[ports.Length];
            var waits = new List<WaitHandle>();
            for (int i = 0; i < ports.Length; i++)
            {
                try { cl[i] = new TcpClient(); ar[i] = cl[i].BeginConnect(ip, ports[i], null, null); waits.Add(ar[i].AsyncWaitHandle); }
                catch { }
            }
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var open = new List<string>();
            for (int i = 0; i < ports.Length; i++)
            {
                if (ar[i] == null) continue;
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                bool done = ar[i].IsCompleted || (left > 0 && ar[i].AsyncWaitHandle.WaitOne(left));
                try { if (done && cl[i].Connected) { cl[i].EndConnect(ar[i]); open.Add(ports[i].ToString()); } } catch { }
                try { cl[i].Close(); } catch { }
            }
            return string.Join(",", open.ToArray());
        }

        /// <summary>Body: mode (express|range|manual), range / ips (teks), port, creds [{u,p}].</summary>
        public string Start(Dictionary<string, object> body, string by, out string error)
        {
            error = null;
            string exe = disc.FindFfmpeg();
            if (exe == null) { error = "ffmpeg tidak ditemukan di server. Letakkan ffmpeg.exe di folder server atau isi FfmpegPath di server.json."; return null; }
            string mode = S(body, "mode");
            List<string> hosts;
            if (mode == "express")
            {
                var sn = LocalSubnets();
                if (sn.Count == 0) { error = "Kartu jaringan server tidak ditemukan."; return null; }
                hosts = ParseRange(string.Join(",", sn.ToArray()), out error);
            }
            else hosts = ParseRange(S(body, mode == "manual" ? "ips" : "range"), out error);
            if (hosts == null) return null;

            int port = 80;
            int.TryParse(S(body, "port"), out port);
            if (port < 1 || port > 65535) port = 80;
            var creds = new List<string[]>();
            object o;
            if (body.TryGetValue("creds", out o) && o is System.Collections.IEnumerable && !(o is string))
                foreach (object x in (System.Collections.IEnumerable)o)
                {
                    var d = x as Dictionary<string, object>;
                    if (d == null || creds.Count >= 8) continue;
                    string u = S(d, "u"), p = S(d, "p");
                    if (u.Length == 0 && p.Length == 0) continue;
                    creds.Add(new[] { u, p });
                }
            if (creds.Count == 0) creds.Add(new[] { "", "" });

            var have = new HashSet<string>();
            foreach (Row c in store.List(Schema.ByRoute("cameras"))) have.Add(Convert.ToString(c["ip_address"]).Trim());

            WizJob job;
            lock (lk)
            {
                foreach (WizJob j in jobs.Values) if (j.State == "running") { error = "Masih ada pemindaian yang berjalan."; return null; }
                job = new WizJob();
                job.Id = (++seq).ToString();
                job.Mode = mode; job.By = by; job.Total = hosts.Count;
                jobs[job.Id] = job;
                while (jobs.Count > 5) { string old = null; foreach (string k in jobs.Keys) { old = k; break; } jobs.Remove(old); }
            }
            Log.Write("Wizard tambah perangkat oleh " + by + ": mode " + mode + ", " + hosts.Count + " alamat");
            bool skipLive = mode == "manual";
            int[] ports = new[] { port, 554, 8000, 443, 8080 };
            Task.Run(delegate { Run(job, exe, hosts, ports, creds, have, skipLive); });
            return job.Id;
        }

        void Run(WizJob job, string exe, List<string> hosts, int[] ports, List<string[]> creds, HashSet<string> have, bool skipLive)
        {
            try
            {
                var opt = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = job.Cts.Token };
                Parallel.ForEach(hosts, opt, delegate(string ip)
                {
                    try
                    {
                        string open = skipLive ? "" : OpenPorts(ip, ports, 700);
                        if (!skipLive && open.Length == 0) return;
                        var it = new WizItem();
                        it.Ip = ip; it.Ports = open; it.Exists = have.Contains(ip);
                        DiscoveryResult r = null;
                        string lastNote = "";
                        foreach (string[] c in creds)
                        {
                            job.Cts.Token.ThrowIfCancellationRequested();
                            r = RtspDiscovery.Discover(exe, ip, c[0], c[1], "", job.Cts.Token);
                            if (r.SubTemplate != null) { it.User = c[0]; it.Pass = c[1]; break; }
                            lastNote = r.Note;
                        }
                        if (r != null && r.SubTemplate != null)
                        {
                            it.Ok = true; it.Method = r.Method; it.Summary = r.Summary();
                            it.Sub = r.SubTemplate; it.Main = r.MainTemplate ?? ""; it.Streams = r.StreamsText();
                            it.Codec = r.Sub.Codec; it.W = r.Sub.Width; it.H = r.Sub.Height;
                            lock (lk) job.Found++;
                        }
                        else if (open.Length > 0 || skipLive)
                        {
                            it.Summary = string.IsNullOrEmpty(lastNote) ? "RTSP tidak terbaca (login salah atau bukan kamera)" : lastNote;
                        }
                        else return;
                        lock (lk) job.Items.Add(it);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log.Write("Wizard " + ip + ": " + ex.Message); }
                    finally { lock (lk) job.Done++; }
                });
                lock (lk) job.State = job.Cts.IsCancellationRequested ? "cancelled" : "done";
            }
            catch (OperationCanceledException) { lock (lk) job.State = "cancelled"; }
            catch (Exception ex) { Log.Write("Wizard error: " + ex.Message); lock (lk) job.State = "error"; }
            Log.Write("Wizard selesai: " + job.Found + " perangkat dengan RTSP terbaca");
        }

        public void Cancel(string id)
        {
            lock (lk) { WizJob j; if (jobs.TryGetValue(id, out j)) j.Cts.Cancel(); }
        }

        static uint Key(string ip) { IPAddress a; return IPAddress.TryParse(ip, out a) && a.AddressFamily == AddressFamily.InterNetwork ? ToU(a) : 0; }

        public object Get(string id)
        {
            lock (lk)
            {
                WizJob j;
                if (!jobs.TryGetValue(id, out j)) return null;
                var items = new List<WizItem>(j.Items);
                items.Sort(delegate(WizItem a, WizItem b) { return Key(a.Ip).CompareTo(Key(b.Ip)); });
                var list = new List<object>();
                foreach (WizItem it in items)
                {
                    var d = new Dictionary<string, object>();
                    d["ip"] = it.Ip; d["ports"] = it.Ports; d["ok"] = it.Ok; d["exists"] = it.Exists; d["user"] = it.User;
                    d["method"] = it.Method; d["summary"] = it.Summary; d["codec"] = it.Codec; d["w"] = it.W; d["h"] = it.H;
                    list.Add(d);
                }
                var res = new Dictionary<string, object>();
                res["job"] = j.Id; res["state"] = j.State; res["total"] = j.Total; res["done"] = j.Done; res["found"] = j.Found; res["items"] = list;
                return res;
            }
        }

        /// <summary>Daftarkan hasil pindai terpilih. Body: job, nvr_id (0 = NVR baru), new_name, items [{ip,name}].</summary>
        public object Add(Dictionary<string, object> body, out string error)
        {
            error = null;
            WizJob j;
            lock (lk) { if (!jobs.TryGetValue(S(body, "job"), out j)) { error = "Hasil pemindaian sudah kedaluwarsa, pindai ulang."; return null; } }
            var picks = new List<KeyValuePair<WizItem, string>>();
            object o;
            if (body.TryGetValue("items", out o) && o is System.Collections.IEnumerable && !(o is string))
                foreach (object x in (System.Collections.IEnumerable)o)
                {
                    var d = x as Dictionary<string, object>;
                    if (d == null) continue;
                    string ip = S(d, "ip");
                    lock (lk) foreach (WizItem it in j.Items) if (it.Ip == ip) { picks.Add(new KeyValuePair<WizItem, string>(it, S(d, "name"))); break; }
                }
            if (picks.Count == 0) { error = "Tidak ada perangkat yang dipilih."; return null; }

            long nvrId = 0;
            long.TryParse(S(body, "nvr_id"), out nvrId);
            TableDef nt = Schema.ByRoute("nvrs"), ct = Schema.ByRoute("cameras");
            if (nvrId == 0)
            {
                string nm = S(body, "new_name").Trim();
                if (nm.Length == 0) { error = "Isi nama NVR/kelompok baru."; return null; }
                WizItem f = picks[0].Key;
                var nb = new Dictionary<string, object>();
                nb["name"] = nm; nb["host"] = f.Ip; nb["port"] = 80; nb["brand"] = "ipro";
                nb["username"] = f.User; nb["password"] = f.Pass; nb["cam_user"] = f.User; nb["cam_pass"] = f.Pass;
                nb["max_channels"] = Math.Max(16, picks.Count);
                nvrId = store.Upsert(nt, nb);
            }
            int next = 0;
            var ipsIn = new HashSet<string>();
            foreach (Row c in store.List(ct))
            {
                if (Convert.ToInt64(c["nvr_id"]) != nvrId) continue;
                next = Math.Max(next, Convert.ToInt32(c["channel"]));
                ipsIn.Add(Convert.ToString(c["ip_address"]).Trim());
            }
            int added = 0, skipped = 0;
            foreach (KeyValuePair<WizItem, string> p in picks)
            {
                WizItem it = p.Key;
                if (ipsIn.Contains(it.Ip)) { skipped++; continue; }
                var cb = new Dictionary<string, object>();
                cb["nvr_id"] = nvrId; cb["channel"] = ++next;
                cb["name"] = p.Value.Trim().Length > 0 ? p.Value.Trim() : "Kamera " + it.Ip;
                cb["ip_address"] = it.Ip;
                cb["cam_user"] = it.User; cb["cam_pass"] = it.Pass;
                if (it.Ok)
                {
                    cb["rtsp_url"] = it.Sub; cb["rtsp_main_url"] = it.Main; cb["rtsp_streams"] = it.Streams;
                    cb["codec"] = it.Codec; cb["width"] = it.W; cb["height"] = it.H; cb["scan_detail"] = it.Summary;
                }
                else cb["scan_detail"] = it.Summary;
                store.Upsert(ct, cb);
                added++;
            }
            var res = new Dictionary<string, object>();
            res["added"] = added; res["skipped"] = skipped; res["nvr_id"] = nvrId;
            return res;
        }
    }
}
