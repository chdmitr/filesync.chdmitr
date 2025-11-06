using Microsoft.Extensions.FileProviders;

namespace FileSyncService.Extensions;

public static class FileServerExtensions
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AppContext.BaseDirectory;

        // Если путь абсолютный — оставляем
        if (Path.IsPathRooted(path))
            return path;

        // Если относительный — строим относительно каталога приложения
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    public static void MapStaticFiles(this WebApplication app, FileSyncConfig cfg)
    {
        var publicPath = NormalizePath(cfg.Files.Public);
        var privatePath = NormalizePath(cfg.Files.Private);
        var mirrorRoot = NormalizePath(cfg.Files.Mirror.BasePath);

        Directory.CreateDirectory(publicPath);
        Directory.CreateDirectory(privatePath);
        Directory.CreateDirectory(mirrorRoot);

        // --- PUBLIC ---
        var publicProvider = new PhysicalFileProvider(publicPath);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = publicProvider,
            ServeUnknownFileTypes = true,
            RequestPath = "/public"
        });
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = publicProvider,
            RequestPath = "/public"
        });

        // --- PRIVATE ---
        var privateProvider = new PhysicalFileProvider(privatePath);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = privateProvider,
            ServeUnknownFileTypes = true,
            RequestPath = "/private"
        });
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = privateProvider,
            RequestPath = "/private"
        });

        // --- MIRROR ---
        var mirrorProvider = new PhysicalFileProvider(mirrorRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = mirrorProvider,
            ServeUnknownFileTypes = true,
            RequestPath = "/mirror"
        });
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = mirrorProvider,
            RequestPath = "/mirror"
        });
    }
}
