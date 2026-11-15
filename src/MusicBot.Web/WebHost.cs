using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.OpenApi.Models;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using MusicBot.Services.Library;
using MusicBot.Services.Metadata;
using MusicBot.Services.Platforms;
using MusicBot.Services.Player;
using MusicBot.Services.Spotify;
using Scalar.AspNetCore;

namespace MusicBot;

/// <summary>
/// Builds and configures the ASP.NET Core web host.
/// Split into CreateBuilder + Configure so callers (e.g. MusicBot.Desktop)
/// can register their own services between the two steps.
/// </summary>
public static class WebHost
{
    /// <summary>
    /// Creates the WebApplicationBuilder with all application services registered.
    /// The caller may add/replace services before calling <see cref="Configure"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var options = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.Host.UseSerilog();
        // Publisher-owned native OAuth client. Never loaded from browser/user input.
        builder.Configuration.AddJsonFile("google-oauth.desktop.json", optional: true, reloadOnChange: false);

        // Explicitly load user secrets from MusicBot.Web assembly regardless of environment.
        // Needed because the entry point is MusicBot.Desktop (no UserSecretsId there).
#if DEBUG
        builder.Configuration.AddUserSecrets(typeof(WebHost).Assembly, optional: true);
#endif

        // ── User data directory (persists across Velopack updates) ───────────
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicBot");
        var toolsDir = Path.Combine(dataDir, "tools");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(toolsDir);
        builder.Configuration["DataDirectory"] = dataDir;
        builder.Configuration["ConnectionStrings:DefaultConnection"] =
            $"Data Source={Path.Combine(dataDir, "musicbot.db")}";
        builder.Configuration["MusicLibrary:ToolsDirectory"] = toolsDir;
        // NOTE: User-overridable settings file (appsettings.user.json) is registered by
        // the Desktop entry point in Program.cs — not here — because content-root path
        // resolution and the decision of "where user-mutable state lives" belong to the
        // host application, not to this shared web library.

        // ── Configuration ────────────────────────────────────────────────────
        builder.Services.Configure<SpotifySettings>(builder.Configuration.GetSection("Spotify"));
        builder.Services.Configure<KickSettings>(builder.Configuration.GetSection("Kick"));
        builder.Services.PostConfigure<SpotifySettings>(s => s.ClientSecret = MusicBot.Services.Security.TokenProtection.Unprotect(s.ClientSecret));
        builder.Services.PostConfigure<KickSettings>(s => s.ClientSecret = MusicBot.Services.Security.TokenProtection.Unprotect(s.ClientSecret));
        builder.Services.Configure<MusicLibrarySettings>(builder.Configuration.GetSection("MusicLibrary"));

