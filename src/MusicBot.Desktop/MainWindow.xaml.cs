using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SysWinMsg = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MusicBot.Desktop;

/// <summary>
/// Main browser window. Embeds WebView2 pointing to the local Kestrel server.
/// A loading overlay hides until the first navigation completes.
/// </summary>
public partial class MainWindow : Window
{
    private const string DashboardUrl = "http://127.0.0.1:3050";

    private static string PrepareWebViewProfile()
    {
        var data = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBot"));
        var oldData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBot"));
        var target = Path.GetFullPath(Path.Combine(data, "WebView2"));
        var previous = Path.GetFullPath(Path.Combine(oldData, "WebView2"));
        if (!target.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !previous.StartsWith(oldData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geçersiz tarayıcı profili yolu.");
        if (!Directory.Exists(target) && Directory.Exists(previous) && !target.Equals(previous, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.CreateDirectory(data); Directory.Move(previous, target); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Old browser profile could not be moved; using the independent profile directory"); }
        }
        return target;
    }

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => WebView.Dispose();
        WindowState = WindowState.Maximized;

        // Pin WebView2 user data to AppData so cookies/storage survive Velopack updates
        WebView.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            UserDataFolder = PrepareWebViewProfile()
        };

        // WIC retains the 256px ICO frame; GDI's Icon selection can stop at 128px.
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
            using var stream = File.Exists(path) ? File.OpenRead(path)
                : System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/MusicBot;component/Resources/icon.ico"))!.Stream;
            Icon = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();
            Icon.Freeze();
        }
        catch
        {
            using var gdiIcon = App.CreateIcon(48);
            Icon = Imaging.CreateBitmapSourceFromHIcon(gdiIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateCaptionState();
        ApplyTheme(MusicBot.AppEvents.CurrentTheme);
        UpdateCaptionState();
    }

    public void ApplyTheme(string theme)
    {
        var light = theme == "light";
        Resources["CaptionBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#ECEAE2" : "#171815"));
        Resources["CaptionText"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#65675C" : "#B2B1A7"));
        Resources["CaptionBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#D8D5CA" : "#34352F"));
        Resources["CaptionHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#DFDDD3" : "#282925"));
        var english = MusicBot.AppEvents.Language == "en";
        MinimizeButton.ToolTip = english ? "Minimize" : "Küçült";
        CloseButton.ToolTip = english ? "Close" : "Kapat";
        UpdateCaptionState();
    }

    private void UpdateCaptionState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Keep content clear of the invisible native resize frame when maximized.
        WindowFrame.Padding = maximized ? new Thickness(6) : new Thickness(0);
        MaximizeGlyph.Data = Geometry.Parse(maximized ? "M0,3 H7 V10 H0 Z M3,3 V0 H10 V7 H7" : "M0,0 H10 V10 H0 Z");
        MaximizeButton.ToolTip = MusicBot.AppEvents.Language == "en" ? (maximized ? "Restore" : "Maximize") : (maximized ? "Geri yükle" : "Büyüt");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnCaptionClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            // YouTube requires an HTTPS app identity for desktop embeds, independent of the loopback UI origin.
            WebView.CoreWebView2.AddWebResourceRequestedFilter("https://*.youtube.com/embed/*", CoreWebView2WebResourceContext.Document);
            WebView.CoreWebView2.AddWebResourceRequestedFilter("https://*.youtube-nocookie.com/embed/*", CoreWebView2WebResourceContext.Document);
            WebView.CoreWebView2.WebResourceRequested += (_, request) =>
            {
                if (MusicBot.Services.Player.YouTubeEmbedIdentity.IsEmbedDocument(request.Request.Uri))
                    request.Request.Headers.SetHeader("Referer", MusicBot.Services.Player.YouTubeEmbedIdentity.Referrer);
            };


#if DEBUG
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled    = true;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled               = true;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled               = true;
            WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
#else
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled    = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled               = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled               = false;
            WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

            // Clear HTTP disk cache on every startup so updated assets are always fetched fresh.
            await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage);

            // Remove the loading overlay once the first page finishes loading
            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
                Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);

            WebView.CoreWebView2.Navigate(DashboardUrl);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "WebView2 başlatılamadı");
            SysWinMsg.Show(
                $"Entegre tarayıcı bileşeni (WebView2) başlatılamadı.\n\n{ex.Message}\n\n" +
                $"Sorun devam ederse lütfen Microsoft Edge WebView2 çalışma zamanını kurun veya güncelleyin.",
                "MusicBot — WebView2 Hatası",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Clears the HTTP disk cache and reloads the dashboard. Safe to call from any thread.
    /// </summary>
    public void Reload()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (WebView.CoreWebView2 == null) return;
            LoadingOverlay.Visibility = Visibility.Visible;
#if DEBUG
            await Task.CompletedTask;
#else
            await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage);
#endif
            WebView.CoreWebView2.Navigate(DashboardUrl);
        });
    }

    /// <summary>
    /// Deletes all cookies for the given URIs from the main WebView2 profile.
    /// Used to clear platform OAuth sessions (Kick) when the user forgets their account.
    /// Must be called from the UI thread.
    /// </summary>
    public async Task DeleteCookiesForDomainsAsync(string[] uris)
    {
        if (WebView.CoreWebView2 == null) return;
        foreach (var uri in uris)
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            foreach (var c in cookies)
                WebView.CoreWebView2.CookieManager.DeleteCookie(c);
        }
        Serilog.Log.Information("MainWindow WebView2: cookies deleted for {Uris}", string.Join(", ", uris));
    }
}
