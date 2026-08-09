using System.Collections.Concurrent;
using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Core;

namespace Aer.Daemon;

public sealed class DoorbellMonitor : IAsyncDisposable, IDisposable
{
    private readonly string _directoryPath;
    private readonly string _targetAdapter;
    private readonly string? _vendorSessionId;
    private readonly Func<string, Task<RoomProjection?>> _loadProjectionAsync;
    private readonly Func<RoomProjection, string?, Task> _broadcastStateAsync;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, byte> _processedAsks = new(StringComparer.Ordinal);

    /// <param name="loadProjectionAsync">
    /// Loads the room's current projection, or null when it cannot be projected. Narrowed from the
    /// whole <c>RoomClient</c> (#445 Phase 4) to the one call this class actually makes: a
    /// <c>RoomClient</c> needs a configuration store, an adapter registry and a
    /// <c>MainWindowViewModel</c> to construct, none of which this monitor's behaviour depends on --
    /// so requiring one meant nothing could drive the watcher, the backup poll or
    /// <see cref="ProcessAskFileAsync"/> in a test, and the test that claimed to hand-rolled the logic
    /// inline instead and passed with this file deleted.
    /// </param>
    public DoorbellMonitor(
        string directoryPath,
        string targetAdapter,
        string? vendorSessionId,
        Func<string, Task<RoomProjection?>> loadProjectionAsync,
        Func<RoomProjection, string?, Task> broadcastStateAsync)
    {
        _directoryPath = directoryPath;
        _targetAdapter = targetAdapter;
        _vendorSessionId = vendorSessionId;
        _loadProjectionAsync = loadProjectionAsync;
        _broadcastStateAsync = broadcastStateAsync;

        var artifactsDir = Path.Combine(directoryPath, ArtifactManager.ArtifactsDirectoryName);
        Directory.CreateDirectory(artifactsDir);

        try
        {
            _watcher = new FileSystemWatcher(artifactsDir, "ask-*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Created += OnAskFileChanged;
            _watcher.Changed += OnAskFileChanged;
            _watcher.Renamed += OnAskFileRenamed;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Fallback to poll loop if FileSystemWatcher fails
        }

        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private void OnAskFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = ProcessAskFileAsync(e.FullPath);
    }

    private void OnAskFileRenamed(object sender, RenamedEventArgs e)
    {
        _ = ProcessAskFileAsync(e.FullPath);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                CheckForAsks();
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 0018 / CLAUDE.md no-swallow: a backup-poll iteration must never die silently --
                // this poll is the guarantee the watcher can miss, and a silent throw here strands
                // the human on every ask the watcher also drops. Log and keep polling.
                Console.Error.WriteLine($"doorbell: backup poll iteration failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void CheckForAsks()
    {
        var artifactsDir = Path.Combine(_directoryPath, ArtifactManager.ArtifactsDirectoryName);
        if (!Directory.Exists(artifactsDir)) return;

        try
        {
            var askFiles = Directory.GetFiles(artifactsDir, "ask-*.json", SearchOption.AllDirectories);
            foreach (var askFile in askFiles)
            {
                _ = ProcessAskFileAsync(askFile);
            }
        }
        catch (Exception ex)
        {
            // 0018 / no-swallow: a directory-enumeration failure is genuinely transient (a file
            // mid-write), but it must be visible, not silent -- a persistent failure here means the
            // backup poll is blind and every watcher-missed ask is stranded.
            Console.Error.WriteLine($"doorbell: enumerating ask files failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task ProcessAskFileAsync(string askFilePath)
    {
        if (string.IsNullOrEmpty(askFilePath) || !File.Exists(askFilePath)) return;

        var fileName = Path.GetFileName(askFilePath);
        if (!fileName.StartsWith("ask-", StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;

        var permissionRequestId = fileName.Substring(4, fileName.Length - 9);
        if (string.IsNullOrWhiteSpace(permissionRequestId)) return;

        if (!_processedAsks.TryAdd(permissionRequestId, 0)) return;

        try
        {
            string jsonText = await File.ReadAllTextAsync(askFilePath).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var outputDir = Path.GetDirectoryName(askFilePath)!;
            var dirName = Path.GetFileName(outputDir);
            var executionIdStr = dirName.StartsWith("execution_", StringComparison.OrdinalIgnoreCase)
                ? dirName.Substring("execution_".Length)
                : dirName;
            var executionId = new ExecutionId(executionIdStr);

            var toolName = root.TryGetProperty("toolName", out var tnElem) && tnElem.ValueKind == JsonValueKind.String
                ? tnElem.GetString()!
                : "unknown";

            string inputJson = "{}";
            if (root.TryGetProperty("inputJson", out var inElem))
            {
                inputJson = inElem.ValueKind == JsonValueKind.String ? inElem.GetString()! : inElem.GetRawText();
            }

            DateTimeOffset askedAt = root.TryGetProperty("askedAt", out var askedAtElem) && askedAtElem.TryGetDateTimeOffset(out var dto)
                ? dto
                : DateTimeOffset.UtcNow;

            var entry = new PendingGateEntry(_directoryPath, outputDir, executionIdStr, askFilePath);
            PendingGateRegistry.Register(permissionRequestId, entry);

            var roomLogPath = Path.Combine(_directoryPath, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);

            await RoomMutationInterface.RaisePermissionAsync(
                _directoryPath,
                reader,
                writer,
                permissionRequestId,
                executionId,
                new StepId(InteractiveSessionMaterializer.DefaultStepId),
                InteractiveSessionMaterializer.DefaultWorkerName,
                _targetAdapter,
                _vendorSessionId ?? "",
                toolName,
                inputJson,
                toolName,
                askedAt).ConfigureAwait(false);

            if (await _loadProjectionAsync(_directoryPath).ConfigureAwait(false) is { } proj)
            {
                await _broadcastStateAsync(proj, _directoryPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 0018 / no-swallow, and this is the LIVE path (not reconciliation): if raising the ask
            // throws, the human never sees the gate and the worker blocks to its timeout. Log it, and
            // drop the dedup mark so a later watcher/poll pass can retry rather than the ask being lost
            // to the in-memory _processedAsks set forever.
            Console.Error.WriteLine($"doorbell: failed to raise permission ask '{permissionRequestId}': {ex.GetType().Name}: {ex.Message}");
            _processedAsks.TryRemove(permissionRequestId, out _);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await _pollTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancel exceptions on shutdown
        }
    }
}
