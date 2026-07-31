using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Windows.Engine;

public interface IBridgeEngine : IDisposable
{
    event Action<EngineStatus>? StatusChanged;
    event Action<ZoneLayout>? LayoutChanged;

    AppConfig Config { get; }
    ZoneLayout Layout { get; }
    IReadOnlyList<DisplaySnapshot> Displays { get; }
    EngineStatus Status { get; }
    Vec2 PhysicalPosition { get; }
    string? CurrentZoneName { get; }

    void Start(AppConfig config);
    void Stop();
    void ApplyConfig(AppConfig config);
    void RebuildLayout(string reason);
}

internal sealed record EngineEnvelope(string Type, string Payload);

internal sealed record ZoneDto(
    string StableId,
    string DisplayName,
    RectDto Pixel,
    RectDto Physical,
    double EffectiveDpi,
    bool IsPrimary,
    double EdidWidthMm,
    double EdidHeightMm);

internal sealed record LiveDto(double X, double Y, string? ZoneName);

internal sealed record RectDto(double X, double Y, double Width, double Height)
{
    public RectD ToRect() => new(X, Y, Width, Height);
    public static RectDto From(RectD rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}

/// <summary>
/// UI-side proxy. The low-level hooks, raw input window, cursor router and drag tracker live in a
/// dedicated child process; only configuration, layout snapshots and diagnostics cross the pipe.
/// A stalled settings window can therefore no longer stall the hook message loop.
/// </summary>
public sealed class EngineProcessProxy : IBridgeEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogSink _log;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _shutdown = new();

    private AppConfig _config = new();
    private ZoneLayout _layout = ZoneLayout.Empty;
    private IReadOnlyList<DisplaySnapshot> _displays = Array.Empty<DisplaySnapshot>();
    private EngineStatus _status = EngineStatus.Stopped;
    private Vec2 _physicalPosition;
    private string? _currentZoneName;
    private bool _disposed;

    public EngineProcessProxy(ILogSink log)
    {
        _log = log;
        string pipeName = $"caya-screenbridge-engine-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the application executable.");

