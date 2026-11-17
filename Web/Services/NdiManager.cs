using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NDI;
using Web.Hubs;

namespace Web.Services
{

    public enum NdiStatus { Disconnected, Connecting, Connected, Reconnecting }
    public sealed class NdiManager : IHostedService, IDisposable
    {
        private readonly IConfiguration _config;
        private readonly ILogger<NdiManager> _logger;
        private readonly IHubContext<RecognitionHub> _hub;

        private NdiReceiver? _receiver;
        private CancellationTokenSource? _captureCts;
        private CancellationTokenSource? _monitorCts;
        private DateTime _connectedAt = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public NdiManager(IConfiguration config, ILogger<NdiManager> logger, IHubContext<RecognitionHub> hub)
        {
            _config = config;
            _logger = logger;
            _hub = hub;
        }

        public NdiStatus Status { get; private set; } = NdiStatus.Disconnected;
        public string? ConnectedSource { get; private set; }
        public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;

        public bool IsAlive => Status == NdiStatus.Connected && (DateTime.UtcNow - LastFrameAt).TotalSeconds < 5;

        public event Action<NdiReceiver.NdiFrame>? FrameReceived;

        public Task StartAsync(CancellationToken ct)
        {
            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var initial = _config["Ndi:SourceName"];
            if (!string.IsNullOrEmpty(initial))
                _ = Task.Run(() => ConnectAsync(initial, _monitorCts.Token));

            _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            DisconnectInternal();
            return Task.CompletedTask;
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
                DisconnectInternal();

                await SetStatusAsync(NdiStatus.Connecting, sourceName, ct);
                _logger.LogInformation("Connecting to NDI source: {Source}", sourceName);

                _receiver = CreateReceiver();
                _receiver.FrameReceived += OnFrame;

                if (!_receiver.Connect(sourceName))
                {
                    await SetStatusAsync(NdiStatus.Disconnected, null, ct);
                    return false;
                }

                ConnectedSource = sourceName;
                LastFrameAt = DateTime.UtcNow;
                _connectedAt = DateTime.UtcNow;
                await SetStatusAsync(NdiStatus.Connected, sourceName, ct);

                _captureCts = new CancellationTokenSource();
                _ = Task.Run(() => _receiver.CaptureLoop(_captureCts.Token), ct);

                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task DisconnectAsync()
        {
            await _lock.WaitAsync();
            try
            {
                DisconnectInternal();
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

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);

                if (Status != NdiStatus.Connected || ConnectedSource is null) continue;

                if (IsAlive)
                {
                    failedAttempts = 0;
                    continue;
                }

                _logger.LogWarning("NDI source lost. Attempting reconnect...");
                await SetStatusAsync(NdiStatus.Reconnecting, ConnectedSource, ct);

                var source = ConnectedSource;
                bool ok = await ConnectAsync(source, ct);

                if (!ok)
                {
                    failedAttempts++;
                    _logger.LogWarning("Recconnect failed (attempt {N})", failedAttempts);

                    if (failedAttempts >= 3)
                    {
                        _logger.LogError("NDI source unavailable. Manual selection required");
                        await SetStatusAsync(NdiStatus.Disconnected, null, ct);
                        failedAttempts = 0;
                    }
                }
                else { failedAttempts = 0; }
            }
        }


        private void DisconnectInternal()
        {
            _captureCts?.Cancel();
            _captureCts?.Dispose();
            _captureCts = null;
            _receiver?.Dispose();
            _receiver = null;
            ConnectedSource = null;
        }

        private async Task SetStatusAsync(
            NdiStatus status, string? source, CancellationToken ct = default)
        {
            Status = status;
            if (status != NdiStatus.Connected) ConnectedSource = source;

            await _hub.Clients.All.SendAsync("NdiStatus", new
            {
                status = status.ToString().ToLower(),
                source
            }, ct);
        }

        private NdiReceiver CreateReceiver() =>
            new(_logger as ILogger<NdiReceiver>
                ?? LoggerFactory.Create(b => b.AddConsole())
                    .CreateLogger<NdiReceiver>(), _config);


        public void Dispose()
        {
            if (_monitorCts != null)
            {
                try { _monitorCts.Cancel(); } catch (ObjectDisposedException) { }
                _monitorCts?.Dispose();
                _monitorCts = null;
            }

            DisconnectInternal();
            _lock.Dispose();
        }
    }
}