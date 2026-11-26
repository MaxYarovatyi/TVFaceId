using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Database;
using Core.Inference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NDI
{
    public sealed class MonitorWorker : BackgroundService
    {
        private FaceDetector? _detector;
        private FaceRecognizer? _recognizer;
        private FaceDatabase? _database;

        private readonly NdiReceiver _receiver;
        private readonly ILogger<MonitorWorker> _logger;
        private readonly ILogger<FaceDatabase> _dbLogger;
        private readonly IConfiguration _config;

        private int _frameCount;

        public MonitorWorker(NdiReceiver receiver, ILogger<MonitorWorker> logger, ILogger<FaceDatabase> dbLogger, IConfiguration config)
        {
            _receiver = receiver;
            _logger = logger;
            _dbLogger = dbLogger;
            _config = config;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _receiver.FrameReceived += OnFrame;

            return Task.Run(() =>
            {
                string? sourceName = _config["Ndi:SourceName"];

                var sources = _receiver.DiscoverSources();
                if (sources.Count == 0)
                {
                    _logger.LogError("No NDI sources. Is vMix running with NDI enabled?");
                    return;
                }

                if (!_receiver.Connect(sourceName))
                {
                    _logger.LogError("Failed to connect to NDI source.");
                    return;
                }
                _detector = new FaceDetector("./models/det_500m.onnx");
                _recognizer = new FaceRecognizer("./models/w600k_mbf.onnx");

                _database = new FaceDatabase(_detector, _recognizer, _dbLogger);
                _database.Load("faces");
                _receiver.CaptureLoop(stoppingToken);
            }, stoppingToken);
        }

        private void OnFrame(NdiReceiver.NdiFrame frame)
        {
            _frameCount++;

            if (_frameCount % 30 == 0)
            {
                _logger.LogInformation(
                    "[Frame #{Count}] {W}x{H} | {Kb} KB | ts={Ts}",
                    _frameCount,
                    frame.Width,
                    frame.Height,
                    frame.BgraData.Length / 1024,
                    frame.TimestampTicks
                );

                var faces = _detector!.Detect(frame.BgraData, frame.Width, frame.Height);
                _logger.LogInformation("Detected {Count} face(s)", faces.Count);

                foreach (var face in faces)
                {
                    var (person, sim) = _database.FindBest(face, frame.BgraData.AsSpan(0, frame.DataSize).ToArray(), frame.Width, frame.Height);
                    if (person is not null)
                        _logger.LogInformation("Recognized: {Name} ({Country}) sim={Sim:F3}",
                            person.Name, person.Country, sim);
                    else
                        _logger.LogInformation("Unknown face, best sim={Sim:F3}", sim);
                }
            }
        }

        public override void Dispose()
        {
            _receiver.Dispose();
            base.Dispose();
        }
    }
}