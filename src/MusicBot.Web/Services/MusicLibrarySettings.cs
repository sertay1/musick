namespace MusicBot.Services;

public class MusicLibrarySettings
{
    private string _libraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicBot", "music-library");

    /// <summary>Directory where downloaded audio files are stored. Resolves relative paths to %LocalAppData%\MusicBot\.</summary>
    public string LibraryPath
    {
        get => _libraryPath;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _libraryPath = Path.IsPathRooted(value)
                ? value
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBot", value);
        }
    }
    /// <summary>Directory where yt-dlp/ffmpeg/deno binaries are stored. Set by WebHost to %AppData%\MusicBot\tools\ so they survive Velopack updates.</summary>
    public string ToolsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicBot", "tools");
    /// <summary>Path to the yt-dlp executable. Must be in PATH or provide full path.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";
    /// <summary>Path to the ffmpeg executable. Must be in PATH or provide full path.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    /// <summary>
    /// When true, yt-dlp downloads the native M4A stream from YouTube without invoking ffmpeg.
    /// NAudio/MediaFoundationReader plays M4A natively on Windows 10/11.
    /// Set to false to convert to MP3 via ffmpeg instead.
    /// </summary>
    public bool UseNativeAudioFormat { get; set; } = true;
}
