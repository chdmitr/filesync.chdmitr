using System.Text;

namespace FileSyncService;

public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FileSyncConfig _cfg;

    public AuthMiddleware(RequestDelegate next, FileSyncConfig cfg)
    {
        _next = next;
        _cfg = cfg;
    }

    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Только private требует авторизации
        if (path.StartsWith("/private", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                context.Response.Headers["WWW-Authenticate"] = "Basic";
                context.Response.StatusCode = 401;
                return;
            }

            var encoded = authHeader.ToString().Split(' ', 2).Last();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = decoded.Split(':', 2);
            if (parts.Length != 2)
            {
                context.Response.StatusCode = 401;
                return;
            }

            var (username, password) = (parts[0], parts[1]);
            if (!_cfg.Config.Auth.Any(a => a.Username == username && a.Password == password))
            {
                context.Response.StatusCode = 401;
                return;
            }
        }

        await _next(context);
    }
}
