using Microsoft.AspNetCore.SignalR;
using NDI;
using Web.Hubs;

namespace Web.Services
{
    public enum NdiStatus { Disconnected, Connecting, Connected, Reconnecting }

    public sealed class NdiManager : IHostedService, IDisposable
    {
        private const int MonitorIntervalMs = 3000;
        private const int MaxReconnectAttempts = 3;
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CaptureStopTimeout = TimeSpan.FromSeconds(3);

        private readonly IConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<NdiManager> _logger;
        private readonly IHubContext<RecognitionHub> _hub;

        // Serializes connect / disconnect / reconnect and guards the receiver fields below.
        private readonly SemaphoreSlim _lock = new(1, 1);
        private NdiReceiver? _receiver;
        private CancellationTokenSource? _captureCts;
        private Task? _captureTask;
        private CancellationTokenSource? _monitorCts;

        public NdiManager(IConfiguration config, ILoggerFactory loggerFactory, ILogger<NdiManager> logger, IHubContext<RecognitionHub> hub)
        {
            _config = config;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _hub = hub;
        }

        public NdiStatus Status { get; private set; } = NdiStatus.Disconnected;
        public string? ConnectedSource { get; private set; }
        public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;

        public bool IsAlive => Status == NdiStatus.Connected && DateTime.UtcNow - LastFrameAt < StaleAfter;

        public event Action<NdiReceiver.NdiFrame>? FrameReceived;

        public Task StartAsync(CancellationToken ct)
        {
            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var initial = _config["Ndi:SourceName"];
            if (!string.IsNullOrEmpty(initial))
            {
                _logger.LogInformation("Connecting to configured NDI source: {Source}", initial);
                _ = Task.Run(() => ConnectAsync(initial, _monitorCts.Token));
            }

            _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken ct)
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;

            await _lock.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
                Status = NdiStatus.Disconnected;
                ConnectedSource = null;
            }
            finally { _lock.Release(); }
        }

        // Public API

        public List<string> DiscoverSources(int waitMs = 2500)
        {
            using var temp = CreateReceiver();
            return temp.DiscoverSources(waitMs).ToList();
        }

        public async Task<bool> ConnectAsync(string sourceName, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _logger.LogInformation("Connecting to NDI source: {Source}", sourceName);
                await SetStatusAsync(NdiStatus.Connecting, sourceName, ct);

                bool ok = await OpenSourceLockedAsync(sourceName);
                await SetStatusAsync(ok ? NdiStatus.Connected : NdiStatus.Disconnected, ok ? sourceName : null, ct);
                return ok;
            }
            finally { _lock.Release(); }
        }

        public async Task DisconnectAsync()
        {
            await _lock.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
                await SetStatusAsync(NdiStatus.Disconnected, null);
            }
            finally { _lock.Release(); }
        }

        private void OnFrame(NdiReceiver.NdiFrame frame)
        {
            LastFrameAt = DateTime.UtcNow;
            FrameReceived?.Invoke(frame);
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            int failedAttempts = 0;

            try
            {
                while (true)
                {
                    await Task.Delay(MonitorIntervalMs, ct);

                    await _lock.WaitAsync(ct);
                    try
                    {
                        if (Status == NdiStatus.Connected && !IsAlive)
                        {
                            _logger.LogWarning("NDI source lost. Attempting reconnect...");
                            failedAttempts = 0;
                            await SetStatusAsync(NdiStatus.Reconnecting, ConnectedSource, ct);
                        }

                        var source = ConnectedSource;
                        if (Status != NdiStatus.Reconnecting || source is null) continue;

                        // Stays in Reconnecting between attempts, so the loop keeps retrying.
                        if (await OpenSourceLockedAsync(source))
                        {
                            failedAttempts = 0;
                            _logger.LogInformation("Reconnected to NDI source: {Source}", source);
                            await SetStatusAsync(NdiStatus.Connected, source, ct);
                        }
                        else
                        {
                            failedAttempts++;
                            _logger.LogWarning("Reconnect failed (attempt {N} of {Max})", failedAttempts, MaxReconnectAttempts);

                            if (failedAttempts >= MaxReconnectAttempts)
                            {
                                _logger.LogError("NDI source unavailable. Manual selection required");
                                failedAttempts = 0;
                                await SetStatusAsync(NdiStatus.Disconnected, null, ct);
                            }
                        }
                    }
                    finally { _lock.Release(); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down
            }
        }

        // Caller must hold _lock. Returns false, leaving no receiver behind, if the source cannot be opened.
        private async Task<bool> OpenSourceLockedAsync(string sourceName)
        {
            await DisconnectInternalAsync();

            var receiver = CreateReceiver();
            receiver.FrameReceived += OnFrame;

            if (!receiver.Connect(sourceName))
            {
                receiver.Dispose();
                return false;
            }

            var captureCts = new CancellationTokenSource();
            _receiver = receiver;
            _captureCts = captureCts;
            LastFrameAt = DateTime.UtcNow;
            _captureTask = Task.Run(() => receiver.CaptureLoop(captureCts.Token));
            return true;
        }

        // Caller must hold _lock. Stops capture and waits for it to exit before the receiver is disposed.
        private async Task DisconnectInternalAsync()
        {
            var receiver = _receiver;
            var captureCts = _captureCts;
            var captureTask = _captureTask;
            _receiver = null;
            _captureCts = null;
            _captureTask = null;

            captureCts?.Cancel();

            if (captureTask is not null)
            {
                try
                {
                    await captureTask.WaitAsync(CaptureStopTimeout);
                }
                catch (TimeoutException)
                {
                    // Never dispose a receiver the capture loop may still be inside; leak it instead.
                    _logger.LogWarning("Capture loop did not stop within {Timeout}; leaking receiver to avoid use-after-free", CaptureStopTimeout);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Capture loop faulted");
                }
            }

            captureCts?.Dispose();
            receiver?.Dispose();
        }

        private async Task SetStatusAsync(NdiStatus status, string? source, CancellationToken ct = default)
        {
            Status = status;
            ConnectedSource = source;

            await _hub.Clients.All.SendAsync("NdiStatus", new
            {
                status = status.ToString().ToLower(),
                source
            }, ct);
        }

        private NdiReceiver CreateReceiver() =>
            new(_loggerFactory.CreateLogger<NdiReceiver>(), _config);

        public void Dispose()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;

            DisconnectInternalAsync().GetAwaiter().GetResult();
        }
    }
}
