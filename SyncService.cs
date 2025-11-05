using Cronos;

namespace FileSyncServer
{
    public class SyncService : BackgroundService
    {
        private readonly FileSyncConfig _cfg;
        private readonly ILogger<SyncService> _logger;
        private readonly HttpClient _client = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        public SyncService(FileSyncConfig cfg, ILogger<SyncService> log)
        {
            _cfg = cfg;
            _logger = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var schedules = _cfg.Config.Sync.Schedule
                .Select(s => CronExpression.Parse(s))
                .ToList();

            _logger.LogInformation("Sync scheduler started with {Count} cron rules", schedules.Count);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextRuns = schedules
                    .Select(c => c.GetNextOccurrence(now, TimeZoneInfo.Local))
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToList();

                var delay = nextRuns.Count != 0 ? nextRuns.Min() - now : TimeSpan.FromHours(12);
                if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);

                _logger.LogInformation("Next sync in {Delay}", delay);
                await Task.Delay(delay, stoppingToken);

                await SyncAll();
            }
        }

        public async Task SyncAll()
        {
            _logger.LogInformation("🔄 Starting synchronization...");
            var mirrorBasePath = FileServerExtensions.NormalizePath(_cfg.Files.Mirror!.BasePath);

            foreach (var category in _cfg.Files.Mirror.Data)
            {
                var dir = Path.Combine(mirrorBasePath, category.Key);
                Directory.CreateDirectory(dir);

                foreach (var kv in category.Value)
                {
                    var localFile = Path.Combine(dir, kv.Key);
                    var remoteUrl = kv.Value;

                    await SyncFile(localFile, remoteUrl);
                }
            }
            _logger.LogInformation("✅ Synchronization finished.");
        }

        private async Task SyncFile(string localPath, string url)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);

                if (File.Exists(localPath))
                {
                    var info = new FileInfo(localPath);
                    req.Headers.IfModifiedSince = info.LastWriteTimeUtc;
                }

                var resp = await _client.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    _logger.LogInformation("No update for {File}", localPath);
                    return;
                }

                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, bytes);
                _logger.LogInformation("Updated {File} ({Size} bytes)", localPath, bytes.Length);
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Timeout while downloading {Url}", url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing {File}", localPath);
            }
        }
    }
}
