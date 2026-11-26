using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using NDI;

namespace Web.Services
{
    /// <summary>
    /// Re-encodes the NDI source as a low-bandwidth MJPEG stream (<c>/video</c>) for browsers.
    /// Settings: Video:MaxFps (default 4), Video:MaxWidth (640), Video:JpegQuality (45).
    /// Frames are encoded only while at least one viewer is connected.
    /// </summary>
    public sealed class VideoStreamer : IHostedService
    {
        private const string Boundary = "frame";

        private readonly NdiManager _ndi;
        private readonly ILogger<VideoStreamer> _logger;
        private readonly TimeSpan _minInterval;
        private readonly int _maxWidth;
        private readonly long _quality;
        private readonly ImageCodecInfo _jpegCodec;

        private readonly object _gate = new();
        private byte[]? _latest;
        private long _version;
        private TaskCompletionSource _frameReady = NewSignal();
        private int _viewers;
        private DateTime _lastEncodeAt = DateTime.MinValue;

        public VideoStreamer(NdiManager ndi, IConfiguration config, ILogger<VideoStreamer> logger)
        {
            _ndi = ndi;
            _logger = logger;

            int maxFps = Math.Clamp(config.GetValue("Video:MaxFps", 4), 1, 30);
            _minInterval = TimeSpan.FromSeconds(1.0 / maxFps);
            _maxWidth = Math.Clamp(config.GetValue("Video:MaxWidth", 640), 160, 1920);
            _quality = Math.Clamp(config.GetValue("Video:JpegQuality", 45L), 10L, 95L);
            _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        }

        public Task StartAsync(CancellationToken ct)
        {
            _ndi.FrameReceived += OnFrame;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _ndi.FrameReceived -= OnFrame;
            return Task.CompletedTask;
        }

        // Runs on the NDI capture thread, so it must stay cheap. Skipped when nobody is watching
        // or when the previous frame was encoded less than one interval ago.
        private void OnFrame(NdiReceiver.NdiFrame frame)
        {
            if (Volatile.Read(ref _viewers) == 0) return;

            var now = DateTime.UtcNow;
            if (now - _lastEncodeAt < _minInterval) return;
            _lastEncodeAt = now;

            try
            {
                Publish(Encode(frame));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Video frame encode failed");
            }
        }

        private byte[] Encode(NdiReceiver.NdiFrame frame)
        {
            int srcW = frame.Width;
            int srcH = frame.Height;
            int srcStride = frame.DataSize / srcH;

            // NDI gives BGRX; the alpha byte is not meaningful, so use a 24-bit-compatible 32bpp RGB bitmap.
            using var src = new Bitmap(srcW, srcH, PixelFormat.Format32bppRgb);
            var data = src.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int rowBytes = Math.Min(srcW * 4, srcStride);
                for (int y = 0; y < srcH; y++)
                    Marshal.Copy(frame.BgraData, y * srcStride, data.Scan0 + y * data.Stride, rowBytes);
            }
            finally
            {
                src.UnlockBits(data);
            }

            int dstW = Math.Min(_maxWidth, srcW);
            int dstH = (int)Math.Round(srcH * (dstW / (double)srcW));

            using var dst = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(src, 0, 0, dstW, dstH);
            }

            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _quality);

            using var ms = new MemoryStream();
            dst.Save(ms, _jpegCodec, encoderParams);
            return ms.ToArray();
        }

        private void Publish(byte[] jpeg)
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                _latest = jpeg;
                _version++;
                previous = _frameReady;
                _frameReady = NewSignal();
            }
            previous.TrySetResult();
        }

        private async Task<(long Version, byte[] Jpeg)> WaitForFrameAsync(long afterVersion, CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_version > afterVersion && _latest is not null)
                        return (_version, _latest);
                    wait = _frameReady.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        /// <summary>Streams frames to one viewer until it disconnects.</summary>
        public async Task StreamAsync(HttpContext http)
        {
            var response = http.Response;
            response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
            response.Headers.CacheControl = "no-store";

            Interlocked.Increment(ref _viewers);
            try
            {
                // Send the headers now, so the browser sees the stream open before the first frame arrives.
                await response.StartAsync(http.RequestAborted);
                await response.Body.FlushAsync(http.RequestAborted);

                long sent = 0;
                while (true)
                {
                    var (version, jpeg) = await WaitForFrameAsync(sent, http.RequestAborted);
                    sent = version;

                    var head = Encoding.ASCII.GetBytes(
                        $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                    await response.Body.WriteAsync(head, http.RequestAborted);
                    await response.Body.WriteAsync(jpeg, http.RequestAborted);
                    await response.Body.WriteAsync("\r\n"u8.ToArray(), http.RequestAborted);
                    await response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // viewer closed the page
            }
            catch (IOException)
            {
                // connection reset while writing
            }
            finally
            {
                Interlocked.Decrement(ref _viewers);
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
