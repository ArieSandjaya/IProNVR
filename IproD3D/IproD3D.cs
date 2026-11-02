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

        /// <summary>Buka jendela wall untuk satu layar. rects = (x,y,w,h) ternormalisasi per kamera.</summary>
        public static Form Open(string title, string[] urls, string[] mainUrls, string[] names, float[] rects,
            Rectangle bounds, string deviceName, string ffmpegDir, int maxStreams, int fps, Action<string> log)
        {
            try { File.WriteAllText(FLog.Path0, ""); } catch { }
            FLog.W("Open " + title + " layar=" + deviceName + " tile=" + urls.Length + " dir=" + ffmpegDir);
            Init(ffmpegDir);
            return new Wall(title, urls, mainUrls, names, rects, bounds, deviceName, maxStreams, fps, log);
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

                    AVBufferRef* r = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                    if (r == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc gagal");
                    var dc = (AVHWDeviceContext*)r->data;
                    var hc = (AVD3D11VADeviceContext*)dc->hwctx;
                    Marshal.AddRef(g.Dev.NativePointer);
                    hc->device = (ID3D11Device*)g.Dev.NativePointer.ToPointer();
                    Marshal.AddRef(g.Ctx.NativePointer);
                    hc->device_context = (ID3D11DeviceContext*)g.Ctx.NativePointer.ToPointer();
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
        public volatile bool UseMain, Visible = true, Stop;
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
        public static volatile int Variant;
        public static readonly D3D.BindFlags[] Variants = new D3D.BindFlags[] {
            D3D.BindFlags.ShaderResource,
            D3D.BindFlags.ShaderResource | D3D.BindFlags.RenderTarget,
            D3D.BindFlags.RenderTarget,
            D3D.BindFlags.None,
            D3D.BindFlags.Decoder,
            D3D.BindFlags.Decoder | D3D.BindFlags.ShaderResource };
        public int TexVar;

        // cache video processor (diurus render thread)
        public D3D.VideoProcessorEnumerator VEnum;
        public D3D.VideoProcessor Vp;
        public D3D.VideoProcessorInputView InView;
        public D3D.VideoProcessorOutputView OutView;
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
                for (int w = 0; w < backoff && !Stop && UseMain == wasMain; w += 100) Thread.Sleep(100);
                if (backoff < 10000) backoff += 1500;
            }
            lock (Lk) { ReleaseSrc(); }
        }

        void ReleaseSrc()
        {
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
                ffmpeg.av_dict_set(&opt, "analyzeduration", "1500000", 0);
                ffmpeg.av_dict_set(&opt, "probesize", "500000", 0);
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
                cc->hw_device_ctx = ffmpeg.av_buffer_ref(G.HwDev);
                cc->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(_fmt) };
                cc->extra_hw_frames = 2;
                cc->thread_count = 1;
                cc->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                e = ffmpeg.avcodec_open2(cc, codec, null);
                if (e < 0) throw new Exception("open codec: " + Err(e));
                DecW = par->width; DecH = par->height;
                pkt = ffmpeg.av_packet_alloc();
                fr = ffmpeg.av_frame_alloc();
                Say("terhubung " + par->codec_id + " " + par->width + "x" + par->height + (UseMain ? " (utama)" : " (sub)"));
                Status = "Menunggu frame...";
                bool main0 = UseMain;
                long lastCopy = 0;
                long minTicks = Stopwatch.Frequency * MinIntervalMs / 1000;
                while (!Stop && UseMain == main0)
                {
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
                        long now = Stopwatch.GetTimestamp();
                        if (Visible && now - lastCopy >= minTicks)
                        {
                            lastCopy = now;
                            if (fr->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                                throw new Exception("frame bukan D3D11 (format " + fr->format + "): dekode GPU tidak aktif");
                            Copy(fr);
                        }
                        ffmpeg.av_frame_unref(fr);
                    }
                }
            }
            finally
            {
                if (fr != null) ffmpeg.av_frame_free(&fr);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (cc != null) ffmpeg.avcodec_free_context(&cc);
                if (fc != null) ffmpeg.avformat_close_input(&fc);
                if (opt != null) ffmpeg.av_dict_free(&opt);
                lock (Lk) { ReleaseSrc(); }
            }
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
                G.Ctx.CopySubresourceRegion(_srcWrap, slice, new D3D.ResourceRegion(0, 0, 0, w, h, 1), Tex, 0, 0, 0, 0);
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
            if (InView != null) { InView.Dispose(); InView = null; }
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
        bool _gdiOk = true;
        int _cw, _ch;
        readonly Font _fName = new Font("Segoe UI", 9f, FontStyle.Bold);
        readonly Font _fSmall = new Font("Segoe UI", 8f);
        long _frames, _lastStat;
        bool _presentLogged;
        static bool _fmtLogged;
        readonly string _title;

        public Wall(string title, string[] urls, string[] mainUrls, string[] names, float[] rects,
            Rectangle bounds, string deviceName, int maxStreams, int fps, Action<string> log)
        {
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
            _tiles = new Tile[n];
            for (int i = 0; i < n; i++)
            {
                var t = new Tile();
                t.G = _g; t.Log = log;
                t.Name = names != null && i < names.Length ? names[i] : ("Cam " + (i + 1));
                t.Url = urls[i];
                t.MainUrl = mainUrls != null && i < mainUrls.Length ? mainUrls[i] : null;
                t.Rel = new RectangleF(rects[i * 4], rects[i * 4 + 1], rects[i * 4 + 2], rects[i * 4 + 3]);
                t.MinIntervalMs = 1000 / _fps - 4;
                if (i >= maxStreams) { t.Status = "Dibatasi (batas stream " + maxStreams + ")"; t.Visible = false; t.Failed = true; }
                _tiles[i] = t;
            }
            var h = Handle;   // paksa handle dibuat
            CreateSwap();
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
            long period = Stopwatch.Frequency / _fps;
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
            ctx.ClearRenderTargetView(_rtv, new RawColor4(0.06f, 0.08f, 0.16f, 1f));
            foreach (var t in _tiles)
            {
                var rc = RectOf(t);
                if (rc.IsEmpty || !t.Visible) continue;
                lock (t.Lk)
                {
                    if (!t.HasFrame || t.Tex == null) continue;
                    try { bool first = t.Shown == 0; Blt(t, rc); t.Shown++; if (first) Say("Blt pertama OK " + t.Name + " rect=" + rc); }
                    catch (Exception ex)
                    {
                        t.Status = "Render: " + ex.Message;
                        Say("Blt galat " + t.Name + " (bind=" + Tile.Variants[t.TexVar] + "): " + ex.Message.Split('\n')[0]);
                        t.DisposeVp();
                        if (ex.Message.Contains("E_INVALIDARG") && t.TexVar == Tile.Variant && Tile.Variant < Tile.Variants.Length - 1)
                        {
                            Tile.Variant++;
                            Say("coba variasi bind berikutnya: " + Tile.Variants[Tile.Variant]);
                            t.HasFrame = false;
                        }
                    }
                }
            }
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
                foreach (var t in _tiles) Say("  " + t.Name + " dec=" + t.Decoded + " salin=" + t.Copied + " tampil=" + t.Shown + " has=" + t.HasFrame + " st=" + t.Status);
            }
        }

        void Blt(Tile t, Rectangle rc)
        {
            int iw = t.TexW, ih = t.TexH;
            // letterbox
            double ar = (double)iw / ih;
            int dw = rc.Width, dh = (int)(dw / ar);
            if (dh > rc.Height) { dh = rc.Height; dw = (int)(dh * ar); }
            int dx = rc.X + (rc.Width - dw) / 2, dy = rc.Y + (rc.Height - dh) / 2;

            if (t.Vp == null || t.KeyTex != t.Tex || t.KeyIw != iw || t.KeyIh != ih || t.KeyOw != _cw || t.KeyOh != _ch)
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
                var ivd = new D3D.VideoProcessorInputViewDescription { FourCC = 0, Dimension = D3D.VpivDimension.Texture2D };
                ivd.Texture2D.MipSlice = 0; ivd.Texture2D.ArraySlice = 0;
                _g.VDev.CreateVideoProcessorInputView(t.Tex, t.VEnum, ivd, out t.InView);
                var ovd = new D3D.VideoProcessorOutputViewDescription { Dimension = D3D.VpovDimension.Texture2D };
                ovd.Texture2D.MipSlice = 0;
                _g.VDev.CreateVideoProcessorOutputView(_back, t.VEnum, ovd, out t.OutView);
                t.KeyTex = t.Tex; t.KeyIw = iw; t.KeyIh = ih; t.KeyOw = _cw; t.KeyOh = _ch;
                _g.VCtx.VideoProcessorSetStreamAutoProcessingMode(t.Vp, 0, false);
                _g.VCtx.VideoProcessorSetStreamFrameFormat(t.Vp, 0, D3D.VideoFrameFormat.Progressive);
                var ocs = new D3D.VideoProcessorColorSpace { Usage = false, RgbRange = false, YCbCrMatrix = true, YCbCrXvYCC = false, NominalRange = 0 };
                _g.VCtx.VideoProcessorSetOutputColorSpace(t.Vp, ocs);
            }
            var cs = new D3D.VideoProcessorColorSpace { Usage = false, RgbRange = false, YCbCrMatrix = t.M709, YCbCrXvYCC = false, NominalRange = t.FullRange ? 2 : 1 };
            var vc = _g.VCtx;
            vc.VideoProcessorSetStreamColorSpace(t.Vp, 0, cs);
            vc.VideoProcessorSetStreamSourceRect(t.Vp, 0, true, new RawRectangle(0, 0, iw, ih));
            vc.VideoProcessorSetStreamDestRect(t.Vp, 0, true, new RawRectangle(dx, dy, dx + dw, dy + dh));
            vc.VideoProcessorSetOutputTargetRect(t.Vp, true, new RawRectangle(rc.X, rc.Y, rc.Right, rc.Bottom));
            var st = new D3D.VideoProcessorStream { Enable = true, InputFrameOrField = 0, PInputSurface = t.InView };
            vc.VideoProcessorBlt(t.Vp, t.OutView, 0, 1, new[] { st });
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
                    g.DrawString("D3D " + _g.Name + "  render " + _frames + "  tile gambar " + alive + "/" + _tiles.Length + "  (log: " + FLog.Path0 + ")", _fSmall, Brushes.Yellow, 4, _ch - 18);
                    using (var bar = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    using (var fg = new SolidBrush(Color.White))
                    using (var dim = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    using (var pen = new Pen(Color.FromArgb(40, 40, 40)))
                    {
                        foreach (var t in _tiles)
                        {
                            var rc = RectOf(t);
                            if (rc.IsEmpty) continue;
                            if (!t.HasFrame)
                            {
                                g.DrawRectangle(pen, rc.X, rc.Y, rc.Width - 1, rc.Height - 1);
                                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                g.DrawString((t.Name ?? "") + "\n" + (t.Status ?? ""), _fSmall, dim, rc, sf);
                            }
                            else
                            {
                                var sz = g.MeasureString(t.Name ?? "", _fName);
                                g.FillRectangle(bar, rc.X, rc.Y, Math.Min(rc.Width, sz.Width + 8), sz.Height + 2);
                                g.DrawString(t.Name ?? "", _fName, fg, rc.X + 4, rc.Y + 1);
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
            base.OnKeyDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_zoom != null)
            {
                _zoom.UseMain = false;
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
            try { if (_surf != null) _surf.Dispose(); } catch { }
            try { if (_rtv != null) _rtv.Dispose(); } catch { }
            try { if (_back != null) _back.Dispose(); } catch { }
            try { if (_sc != null) _sc.Dispose(); } catch { }
            Say("ditutup");
        }
    }
}
