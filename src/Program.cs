using System.Net;
using System.Security.Cryptography.X509Certificates;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FileSyncService;
using FileSyncService.Tasks;
using FileSyncService.Extensions;
using FileServerExtensions = FileSyncService.Extensions.FileServerExtensions;
using Microsoft.Extensions.FileProviders;

const string configPath = "config.yml";

#region Preparatory check

// Проверяем наличие файла конфигурации
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"❌ Configuration file not found: {Path.GetFullPath(configPath)}");
    Console.Error.WriteLine("Please ensure config.yml exists in the working directory.");
    Environment.Exit(1);
}
string yaml;
try
{
    yaml = File.ReadAllText(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ Failed to read configuration file: {ex.Message}");
    Environment.Exit(1);
    throw; // Недостижимо, но нужно для компиляции
}

FileSyncConfig cfg;
try
{
    var deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    cfg = deserializer.Deserialize<FileSyncConfig>(yaml)
        ?? throw new InvalidOperationException("Configuration file is empty or invalid YAML.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ Failed to parse configuration file: {ex.Message}");
    Environment.Exit(1);
    throw;
}

// Проверка наличия сертификатов
var publicCertPath = cfg.Config.Https.CertPathPublic;
var privateCertPath = cfg.Config.Https.CertPathPrivate;
if (string.IsNullOrWhiteSpace(publicCertPath) || string.IsNullOrWhiteSpace(privateCertPath))
{
    Console.Error.WriteLine("❌ Invalid HTTPS certificate paths in config.yml (cert_path_public / cert_path_private).");
    Environment.Exit(1);
}

if (!File.Exists(publicCertPath))
{
    Console.Error.WriteLine($"❌ HTTPS public certificate not found: {Path.GetFullPath(publicCertPath)}");
    Environment.Exit(1);
}

if (!File.Exists(privateCertPath))
{
    Console.Error.WriteLine($"❌ HTTPS private key not found: {Path.GetFullPath(privateCertPath)}");
    Environment.Exit(1);
}

// Локальная функция для подстановки переменной окружения
static string ResolveEnv(string value, string field)
{
    if (!value.StartsWith("env."))
        return value;

    var varName = value["env.".Length..];
    var envVar = Environment.GetEnvironmentVariable(varName);

    if (!string.IsNullOrEmpty(envVar))
        return envVar;

    Console.Error.WriteLine($"❌ Missing environment variable: {varName} (for {field} field)");
    Environment.Exit(1);
    return string.Empty; // Недостижимо, но нужно для компиляции
}

// Подставляем переменные окружения
foreach (var auth in cfg.Config.Auth)
{
    auth.Username = ResolveEnv(auth.Username, "username");
    auth.Password = ResolveEnv(auth.Password, "password");
}
#endregion

#region Builder prepare

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
var rotation = cfg.Config.Log?.Rotation ?? new FileSyncConfig.ConfigSection.LogSection.RotationSection();
builder.Logging.AddFile(logPath, rotation.MaxSizeMb, rotation.MaxFiles);

// HTTPS
builder.WebHost.ConfigureKestrel(opt =>
{
    var cert = X509Certificate2.CreateFromPemFile(publicCertPath, privateCertPath);
    opt.ListenAnyIP(cfg.Config.Https.Port, listen => listen.UseHttps(cert));
});

// Триггерим при старте
var needSyncOnStart = args.Contains("--sync-at-start") || args.Contains("-s");

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<FileSyncTask>(sp, needSyncOnStart));
builder.Services.AddHostedService(provider => provider.GetRequiredService<FileSyncTask>());

#endregion

#region Web app configure

var app = builder.Build();

app.UseMiddleware<AuthMiddleware>(cfg);
app.MapStaticFiles(cfg);

var logger = app.Services.GetRequiredService<ILogger<Program>>();
if (logger.IsEnabled(LogLevel.Information))
{
    logger.LogInformation("--------------------------------------------------");
    logger.LogInformation(" FileSyncService started");
    logger.LogInformation("--------------------------------------------------");
    logger.LogInformation("HTTPS port:        {Port}", cfg.Config.Https.Port);
    logger.LogInformation("Log file:          {LogPath}", Path.GetFullPath(logPath));
    logger.LogInformation("Public directory:  {Path}", FileServerExtensions.NormalizePath(cfg.Files.Public));
    logger.LogInformation("Private directory: {Path}", FileServerExtensions.NormalizePath(cfg.Files.Private));
    logger.LogInformation("Mirror root:       {Path}", FileServerExtensions.NormalizePath("data/mirror"));
    logger.LogInformation("Sync schedule:");
    foreach (var s in cfg.Config.Sync.Schedule)
        logger.LogInformation("  - {Schedule}", s);
    logger.LogInformation("--------------------------------------------------");
}

#endregion

#region Endpoints

// Ручной триггер только с localhost
app.MapPost("/sync/now", async (HttpContext ctx, FileSyncTask sync, ILogger<Program> log, CancellationToken ct) =>
{
    var remoteIp = ctx.Connection.RemoteIpAddress;
    if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
    {
        log.LogWarning("Unauthorized /sync/now access attempt from {IP}", remoteIp);
        return Results.StatusCode(403);
    }

    log.LogInformation("Manual sync triggered from localhost");
    await sync.SyncAll(ct);
    return Results.Ok(new { status = "started", time = DateTime.Now });
});

// Status data serialization
app.MapGet("/meta.json", (FileSyncConfig cfg, FileSyncTask sync) =>
{
    var status = sync.LastStatus;
    return Results.Json(new
    {
        publicPath = FileServerExtensions.NormalizePath(cfg.Files.Public),
        privatePath = FileServerExtensions.NormalizePath(cfg.Files.Private),
        mirrorPath = FileServerExtensions.NormalizePath(cfg.Files.Mirror.BasePath),
        serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        syncStatus = status == null ? null : new
        {
            lastRun = status.LastRun is not null ? DateTime.SpecifyKind(status.LastRun.Value, DateTimeKind.Utc) : (object) "",
            nextRun = status.NextRun is not null ? DateTime.SpecifyKind(status.NextRun.Value, DateTimeKind.Utc) : (object) "",
            duration = status.Duration.ToString(@"hh\:mm\:ss\.fff"),
            updatedFiles = status.UpdatedFiles,
            status = status.Status.ToString(),
            error = status.Error
        }
    });
});

// Main page
var fp = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "Static"));
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = fp,
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fp,
    ServeUnknownFileTypes = true
});

#endregion

await app.RunAsync();
