using System.Diagnostics;
using System.Text;
namespace MusicBot.Services;

public static class UpdateInstallerLauncher
{
    public static string WaitScript(string installer, int processId)
    {
        if (processId <= 0 || !Path.IsPathFullyQualified(installer)) throw new ArgumentException("Geçersiz kurulum isteği.");
        var literal = installer.Replace("'", "''");
        return $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; Start-Process -FilePath '{literal}'";
    }
    public static void StartAfterExit(string installer, int processId)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(WaitScript(installer, processId))) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Güncelleme bekleyicisi başlatılamadı.");
    }
}
