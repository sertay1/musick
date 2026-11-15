using System.Net;
namespace MusicBot.Services.Security;
public static class LocalRequestSecurity
{
    public const string Header = "X-Musick-Request";
    public static bool OriginAllowed(string? origin, int port, bool development = false)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost" or "[::1]"
            && (uri.Port == port || development && uri.Port == 5173) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);
    }
    public static bool Callback(PathString path) => path.Value is "/api/auth/google/callback" or "/api/auth/spotify/callback" or "/api/auth/kick/callback";
    public static async Task ProtectAsync(HttpContext context, RequestDelegate next, bool development)
    {
        var request = context.Request;
        var callback = HttpMethods.IsGet(request.Method) && Callback(request.Path);
        var localHost = request.Host.Host is "127.0.0.1" or "localhost" or "[::1]";
        if (!localHost || context.Connection.RemoteIpAddress is {} ip && !IPAddress.IsLoopback(ip))
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        var privateSurface = request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hub");
        if (privateSurface && !callback)
        {
            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0 && !OriginAllowed(origin, request.Host.Port ?? 3050, development)
                || request.Headers["Sec-Fetch-Site"] == "cross-site")
            { context.Response.StatusCode = 403; return; }
            if (request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                && !HttpMethods.IsOptions(request.Method) && request.Headers[Header] != "1")
            { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "İstek uygulama arayüzünden doğrulanmadı." }); return; }
            context.Response.Headers.CacheControl = "no-store";
        }
        if (!request.Path.StartsWithSegments("/overlays"))
        {
            context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://www.youtube.com https://s.ytimg.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; img-src 'self' https: data: blob:; font-src 'self' https://fonts.gstatic.com data:; connect-src 'self' ws://127.0.0.1:* ws://localhost:* https://www.youtube.com https://www.youtube-nocookie.com; frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";
        }
        await next(context);
    }
}