        _process = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = $"--engine-worker {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        }) ?? throw new InvalidOperationException("The hook engine process could not be started.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
        _log.Info("EngineProxy", $"Hook engine started in process {_process.Id}.");
    }

    public event Action<EngineStatus>? StatusChanged;
    public event Action<ZoneLayout>? LayoutChanged;

    public AppConfig Config => _config;
    public ZoneLayout Layout => _layout;
    public IReadOnlyList<DisplaySnapshot> Displays => _displays;
    public EngineStatus Status => _status;
    public Vec2 PhysicalPosition => _physicalPosition;
    public string? CurrentZoneName => _currentZoneName;

    public void Start(AppConfig config)
    {
        _config = config;
        Send("start", config);
    }

    public void Stop() => Send("stop", string.Empty);

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        Send("config", config);
    }

    public void RebuildLayout(string reason) => Send("rebuild", reason);

    private void Send<T>(string type, T payload)
    {
        if (_disposed || !_pipe.IsConnected)
        {
            return;
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string line = JsonSerializer.Serialize(new EngineEnvelope(type, json), JsonOptions);
        lock (_writeLock)
        {
            _writer.WriteLine(line);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested && _pipe.IsConnected)
            {
                string? line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                EngineEnvelope? envelope = JsonSerializer.Deserialize<EngineEnvelope>(line, JsonOptions);
                if (envelope is null)
                {
                    continue;
                }

                switch (envelope.Type)
                {
                    case "status":
                        EngineStatus? status = JsonSerializer.Deserialize<EngineStatus>(envelope.Payload, JsonOptions);
                        if (status is not null)
                        {
                            _status = status;
                            StatusChanged?.Invoke(status);
                        }
                        break;

                    case "live":
                        LiveDto? live = JsonSerializer.Deserialize<LiveDto>(envelope.Payload, JsonOptions);
                        if (live is not null)
                        {
                            _physicalPosition = new Vec2(live.X, live.Y);
                            _currentZoneName = live.ZoneName;
                        }
                        break;

                    case "layout":
                        List<ZoneDto>? zones = JsonSerializer.Deserialize<List<ZoneDto>>(envelope.Payload, JsonOptions);
                        if (zones is not null)
                        {
                            _layout = new ZoneLayout(zones.Select(ToZone).ToArray());
                            _displays = zones.Select(ToSnapshot).ToArray();
                            LayoutChanged?.Invoke(_layout);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("EngineProxy", $"Hook engine connection failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                _status = EngineStatus.Stopped;
                StatusChanged?.Invoke(_status);
            }
        }
    }

    private static DisplayZone ToZone(ZoneDto zone) => new(
        zone.StableId,
        zone.DisplayName,
        zone.Pixel.ToRect(),
        zone.Physical.ToRect(),
        zone.EffectiveDpi,
        zone.IsPrimary);

    private static DisplaySnapshot ToSnapshot(ZoneDto zone) => new()
    {
        DeviceId = zone.StableId,
        StableId = zone.StableId,
        FriendlyName = zone.DisplayName,
        PixelBounds = zone.Pixel.ToRect(),
        WorkArea = zone.Pixel.ToRect(),
        EffectiveDpiX = zone.EffectiveDpi,
        EffectiveDpiY = zone.EffectiveDpi,
        EdidWidthMm = zone.EdidWidthMm,
        EdidHeightMm = zone.EdidHeightMm,
        IsPrimary = zone.IsPrimary,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try { Send("shutdown", string.Empty); } catch { }
        _disposed = true;
        _shutdown.Cancel();
        try { _pipe.Dispose(); } catch { }
        if (!_process.HasExited)
        {
            if (!_process.WaitForExit(1500))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        _process.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>Child-process host for the real engine.</summary>
public static class EngineWorkerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string pipeName, ILogSink log, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var engine = new BridgeEngine(log);
        var outgoing = Channel.CreateUnbounded<EngineEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void QueueStatus(EngineStatus status) => outgoing.Writer.TryWrite(
            new EngineEnvelope("status", JsonSerializer.Serialize(status, JsonOptions)));
        void QueueLayout(ZoneLayout layout)
        {
            var zones = layout.Zones.Select(z =>
            {
                DisplaySnapshot? snapshot = engine.Displays.FirstOrDefault(d => d.StableId == z.StableId);
                return new ZoneDto(
                    z.StableId,
                    z.DisplayName,
                    RectDto.From(z.PixelBounds),
                    RectDto.From(z.PhysicalBounds),
                    z.EffectiveDpi,
                    z.IsPrimary,
                    snapshot?.EdidWidthMm ?? 0,
                    snapshot?.EdidHeightMm ?? 0);
            }).ToList();
            outgoing.Writer.TryWrite(new EngineEnvelope("layout", JsonSerializer.Serialize(zones, JsonOptions)));
        }

        engine.StatusChanged += QueueStatus;
        engine.LayoutChanged += QueueLayout;

        Task writerTask = Task.Run(async () =>
        {
            await foreach (EngineEnvelope envelope in outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
            }
        }, cancellationToken);

        using var liveShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task liveTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                while (await timer.WaitForNextTickAsync(liveShutdown.Token).ConfigureAwait(false))
                {
                    Vec2 position = engine.PhysicalPosition;
                    var live = new LiveDto(position.X, position.Y, engine.CurrentZoneName);
                    outgoing.Writer.TryWrite(new EngineEnvelope("live", JsonSerializer.Serialize(live, JsonOptions)));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, liveShutdown.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                EngineEnvelope? envelope = JsonSerializer.Deserialize<EngineEnvelope>(line, JsonOptions);
                if (envelope is null)
                {
                    continue;
                }

                switch (envelope.Type)
                {
                    case "start":
                        AppConfig? start = JsonSerializer.Deserialize<AppConfig>(envelope.Payload, JsonOptions);
                        if (start is not null)
                        {
                            engine.Start(start);
                            QueueLayout(engine.Layout);
                            QueueStatus(engine.Status);
                        }
                        break;
                    case "config":
                        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(envelope.Payload, JsonOptions);
                        if (config is not null)
                        {
                            engine.ApplyConfig(config);
                        }
                        break;
                    case "rebuild":
                        string reason = JsonSerializer.Deserialize<string>(envelope.Payload, JsonOptions) ?? "remote";
                        engine.RebuildLayout(reason);
                        break;
                    case "stop":
                        engine.Stop();
                        break;
                    case "shutdown":
                        return;
                }
            }
        }
        finally
        {
            engine.StatusChanged -= QueueStatus;
            engine.LayoutChanged -= QueueLayout;
            liveShutdown.Cancel();
            try { await liveTask.ConfigureAwait(false); } catch { }
            outgoing.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); } catch { }
        }
    }
}
