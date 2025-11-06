using Cronos;

namespace FileSyncService.Tasks;

public class FileSyncTask : BackgroundService
{

    private readonly FileSyncConfig _cfg;
    private readonly ILogger<FileSyncTask> _logger;
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private bool _isSyncOnStartup = false;

    public FileSyncTask(FileSyncConfig cfg, ILogger<FileSyncTask> log, bool isSyncOnStartup)
    {
        _cfg = cfg;
        _logger = log;
        _isSyncOnStartup = isSyncOnStartup;
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
                _logger.LogError("Sync at start failed: {ex}", ex.Message);
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

            var delay = nextRun - now;
            _logger.LogInformation(
                "Next sync in {Delay:dd\\.hh\\:mm\\:ss} at {NextRun}",
                delay,
                nextRun.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            );

            await DelayUntil(nextRun, ct);
            if (!ct.IsCancellationRequested)
            {
                await SyncAll(ct);
            }
        }
    }

    public async Task SyncAll(CancellationToken ct)
    {
        _logger.LogInformation("🔄 Starting synchronization...");
        var mirrorBasePath = Extensions.FileServerExtensions.NormalizePath(_cfg.Files.Mirror!.BasePath);

        foreach (var category in _cfg.Files.Mirror.Data)
        {
            var dir = Path.Combine(mirrorBasePath, category.Key);
            Directory.CreateDirectory(dir);

            foreach (var kv in category.Value)
            {
                var localFile = Path.Combine(dir, kv.Key);
                var remoteUrl = kv.Value;

                await SyncFile(localFile, remoteUrl, ct);
            }
        }

        _logger.LogInformation("✅ Synchronization finished.");
    }

    private async Task SyncFile(string localPath, string url, CancellationToken ct)
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
                        return;
                    }
                    resp.EnsureSuccessStatusCode();
                    bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                }
            }

            await File.WriteAllBytesAsync(localPath, bytes, ct);
            _logger.LogInformation("Updated {File} ({Size} bytes)", localPath, bytes.Length);
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

            await Task.Delay(chunk, ct).ContinueWith(_ => {}, CancellationToken.None);

            delay = nextRun - DateTime.UtcNow;
        }
    }
}
