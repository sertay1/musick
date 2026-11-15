using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog.Events;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MusicBot.Desktop;

public partial class LogViewerWindow : Window
{
    private readonly DispatcherTimer _refresh;
    private bool _dirty = true;
    private long _seen;
    private DateTime _clearedAt = DateTime.MinValue;
    private List<Row> _visible = new();
    private sealed record Row(string Time, string Severity, string Category, string Message, string Details);

    public LogViewerWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        ApplyTheme(MusicBot.AppEvents.CurrentTheme);
    }
    public void ApplyTheme(string theme)
    {
        var light = theme == "light";
        Resources["Surface"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#F8FAFC" : "#15181D"));
        Resources["Panel"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#20252D"));
        Resources["Text"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#172033" : "#E5E7EB"));
    }
    private void Refresh()
    {
        if (!IsVisible || PauseUpdates.IsChecked == true) return;
        var version = LogSink.Version;
        if (!_dirty && version == _seen) return;
        _seen = version; _dirty = false;
        var minimum = LevelFilter.SelectedIndex switch { 1 => LogEventLevel.Warning, 2 => LogEventLevel.Error, 3 => LogEventLevel.Verbose, _ => LogEventLevel.Information };
        var query = SearchBox.Text ?? "";
        _visible = LogSink.Snapshot().Where(e => e.Timestamp > _clearedAt && e.Level >= minimum)
            .Where(e => Technical.IsChecked == true || !(e.Message.Contains("HTTP request", StringComparison.OrdinalIgnoreCase) || e.Message.StartsWith("Received HTTP") || e.Message.StartsWith("End processing HTTP")))
            .Where(e => e.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase) || (e.Exception?.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(e => new Row(e.Timestamp.ToString("HH:mm:ss"), e.Level switch { LogEventLevel.Warning => "UYARI", LogEventLevel.Error or LogEventLevel.Fatal => "HATA", LogEventLevel.Debug or LogEventLevel.Verbose => "AYRINTI", _ => "BİLGİ" },
                e.Message.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ? "Spotify" : e.Message.Contains("Kick", StringComparison.OrdinalIgnoreCase) ? "Kick" : e.Message.Contains("download", StringComparison.OrdinalIgnoreCase) ? "İndirme" : "Uygulama", e.Message, e.Exception?.Message ?? "")).ToList();
        EntriesBox.ItemsSource = _visible;
        StatusText.Text = $"{_visible.Count} kayıt · En fazla 2000 kayıt tutulur";
        if (AutoScroll.IsChecked == true && _visible.Count > 0) EntriesBox.ScrollIntoView(_visible[^1]);
    }
    private void OnFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _dirty = true;
    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _dirty = true;
    private void OnCheckChanged(object sender, RoutedEventArgs e) => _dirty = true;
    private void OnClear(object sender, RoutedEventArgs e) { _clearedAt = DateTime.Now; _dirty = true; Refresh(); }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_visible.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _visible.Select(r => $"[{r.Time}] {r.Severity} · {r.Category}: {r.Message} {r.Details}")));
    }
}
