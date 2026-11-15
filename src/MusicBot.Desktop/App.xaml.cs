using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Data;
using MusicBot.Services;
using SysWin = System.Windows;

// Alias WinForms types to avoid ambiguity with WPF equivalents
using WFFont      = System.Drawing.Font;
using WFFontStyle = System.Drawing.FontStyle;
using WFSysFonts  = System.Drawing.SystemFonts;

namespace MusicBot.Desktop;

/// <summary>
/// WPF application root. Manages the system tray icon, the main browser window,
/// and the log viewer window. Receives the running WebApplication so it can stop
/// it on exit.
/// </summary>
public partial class App : SysWin.Application
{
    // Set by Program.cs before calling Run()
    public WebApplication Host { get; set; } = null!;

    private NotifyIcon         _tray        = null!;
    private MainWindow?        _mainWindow;
    private LogViewerWindow?   _logViewer;
    private MediaKeyHook?       _mediaKeys;
    private bool               _isExiting;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        System.Windows.Forms.Application.EnableVisualStyles();

        // Subscribe to Web → Desktop events
        MusicBot.AppEvents.OnThemeChanged += theme => Dispatcher.Invoke(() => { _logViewer?.ApplyTheme(theme); _mainWindow?.ApplyTheme(theme); });
        MusicBot.AppEvents.OnOpenSpotifyRequested += () => Dispatcher.Invoke(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "spotify:", UseShellExecute = true }));
        MusicBot.AppEvents.OnOpenLogRequested              += () => Dispatcher.Invoke(ShowLogs);
        MusicBot.AppEvents.OnOpenLogDirRequested           += () => Dispatcher.Invoke(OpenLogDir);
        MusicBot.AppEvents.OnPlatformAuthForgotten         += ForgetPlatformSessionAsync;
        MusicBot.AppEvents.OnShutdownRequested             += () => Dispatcher.Invoke(ExitApp);

        _tray      = BuildTray();
        _mediaKeys = new MediaKeyHook(
            Host.Services.GetRequiredService<MusicBot.Services.UserContextManager>(),
            Host.Services.GetRequiredService<MusicBot.Services.CommandRouterService>());
        ShowMainWindow();

        _ = Host.Services.GetRequiredService<MusicBot.Services.Platforms.YouTubeAuthService>()
                         .InitAsync();

        // Auto-open log viewer if configured
        if (Host.Configuration.GetValue<bool>("Desktop:OpenLogOnStart", false))
            ShowLogs();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mediaKeys?.Dispose();
        _tray.Dispose();
        base.OnExit(e);
    }

    // ── Window management ─────────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += (_, ev) =>
            {
                if (_isExiting) return;
                ev.Cancel = true;
                _mainWindow.Hide();
                if (Host.Configuration.GetValue("Desktop:NotificationsEnabled", true))
                    _tray.ShowBalloonTip(4000, "Musick", AppEvents.Language == "en"
                        ? "Musick is running in the system tray. Double-click its icon to reopen it."
                        : "Musick sistem tepsisine alındı ve çalışmaya devam ediyor. Yeniden açmak için simgesine çift tıklayın.", ToolTipIcon.Info);
            };
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.WindowState = SysWin.WindowState.Normal;
    }

    private void ShowLogs()
    {
        _logViewer ??= new LogViewerWindow();
        if (_logViewer.IsVisible) { _logViewer.Hide(); return; }
        _logViewer.ApplyTheme(MusicBot.AppEvents.CurrentTheme);
        _logViewer.Show();
        _logViewer.Activate();
        _logViewer.WindowState = SysWin.WindowState.Normal;
    }

    private static void OpenLogDir()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicBot", "logs");
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private Task ForgetPlatformSessionAsync(string platform)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                switch (platform.ToLowerInvariant())
                {
                    case "kick":
                        // OAuth token is cleared from DB by KickAuthService.
                        // No WebView2 cookies to clear — auth happens in the system browser.
                        break;
                }
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "{Platform}: session clear failed", platform);
                tcs.SetResult();
            }
        });
        return tcs.Task;
    }

    private async void ExitApp()
    {
        _logViewer?.Hide();
        _mainWindow?.Hide();

        _tray.Text = "Musick — Kapatılıyor…";

        // Stop playback first so NAudio releases file handles before cleanup
        try
        {
            var userContext = Host.Services.GetService<MusicBot.Services.UserContextManager>();
            if (userContext != null)
            {
                var services = userContext.GetOrCreate(MusicBot.LocalUser.Id);
                await services.Player.StopAsync();
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: error stopping playback"); }


        // Allow mainWindow.Closing to proceed (handler normally cancels it)
        _isExiting = true;
        _mainWindow?.Close();

        CleanupOldLogs();
        int filesDeleted = await CleanupOrphanedMusicFilesAsync();

        _tray.Visible = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try   { await Host.StopAsync(cts.Token); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: error stopping host"); }

        Shutdown();
    }

    /// <summary>Deletes log files older than 7 days from the logs/ directory.</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MusicBot", "logs");
            if (!Directory.Exists(logsDir)) return;
            var cutoff  = DateTime.UtcNow.AddDays(-7);
            int deleted = 0;
            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); deleted++; }
                    catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: could not delete old log {Path}", file); }
                }
            }
            if (deleted > 0)
                Serilog.Log.Information("Shutdown: deleted {Count} old log file(s)", deleted);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: CleanupOldLogs failed"); }
    }

    /// <summary>
    /// Cleans up music files on shutdown. Returns the number of files deleted.
    /// – SaveDownloads=false: deletes all cached tracks (files + DB rows).
    /// – SaveDownloads=true:  deletes only orphaned files not tracked in the DB.
    /// </summary>
    private async Task<int> CleanupOrphanedMusicFilesAsync()
    {
        int totalDeleted = 0;
        try
        {
            var settings = Host.Services.GetService<Microsoft.Extensions.Options.IOptions<MusicLibrarySettings>>();
            var libPath  = settings?.Value.LibraryPath;
            if (string.IsNullOrEmpty(libPath) || !Directory.Exists(libPath))
            {
                Serilog.Log.Information("Shutdown: music library path not configured or missing, skipping file cleanup");
                return 0;
            }

            var queueSettings = Host.Services.GetService<QueueSettingsService>();
            bool saveDownloads = queueSettings?.SaveDownloads ?? false;
            Serilog.Log.Information("Shutdown: starting file cleanup (SaveDownloads={SaveDownloads}, path={Path})", saveDownloads, libPath);

            using var scope = Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            if (!saveDownloads)
            {
                // Temp mode: delete every file tracked in the DB
                var allTracks = await db.CachedTracks.ToListAsync();
                Serilog.Log.Information("Shutdown: {Count} track(s) in CachedTracks", allTracks.Count);

                foreach (var track in allTracks)
                {
                    try
                    {
                        if (File.Exists(track.FilePath)) { File.Delete(track.FilePath); totalDeleted++; }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Shutdown: could not delete {Path}", track.FilePath);
                    }
                }
                db.CachedTracks.RemoveRange(allTracks);
                await db.SaveChangesAsync();

                // Second pass: catch files whose DB record was already removed mid-session
                foreach (var file in Directory.GetFiles(libPath))
                {
                    try { File.Delete(file); totalDeleted++; }
                    catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: could not delete orphan {Path}", file); }
                }
            }
            else
            {
                // Persistent mode: only remove files that escaped the DB
                var knownPaths = (await db.CachedTracks.Select(t => t.FilePath).ToListAsync())
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(libPath))
                {
                    if (!knownPaths.Contains(file))
                    {
                        try { File.Delete(file); totalDeleted++; }
                        catch (Exception ex) { Serilog.Log.Warning(ex, "Shutdown: could not delete orphan {Path}", file); }
                    }
                }
            }

            Serilog.Log.Information("Shutdown: file cleanup complete — {Deleted} file(s) deleted", totalDeleted);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Shutdown: unhandled error in CleanupOrphanedMusicFilesAsync");
        }
        return totalDeleted;
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        menu.BackColor = Color.FromArgb(22, 27, 34);
        menu.ForeColor = Color.FromArgb(200, 210, 220);

        menu.Items.Add(new ToolStripMenuItem("♫  MusicBot")
        {
            Enabled   = false,
            Font      = new WFFont("Segoe UI", 9f, WFFontStyle.Bold),
            ForeColor = Color.FromArgb(140, 100, 240),
        });
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "🌐  Paneli Aç",           () => ShowMainWindow());
        AddMenuItem(menu, "🔄  Arayüzü Yenile",      () => _mainWindow?.Reload());
        AddMenuItem(menu, "📋  Logları Görüntüle",   () => ShowLogs());
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "✕  Çıkış",                () => ExitApp());
        menu.Opening += (_, _) =>
        {
            var translations = new Dictionary<string, string> { ["🌐  Paneli Aç"] = "🌐  Open panel", ["🔄  Arayüzü Yenile"] = "🔄  Refresh interface", ["📋  Logları Görüntüle"] = "📋  View logs", ["✕  Çıkış"] = "✕  Exit" };
            foreach (var entry in menu.Items.OfType<ToolStripMenuItem>())
                if (entry.Tag is string original) entry.Text = AppEvents.Language == "en" && translations.TryGetValue(original, out var translated) ? translated : original;
        };

        var tray = new NotifyIcon
        {
            Icon             = CreateIcon(),
            Text             = "Musick",
            Visible          = true,
            ContextMenuStrip = menu,
        };

        tray.DoubleClick       += (_, _) => Safe(ShowMainWindow);
        tray.BalloonTipClicked += (_, _) => Safe(ShowMainWindow);

        return tray;
    }

    private static void AddMenuItem(ContextMenuStrip menu, string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Color.FromArgb(200, 210, 220), Tag = text };
        item.Click += (_, _) => Safe(action);
        menu.Items.Add(item);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            SysWin.MessageBox.Show(ex.Message, "Musick — Hata",
                SysWin.MessageBoxButton.OK, SysWin.MessageBoxImage.Warning);
        }
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    internal static Icon CreateIcon(int size = 32)
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath, size, size);
            }
        }
        catch { }

        try
        {
            var resUri = new Uri("pack://application:,,,/MusicBot;component/Resources/icon.ico", UriKind.RelativeOrAbsolute);
            var streamInfo = SysWin.Application.GetResourceStream(resUri);
            if (streamInfo != null)
            {
                using (streamInfo.Stream)
                {
                    return new Icon(streamInfo.Stream, size, size);
                }
            }
        }
        catch { }

        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            using var bg = new SolidBrush(Color.FromArgb(83, 252, 24));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var font  = new WFFont("Segoe UI", 14f, WFFontStyle.Bold);
            using var brush = new SolidBrush(Color.Black);
            var sz = g.MeasureString("M", font);
            g.DrawString("M", font, brush,
                (32 - sz.Width) / 2,
                (32 - sz.Height) / 2);
        }

        var hicon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hicon).Clone();
        DestroyIcon(hicon);
        return icon;
    }
}

// ── Dark context menu theme ────────────────────────────────────────────────────

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? Color.FromArgb(200, 210, 220)
            : Color.FromArgb(80, 90, 100);
        base.OnRenderItemText(e);
    }
}

internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected              => Color.FromArgb(48, 54, 61);
    public override Color MenuItemBorder                => Color.FromArgb(48, 54, 61);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(48, 54, 61);
    public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(48, 54, 61);
    public override Color MenuBorder                    => Color.FromArgb(48, 54, 61);
    public override Color ToolStripDropDownBackground   => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientBegin      => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientMiddle     => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientEnd        => Color.FromArgb(22, 27, 34);
    public override Color SeparatorDark                 => Color.FromArgb(48, 54, 61);
    public override Color SeparatorLight                => Color.FromArgb(48, 54, 61);
}
