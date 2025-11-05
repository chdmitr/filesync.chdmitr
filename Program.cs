using System.Net;
using System.Security.Cryptography.X509Certificates;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FileSyncServer;

var yaml = File.ReadAllText("config.yml");
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

var cfg = deserializer.Deserialize<FileSyncConfig>(yaml);

// Подставляем переменные окружения
foreach (var auth in cfg.Config.Auth)
{
    if (auth.Username.StartsWith("env."))
    {
        var envVar = Environment.GetEnvironmentVariable(auth.Username["env.".Length..])
            ?? throw new InvalidOperationException($"Missing environment variable: {auth.Username} (for username)");
        auth.Username = envVar;
    }
    if (auth.Password.StartsWith("env."))
    {
        var envVar = Environment.GetEnvironmentVariable(auth.Username["env.".Length..])
            ?? throw new InvalidOperationException($"Missing environment variable: {auth.Password} (for username)");
        auth.Password = envVar;
    }
}

var builder = WebApplication.CreateBuilder(args);

// Логирование
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opt =>
{
    opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    opt.SingleLine = true;
});

// Путь к лог-файлу и параметры ротации
var logPath = cfg.Config.Log.Path;
if (string.IsNullOrWhiteSpace(logPath))
{
    Directory.CreateDirectory("logs");
    logPath = Path.Combine("logs", "filesync.log");
}
var rotation = cfg.Config.Log.Rotation ?? new FileSyncConfig.ConfigSection.LogSection.RotationSection();
builder.Logging.AddFile(logPath, rotation.MaxSizeMb, rotation.MaxFiles);

builder.WebHost.ConfigureKestrel(opt =>
{
    var cert = X509Certificate2.CreateFromPemFile(
        cfg.Config.Https.CertPathPublic,
        cfg.Config.Https.CertPathPrivate);
    opt.ListenAnyIP(cfg.Config.Https.Port, listen => listen.UseHttps(cert));
});

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<SyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SyncService>());

var app = builder.Build();

app.UseMiddleware<AuthMiddleware>(cfg);
app.MapStaticFiles(cfg);

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("--------------------------------------------------");
logger.LogInformation(" FileSyncServer started");
logger.LogInformation("--------------------------------------------------");
logger.LogInformation("HTTPS port:        {Port}", cfg.Config.Https.Port);
logger.LogInformation("Log file:          {LogPath}", Path.GetFullPath(cfg.Config.Log.Path));
logger.LogInformation("Public directory:  {Path}", FileSyncServer.FileServerExtensions.NormalizePath(cfg.Files.Public));
logger.LogInformation("Private directory: {Path}", FileSyncServer.FileServerExtensions.NormalizePath(cfg.Files.Private));
logger.LogInformation("Mirror root:       {Path}", FileSyncServer.FileServerExtensions.NormalizePath("data/mirror"));
logger.LogInformation("Sync schedule:     {Schedule}", string.Join(", ", cfg.Config.Sync.Schedule));
logger.LogInformation("--------------------------------------------------");

// Ручной триггер только с localhost
app.MapPost("/sync/now", async (HttpContext ctx, SyncService sync, ILogger<Program> log) =>
{
    var remoteIp = ctx.Connection.RemoteIpAddress;
    if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
    {
        log.LogWarning("Unauthorized /sync/now access attempt from {IP}", remoteIp);
        return Results.StatusCode(403);
    }

    log.LogInformation("Manual sync triggered from localhost");
    await sync.SyncAll();
    return Results.Ok(new { status = "started", time = DateTime.UtcNow });
});

// Триггерим при старте
if (args.Contains("--sync-at-start") || args.Contains("-s"))
{
    try
    {
        await app.Services.GetRequiredService<SyncService>().SyncAll();
    }
    catch (Exception ex)
    {
        logger.LogError("Sync at start failed: {ex}", ex.Message);
    }
}

await app.RunAsync();
