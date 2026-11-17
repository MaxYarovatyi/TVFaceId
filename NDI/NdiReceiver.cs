using NewTek;
using NewTek.NDI;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using System.Buffers;

namespace NDI
{
    public class NdiReceiver : IDisposable
    {
        public sealed record NdiFrame(byte[] BrgaData, int Width, int Height, int DataSize, long TimestampTicks)
        {
            public void Return() => ArrayPool<byte>.Shared.Return(BrgaData);
        }

        public event Action<NdiFrame>? FrameReceived;
        private int _frameSkip = 0;

        private readonly ILogger<NdiReceiver> _logger;
        private IntPtr _finder = IntPtr.Zero;
        private IntPtr _recv = IntPtr.Zero;
        private bool _disposed;
        private readonly string? _extraIps;

        public NdiReceiver(ILogger<NdiReceiver> logger, IConfiguration config)
        {
            _logger = logger;
            _extraIps = config["Ndi:ExtraIps"];

            if (!NDIlib.initialize())
                throw new InvalidOperationException("NDI initialization failed");

            _logger.LogInformation("NDI installed. Version: {Ver}", NDIlib.version());
        }

        public IReadOnlyList<string> DiscoverSources(int waitMs = 2000)
        {

            var findSettings = new NDIlib.find_create_t
            {
                show_local_sources = true,
                p_groups = IntPtr.Zero,
                p_extra_ips = IntPtr.Zero
            };

            _finder = string.IsNullOrEmpty(_extraIps)
                ? NDIlib.find_create_v2(ref findSettings)
                : CreateFinderWithExtraIps(_extraIps);


            if (_finder == IntPtr.Zero)
            {
                _logger.LogError("Failed to create NDI finder.");
                return [];
            }

            _logger.LogInformation("Scanning for NDI sources ({Wait}ms)", waitMs);
            Thread.Sleep(waitMs);

            uint count = 0;
            IntPtr sourcesPtr = NDIlib.find_get_current_sources(_finder, ref count);

            var names = new List<string>((int)count);
            int stride = Marshal.SizeOf<NDIlib.source_t>();

            for (int i = 0; i < count; i++)
            {
                var src = Marshal.PtrToStructure<NDIlib.source_t>(sourcesPtr + i * stride);
                string name = UTF8ToString(src.p_ndi_name);
                names.Add(name);
                _logger.LogInformation(" - {Name}", name);
            }

            return names;
        }

        public bool Connect(string? sourceName = null)
        {
            if (_finder == IntPtr.Zero)
                DiscoverSources();

            uint count = 0;
            IntPtr sourcesPtr = NDIlib.find_get_current_sources(_finder, ref count);

            if (count == 0)
            {
                _logger.LogWarning("No NDI sources available.");
                return false;
            }
            ;

            int stride = Marshal.SizeOf<NDIlib.source_t>();
            NDIlib.source_t target = Marshal.PtrToStructure<NDIlib.source_t>(sourcesPtr);

            if (sourceName is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    var src = Marshal.PtrToStructure<NDIlib.source_t>(sourcesPtr + i * stride);
                    if (UTF8ToString(src.p_ndi_name).Contains(sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = src;
                        break;
                    }
                }
            }

            _logger.LogInformation("Connecting to: {Name}", UTF8ToString(target.p_ndi_name));

            var recvSettings = new NDIlib.recv_create_v3_t
            {
                source_to_connect_to = target,
                color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
                bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_lowest,
                allow_video_fields = false,
                p_ndi_recv_name = IntPtr.Zero
            };

            _recv = NDIlib.recv_create_v3(ref recvSettings);

            if (_recv == IntPtr.Zero)
            {
                _logger.LogError("Failed to create NDI receiver.");
                return false;
            }

            _logger.LogInformation("Receiver created successfully.");
            return true;
        }

        public void CaptureLoop(CancellationToken ct)
        {
            if (_recv == IntPtr.Zero)
                throw new InvalidOperationException("Call Connect() before CaptureLoop()");

            var video = new NDIlib.video_frame_v2_t();
            var audio = new NDIlib.audio_frame_v3_t();
            var meta = new NDIlib.metadata_frame_t();

            _logger.LogInformation("Capture loop started.");

            while (!ct.IsCancellationRequested)
            {
                var frameType = NDIlib.recv_capture_v3(_recv, ref video, ref audio, ref meta, 500);

                switch (frameType)
                {
                    case NDIlib.frame_type_e.frame_type_video:
                        if (_frameSkip++ % 2 == 0)
                        {
                            ProcessVideoFrame(ref video);
                        }
                        NDIlib.recv_free_video_v2(_recv, ref video);
                        break;

                    case NDIlib.frame_type_e.frame_type_audio:
                        NDIlib.recv_free_audio_v3(_recv, ref audio);
                        break;

                    case NDIlib.frame_type_e.frame_type_metadata:
                        NDIlib.recv_free_metadata(_recv, ref meta);
                        break;
                }
            }

            _logger.LogInformation("Capture loop stopped.");
        }

        private void ProcessVideoFrame(ref NDIlib.video_frame_v2_t frame)
        {
            if (frame.p_data == IntPtr.Zero) return;

            int size = frame.line_stride_in_bytes * frame.yres;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            Marshal.Copy(frame.p_data, buffer, 0, size);

            FrameReceived?.Invoke(new NdiFrame(
                BrgaData: buffer,
                Width: frame.xres,
                Height: frame.yres,
                DataSize: size,
                TimestampTicks: frame.timestamp
            ));
        }

        private static string UTF8ToString(IntPtr ptr) =>
            ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_recv != IntPtr.Zero) NDIlib.recv_destroy(_recv);
            if (_finder != IntPtr.Zero) NDIlib.find_destroy(_finder);

            NDIlib.destroy();
        }

        private static IntPtr CreateFinderWithExtraIps(string extraIps)
        {
            IntPtr extraIpsPtr = Marshal.StringToHGlobalAnsi(extraIps);

            try
            {
                var findSettings = new NDIlib.find_create_t
                {
                    show_local_sources = true,
                    p_groups = IntPtr.Zero,
                    p_extra_ips = extraIpsPtr
                };
                return NDIlib.find_create_v2(ref findSettings);
            }
            finally
            {
                if (extraIpsPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(extraIpsPtr);
            }
        }
    }
}