namespace MusicBot.Services.Security;
public static class LegacyAccountCleanup
{
    public static void RemoveRetiredProfile(string localAppData)
    {
        var root = Path.GetFullPath(Path.Combine(localAppData, "MusicBot"));
        if (Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return;
        var profile = Path.GetFullPath(Path.Combine(root, "YouTubeWebView"));
        if (!profile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var cookies = Path.Combine(root, "youtube_cookies.txt");
            if (File.Exists(cookies) && !File.GetAttributes(cookies).HasFlag(FileAttributes.ReparsePoint)) File.Delete(cookies);
            if (Directory.Exists(profile) && !File.GetAttributes(profile).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(profile, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
