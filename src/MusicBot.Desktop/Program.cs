using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Serilog;

namespace MusicBot.Desktop;

public static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   private static extern bool  ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]   private static extern bool  SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]   private static extern bool  IsIconic(IntPtr hWnd);

    /// <summary>
    /// Sets a fixed AppUserModelID for this process so Windows and audio routers
    /// (Mixline, etc.) always identify MusicBot as the same application regardless
    /// of executable path, PID, or whether running in debug or release mode.
    /// </summary>
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Held for the lifetime of the process to enforce single-instance
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Installer/update hooks must run before logging, the single-instance lock or UI startup.
        Velopack.VelopackApp.Build().Run();
        try { Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }
        BootTrace($"Main start, args=[{string.Join(' ', args)}]");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            BootTrace($"UnhandledException: {e.ExceptionObject}");

        // ── Single instance guard ─────────────────────────────────────────────
        _instanceMutex = new Mutex(initiallyOwned: true, @"Global\MusicBot.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            var hWnd = FindWindow(null, "Musick") != IntPtr.Zero ? FindWindow(null, "Musick") : FindWindow(null, "MusicBot");
            if (hWnd != IntPtr.Zero)
            {
                if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
                SetForegroundWindow(hWnd);
            }
            _instanceMutex.Dispose();
            return;
        }

        SetCurrentProcessExplicitAppUserModelID("Musick.Player");

        try
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                System.Diagnostics.ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex)
        {
            BootTrace($"Could not raise process priority: {ex.Message}");
        }

        // Hide console window if attached
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd != IntPtr.Zero)
            ShowWindow(consoleHwnd, 0);

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MusicBot", "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(logDir, "musicbot-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(new LogSink())
                .CreateLogger();

            MusicBot.AppInfo.SetVersion(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            Log.Information("MusicBot {Version} (Kick Edition)", MusicBot.AppInfo.Version);

            // ── Web Host ───────────────────────────────────────────────────────
            var builder = WebHost.CreateBuilder(args);

            var userDataDir = builder.Configuration["DataDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MusicBot");
            Directory.CreateDirectory(userDataDir);
            MusicBot.Services.Security.LegacyAccountCleanup.RemoveRetiredProfile(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            MusicBot.Services.Security.CredentialProtection.MigrateFile(Path.Combine(userDataDir, "appsettings.user.json"));
            var userConfigProvider = new PhysicalFileProvider(userDataDir);
            builder.Configuration.AddJsonFile(
                userConfigProvider,
                "appsettings.user.json",
                optional: true,
                reloadOnChange: true);
            Log.Information("Configuración de usuario montada desde {Path}",
                Path.Combine(userDataDir, "appsettings.user.json"));

            var webApp = WebHost.Configure(builder);

            // ── Start Kestrel server ──────────────────────────────────────────
            webApp.StartAsync().GetAwaiter().GetResult();

            // ── Start WPF on the STA thread ───────────────────────────────────
            if (System.Windows.Application.ResourceAssembly == null)
            {
                System.Windows.Application.ResourceAssembly = typeof(App).Assembly;
            }
            var wpfApp = new App { Host = webApp };
            wpfApp.Run();

            // Cleanly stop the host after WPF exits
            webApp.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            var errorMessage = (root != null && root != ex)
                ? $"{ex.Message}\n\nDetay: {root.Message}"
                : ex.Message;

            Log.Fatal(ex, "MusicBot başlatılırken beklenmedik bir hata oluştu");
            System.Windows.MessageBox.Show(
                $"MusicBot başlatılırken kritik bir hata oluştu:\n\n{errorMessage}",
                "MusicBot — Hata",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            Log.CloseAndFlush();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
    }

    private static void BootTrace(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicBot");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "boot.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
