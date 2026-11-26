using System.Threading.Channels;
using Core;
using Core.Database;
using Core.Inference;
using Microsoft.AspNetCore.SignalR;
using NDI;
using Web.Hubs;
using Web.Models;

namespace Web.Services
{
    public sealed class RecognitionWorker : BackgroundService
    {
        private readonly IHubContext<RecognitionHub> _hub;
        private readonly FaceDatabase _db;
        private readonly NdiManager _ndi;
        private readonly ILogger<RecognitionWorker> _logger;
        private DateTime _lastProcessed;

        private bool _sleeping = false;
        private DateTime _lastFaceAt = DateTime.UtcNow;
        private byte[]? _sceneSnap = null;
        private static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(30);

        private readonly Channel<NdiReceiver.NdiFrame> _frames =
            Channel.CreateBounded<NdiReceiver.NdiFrame>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        private List<FaceResult> _lastResults = [];

        public RecognitionWorker(IHubContext<RecognitionHub> hub, FaceDatabase db, NdiManager ndi, ILogger<RecognitionWorker> logger)
        {
            _hub = hub;
            _db = db;
            _ndi = ndi;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            using var detector = new FaceDetector("models/det_500m.onnx");
            using var recognizer = new FaceRecognizer("models/w600k_mbf.onnx");

            _ndi.FrameReceived += frame => _frames.Writer.TryWrite(frame);

            var throttle = TimeSpan.FromMilliseconds(1000);

            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                if (DateTime.UtcNow - _lastProcessed < throttle)
                {
                    frame.Return();
                    continue;
                }
                _lastProcessed = DateTime.UtcNow;

                try
                {
                    await ProcessFrameAsync(frame, detector, recognizer, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Frame processing error");
                }
                finally { frame.Return(); }
            }
        }

        private async Task ProcessFrameAsync(NdiReceiver.NdiFrame frame, FaceDetector detector, FaceRecognizer recognizer, CancellationToken ct)
        {
            if (_sleeping)
            {
                if (!HasSceneChanged(frame.BgraData.AsSpan(0, frame.DataSize).ToArray(), frame.Width, frame.Height))
                    return;

                var check = detector.Detect(frame.BgraData.AsSpan(0, frame.DataSize).ToArray(), frame.Width, frame.Height);
                if (check.Count == 0) return;

                _sleeping = false;
                _lastFaceAt = DateTime.UtcNow;
                _logger.LogInformation("Waking up");
            }

            var detected = detector.Detect(frame.BgraData, frame.Width, frame.Height);
            var results = new List<FaceResult>(detected.Count);

            foreach (var face in detected)
            {
                var (person, sim) = _db.FindBest(face, frame.BgraData.AsSpan(0, frame.DataSize).ToArray(), frame.Width, frame.Height);

                if (person is null) continue;

                results.Add(new FaceResult(
                    Name: person.Name,
                    Country: person.Country,
                    Role: person.Role,
                    Confidence: (int)(sim * 100),
                    PhotoUrl: $"/faces/{person.PhotoPath}"
                ));
            }

            if (detected.Count > 0)
                _lastFaceAt = DateTime.UtcNow;

            if (DateTime.UtcNow - _lastFaceAt > SleepAfter)
            {
                _sleeping = true;
                _logger.LogInformation("Going to sleep - no faces for {S}s", SleepAfter);
                await _hub.Clients.All.SendAsync("RecognitionUpdate", new List<FaceResult>(), ct);
                return;
            }

            if (!ResultsEqual(_lastResults, results))
            {
                _lastResults = results;
                await _hub.Clients.All.SendAsync("RecognitionUpdate", results, ct);
            }
        }

        private static bool ResultsEqual(List<FaceResult> a, List<FaceResult> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name || Math.Abs(a[i].Confidence - b[i].Confidence) > 3)
                    return false;
            return true;
        }
        public void ForceProcess() => _lastProcessed = DateTime.MinValue;

        private bool HasSceneChanged(byte[] bgra, int w, int h)
        {
            const int S = 16;
            var snap = new byte[S * S];
            int sx = w / S, sy = h / S;

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = (y * sy * w + x * sx) * 4;
                    snap[y * S + x] = (byte)((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3);
                }
            }

            if (_sceneSnap is null) { _sceneSnap = snap; return true; }

            int diff = 0;
            for (int i = 0; i < snap.Length; i++)
                diff += Math.Abs(snap[i] - _sceneSnap[i]);

            _sceneSnap = snap;
            return diff / snap.Length > 8;
        }
    }
}