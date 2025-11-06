using Cronos;
using FileServerExtensions = FileSyncService.Extensions.FileServerExtensions;

namespace FileSyncService.Tasks;

public enum FileSyncStatus
{
    Idle,
    Running,
    Ok,
    Error
}

public class SyncStatusInfo
{
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public TimeSpan Duration { get; set; }
    public int UpdatedFiles { get; set; }
    public FileSyncStatus Status { get; set; } = FileSyncStatus.Idle;
    public string? Error { get; set; }
}

public class FileSyncTask : BackgroundService
{
    private readonly FileSyncConfig _cfg;
    private readonly ILogger<FileSyncTask> _logger;
    private readonly HttpClient _client;
    private bool _isSyncOnStartup = false;


    private SyncStatusInfo? _lastStatus;
    private readonly object _statusLock = new();

    public SyncStatusInfo LastStatus
    {
        get
        {
            lock (_statusLock)
            {
                return _lastStatus ?? new SyncStatusInfo
                {
                    LastRun = null,
                    NextRun = null,
                    Duration = TimeSpan.Zero,
                    UpdatedFiles = 0,
                    Status = FileSyncStatus.Idle,
                    Error = null
                };
            }
        }
    }

    public FileSyncTask(FileSyncConfig cfg, ILogger<FileSyncTask> logger, bool isSyncOnStartup)
    {
        _cfg = cfg;
        _logger = logger;
        _isSyncOnStartup = isSyncOnStartup;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var schedules = _cfg.Config.Sync.Schedule
            .Select(s => CronExpression.Parse(s, CronFormat.IncludeSeconds))
            .ToList();

        _logger.LogInformation("🕓 Sync scheduler started with {Count} cron rules", schedules.Count);

        if (_isSyncOnStartup)
        {
            _logger.LogInformation("Sync at start...");
            try
            {
                await SyncAll(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync at start failed");
            }
        }

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRuns = schedules
                .Select(c => c.GetNextOccurrence(now, TimeZoneInfo.Local))
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();

            var nextRun = nextRuns.Count != 0 ? nextRuns.Min() : now.AddHours(12);
            if (nextRun < now)
                nextRun = now.AddMinutes(1);

            _lastStatus = new SyncStatusInfo
            {
                LastRun = _lastStatus?.LastRun,
                NextRun = nextRun,
                Duration = _lastStatus?.Duration ?? TimeSpan.Zero,
                UpdatedFiles = _lastStatus?.UpdatedFiles ?? 0,
                Status = _lastStatus?.Status ?? FileSyncStatus.Idle,
                Error = _lastStatus?.Error
            };

            var delay = nextRun - now;
            _logger.LogInformation(
                "Next sync in {Delay:dd\\.hh\\:mm\\:ss} at {NextRun}",
                delay,
                nextRun.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            );

            await DelayUntil(nextRun, ct);
            if (!ct.IsCancellationRequested)
                await SyncAll(ct);
        }
    }

    public async Task SyncAll(CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var updatedCount = 0;
        UpdateStatus(new SyncStatusInfo
        {
            LastRun = startTime,
            NextRun = null,
            Status = FileSyncStatus.Running,
            UpdatedFiles = 0,
            Duration = TimeSpan.Zero
        });

        _logger.LogInformation("🔄 Starting synchronization...");

        try
        {
            var mirrorBasePath = FileServerExtensions.NormalizePath(_cfg.Files.Mirror!.BasePath);

            foreach (var category in _cfg.Files.Mirror.Data)
            {
                var dir = Path.Combine(mirrorBasePath, category.Key);
                Directory.CreateDirectory(dir);

                foreach (var kv in category.Value)
                {
                    var localFile = Path.Combine(dir, kv.Key);
                    var remoteUrl = kv.Value;

                    if (await SyncFile(localFile, remoteUrl, ct))
                        updatedCount++;
                }
            }

            var duration = DateTime.UtcNow - startTime;
            UpdateStatus(new SyncStatusInfo
            {
                LastRun = startTime,
                NextRun = null,
                Duration = duration,
                UpdatedFiles = updatedCount,
                Status = FileSyncStatus.Ok
            });

            _logger.LogInformation("✅ Synchronization finished. Updated {Count} files.", updatedCount);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            UpdateStatus(new SyncStatusInfo
            {
                LastRun = startTime,
                NextRun = null,
                Duration = duration,
                UpdatedFiles = updatedCount,
                Status = FileSyncStatus.Error,
                Error = ex.Message
            });

            _logger.LogError(ex, "Synchronization failed");
        }
    }

    private async Task<bool> SyncFile(string localPath, string url, CancellationToken ct)
    {
        try
        {
            byte[] bytes;

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (File.Exists(localPath))
                {
                    var info = new FileInfo(localPath);
                    req.Headers.IfModifiedSince = info.LastWriteTimeUtc;
                }

                using (var resp = await _client.SendAsync(req, ct))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                    {
                        _logger.LogInformation("No update for {File}", localPath);
                        return false;
                    }
                    resp.EnsureSuccessStatusCode();
                    bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                }
            }

            await File.WriteAllBytesAsync(localPath, bytes, ct);
            _logger.LogInformation("Updated {File} ({Size} bytes)", localPath, bytes.Length);
            return true;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Timeout while downloading {Url}", url);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Operation is canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing {File}", localPath);
        }
        return false;
    }

    private void UpdateStatus(SyncStatusInfo newStatus)
    {
        lock (_statusLock)
        {
            _lastStatus = newStatus;
        }
    }

    /// <summary>
    /// Safely delays the log until a specified time.
    /// Supports very long intervals and rounds the log to the nearest minute.
    /// </summary>
    private async Task DelayUntil(DateTime nextRun, CancellationToken ct)
    {
        TimeSpan delay = nextRun - DateTime.UtcNow;
        var maxDelay = TimeSpan.FromMilliseconds(int.MaxValue);

        while (delay > TimeSpan.Zero && !ct.IsCancellationRequested)
        {
            var chunk = delay > maxDelay ? maxDelay : delay;

            if (chunk > TimeSpan.FromMinutes(1))
                _logger.LogInformation("⏳ [DelayUntil] Waiting {Delay:dd\\.hh\\:mm\\:ss} until next run...", chunk);

            await Task.Delay(chunk, ct).ContinueWith(_ => { }, CancellationToken.None);

            delay = nextRun - DateTime.UtcNow;
        }
    }
}
