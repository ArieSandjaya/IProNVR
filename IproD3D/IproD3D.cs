// IproD3D - modul opsional: dekode D3D11VA (ffmpeg in-process) + render Direct3D 11.
// Satu perangkat D3D11 per GPU, satu jendela/swapchain per layar, semua kamera digambar
// sebagai kotak di swapchain itu lewat D3D11 Video Processor (NV12 -> BGRA, skala di GPU).
// Dimuat lewat reflection oleh IproNvrTool; bila gagal, aplikasi utama tetap jalan.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FFmpeg.AutoGen;
using SharpDX.Mathematics.Interop;
using D3D = SharpDX.Direct3D11;
using DX = SharpDX.DXGI;

namespace IproD3D
{
    static class FLog
    {
        static readonly object L = new object();
        public static string Path0 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IproNvrTool", "d3d.log");
        public static void W(string m)
        {
            try
            {
                lock (L)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path0));
                    File.AppendAllText(Path0, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\r\n");
                }
            }
            catch { }
        }
    }

    public static class Entry
    {
        const int NeedAvcodecMajor = 63;
        static bool _init;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string path);

        /// <summary>Memuat DLL ffmpeg dari folder dir. Mengembalikan null bila OK, atau pesan galat.</summary>
        public static string Probe(string dir)
        {
            try { Init(dir); return null; }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        internal static void Init(string dir)
        {
            if (_init) return;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new InvalidOperationException("Folder DLL ffmpeg tidak ada: " + dir);
            if (!File.Exists(Path.Combine(dir, "avcodec-" + NeedAvcodecMajor + ".dll")))
                throw new InvalidOperationException("avcodec-" + NeedAvcodecMajor + ".dll tidak ada di " + dir + " (butuh ffmpeg 9.0 shared)");
            SetDllDirectory(dir);
            ffmpeg.RootPath = dir;
            DynamicallyLoadedBindings.Initialize();
            uint v = ffmpeg.avcodec_version();
            if ((int)(v >> 16) != NeedAvcodecMajor)
                throw new InvalidOperationException("Versi avcodec " + (v >> 16) + " tidak cocok (butuh " + NeedAvcodecMajor + ")");
            ffmpeg.av_log_set_level(16);
            ffmpeg.avformat_network_init();
            _init = true;
        }

        /// <summary>Buat satu kotak kamera (kontrol WinForms) berdekode GPU untuk tampilan Operation / Live.</summary>
        /// <summary>Adaptive stream Wall: tile yang sejak awal memakai stream utama (diisi aplikasi sebelum Open).</summary>
        public static bool[] StartMain;

        public static Control CreateCell(string name, string url, string mainUrl, string ffmpegDir, int fps, Action<string> log)
        {
            Init(ffmpegDir);
            return new CellSurface(name, url, mainUrl, fps, log);
        }

        /// <summary>Buka jendela wall untuk satu layar. rects = (x,y,w,h) ternormalisasi per kamera.</summary>
        public static Form Open(string title, string[] urls, string[] mainUrls, string[] names, float[] rects,
            Rectangle bounds, string deviceName, string ffmpegDir, int maxStreams, int fps, Action<string> log, Func<int, bool> isRec, Action<int, int, int, int> ptz)
        {
            try { File.WriteAllText(FLog.Path0, ""); } catch { }
            FLog.W("Open " + title + " layar=" + deviceName + " tile=" + urls.Length + " dir=" + ffmpegDir);
            Init(ffmpegDir);
            return new Wall(title, urls, mainUrls, names, rects, bounds, deviceName, maxStreams, fps, log, isRec, ptz);
        }
    }

    // ------------------------------------------------------------------ GPU

    unsafe sealed class Gpu
    {
        public D3D.Device Dev;
        public D3D.DeviceContext Ctx;
        public D3D.VideoDevice VDev;
        public D3D.VideoContext VCtx;
        public AVBufferRef* HwDev;
        public string Name;
        // pipeline shader NV12 -> RGB (cara mpv: salin frame dekoder ke tekstur sendiri lalu sampling Y dan UV)
        public D3D.VertexShader Vs;
        public D3D.PixelShader Ps;
        public D3D.Buffer Cb;
        public D3D.SamplerState Smp;
        public string ShaderErr;

        const string Hlsl =
@"cbuffer cb : register(b0) { float4 p; float4 q; };
Texture2D ty : register(t0);
Texture2D tuv : register(t1);
SamplerState sm : register(s0);
struct VSO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSO vs(uint id : SV_VertexID)
{
    VSO o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 ps(VSO i) : SV_Target
{
    float2 uv = q.yz + (i.uv - 0.5) / q.x;
    float y = ty.Sample(sm, uv).r;
    float2 c = tuv.Sample(sm, uv).rg - 0.5;
    if (p.y < 0.5) { y = (y - 16.0 / 255.0) * (255.0 / 219.0); c = c * (255.0 / 224.0); }
    float3 rgb;
    if (p.x > 0.5) rgb = float3(y + 1.5748 * c.y, y - 0.1873 * c.x - 0.4681 * c.y, y + 1.8556 * c.x);
    else rgb = float3(y + 1.402 * c.y, y - 0.3441 * c.x - 0.7141 * c.y, y + 1.772 * c.x);
    return float4(saturate(rgb), 1);
}";

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int D3DCompile([MarshalAs(UnmanagedType.LPArray)] byte[] src, UIntPtr len, [MarshalAs(UnmanagedType.LPStr)] string name,
            IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target,
            uint f1, uint f2, out IntPtr code, out IntPtr err);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BlobFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RelFn(IntPtr self);

        static IntPtr VtFn(IntPtr obj, int idx) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), idx * IntPtr.Size); }
        static byte[] BlobBytes(IntPtr blob)
        {
            var gp = (BlobFn)Marshal.GetDelegateForFunctionPointer(VtFn(blob, 3), typeof(BlobFn));
            var gs = (BlobFn)Marshal.GetDelegateForFunctionPointer(VtFn(blob, 4), typeof(BlobFn));
            IntPtr ptr = gp(blob); int n = (int)gs(blob);
            var b = new byte[n]; Marshal.Copy(ptr, b, 0, n);
            return b;
        }
        static void BlobRelease(IntPtr blob) { if (blob != IntPtr.Zero) ((RelFn)Marshal.GetDelegateForFunctionPointer(VtFn(blob, 2), typeof(RelFn)))(blob); }
        static byte[] Compile(string entry, string target)
        {
            byte[] src = Encoding.ASCII.GetBytes(Hlsl);
            IntPtr code, err;
            int hr = D3DCompile(src, (UIntPtr)(uint)src.Length, "ipro.hlsl", IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out code, out err);
            if (hr < 0 || code == IntPtr.Zero)
            {
                string m = "";
                if (err != IntPtr.Zero) { m = Encoding.ASCII.GetString(BlobBytes(err)); BlobRelease(err); }
                throw new InvalidOperationException("D3DCompile " + entry + " hr=0x" + hr.ToString("x") + " " + m);
            }
            byte[] r = BlobBytes(code);
            BlobRelease(code); BlobRelease(err);
            return r;
        }

        void InitShaders()
        {
            try
            {
                Vs = new D3D.VertexShader(Dev, Compile("vs", "vs_4_0"), null);
                Ps = new D3D.PixelShader(Dev, Compile("ps", "ps_4_0"), null);
                Cb = new D3D.Buffer(Dev, 32, D3D.ResourceUsage.Default, D3D.BindFlags.ConstantBuffer, D3D.CpuAccessFlags.None, D3D.ResourceOptionFlags.None, 0);
                Smp = new D3D.SamplerState(Dev, new D3D.SamplerStateDescription
                {
                    Filter = D3D.Filter.MinMagMipLinear,
                    AddressU = D3D.TextureAddressMode.Clamp, AddressV = D3D.TextureAddressMode.Clamp, AddressW = D3D.TextureAddressMode.Clamp,
                    MaximumAnisotropy = 1, ComparisonFunction = D3D.Comparison.Never, MinimumLod = 0, MaximumLod = float.MaxValue
                });
                FLog.W("shader NV12->RGB siap");
            }
            catch (Exception ex)
            {
                ShaderErr = ex.Message;
                Vs = null; Ps = null;
                FLog.W("shader gagal, pakai Video Processor: " + ex.Message);
            }
        }
        // satu kunci untuk semua akses immediate context: dipakai ffmpeg (lewat hwctx->lock) dan kode kita,
        // supaya perintah salin/gambar tidak menyelip di antara BeginFrame/EndFrame dekoder lain.
        public readonly object Sync = new object();
        static readonly AVD3D11VADeviceContext_lock LockCb = delegate(void* c) { System.Threading.Monitor.Enter(((Gpu)GCHandle.FromIntPtr((IntPtr)c).Target).Sync); };
        static readonly AVD3D11VADeviceContext_unlock UnlockCb = delegate(void* c) { System.Threading.Monitor.Exit(((Gpu)GCHandle.FromIntPtr((IntPtr)c).Target).Sync); };
        static readonly Dictionary<long, Gpu> Map = new Dictionary<long, Gpu>();

        public static Gpu For(string deviceName)
        {
            lock (Map)
            {
                DX.Adapter1 pick = null;
                using (var f = new DX.Factory1())
                {
                    var list = f.Adapters1;
                    foreach (var a in list)
                    {
                        if (pick != null) break;
                        foreach (var o in a.Outputs)
                            if (string.Equals(o.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                    }
                    if (pick == null && list.Length > 0) pick = list[0];
                    if (pick == null) throw new InvalidOperationException("Tidak ada adapter DXGI");
                    long luid = pick.Description1.Luid;
                    Gpu g;
                    if (Map.TryGetValue(luid, out g)) return g;
                    g = new Gpu();
                    g.Name = pick.Description1.Description;
                    g.Dev = new D3D.Device(pick, D3D.DeviceCreationFlags.VideoSupport | D3D.DeviceCreationFlags.BgraSupport,
                        new[] { SharpDX.Direct3D.FeatureLevel.Level_11_1, SharpDX.Direct3D.FeatureLevel.Level_11_0 });
                    using (var mt = g.Dev.QueryInterface<D3D.Multithread>()) mt.SetMultithreadProtected(true);
                    g.Ctx = g.Dev.ImmediateContext;
                    g.VDev = g.Dev.QueryInterface<D3D.VideoDevice>();
                    g.VCtx = g.Ctx.QueryInterface<D3D.VideoContext>();
                    g.InitShaders();

                    AVBufferRef* r = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                    if (r == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc gagal");
                    var dc = (AVHWDeviceContext*)r->data;
                    var hc = (AVD3D11VADeviceContext*)dc->hwctx;
                    Marshal.AddRef(g.Dev.NativePointer);
                    hc->device = (ID3D11Device*)g.Dev.NativePointer.ToPointer();
                    Marshal.AddRef(g.Ctx.NativePointer);
                    hc->device_context = (ID3D11DeviceContext*)g.Ctx.NativePointer.ToPointer();
                    hc->@lock = new AVD3D11VADeviceContext_lock_func { Pointer = Marshal.GetFunctionPointerForDelegate(LockCb) };
                    hc->unlock = new AVD3D11VADeviceContext_unlock_func { Pointer = Marshal.GetFunctionPointerForDelegate(UnlockCb) };
                    hc->lock_ctx = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(g));
                    int e = ffmpeg.av_hwdevice_ctx_init(r);
                    if (e < 0) throw new InvalidOperationException("av_hwdevice_ctx_init: " + Tile.Err(e));
                    g.HwDev = r;
                    Map[luid] = g;
                    return g;
                }
            }
        }
    }

    // ------------------------------------------------------------------ Tile (satu kamera)

    unsafe sealed class Tile
    {
        public string Name, Url, MainUrl;
        public RectangleF Rel;
        public Gpu G;
        public volatile bool UseMain, Visible = true, Stop, Rec;
        public bool AdaptMain;   // adaptif: ukuran kotak cukup besar -> stream utama (dipulihkan setelah zoom/full view)
        public volatile float Zoom = 1f, Cx = 0.5f, Cy = 0.5f;   // zoom digital: pembesaran dan titik tengah (0..1 pada gambar)
        public Action<string> Log;
        public int MinIntervalMs = 66;

        // hasil (dipakai render thread di bawah Lk)
        public readonly object Lk = new object();
        public D3D.Texture2D Tex;
        public int TexW, TexH;
        public bool HasFrame, M709 = true, FullRange;
        public string Status = "Menyambung...";
        public long Decoded, Copied, Shown;
        public int DecW, DecH;
        public bool Failed;
        // variasi bind flag tekstur NV12 untuk input Video Processor (driver berbeda-beda); dicoba berurutan sampai input view berhasil
        public static volatile int Variant = 1;
        public static readonly D3D.BindFlags[] Variants = new D3D.BindFlags[] {
            D3D.BindFlags.ShaderResource,
            D3D.BindFlags.ShaderResource | D3D.BindFlags.RenderTarget,
            D3D.BindFlags.RenderTarget,
            D3D.BindFlags.None,
            D3D.BindFlags.Decoder,
            D3D.BindFlags.Decoder | D3D.BindFlags.ShaderResource };
        public int TexVar;
        public int InY = -1, ProbeN, KeyCs = -1, ProbeFrames;
        // mode langsung: Video Processor membaca tekstur dekoder (array slice) tanpa salinan; frame ditahan (ref) sampai diganti
        public static volatile bool Direct;
        public bool IsDirect;
        public int ArrW, ArrH, HeldW, HeldH, HeldSlice;
        AVFrame* _held;
        public D3D.Texture2D ArrTex { get { return _srcWrap; } }
        public readonly Dictionary<int, D3D.VideoProcessorInputView> Views = new Dictionary<int, D3D.VideoProcessorInputView>();
        bool _sw, _swFb;
        byte[] _nv;

        // cache video processor (diurus render thread)
        public D3D.VideoProcessorEnumerator VEnum;
        public D3D.VideoProcessor Vp;
        public D3D.VideoProcessorOutputView OutView;
        public D3D.ShaderResourceView SrvY, SrvUV;
        public D3D.Texture2D SrvTex;
        public int KeyIw, KeyIh, KeyOw, KeyOh;
        public D3D.Texture2D KeyTex;

        Thread _th;
        long _deadline;
        AVIOInterruptCB_callback _intr;
        static readonly AVCodecContext_get_format _fmt = GetFmt;
        D3D.Texture2D _srcWrap;
        IntPtr _srcPtr;

        public void Start(int delayMs)
        {
            _th = new Thread(() => { if (delayMs > 0) Thread.Sleep(delayMs); Run(); });
            _th.IsBackground = true;
            _th.Name = "d3d-" + Name;
            _th.Start();
        }

        public void Join() { try { if (_th != null) _th.Join(4000); } catch { } }

        public static string Err(int e)
        {
            byte* buf = stackalloc byte[256];
            ffmpeg.av_strerror(e, buf, 256);
            return Marshal.PtrToStringAnsi((IntPtr)buf) + " (" + e + ")";
        }

        static string Mask(string s)
        {
            if (s == null) return "";
            int a = s.IndexOf("://");
            int b = s.IndexOf('@');
            if (a > 0 && b > a) return s.Substring(0, a + 3) + "***@" + s.Substring(b + 1);
            return s;
        }

        void Say(string m) { FLog.W(Name + ": " + m); var l = Log; if (l != null) { try { l(Name + ": " + m); } catch { } } }

        int Intr(void* p)
        {
            if (Stop) return 1;
            return Stopwatch.GetTimestamp() > _deadline ? 1 : 0;
        }

        void Arm(double sec) { _deadline = Stopwatch.GetTimestamp() + (long)(sec * Stopwatch.Frequency); }

        static AVPixelFormat GetFmt(AVCodecContext* s, AVPixelFormat* fmt)
        {
            for (AVPixelFormat* p = fmt; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        // ---- buffer pemutaran (jitter buffer): frame diantre lalu ditampilkan menurut PTS + tunda, supaya gerak halus walau paket datang bergerombol
        public static int DelayMs = 250;
        struct QItem { public IntPtr F; public long Due; }
        readonly object _q = new object();
        readonly List<QItem> _qi = new List<QItem>();
        double _minTr = double.MaxValue, _lastP = double.NaN, _lastKeptP = double.NaN, _lastArr = double.NaN, _emaIv = 0.1;
        volatile string _copyErr;

        public int QCount { get { lock (_q) return _qi.Count; } }

        /// <summary>Buang frame yang menunggu (dipakai saat tile disembunyikan, supaya permukaan dekoder tidak tertahan).</summary>
        public void FlushQueue()
        {
            lock (_q)
            {
                if (_qi.Count == 0) return;
                foreach (var it in _qi) { AVFrame* f = (AVFrame*)it.F; ffmpeg.av_frame_free(&f); }
                _qi.Clear();
            }
        }

        void ClearQ()
        {
            lock (_q)
            {
                foreach (var it in _qi) { AVFrame* f = (AVFrame*)it.F; ffmpeg.av_frame_free(&f); }
                _qi.Clear();
            }
            _minTr = double.MaxValue; _lastP = double.NaN; _lastKeptP = double.NaN; _lastArr = double.NaN; _emaIv = 0.1;
            _t0 = double.NaN; _prevDue = double.NaN; _n = 0;
        }

        double _t0 = double.NaN, _prevDue = double.NaN;
        int _n;

        // Jadwal tampil: tiap frame ditunda DelayMs dari kedatangannya, dan jarak antar frame diratakan menurut
        // rata-rata laju kedatangan (frame yang datang bergerombol dibagi rata, tanpa membuang frame).
        void Enqueue(AVFrame* fr, double tb)
        {
            double a = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (double.IsNaN(_lastArr) || a - _lastArr > 3.0) { _t0 = a; _n = 0; _prevDue = double.NaN; }   // stream baru / jeda panjang
            _lastArr = a; _n++;
            double iv = _n >= 8 ? (a - _t0) / (_n - 1) : 0.1;
            if (iv < 0.015) iv = 0.015; else if (iv > 0.5) iv = 0.5;
            if (_n > 200) { _t0 = a - iv * 50; _n = 51; }   // jendela bergulir
            double d = DelayMs / 1000.0;
            double due = a + d;
            if (!double.IsNaN(_prevDue))
            {
                double sched = _prevDue + iv;
                if (sched > due) due = sched;
                if (due - a > d + 0.5) due = a + d;   // terlalu jauh tertinggal: sinkron ulang
            }
            _prevDue = due;
            AVFrame* c = ffmpeg.av_frame_clone(fr);
            if (c == null) return;
            lock (_q)
            {
                if (_qi.Count >= 10) { AVFrame* o = (AVFrame*)_qi[0].F; ffmpeg.av_frame_free(&o); _qi.RemoveAt(0); }
                _qi.Add(new QItem { F = (IntPtr)c, Due = (long)(due * Stopwatch.Frequency) });
            }
        }

        // dipanggil render thread sebelum menggambar tile: tampilkan frame terbaru yang sudah jatuh tempo
        public void PresentDue()
        {
            IntPtr pick = IntPtr.Zero;
            long now = Stopwatch.GetTimestamp();
            lock (_q)
            {
                int n = 0;
                while (n < _qi.Count && _qi[n].Due <= now) n++;
                for (int i = 0; i < n; i++)
                {
                    if (pick != IntPtr.Zero) { AVFrame* o = (AVFrame*)pick; ffmpeg.av_frame_free(&o); }
                    pick = _qi[i].F;
                }
                if (n > 0) _qi.RemoveRange(0, n);
            }
            if (pick == IntPtr.Zero) return;
            AVFrame* f = (AVFrame*)pick;
            try { ShowFrame(f); }
            catch (Exception ex) { _copyErr = ex.Message; }
            finally { ffmpeg.av_frame_free(&f); }
        }

        void ShowFrame(AVFrame* fr)
        {
            if (_sw || _swFb) { CopySw(fr); return; }
            if (fr->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                var pf = (AVPixelFormat)fr->format;
                if (pf == AVPixelFormat.AV_PIX_FMT_YUV420P || pf == AVPixelFormat.AV_PIX_FMT_YUVJ420P)
                {
                    // dekoder GPU menolak stream ini dan ffmpeg memberi frame software: pakai jalur upload
                    _swFb = true;
                    Say("dekoder GPU tidak dipakai untuk stream ini (" + pf + ") -> dekode software, upload ke GPU");
                    CopySw(fr);
                    return;
                }
                throw new Exception("frame bukan D3D11 (format " + fr->format + "): dekode GPU tidak aktif");
            }
            if (Direct) Hold(fr); else Copy(fr);
        }

        static readonly Random _rnd = new Random();

        void Run()
        {
            int backoff = 1500;
            while (!Stop)
            {
                bool wasMain = UseMain;
                try
                {
                    Session(wasMain ? (string.IsNullOrEmpty(MainUrl) ? Url : MainUrl) : Url);
                    backoff = 1500;
                }
                catch (Exception ex)
                {
                    Status = "Galat: " + ex.Message;
                    Say(Status);
                }
                if (Stop) break;
                if (UseMain != wasMain) continue;   // ganti stream, langsung sambung lagi
                lock (Lk) { HasFrame = false; }
                int wait = backoff + _rnd.Next(0, 3000);
                for (int w = 0; w < wait && !Stop && UseMain == wasMain; w += 100) Thread.Sleep(100);
                if (backoff < 10000) backoff += 1500;
            }
            lock (Lk) { ReleaseSrc(); }
        }

        void ReleaseSrc()
        {
            if (_held != null) { AVFrame* hh = _held; ffmpeg.av_frame_free(&hh); _held = null; }
            if (_srcWrap != null) { try { _srcWrap.Dispose(); } catch { } _srcWrap = null; _srcPtr = IntPtr.Zero; }
        }

        void Session(string url)
        {
            AVFormatContext* fc = null;
            AVCodecContext* cc = null;
            AVPacket* pkt = null;
            AVFrame* fr = null;
            AVDictionary* opt = null;
            try
            {
                Status = "Menyambung...";
                fc = ffmpeg.avformat_alloc_context();
                _intr = Intr;
                fc->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_intr) };
                fc->interrupt_callback.opaque = null;
                ffmpeg.av_dict_set(&opt, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&opt, "timeout", "8000000", 0);
                ffmpeg.av_dict_set(&opt, "fflags", "nobuffer", 0);
                ffmpeg.av_dict_set(&opt, "flags", "low_delay", 0);
                ffmpeg.av_dict_set(&opt, "max_delay", "500000", 0);
                ffmpeg.av_dict_set(&opt, "analyzeduration", "800000", 0);
                ffmpeg.av_dict_set(&opt, "probesize", "300000", 0);
                Arm(12);
                int e = ffmpeg.avformat_open_input(&fc, url, null, &opt);
                ffmpeg.av_dict_free(&opt);
                if (e < 0) throw new Exception("open: " + Err(e) + " " + Mask(url));
                Arm(12);
                e = ffmpeg.avformat_find_stream_info(fc, null);
                if (e < 0) throw new Exception("stream info: " + Err(e));
                int vi = -1;
                for (int i = 0; i < (int)fc->nb_streams; i++)
                    if (fc->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) { vi = i; break; }
                if (vi < 0) throw new Exception("tidak ada stream video");
                AVCodecParameters* par = fc->streams[vi]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null) throw new Exception("decoder tidak ada: " + par->codec_id);
                cc = ffmpeg.avcodec_alloc_context3(codec);
                e = ffmpeg.avcodec_parameters_to_context(cc, par);
                if (e < 0) throw new Exception("params: " + Err(e));
                // codec yang tidak punya dekoder D3D11VA (MJPEG, MPEG-4, dll) didekode software lalu diunggah ke GPU
                _swFb = false;
                var cid = par->codec_id;
                _sw = !(cid == AVCodecID.AV_CODEC_ID_H264 || cid == AVCodecID.AV_CODEC_ID_HEVC || cid == AVCodecID.AV_CODEC_ID_VP9 ||
                        cid == AVCodecID.AV_CODEC_ID_AV1 || cid == AVCodecID.AV_CODEC_ID_MPEG2VIDEO);
                if (!_sw)
                {
                    cc->hw_device_ctx = ffmpeg.av_buffer_ref(G.HwDev);
                    cc->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(_fmt) };
                    cc->extra_hw_frames = 12;
                    cc->thread_count = 1;
                }
                else cc->thread_count = 2;
                cc->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                e = ffmpeg.avcodec_open2(cc, codec, null);
                if (e < 0) throw new Exception("open codec: " + Err(e));
                DecW = par->width; DecH = par->height;
                pkt = ffmpeg.av_packet_alloc();
                fr = ffmpeg.av_frame_alloc();
                Say("terhubung " + par->codec_id + (_sw ? " [software]" : " [GPU]") + " " + par->width + "x" + par->height + (UseMain ? " (utama)" : " (sub)"));
                Status = "Menunggu frame...";
                bool main0 = UseMain;
                ClearQ();
                var tbr = fc->streams[vi]->time_base;
                double tb = tbr.den != 0 ? (double)tbr.num / tbr.den : 0;
                while (!Stop && UseMain == main0)
                {
                    if (_copyErr != null) { string cm = _copyErr; _copyErr = null; throw new Exception(cm); }
                    Arm(10);
                    e = ffmpeg.av_read_frame(fc, pkt);
                    if (e < 0) throw new Exception("baca: " + Err(e));
                    if (pkt->stream_index != vi) { ffmpeg.av_packet_unref(pkt); continue; }
                    e = ffmpeg.avcodec_send_packet(cc, pkt);
                    ffmpeg.av_packet_unref(pkt);
                    if (e < 0 && e != ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
                    while (true)
                    {
                        e = ffmpeg.avcodec_receive_frame(cc, fr);
                        if (e == ffmpeg.AVERROR(ffmpeg.EAGAIN) || e == ffmpeg.AVERROR_EOF) break;
                        if (e < 0) throw new Exception("decode: " + Err(e));
                        Decoded++;
                        if (Visible) Enqueue(fr, tb);
                        ffmpeg.av_frame_unref(fr);
                    }
                }
            }
            finally
            {
                ClearQ();
                if (fr != null) ffmpeg.av_frame_free(&fr);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (cc != null) ffmpeg.avcodec_free_context(&cc);
                if (fc != null) ffmpeg.avformat_close_input(&fc);
                if (opt != null) ffmpeg.av_dict_free(&opt);
                lock (Lk) { ReleaseSrc(); }
            }
        }

        // diagnostik: baca balik beberapa byte dari tekstur hasil salin (hanya 3 frame pertama)
        void ProbeTex(D3D.Resource src, int sub, int w, int h)
        {
            try
            {
                var d = new D3D.Texture2DDescription
                {
                    Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = DX.Format.NV12,
                    SampleDescription = new DX.SampleDescription(1, 0), Usage = D3D.ResourceUsage.Staging,
                    BindFlags = D3D.BindFlags.None, CpuAccessFlags = D3D.CpuAccessFlags.Read, OptionFlags = D3D.ResourceOptionFlags.None
                };
                using (var st = new D3D.Texture2D(G.Dev, d))
                {
                    G.Ctx.CopySubresourceRegion(src, sub, new D3D.ResourceRegion(0, 0, 0, w, h, 1), st, 0, 0, 0, 0);
                    var box = G.Ctx.MapSubresource(st, 0, D3D.MapMode.Read, D3D.MapFlags.None);
                    try
                    {
                        byte* b = (byte*)box.DataPointer;
                        int pitch = box.RowPitch;
                        int ys = 0, n = 0;
                        for (int y = h / 2; y < h / 2 + 4; y++) for (int x = w / 2; x < w / 2 + 4; x++) { ys += b[y * pitch + x]; n++; }
                        byte* uv = b + h * pitch + (h / 4) * pitch + (w / 2 & ~1);
                        InY = ys / n; Say("probe salinan #" + (Copied + 1) + ": Y~" + (ys / n) + " U=" + uv[0] + " V=" + uv[1] + " pitch=" + pitch);
                    }
                    finally { G.Ctx.UnmapSubresource(st, 0); }
                }
            }
            catch (Exception ex) { Say("probe gagal: " + ex.Message.Split('\n')[0]); }
        }

        /// <summary>Baca balik frame yang sedang tampil dari GPU dan ubah ke Bitmap RGB (untuk simpan/cetak gambar dan dewarp fisheye).
        /// maxW > 0: perkecil dengan melompati piksel bila lebih lebar dari maxW.</summary>
        public Bitmap Snapshot(int maxW)
        {
            byte[] yb, uvb; int w, h; bool m709, full;
            lock (Lk)
            {
                if (Tex == null || !HasFrame) return null;
                w = TexW; h = TexH; m709 = M709; full = FullRange;
                var d = new D3D.Texture2DDescription
                {
                    Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = DX.Format.NV12,
                    SampleDescription = new DX.SampleDescription(1, 0), Usage = D3D.ResourceUsage.Staging,
                    BindFlags = D3D.BindFlags.None, CpuAccessFlags = D3D.CpuAccessFlags.Read, OptionFlags = D3D.ResourceOptionFlags.None
                };
                yb = new byte[w * h]; uvb = new byte[w * h / 2];
                using (var st = new D3D.Texture2D(G.Dev, d))
                lock (G.Sync)
                {
                    G.Ctx.CopySubresourceRegion(Tex, 0, new D3D.ResourceRegion(0, 0, 0, w, h, 1), st, 0, 0, 0, 0);
                    var box = G.Ctx.MapSubresource(st, 0, D3D.MapMode.Read, D3D.MapFlags.None);
                    try
                    {
                        byte* b = (byte*)box.DataPointer;
                        int pitch = box.RowPitch;
                        for (int r = 0; r < h; r++) Marshal.Copy((IntPtr)(b + (long)r * pitch), yb, r * w, w);
                        byte* u0 = b + (long)h * pitch;
                        for (int r = 0; r < h / 2; r++) Marshal.Copy((IntPtr)(u0 + (long)r * pitch), uvb, r * w, w);
                    }
                    finally { G.Ctx.UnmapSubresource(st, 0); }
                }
            }
            int step = maxW > 0 && w > maxW ? (w + maxW - 1) / maxW : 1;
            int ow = w / step, oh = h / step;
            var bmp = new Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, ow, oh), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                float kr = m709 ? 1.5748f : 1.402f, kgu = m709 ? 0.1873f : 0.3441f, kgv = m709 ? 0.4681f : 0.7141f, kb = m709 ? 1.8556f : 1.772f;
                float ys = full ? 1f : 1.16438f, cs = full ? 1f : 1.13839f, yo = full ? 0f : 16f;
                for (int oy = 0; oy < oh; oy++)
                {
                    byte* row = (byte*)bd.Scan0 + (long)oy * bd.Stride;
                    int sy = oy * step;
                    for (int ox = 0; ox < ow; ox++)
                    {
                        int sx = ox * step;
                        float yv = (yb[sy * w + sx] - yo) * ys;
                        int ui = (sy >> 1) * w + (sx & ~1);
                        float u = (uvb[ui] - 128f) * cs, v = (uvb[ui + 1] - 128f) * cs;
                        float r = yv + kr * v, g = yv - kgu * u - kgv * v, bl = yv + kb * u;
                        row[ox * 3] = (byte)(bl < 0 ? 0 : bl > 255 ? 255 : bl);
                        row[ox * 3 + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        row[ox * 3 + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        void CopySw(AVFrame* fr)
        {
            var pf = (AVPixelFormat)fr->format;
            if (pf != AVPixelFormat.AV_PIX_FMT_YUV420P && pf != AVPixelFormat.AV_PIX_FMT_YUVJ420P)
                throw new Exception("format software " + pf + " belum didukung (butuh yuv420p)");
            int w = fr->width & ~1, h = fr->height & ~1;
            int need = w * h * 3 / 2;
            if (_nv == null || _nv.Length != need) _nv = new byte[need];
            int ls0 = fr->linesize[0], ls1 = fr->linesize[1], ls2 = fr->linesize[2];
            fixed (byte* dst = _nv)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* sp = fr->data[0] + (long)y * ls0;
                    byte* dp = dst + y * w;
                    for (int x = 0; x < w; x++) dp[x] = sp[x];
                }
                byte* uvd = dst + w * h;
                int cw = w / 2;
                for (int y = 0; y < h / 2; y++)
                {
                    byte* up = fr->data[1] + (long)y * ls1;
                    byte* vp = fr->data[2] + (long)y * ls2;
                    byte* dp = uvd + y * w;
                    for (int x = 0; x < cw; x++) { dp[2 * x] = up[x]; dp[2 * x + 1] = vp[x]; }
                }
                { int ysum = 0; for (int y = h / 2; y < h / 2 + 4; y++) for (int x = w / 2; x < w / 2 + 4; x++) ysum += dst[y * w + x]; InY = ysum / 16; }
                lock (Lk)
                {
                    if (Tex == null || TexW != w || TexH != h || TexVar != Variant)
                    {
                        if (Tex != null) { Tex.Dispose(); Tex = null; }
                        var d = new D3D.Texture2DDescription
                        {
                            Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                            Format = DX.Format.NV12,
                            SampleDescription = new DX.SampleDescription(1, 0),
                            Usage = D3D.ResourceUsage.Default,
                            BindFlags = Variants[Variant],
                            CpuAccessFlags = D3D.CpuAccessFlags.None,
                            OptionFlags = D3D.ResourceOptionFlags.None
                        };
                        Tex = new D3D.Texture2D(G.Dev, d);
                        TexW = w; TexH = h; TexVar = Variant;
                        Say("tekstur NV12 (software) bind=" + d.BindFlags);
                    }
                    lock (G.Sync) { G.Ctx.UpdateSubresource(new SharpDX.DataBox((IntPtr)dst, w, need), Tex, 0); }
                    M709 = fr->colorspace == AVColorSpace.AVCOL_SPC_BT709 || (fr->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED && h >= 720);
                    FullRange = pf == AVPixelFormat.AV_PIX_FMT_YUVJ420P || fr->color_range == AVColorRange.AVCOL_RANGE_JPEG;
                    if (!HasFrame) Say("frame pertama (software) " + w + "x" + h + " " + pf);
                    HasFrame = true;
                    Copied++;
                    Status = null;
                }
            }
            DecW = w; DecH = h;
        }

        // mode langsung: tahan frame (ref) dan biarkan Video Processor membaca tekstur dekoder
        void Hold(AVFrame* fr)
        {
            IntPtr arr = (IntPtr)fr->data[0];
            int slice = (int)(IntPtr)fr->data[1];
            int w = fr->width & ~1, h = fr->height & ~1;
            lock (Lk)
            {
                if (_srcPtr != arr)
                {
                    if (_held != null) { AVFrame* hh = _held; ffmpeg.av_frame_free(&hh); _held = null; }
                    ReleaseSrc();
                    Marshal.AddRef(arr);
                    _srcWrap = new D3D.Texture2D(arr);
                    _srcPtr = arr;
                    Say("mode langsung: tekstur dekoder baru");
                }
                if (ArrW == 0 || _srcPtr != arr)
                { var ad = _srcWrap.Description; ArrW = ad.Width; ArrH = ad.Height; }
                if (_held == null) _held = ffmpeg.av_frame_alloc(); else ffmpeg.av_frame_unref(_held);
                ffmpeg.av_frame_ref(_held, fr);
                HeldSlice = slice; HeldW = w; HeldH = h;
                M709 = fr->colorspace == AVColorSpace.AVCOL_SPC_BT709 ||
                       (fr->colorspace != AVColorSpace.AVCOL_SPC_BT470BG && fr->colorspace != AVColorSpace.AVCOL_SPC_SMPTE170M && h >= 720);
                FullRange = fr->color_range == AVColorRange.AVCOL_RANGE_JPEG;
                IsDirect = true;
                if (ProbeFrames++ < 3) lock (G.Sync) { ProbeTex(_srcWrap, slice, w, h); }
                HasFrame = true;
                Copied++;
                Status = null;
            }
            DecW = w; DecH = h;
        }

        void Copy(AVFrame* fr)
        {
            IntPtr arr = (IntPtr)fr->data[0];
            int slice = (int)(IntPtr)fr->data[1];
            int w = fr->width & ~1, h = fr->height & ~1;
            lock (Lk)
            {
                if (_srcPtr != arr)
                {
                    ReleaseSrc();
                    Marshal.AddRef(arr);
                    _srcWrap = new D3D.Texture2D(arr);
                    _srcPtr = arr;
                }
                if (Tex == null || TexW != w || TexH != h || TexVar != Variant)
                {
                    if (Tex != null) { Tex.Dispose(); Tex = null; }
                    var d = _srcWrap.Description;
                    d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
                    d.Usage = D3D.ResourceUsage.Default;
                    d.BindFlags = Variants[Variant]; TexVar = Variant; Say("tekstur NV12 bind=" + d.BindFlags);
                    d.CpuAccessFlags = D3D.CpuAccessFlags.None;
                    d.OptionFlags = D3D.ResourceOptionFlags.None;
                    Tex = new D3D.Texture2D(G.Dev, d);
                    TexW = w; TexH = h;
                }
                IsDirect = false;
                lock (G.Sync)
                {
                    G.Ctx.CopySubresourceRegion(_srcWrap, slice, new D3D.ResourceRegion(0, 0, 0, w, h, 1), Tex, 0, 0, 0, 0);
                    if (ProbeFrames++ < 3) ProbeTex(Tex, 0, w, h);
                }
                M709 = fr->colorspace == AVColorSpace.AVCOL_SPC_BT709 ||
                       (fr->colorspace != AVColorSpace.AVCOL_SPC_BT470BG && fr->colorspace != AVColorSpace.AVCOL_SPC_SMPTE170M && h >= 720);
                FullRange = fr->color_range == AVColorRange.AVCOL_RANGE_JPEG;
                if (!HasFrame) Say("frame pertama disalin " + w + "x" + h + " fmt=" + _srcWrap.Description.Format + " slice=" + slice + " m709=" + M709);
                HasFrame = true;
                Copied++;
                Status = null;
            }
            DecW = w; DecH = h;
        }

        // dipanggil render thread (di bawah Lk)
        public void DisposeVp()
        {
            foreach (var kv in Views) { try { kv.Value.Dispose(); } catch { } }
            Views.Clear();
            if (SrvY != null) { SrvY.Dispose(); SrvY = null; }
            if (SrvUV != null) { SrvUV.Dispose(); SrvUV = null; }
            SrvTex = null;
            if (OutView != null) { OutView.Dispose(); OutView = null; }
            if (Vp != null) { Vp.Dispose(); Vp = null; }
            if (VEnum != null) { VEnum.Dispose(); VEnum = null; }
            KeyTex = null;
        }

        public void DisposeAll()
        {
            lock (Lk)
            {
                DisposeVp();
                if (Tex != null) { Tex.Dispose(); Tex = null; }
                HasFrame = false;
            }
        }
    }

    // ------------------------------------------------------------------ CellSurface (satu kotak kamera di dalam jendela biasa)

    static class Painter
    {
        // gambar tekstur NV12 sebuah tile ke render target (viewport = dx,dy,dw,dh); pemanggil memegang t.Lk dan g.Sync
        public static void Draw(Gpu g, Tile t, D3D.RenderTargetView rtv, int dx, int dy, int dw, int dh)
        {
            var tex = t.Tex;
            if (tex == null || dw < 2 || dh < 2) return;
            if (t.SrvTex != tex)
            {
                t.DisposeVp();
                var d1 = new D3D.ShaderResourceViewDescription { Format = DX.Format.R8_UNorm, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D };
                d1.Texture2D.MipLevels = 1; d1.Texture2D.MostDetailedMip = 0;
                var d2 = new D3D.ShaderResourceViewDescription { Format = DX.Format.R8G8_UNorm, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D };
                d2.Texture2D.MipLevels = 1; d2.Texture2D.MostDetailedMip = 0;
                t.SrvY = new D3D.ShaderResourceView(g.Dev, tex, d1);
                t.SrvUV = new D3D.ShaderResourceView(g.Dev, tex, d2);
                t.SrvTex = tex;
            }
            var ctx = g.Ctx;
            var cb = new float[] { t.M709 ? 1f : 0f, t.FullRange ? 1f : 0f, 0f, 0f, t.Zoom, t.Cx, t.Cy, 0f };
            unsafe { fixed (float* pcb = cb) ctx.UpdateSubresource(new SharpDX.DataBox((IntPtr)pcb, 32, 32), g.Cb, 0); }
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.VertexShader.Set(g.Vs);
            ctx.PixelShader.Set(g.Ps);
            ctx.PixelShader.SetConstantBuffer(0, g.Cb);
            ctx.PixelShader.SetSampler(0, g.Smp);
            ctx.PixelShader.SetShaderResources(0, new D3D.ShaderResourceView[] { t.SrvY, t.SrvUV });
            ctx.Rasterizer.SetViewport(dx, dy, dw, dh, 0f, 1f);
            ctx.OutputMerger.SetRenderTargets(rtv);
            ctx.Draw(3, 0);
            ctx.PixelShader.SetShaderResources(0, new D3D.ShaderResourceView[2]);
            ctx.OutputMerger.ResetTargets();
        }
    }

    /// <summary>Kontrol WinForms biasa yang menampilkan satu kamera lewat dekode GPU (D3D11VA) + render D3D11.
    /// Dipakai tampilan Operation / Live: satu kontrol per kotak, semua digambar oleh satu thread render bersama.</summary>
    public sealed class CellSurface : Control
    {
        static readonly List<CellSurface> All = new List<CellSurface>();
        static Thread _rt;
        static volatile bool _rtStop;

        readonly Tile _t = new Tile();
        readonly object _rl = new object();
        Gpu _g;
        DX.SwapChain _sc;
        D3D.Texture2D _back;
        D3D.RenderTargetView _rtv;
        DX.Surface1 _surf;
        int _cw, _ch;
        volatile int _w, _h;
        volatile bool _vis = true, _down;
        bool _started;
        string _lastStatus = "";
        readonly Font _f = new Font("Segoe UI", 9f);
        System.Windows.Forms.Timer _stTimer;
        bool _pan;
        Point _panLast;

        /// <summary>Status sambungan ("" = gambar tampil); dipanggil di UI thread.</summary>
        public event Action<string> StatusChanged;
        public bool Zoomed { get { return _t.Zoom > 1.01f; } }
        volatile bool _headless;
        /// <summary>Mode tanpa gambar: hanya dekode dan salin ke tekstur (dipakai fisheye: gambar diambil lewat Snapshot lalu di-dewarp aplikasi).</summary>
        public bool Headless { get { return _headless; } set { _headless = value; } }
        /// <summary>Frame yang sedang tampil sebagai Bitmap RGB (null bila belum ada gambar).</summary>
        public Bitmap Snapshot(int maxW) { return _t.Snapshot(maxW); }
        public bool HasPicture { get { return _t.HasFrame; } }
        /// <summary>Ada URL stream utama untuk kotak ini (adaptive stream).</summary>
        public bool HasMain { get { return !string.IsNullOrEmpty(_t.MainUrl); } }
        /// <summary>Stream yang dipakai: true = utama, false = sub. Mengganti akan menyambung ulang ke stream lain di kotak yang sama.</summary>
        public bool UseMain { get { return _t.UseMain; } set { if (value && !HasMain) value = false; if (_t.UseMain != value) _t.UseMain = value; } }

        internal CellSurface(string name, string url, string mainUrl, int fps, Action<string> log)
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            DoubleBuffered = false;
            BackColor = Color.Black;
            _t.Name = name; _t.Url = url; _t.MainUrl = mainUrl; _t.Log = log;
            fps = fps < 5 ? 5 : fps > 30 ? 30 : fps;
            _t.MinIntervalMs = 1000 / fps - 4;
            _t.Status = "Menyambung...";
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _w = ClientSize.Width; _h = ClientSize.Height; _vis = Visible;
            if (_started) return;
            _started = true;
            try
            {
                var sc = Screen.FromControl(this);
                _g = Gpu.For(sc.DeviceName);
                _t.G = _g;
                _t.Start(0);
            }
            catch (Exception ex) { _t.Status = "GPU: " + ex.Message; _t.Failed = true; }
            lock (All) { All.Add(this); if (_rt == null) { _rtStop = false; _rt = new Thread(RenderAll) { IsBackground = true, Name = "d3d-cells" }; _rt.Start(); } }
            _stTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _stTimer.Tick += delegate
            {
                string st = _t.HasFrame ? "" : (_t.Status ?? "");
                if (st != _lastStatus) { _lastStatus = st; var h = StatusChanged; if (h != null) h(st); }
            };
            _stTimer.Start();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); _w = ClientSize.Width; _h = ClientSize.Height; }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _vis = Visible; }

        // ---- zoom digital: roda = zoom, tarik kiri = geser (saat sudah di-zoom), klik tengah = kembali 1x
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float u = Math.Max(0f, Math.Min(1f, e.X / (float)Math.Max(1, _w))), v = Math.Max(0f, Math.Min(1f, e.Y / (float)Math.Max(1, _h)));
            float z0 = _t.Zoom;
            float z1 = e.Delta > 0 ? z0 * 1.25f : z0 / 1.25f;
            z1 = Math.Max(1f, Math.Min(8f, z1));
            if (z1 < 1.02f) z1 = 1f;
            float sx = _t.Cx + (u - 0.5f) / z0, sy = _t.Cy + (v - 0.5f) / z0;
            _t.Zoom = z1;
            _t.Cx = sx - (u - 0.5f) / z1; _t.Cy = sy - (v - 0.5f) / z1;
            Clamp();
        }

        void Clamp()
        {
            float half = 0.5f / _t.Zoom;
            _t.Cx = Math.Max(half, Math.Min(1f - half, _t.Cx));
            _t.Cy = Math.Max(half, Math.Min(1f - half, _t.Cy));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Middle) { _t.Zoom = 1f; _t.Cx = 0.5f; _t.Cy = 0.5f; return; }
            if (e.Button == MouseButtons.Left && Zoomed) { _pan = true; _panLast = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_pan || (e.Button & MouseButtons.Left) == 0) return;
            _t.Cx -= (e.X - _panLast.X) / (Math.Max(1, _w) * _t.Zoom);
            _t.Cy -= (e.Y - _panLast.Y) / (Math.Max(1, _h) * _t.Zoom);
            Clamp();
            _panLast = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pan = false; }

        // ---- render (thread bersama)
        static void RenderAll()
        {
            long period = Stopwatch.Frequency / 25;
            long next = Stopwatch.GetTimestamp();
            long lastStat = next, lastDec = 0, lastCopy = 0, ticks = 0;
            while (!_rtStop)
            {
                CellSurface[] snap;
                lock (All) snap = All.ToArray();
                foreach (var c in snap) { try { c.RenderOnce(); } catch (Exception ex) { FLog.W("sel render galat: " + ex.GetType().Name + " " + ex.Message); } }
                ticks++;
                long nowS = Stopwatch.GetTimestamp();
                if (nowS - lastStat > 10 * Stopwatch.Frequency && snap.Length > 0)
                {
                    double sec = (double)(nowS - lastStat) / Stopwatch.Frequency;
                    long dec = 0, cp = 0, q = 0; int pic = 0, vis = 0;
                    foreach (var c in snap) { dec += c._t.Decoded; cp += c._t.Copied; q += c._t.QCount; if (c._t.HasFrame) pic++; if (c._vis) vis++; }
                    FLog.W("[sel] kotak=" + snap.Length + " terlihat=" + vis + " bergambar=" + pic + "  dekode/dtk=" + ((dec - lastDec) / sec).ToString("0") +
                           "  salin/dtk=" + ((cp - lastCopy) / sec).ToString("0") + "  antre=" + q + "  render/dtk=" + (ticks / sec).ToString("0.0"));
                    lastStat = nowS; lastDec = dec; lastCopy = cp; ticks = 0;
                }
                next += period;
                long now = Stopwatch.GetTimestamp();
                if (next < now) next = now;
                int ms = (int)((next - now) * 1000 / Stopwatch.Frequency);
                Thread.Sleep(ms > 0 ? ms : 1);
            }
        }

        void MakeSwap(int w, int h)
        {
            FreeSwap();
            var desc = new DX.SwapChainDescription
            {
                ModeDescription = new DX.ModeDescription(w, h, new DX.Rational(60, 1), DX.Format.B8G8R8A8_UNorm),
                SampleDescription = new DX.SampleDescription(1, 0),
                Usage = DX.Usage.RenderTargetOutput,
                BufferCount = 1,
                OutputHandle = Handle,
                IsWindowed = true,
                SwapEffect = DX.SwapEffect.Discard,
                Flags = DX.SwapChainFlags.GdiCompatible
            };
            lock (_g.Sync)
            {
                using (var fac = new DX.Factory1())
                {
                    _sc = new DX.SwapChain(fac, _g.Dev, desc);
                    fac.MakeWindowAssociation(Handle, DX.WindowAssociationFlags.IgnoreAll);
                }
                _back = _sc.GetBackBuffer<D3D.Texture2D>(0);
                _rtv = new D3D.RenderTargetView(_g.Dev, _back);
                try { _surf = _sc.GetBackBuffer<DX.Surface1>(0); } catch { _surf = null; }
            }
            _cw = w; _ch = h;
        }

        void FreeSwap()
        {
            if (_g == null) return;
            lock (_g.Sync)
            {
                try { if (_surf != null) _surf.Dispose(); } catch { }
                try { if (_rtv != null) _rtv.Dispose(); } catch { }
                try { if (_back != null) _back.Dispose(); } catch { }
                try { if (_sc != null) _sc.Dispose(); } catch { }
                _surf = null; _rtv = null; _back = null; _sc = null;
            }
            _t.DisposeVp();
        }

        void RenderOnce()
        {
            if (_g == null || _t.Failed && _t.G == null) return;
            lock (_rl)
            {
                if (_down || !IsHandleCreated) return;
                if (_headless) { _t.Visible = true; _t.PresentDue(); return; }
                int w = _w, h = _h;
                if (!_vis || w < 8 || h < 8) { _t.Visible = false; _t.FlushQueue(); return; }
                _t.Visible = true;
                if (_sc == null || w != _cw || h != _ch) { lock (_t.Lk) { MakeSwap(w, h); } }
                _t.PresentDue();
                lock (_t.Lk)
                {
                    lock (_g.Sync) { _g.Ctx.ClearRenderTargetView(_rtv, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 1f)); }
                    if (_t.HasFrame && _t.Tex != null)
                    {
                        try { lock (_g.Sync) { Painter.Draw(_g, _t, _rtv, 0, 0, _cw, _ch); } _t.Shown++; }
                        catch (Exception ex) { _t.Status = "Render: " + ex.Message; _t.DisposeVp(); _t.HasFrame = false; }
                    }
                    else if (_surf != null)
                    {
                        IntPtr hdc = IntPtr.Zero;
                        try
                        {
                            hdc = _surf.GetDC(false);
                            using (var g = Graphics.FromHdc(hdc))
                            {
                                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                g.DrawString(_t.Status ?? "", _f, Brushes.Gainsboro, new RectangleF(0, 0, _cw, _ch), sf);
                            }
                        }
                        catch { }
                        finally { if (hdc != IntPtr.Zero) { try { _surf.ReleaseDC(); } catch { } } }
                    }
                }
                _sc.TryPresent(0, DX.PresentFlags.None);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_stTimer != null) { _stTimer.Stop(); _stTimer.Dispose(); } } catch { }
                lock (All) { All.Remove(this); }
                _t.Stop = true;
                lock (_rl)
                {
                    _down = true;
                    try { FreeSwap(); } catch { }
                }
                var t = _t;
                ThreadPool.QueueUserWorkItem(delegate { try { t.Join(); t.DisposeAll(); } catch { } });
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ Wall (jendela per layar)

    sealed class Wall : Form
    {
        readonly Tile[] _tiles;
        readonly Gpu _g;
        readonly Action<string> _log;
        readonly int _fps;
        DX.SwapChain _sc;
        D3D.Texture2D _back;
        D3D.RenderTargetView _rtv;
        DX.Surface1 _surf;
        Thread _rt;
        volatile bool _stop;
        Tile _zoom;
        bool _info;
        readonly Func<int, bool> _isRec;
        // PTZ: klik memilih kamera, roda mouse = zoom in/out, panah = geser, PgUp/PgDn atau +/- = zoom (tahan)
        readonly Action<int, int, int, int> _ptz;   // (indeks tile, pan, tilt, zoom); 0,0,0 = berhenti
        int _sel = -1;
        int _hPan, _hTilt, _hZoom;
        System.Windows.Forms.Timer _holdTimer, _wheelTimer;
        const int PtzSpeed = 8;

        void PtzSend(int pan, int tilt, int zoom)
        {
            if (_ptz == null || _sel < 0 || _sel >= _tiles.Length) return;
            try { _ptz(_sel, pan, tilt, zoom); } catch (Exception ex) { Say("PTZ galat: " + ex.Message); }
        }

        void PtzHoldStart(int pan, int tilt, int zoom)
        {
            if (_sel < 0) return;
            if (_hPan == pan && _hTilt == tilt && _hZoom == zoom) return;
            _hPan = pan; _hTilt = tilt; _hZoom = zoom;
            PtzSend(pan, tilt, zoom);
            if (_holdTimer == null)
            {
                _holdTimer = new System.Windows.Forms.Timer { Interval = 350 };
                _holdTimer.Tick += delegate { if (_hPan != 0 || _hTilt != 0 || _hZoom != 0) PtzSend(_hPan, _hTilt, _hZoom); };
            }
            _holdTimer.Start();
        }

        void PtzHoldStop()
        {
            if (_holdTimer != null) _holdTimer.Stop();
            bool was = _hPan != 0 || _hTilt != 0 || _hZoom != 0;
            _hPan = _hTilt = _hZoom = 0;
            if (was) PtzSend(0, 0, 0);
        }

        int HitTile(Point pt)
        {
            for (int i = 0; i < _tiles.Length; i++)
            {
                var rc = RectOf(_tiles[i]);
                if (!rc.IsEmpty && rc.Contains(pt)) return i;
            }
            return -1;
        }

        // area gambar sebuah tile (di bawah bilah nama)
        RectangleF VideoRect(Tile t)
        {
            var rc = RectOf(t);
            int bar = BarH;
            return new RectangleF(rc.X, rc.Y + bar, rc.Width, Math.Max(1, rc.Height - bar));
        }

        static void ClampView(Tile t)
        {
            float half = 0.5f / t.Zoom;
            t.Cx = Math.Max(half, Math.Min(1f - half, t.Cx));
            t.Cy = Math.Max(half, Math.Min(1f - half, t.Cy));
        }

        static void ResetZoom(Tile t) { t.Zoom = 1f; t.Cx = 0.5f; t.Cy = 0.5f; }

        int _dragTile = -1;
        Point _dragLast;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int hit = HitTile(e.Location);
            if (e.Button == MouseButtons.Right) { if (hit >= 0) ResetZoom(_tiles[hit]); return; }   // klik kanan = zoom digital kembali 1x
            if (e.Button != MouseButtons.Left) return;
            if (hit != _sel) { PtzHoldStop(); _sel = hit; }
            if (hit >= 0 && _tiles[hit].Zoom > 1f) { _dragTile = hit; _dragLast = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragTile < 0 || (e.Button & MouseButtons.Left) == 0) return;
            var t = _tiles[_dragTile];
            var vr = VideoRect(t);
            t.Cx -= (e.X - _dragLast.X) / (vr.Width * t.Zoom);
            t.Cy -= (e.Y - _dragLast.Y) / (vr.Height * t.Zoom);
            ClampView(t);
            _dragLast = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragTile = -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int hit = HitTile(e.Location);
            if (hit < 0) return;
            if ((ModifierKeys & Keys.Control) != 0)
            {
                // Ctrl + roda = zoom optik PTZ pada kamera di bawah kursor
                if (_ptz == null) return;
                if (hit != _sel) { PtzHoldStop(); _sel = hit; }
                PtzSend(0, 0, e.Delta > 0 ? PtzSpeed : -PtzSpeed);
                if (_wheelTimer == null)
                {
                    _wheelTimer = new System.Windows.Forms.Timer { Interval = 300 };
                    _wheelTimer.Tick += delegate { _wheelTimer.Stop(); PtzSend(0, 0, 0); };
                }
                _wheelTimer.Stop(); _wheelTimer.Start();
                return;
            }
            // roda = zoom digital (semua kamera), titik di bawah kursor tetap di tempatnya
            var t = _tiles[hit];
            var vr = VideoRect(t);
            float u = Math.Max(0f, Math.Min(1f, (e.X - vr.X) / vr.Width)), v = Math.Max(0f, Math.Min(1f, (e.Y - vr.Y) / vr.Height));
            float z0 = t.Zoom;
            float z1 = e.Delta > 0 ? z0 * 1.25f : z0 / 1.25f;
            z1 = Math.Max(1f, Math.Min(8f, z1));
            if (z1 < 1.02f) z1 = 1f;
            float sx = t.Cx + (u - 0.5f) / z0, sy = t.Cy + (v - 0.5f) / z0;
            t.Zoom = z1;
            t.Cx = sx - (u - 0.5f) / z1; t.Cy = sy - (v - 0.5f) / z1;
            ClampView(t);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.PageUp: case Keys.PageDown: case Keys.Add: case Keys.Subtract: case Keys.Oemplus: case Keys.OemMinus:
                    PtzHoldStop(); break;
            }
        }
        long _lastRecPoll;
        float _dpi = 96f;
        int BarH { get { return (int)(18 * _dpi / 96f); } }   // tinggi bilah nama, sama dengan sel multiview utama
        static readonly Color BarBack = Color.FromArgb(40, 46, 60), RecRed = Color.FromArgb(235, 30, 30);
        bool _gdiOk = true;
        int _cw, _ch;
        readonly Font _fName = new Font("Segoe UI", 9f, FontStyle.Bold);
        readonly Font _fSmall = new Font("Segoe UI", 8f);
        readonly Font _fBar = new Font("Segoe UI", 8.5f);
        readonly Font _fRec = new Font("Segoe UI", 11f);
        long _frames, _lastStat;
        bool _presentLogged;
        static bool _fmtLogged;
        static volatile int CsVar;
        bool _useShader;
        D3D.Texture2D _stg;
        long _csChangedAt;
        static readonly string[] CsNames = { "deteksi", "rentang penuh 0-255", "rentang tak ditentukan", "rentang dibalik", "matriks dibalik", "tanpa color space" };
        readonly string _title;

        public Wall(string title, string[] urls, string[] mainUrls, string[] names, float[] rects,
            Rectangle bounds, string deviceName, int maxStreams, int fps, Action<string> log, Func<int, bool> isRec, Action<int, int, int, int> ptz)
        {
            _isRec = isRec; _ptz = ptz;
            _title = title; _log = log; _fps = fps < 5 ? 5 : fps > 30 ? 30 : fps;
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            KeyPreview = true;
            DoubleBuffered = false;
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
            ShowInTaskbar = true;

            _g = Gpu.For(deviceName);
            Say("GPU: " + _g.Name);
            int n = urls.Length;
            bool[] sm = Entry.StartMain; Entry.StartMain = null;
            _tiles = new Tile[n];
            for (int i = 0; i < n; i++)
            {
                var t = new Tile();
                t.G = _g; t.Log = log;
                t.Name = names != null && i < names.Length ? names[i] : ("Cam " + (i + 1));
                t.Url = urls[i];
                t.MainUrl = mainUrls != null && i < mainUrls.Length ? mainUrls[i] : null;
                t.Rel = new RectangleF(rects[i * 4], rects[i * 4 + 1], rects[i * 4 + 2], rects[i * 4 + 3]);
                if (sm != null && i < sm.Length && sm[i] && !string.IsNullOrEmpty(t.MainUrl)) { t.AdaptMain = true; t.UseMain = true; }
                t.MinIntervalMs = 1000 / _fps - 4;
                if (i >= maxStreams) { t.Status = "Dibatasi (batas stream " + maxStreams + ")"; t.Visible = false; t.Failed = true; }
                _tiles[i] = t;
            }
            var h = Handle;   // paksa handle dibuat
            try { using (var gg = Graphics.FromHwnd(h)) _dpi = gg.DpiX; } catch { }
            CreateSwap();
            _useShader = _g.Vs != null && _g.Ps != null;
            Say(_useShader ? "render: shader NV12->RGB" : "render: Video Processor (shader gagal: " + _g.ShaderErr + ")");
            int started = 0;
            foreach (var t in _tiles) if (!t.Failed) { t.Start(started * 120); started++; }
            _rt = new Thread(RenderLoop) { IsBackground = true, Name = "d3d-render" };
            _rt.Start();
            Say("jendela " + bounds.Width + "x" + bounds.Height + ", " + started + " stream, " + _fps + " fps");
        }

        void Say(string m) { FLog.W("[wall] " + m); if (_log != null) { try { _log("[D3D] " + m); } catch { } } }

        void CreateSwap()
        {
            _cw = ClientSize.Width; _ch = ClientSize.Height;
            var desc = new DX.SwapChainDescription
            {
                ModeDescription = new DX.ModeDescription(_cw, _ch, new DX.Rational(60, 1), DX.Format.B8G8R8A8_UNorm),
                SampleDescription = new DX.SampleDescription(1, 0),
                Usage = DX.Usage.RenderTargetOutput,
                BufferCount = 1,
                OutputHandle = Handle,
                IsWindowed = true,
                SwapEffect = DX.SwapEffect.Discard,
                Flags = DX.SwapChainFlags.GdiCompatible
            };
            using (var fac = new DX.Factory1())
            {
                _sc = new DX.SwapChain(fac, _g.Dev, desc);
                fac.MakeWindowAssociation(Handle, DX.WindowAssociationFlags.IgnoreAll);
            }
            _back = _sc.GetBackBuffer<D3D.Texture2D>(0);
            _rtv = new D3D.RenderTargetView(_g.Dev, _back);
            try { _surf = _sc.GetBackBuffer<DX.Surface1>(0); } catch (Exception ex) { _gdiOk = false; Say("GDI overlay mati: " + ex.Message); }
        }

        Rectangle RectOf(Tile t)
        {
            if (_zoom != null) return t == _zoom ? new Rectangle(0, 0, _cw, _ch) : Rectangle.Empty;
            int x = (int)Math.Round(t.Rel.X * _cw), y = (int)Math.Round(t.Rel.Y * _ch);
            int r = (int)Math.Round((t.Rel.X + t.Rel.Width) * _cw), b = (int)Math.Round((t.Rel.Y + t.Rel.Height) * _ch);
            var rc = Rectangle.FromLTRB(x, y, r, b);
            rc.Inflate(-1, -1);
            return rc;
        }

        void RenderLoop()
        {
            long period = Stopwatch.Frequency / Math.Max(_fps, 25);
            long next = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                try { Render(); }
                catch (Exception ex)
                {
                    Say("render galat: " + ex.GetType().Name + " " + ex.Message);
                    Thread.Sleep(500);
                }
                next += period;
                long now = Stopwatch.GetTimestamp();
                if (next < now) next = now;
                int ms = (int)((next - now) * 1000 / Stopwatch.Frequency);
                if (ms > 0) Thread.Sleep(ms);
            }
        }

        void Render()
        {
            var ctx = _g.Ctx;
            lock (_g.Sync) { ctx.ClearRenderTargetView(_rtv, new RawColor4(0.06f, 0.08f, 0.16f, 1f)); }
            var probes = new List<KeyValuePair<Tile, Rectangle>>();
            if (_isRec != null && Stopwatch.GetTimestamp() - _lastRecPoll > 2 * Stopwatch.Frequency)
            {
                _lastRecPoll = Stopwatch.GetTimestamp();
                for (int i = 0; i < _tiles.Length; i++) { try { _tiles[i].Rec = _isRec(i); } catch { } }
            }
            foreach (var t in _tiles)
            {
                var rc = RectOf(t);
                if (rc.IsEmpty || !t.Visible) continue;
                t.PresentDue();
                lock (t.Lk)
                {
                    if (!t.HasFrame || t.Tex == null) continue;
                    try { bool first = t.Shown == 0; lock (_g.Sync) { if (_useShader) DrawShader(t, rc); else Blt(t, rc); } t.Shown++; if (first) Say("Blt pertama OK " + t.Name + " rect=" + rc); if (t.ProbeN < 4 && t.InY >= 0 && t.Shown >= 2) probes.Add(new KeyValuePair<Tile, Rectangle>(t, rc)); }
                    catch (Exception ex)
                    {
                        t.Status = "Render: " + ex.Message;
                        Say("Blt galat " + t.Name + " (bind=" + Tile.Variants[t.TexVar] + "): " + ex.Message.Split('\n')[0]);
                        t.DisposeVp();
                        if (_useShader) { _useShader = false; Say("shader gagal -> kembali ke Video Processor"); }
                        else if (ex.Message.Contains("E_INVALIDARG") && !t.IsDirect && t.TexVar == Tile.Variant && Tile.Variant < Tile.Variants.Length - 1)
                        {
                            Tile.Variant++;
                            Say("coba variasi bind berikutnya: " + Tile.Variants[Tile.Variant]);
                            t.HasFrame = false;
                        }
                    }
                }
            }
            if (probes.Count > 0) { try { ProbeOut(probes); } catch (Exception ex) { Say("probe tampil gagal: " + ex.Message.Split('\n')[0]); foreach (var kv in probes) kv.Key.ProbeN = 99; } }
            if (_gdiOk) Overlay();
            var r = _sc.TryPresent(1, DX.PresentFlags.None);
            if (_frames == 0 || (r.Failure && !_presentLogged)) { _presentLogged = r.Failure; Say("Present: " + r); }
            _frames++;
            long now = Stopwatch.GetTimestamp();
            if (now - _lastStat > 5 * Stopwatch.Frequency)
            {
                _lastStat = now;
                var sb = new StringBuilder("HEALTH ");
                int ok = 0, dec = 0;
                foreach (var t in _tiles) { if (t.HasFrame) ok++; if (!t.Failed) dec++; }
                Say("HEALTH tile hidup " + ok + "/" + dec + ", frame render " + _frames);
                foreach (var t in _tiles) Say("  " + t.Name + " dec=" + t.Decoded + " salin=" + t.Copied + " antre=" + t.QCount + " tampil=" + t.Shown + " has=" + t.HasFrame + " st=" + t.Status);
            }
        }

        void DrawShader(Tile t, Rectangle rc)
        {
            var tex = t.Tex;
            if (tex == null) return;
            int vw = t.TexW, vh = t.TexH;
            int bar = BarH;
            int dw = rc.Width, dh = rc.Height - bar, dx = rc.X, dy = rc.Y + bar;
            if (dh < 2) return;
            if (t.SrvTex != tex)
            {
                t.DisposeVp();
                var d1 = new D3D.ShaderResourceViewDescription { Format = DX.Format.R8_UNorm, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D };
                d1.Texture2D.MipLevels = 1; d1.Texture2D.MostDetailedMip = 0;
                var d2 = new D3D.ShaderResourceViewDescription { Format = DX.Format.R8G8_UNorm, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D };
                d2.Texture2D.MipLevels = 1; d2.Texture2D.MostDetailedMip = 0;
                t.SrvY = new D3D.ShaderResourceView(_g.Dev, tex, d1);
                t.SrvUV = new D3D.ShaderResourceView(_g.Dev, tex, d2);
                t.SrvTex = tex;
            }
            var ctx = _g.Ctx;
            var cb = new float[] { t.M709 ? 1f : 0f, t.FullRange ? 1f : 0f, 0f, 0f, t.Zoom, t.Cx, t.Cy, 0f };
            unsafe { fixed (float* pcb = cb) ctx.UpdateSubresource(new SharpDX.DataBox((IntPtr)pcb, 32, 32), _g.Cb, 0); }
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.VertexShader.Set(_g.Vs);
            ctx.PixelShader.Set(_g.Ps);
            ctx.PixelShader.SetConstantBuffer(0, _g.Cb);
            ctx.PixelShader.SetSampler(0, _g.Smp);
            ctx.PixelShader.SetShaderResources(0, new D3D.ShaderResourceView[] { t.SrvY, t.SrvUV });
            ctx.Rasterizer.SetViewport(dx, dy, dw, dh, 0f, 1f);
            ctx.OutputMerger.SetRenderTargets(_rtv);
            ctx.Draw(3, 0);
            ctx.PixelShader.SetShaderResources(0, new D3D.ShaderResourceView[2]);
            ctx.OutputMerger.ResetTargets();
        }

        void Blt(Tile t, Rectangle rc)
        {
            bool dir = t.IsDirect;
            D3D.Texture2D src = dir ? t.ArrTex : t.Tex;
            int iw = dir ? t.ArrW : t.TexW, ih = dir ? t.ArrH : t.TexH;
            int vw = dir ? t.HeldW : t.TexW, vh = dir ? t.HeldH : t.TexH;
            int slice = dir ? t.HeldSlice : 0;
            if (src == null) return;
            // letterbox
            double ar = (double)vw / vh;
            int dw = rc.Width, dh = (int)(dw / ar);
            if (dh > rc.Height) { dh = rc.Height; dw = (int)(dh * ar); }
            int dx = rc.X + (rc.Width - dw) / 2, dy = rc.Y + (rc.Height - dh) / 2;

            if (t.Vp == null || t.KeyCs != CsVar || t.KeyTex != src || t.KeyIw != iw || t.KeyIh != ih || t.KeyOw != _cw || t.KeyOh != _ch)
            {
                t.DisposeVp();
                var cd = new D3D.VideoProcessorContentDescription
                {
                    InputFrameFormat = D3D.VideoFrameFormat.Progressive,
                    InputFrameRate = new DX.Rational(30, 1),
                    InputWidth = iw, InputHeight = ih,
                    OutputFrameRate = new DX.Rational(30, 1),
                    OutputWidth = _cw, OutputHeight = _ch,
                    Usage = D3D.VideoUsage.PlaybackNormal
                };
                _g.VDev.CreateVideoProcessorEnumerator(ref cd, out t.VEnum);
                if (!_fmtLogged)
                {
                    _fmtLogged = true;
                    int fl;
                    t.VEnum.CheckVideoProcessorFormat(DX.Format.NV12, out fl);
                    Say("VP NV12 flags=0x" + fl.ToString("x") + " (1=input, 2=output)");
                    Say("VP caps: " + t.VEnum.VideoProcessorCaps.FeatureCaps);
                }
                _g.VDev.CreateVideoProcessor(t.VEnum, 0, out t.Vp);
                var ovd = new D3D.VideoProcessorOutputViewDescription { Dimension = D3D.VpovDimension.Texture2D };
                ovd.Texture2D.MipSlice = 0;
                _g.VDev.CreateVideoProcessorOutputView(_back, t.VEnum, ovd, out t.OutView);
                t.KeyCs = CsVar; t.KeyTex = src; t.KeyIw = iw; t.KeyIh = ih; t.KeyOw = _cw; t.KeyOh = _ch;
                _g.VCtx.VideoProcessorSetStreamAutoProcessingMode(t.Vp, 0, false);
                _g.VCtx.VideoProcessorSetStreamFrameFormat(t.Vp, 0, D3D.VideoFrameFormat.Progressive);
                if (CsVar != 5)
                {
                    var ocs = new D3D.VideoProcessorColorSpace { Usage = false, RgbRange = false, YCbCrMatrix = true, YCbCrXvYCC = false, NominalRange = 0 };
                    _g.VCtx.VideoProcessorSetOutputColorSpace(t.Vp, ocs);
                }
            }
            D3D.VideoProcessorInputView iv;
            if (!t.Views.TryGetValue(slice, out iv))
            {
                var ivd = new D3D.VideoProcessorInputViewDescription { FourCC = 0, Dimension = D3D.VpivDimension.Texture2D };
                ivd.Texture2D.MipSlice = 0; ivd.Texture2D.ArraySlice = slice;
                _g.VDev.CreateVideoProcessorInputView(src, t.VEnum, ivd, out iv);
                t.Views[slice] = iv;
            }
            var vc = _g.VCtx;
            if (CsVar != 5)
            {
                int nom = t.FullRange ? 2 : 1;
                bool mx = t.M709;
                switch (CsVar) { case 1: nom = 2; break; case 2: nom = 0; break; case 3: nom = t.FullRange ? 1 : 2; break; case 4: mx = !mx; break; }
                var cs = new D3D.VideoProcessorColorSpace { Usage = false, RgbRange = false, YCbCrMatrix = mx, YCbCrXvYCC = false, NominalRange = nom };
                vc.VideoProcessorSetStreamColorSpace(t.Vp, 0, cs);
            }
            vc.VideoProcessorSetStreamSourceRect(t.Vp, 0, true, new RawRectangle(0, 0, vw, vh));
            vc.VideoProcessorSetStreamDestRect(t.Vp, 0, true, new RawRectangle(dx, dy, dx + dw, dy + dh));
            vc.VideoProcessorSetOutputTargetRect(t.Vp, true, new RawRectangle(rc.X, rc.Y, rc.Right, rc.Bottom));
            var st = new D3D.VideoProcessorStream { Enable = true, InputFrameOrField = 0, PInputSurface = iv };
            vc.VideoProcessorBlt(t.Vp, t.OutView, 0, 1, new[] { st });
        }

        // kalibrasi: baca satu piksel hasil gambar; bila hitam padahal isi tekstur terang, ganti pengaturan color space
        void ProbeOut(List<KeyValuePair<Tile, Rectangle>> list)
        {
            if (_stg == null)
                _stg = new D3D.Texture2D(_g.Dev, new D3D.Texture2DDescription
                {
                    Width = _cw, Height = _ch, MipLevels = 1, ArraySize = 1, Format = DX.Format.B8G8R8A8_UNorm,
                    SampleDescription = new DX.SampleDescription(1, 0), Usage = D3D.ResourceUsage.Staging,
                    BindFlags = D3D.BindFlags.None, CpuAccessFlags = D3D.CpuAccessFlags.Read, OptionFlags = D3D.ResourceOptionFlags.None
                });
            bool bad = false;
            lock (_g.Sync)
            {
                _g.Ctx.CopyResource(_back, _stg);
                var box = _g.Ctx.MapSubresource(_stg, 0, D3D.MapMode.Read, D3D.MapFlags.None);
                try
                {
                    foreach (var kv in list)
                    {
                        var t = kv.Key; var rc = kv.Value;
                        int cx = rc.X + rc.Width / 2 - 2, cy = rc.Y + rc.Height / 2 - 2, sum = 0;
                        unsafe
                        {
                            byte* b = (byte*)box.DataPointer;
                            for (int y = cy; y < cy + 4; y++) for (int x = cx; x < cx + 4; x++)
                            { byte* px = b + y * box.RowPitch + x * 4; sum += (px[0] + px[1] + px[2]) / 3; }
                        }
                        int outY = sum / 16;
                        t.ProbeN++;
                        bool dark = outY < 20 && t.InY > 50;
                        Say("probe tampil " + t.Name + ": luma keluar=" + outY + " masuk Y=" + t.InY + " cs=" + CsNames[CsVar] + (dark ? " HITAM" : " ok"));
                        if (dark && !_useShader) bad = true; else if (!dark) t.ProbeN = 99; else t.ProbeN = 99;
                    }
                }
                finally { _g.Ctx.UnmapSubresource(_stg, 0); }
            }
            long now = Stopwatch.GetTimestamp();
            if (bad && now - _csChangedAt > Stopwatch.Frequency / 2)
            {
                if (!Tile.Direct && CsVar >= 1)
                {
                    Tile.Direct = true; CsVar = 0; _csChangedAt = now;
                    Say("hasil tetap hitam -> mode langsung (VP membaca tekstur dekoder tanpa salinan)");
                    foreach (var t in _tiles) { t.ProbeN = 0; t.InY = -1; t.ProbeFrames = 0; }
                }
                else if (CsVar < CsNames.Length - 1)
                {
                    CsVar++;
                    _csChangedAt = now;
                    Say("hasil hitam -> coba color space: " + CsNames[CsVar]);
                    foreach (var t in _tiles) t.ProbeN = 0;
                }
            }
        }

        void Overlay()
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = _surf.GetDC(false);
                using (var g = Graphics.FromHdc(hdc))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    int alive = 0; foreach (var t0 in _tiles) if (t0.HasFrame) alive++;
                    if (_info) g.DrawString("D3D " + _g.Name + "  render " + _frames + "  tile gambar " + alive + "/" + _tiles.Length + "  (log: " + FLog.Path0 + ")", _fSmall, Brushes.Yellow, 4, _ch - 18);
                    using (var bar = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    using (var barBack = new SolidBrush(BarBack))
                    using (var recBr = new SolidBrush(RecRed))
                    using (var fg = new SolidBrush(Color.White))
                    using (var dim = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    using (var pen = new Pen(Color.FromArgb(40, 40, 40)))
                    {
                        foreach (var t in _tiles)
                        {
                            var rc = RectOf(t);
                            if (rc.IsEmpty) continue;
                            if (_sel >= 0 && _sel < _tiles.Length && _tiles[_sel] == t)
                                using (var sp = new Pen(Color.FromArgb(255, 160, 0), 3)) g.DrawRectangle(sp, rc.X + 1, rc.Y + 1, rc.Width - 3, rc.Height - 3);
                            int bh = BarH;
                            var barR = new Rectangle(rc.X, rc.Y, rc.Width, bh);
                            g.FillRectangle(barBack, barR);
                            int recW = t.Rec ? (int)(22 * _dpi / 96f) : 0;
                            var nameR = new RectangleF(rc.X + 4, rc.Y, Math.Max(1, rc.Width - 8 - recW), bh);
                            var nf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                            g.DrawString(t.Name ?? "", _fBar, fg, nameR, nf);
                            if (t.Zoom > 1.01f)
                                g.DrawString("x" + t.Zoom.ToString("0.0"), _fSmall, Brushes.Orange, rc.Right - recW - 34, rc.Y + (bh - _fSmall.Height) / 2);
                            if (t.Rec)
                            {
                                var rf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                g.DrawString("\u25CF", _fRec, recBr, new RectangleF(rc.Right - recW, rc.Y, recW, bh), rf);
                            }
                            if (!t.HasFrame)
                            {
                                g.DrawRectangle(pen, rc.X, rc.Y, rc.Width - 1, rc.Height - 1);
                                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                g.DrawString(t.Status ?? "", _fSmall, dim, new RectangleF(rc.X, rc.Y + bh, rc.Width, rc.Height - bh), sf);
                            }
                            if (_info)
                            {
                                string s = t.DecW + "x" + t.DecH + "  dec " + t.Decoded + "  salin " + t.Copied + "  tampil " + t.Shown;
                                g.FillRectangle(bar, rc.X, rc.Bottom - 16, Math.Min(rc.Width, 330), 16);
                                g.DrawString(s, _fSmall, fg, rc.X + 3, rc.Bottom - 15);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _gdiOk = false;
                Say("overlay GDI dimatikan: " + ex.Message);
            }
            finally
            {
                if (hdc != IntPtr.Zero) { try { _surf.ReleaseDC(); } catch { } }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.KeyCode == Keys.I) _info = !_info;
            if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.Home) { int h0 = _sel >= 0 ? _sel : -1; if (h0 >= 0) ResetZoom(_tiles[h0]); }
            switch (e.KeyCode)
            {
                case Keys.Left: PtzHoldStart(-PtzSpeed, 0, 0); e.Handled = true; break;
                case Keys.Right: PtzHoldStart(PtzSpeed, 0, 0); e.Handled = true; break;
                case Keys.Up: PtzHoldStart(0, -PtzSpeed, 0); e.Handled = true; break;
                case Keys.Down: PtzHoldStart(0, PtzSpeed, 0); e.Handled = true; break;
                case Keys.PageUp: case Keys.Add: case Keys.Oemplus: PtzHoldStart(0, 0, PtzSpeed); e.Handled = true; break;
                case Keys.PageDown: case Keys.Subtract: case Keys.OemMinus: PtzHoldStart(0, 0, -PtzSpeed); e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_zoom != null)
            {
                _zoom.UseMain = _zoom.AdaptMain;
                _zoom = null;
                foreach (var t in _tiles) if (!t.Failed) t.Visible = true;
                return;
            }
            foreach (var t in _tiles)
            {
                var rc = RectOf(t);
                if (!rc.IsEmpty && rc.Contains(e.Location) && !t.Failed)
                {
                    _zoom = t;
                    foreach (var o in _tiles) o.Visible = (o == t);
                    if (!string.IsNullOrEmpty(t.MainUrl)) t.UseMain = true;
                    break;
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Shutdown();
        }

        bool _down;
        void Shutdown()
        {
            if (_down) return;
            _down = true;
            _stop = true;
            foreach (var t in _tiles) t.Stop = true;
            try { if (_rt != null) _rt.Join(2000); } catch { }
            foreach (var t in _tiles) t.Join();
            foreach (var t in _tiles) { try { t.DisposeAll(); } catch { } }
            try { if (_stg != null) _stg.Dispose(); } catch { }
            try { if (_surf != null) _surf.Dispose(); } catch { }
            try { if (_rtv != null) _rtv.Dispose(); } catch { }
            try { if (_back != null) _back.Dispose(); } catch { }
            try { if (_sc != null) _sc.Dispose(); } catch { }
            Say("ditutup");
        }
    }
}