        // ── Database ─────────────────────────────────────────────────────────
        builder.Services.AddDbContext<MusicBotDbContext>(o =>
            o.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                        ?? "Data Source=musicbot.db"));

        // ── Core services ────────────────────────────────────────────────────
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient("itunes");

        builder.Services.AddSingleton<IMetadataService, ItunesMetadataService>();
        builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
        builder.Services.AddSingleton<YtDlpDownloaderService>();
        builder.Services.AddHostedService<YtDlpSetupService>();

        builder.Services.AddSingleton<UserContextManager>();
        builder.Services.AddSingleton<MusicBot.Services.Commands.CommandConfigService>();
        builder.Services.AddSingleton<KickVoteService>();
        builder.Services.AddSingleton<CommandRouterService>();

        // Restore the queue before playback and platform services can modify it.
        builder.Services.AddHostedService<QueuePersistenceService>();
        builder.Services.AddSingleton<PlaybackSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PlaybackSyncService>());
        builder.Services.AddHostedService<SignalRBroadcastService>();
        builder.Services.AddSingleton<IKickChatReader, KickChatSpyReader>();
        builder.Services.AddSingleton<PlatformConnectionManager>();
        builder.Services.AddSingleton<KickAuthService>();
        builder.Services.AddSingleton<YouTubeOAuthService>();
        builder.Services.AddSingleton<YouTubeAuthService>();
        builder.Services.AddHostedService<PlatformAutoConnectService>();
        builder.Services.AddHostedService<AudioDeviceRestoreService>();
        builder.Services.AddHostedService<MusicBot.Services.Player.SpotifyAudioRestoreService>();
        builder.Services.AddSingleton<IntegrationStatusTracker>();
        builder.Services.AddSingleton<QueueSettingsService>();
        builder.Services.AddSingleton<BannedSongService>();
        builder.Services.AddSingleton<AutoQueueService>();
        builder.Services.AddSingleton<PlaylistLibraryService>();
        builder.Services.AddSingleton<ChatResponseService>();
        builder.Services.AddSingleton<ChatActivityTracker>();
        builder.Services.AddSingleton<PresenceCheckService>();
        builder.Services.AddSingleton<TickerMessageService>();
        builder.Services.AddHostedService<TickerChatService>();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.CamelCase);

        // AddApplicationPart ensures MVC discovers controllers in MusicBot.Web.dll
        // even when Assembly.GetEntryAssembly() points to MusicBot.Desktop.dll.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(WebHost).Assembly)
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy        = System.Text.Json.JsonNamingPolicy.CamelCase;
                o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            });

        // ── OpenAPI ──────────────────────────────────────────────────────────
        builder.Services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo { Title = "MusicBot API", Version = "v1" };
                return Task.CompletedTask;
            });
        });

        // Enforce local loopback binding (127.0.0.1:3050 only) for security
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(System.Net.IPAddress.Loopback, 3050);
        });

        // ── CORS ─────────────────────────────────────────────────────────────
        builder.Services.AddCors(o =>
        {
            bool IsAllowedOrigin(string origin) => MusicBot.Services.Security.LocalRequestSecurity.OriginAllowed(origin, 3050, builder.Environment.IsDevelopment());

            o.AddDefaultPolicy(p =>
                p.SetIsOriginAllowed(IsAllowedOrigin)
                 .AllowAnyMethod()
                 .AllowAnyHeader());

            o.AddPolicy("SignalR", p =>
                p.SetIsOriginAllowed(IsAllowedOrigin)
                 .AllowAnyMethod()
                 .AllowAnyHeader()
                 .AllowCredentials());
        });

        return builder;
    }

    /// <summary>
    /// Builds the WebApplication from the given builder and configures the middleware pipeline.
    /// </summary>
    public static WebApplication Configure(WebApplicationBuilder builder)
    {
        var app = builder.Build();

        // ── DB init + seed local user ────────────────────────────────────────
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            db.Database.EnsureCreated();
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PersistedQueueItems ADD COLUMN PreparedForSpotify INTEGER NOT NULL DEFAULT 0"); } catch { }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN SourceUrl TEXT NULL"); } catch { }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN SyncError TEXT NULL"); } catch { }

            // Safe migrations for existing databases
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN IsSystem INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN PinOrder INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PersistedQueueItems ADD COLUMN IsPlaylistItem INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN SortOrder INTEGER NULL"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN AudioDeviceId TEXT NULL"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN AudioVolume REAL NULL"); }
            catch { /* column already exists */ }

            if (!db.Users.Any())
            {
                db.Users.Add(new AppUser
                {
                    Id           = LocalUser.Id,
                    Username     = LocalUser.Username,
                    Slug         = LocalUser.Slug,
                    PasswordHash = "",
                    OverlayToken = LocalUser.OverlayToken,
                });
                db.SaveChanges();
            }
        }

        // Seed system playlists (Liked Songs)
        var plSvc = app.Services.GetRequiredService<PlaylistLibraryService>();
        plSvc.EnsureSystemPlaylistsAsync().GetAwaiter().GetResult();

        app.Use((context, next) => MusicBot.Services.Security.LocalRequestSecurity.ProtectAsync(context, next, app.Environment.IsDevelopment()));
        app.UseCors();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (SpotifyRateLimitException ex) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, retryAfterSeconds = ex.RetryAfterSeconds });
            }
            catch (SpotifyPlayerCommandException ex) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
            {
                context.Response.StatusCode=ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new { error=ex.Message });
            }
            catch (HttpRequestException ex) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
            {
                app.Logger.LogWarning(ex, "Remote music service request failed");
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new { error = "Müzik servisi isteği tamamlayamadı. Bağlantınızı kontrol edip tekrar deneyin." });
            }
            catch (InvalidOperationException ex) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
            {
                app.Logger.LogWarning(ex, "Application request could not be completed");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.File.Name;
                // Vite hashes JS/CSS asset names — cache them indefinitely
                if ((path.EndsWith(".js") || path.EndsWith(".css")) &&
                    System.Text.RegularExpressions.Regex.IsMatch(path, @"-[A-Za-z0-9]{8,}\.(js|css)$"))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
                else
                {
                    // index.html and everything else: always revalidate
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                }
            }
        });

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(o => o.WithTitle("Musick API").WithTheme(ScalarTheme.DeepSpace));
        }

        app.MapControllers();
        app.MapHub<OverlayHub>("/hub/overlay").RequireCors("SignalR");
        app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
        app.MapFallbackToFile("index.html");

        return app;
    }
}
