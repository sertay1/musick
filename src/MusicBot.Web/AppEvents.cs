namespace MusicBot;

/// <summary>
/// Static event bus for cross-layer communication (Web → Desktop).
/// The WPF App subscribes to these events.
/// </summary>
public static class AppEvents
{
    public static string Language { get; set; } = "tr";
    public static string CurrentTheme { get; private set; } = "dark";
    public static event Action<string>? OnThemeChanged;
    public static void SetTheme(string theme) { CurrentTheme = theme; OnThemeChanged?.Invoke(theme); }
    public static event Action? OnOpenSpotifyRequested;
    public static bool RequestOpenSpotify() { if (OnOpenSpotifyRequested == null) return false; OnOpenSpotifyRequested.Invoke(); return true; }
    public static event Action? OnOpenLogRequested;
    public static void RequestOpenLog() => OnOpenLogRequested?.Invoke();

    public static event Action? OnOpenLogDirRequested;
    public static void RequestOpenLogDir() => OnOpenLogDirRequested?.Invoke();

    // ── YouTube cookie login (for yt-dlp bot detection bypass) ────────────────

    /// <summary>
    /// Raised when the user wants to log in to YouTube/Google.
    /// The Desktop layer opens a WebView2 window, captures session cookies,
    /// writes them to a Netscape-format cookies.txt file, and posts the path
    /// back to <see cref="OnYouTubeCookiesCaptured"/>.
    /// </summary>
    public static event Action? OnYouTubeLoginRequested;
    public static void RequestYouTubeLogin() => OnYouTubeLoginRequested?.Invoke();

    /// <summary>Raised when the YouTube login window is closed before completing login.</summary>
    public static event Action? OnYouTubeLoginCancelled;
    public static void NotifyYouTubeLoginCancelled() => OnYouTubeLoginCancelled?.Invoke();

    /// <summary>
    /// Raised on startup when the YouTube cookies feature is enabled and a saved session exists.
    /// The Desktop layer silently restores the WebView2 session in the background and rewrites
    /// the cookies.txt file with fresh values.
    /// </summary>
    public static event Action? OnYouTubeSessionRestoreRequested;
    public static void RequestYouTubeSessionRestore() => OnYouTubeSessionRestoreRequested?.Invoke();

    /// <summary>
    /// Raised by the Desktop layer after writing fresh YouTube cookies to disk.
    /// cookiesFilePath: absolute path to the Netscape-format cookies.txt file.
    /// accountLabel: Google account email/name when detectable, else null.
    /// </summary>
    public static event Action<string, string?>? OnYouTubeCookiesCaptured;
    public static void NotifyYouTubeCookiesCaptured(string cookiesFilePath, string? accountLabel)
        => OnYouTubeCookiesCaptured?.Invoke(cookiesFilePath, accountLabel);

    /// <summary>
    /// Raised when the user forgets a platform account.
    /// The Desktop layer must clear any WebView2 session cookies for that platform.
    /// platform: "kick" | "youtube"
    /// </summary>
    public static event Func<string, Task>? OnPlatformAuthForgotten;
    public static Task NotifyPlatformAuthForgotten(string platform)
        => OnPlatformAuthForgotten?.Invoke(platform) ?? Task.CompletedTask;

    /// <summary>
    /// Raised by the API when the user requests a full application shutdown.
    /// The Desktop layer performs a clean exit (stops playback, closes tray, terminates process).
    /// </summary>
    public static event Action? OnShutdownRequested;
    public static void RequestShutdown() => OnShutdownRequested?.Invoke();
}
