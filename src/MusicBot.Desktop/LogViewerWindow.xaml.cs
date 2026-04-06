using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Serilog.Events;
using WpfColor = System.Windows.Media.Color;

namespace MusicBot.Desktop;

/// <summary>
/// WPF log viewer window. Subscribes to LogSink.OnEntry and displays
/// color-coded log entries in a RichTextBox. Hides instead of closing.
/// </summary>
public partial class LogViewerWindow : Window
{
    private LogEventLevel _minLevel = LogEventLevel.Verbose;

    private static readonly SolidColorBrush DimBrush  = new(WpfColor.FromRgb(110, 120, 135));
    private static readonly SolidColorBrush FgBrush   = new(WpfColor.FromRgb(200, 210, 220));
    private static readonly SolidColorBrush WarnBrush = new(WpfColor.FromRgb(245, 158,  11));
    private static readonly SolidColorBrush ErrBrush  = new(WpfColor.FromRgb(239,  68,  68));
    private static readonly SolidColorBrush VerbBrush = new(WpfColor.FromRgb( 80,  92, 110));

    public LogViewerWindow()
    {
        InitializeComponent();
        LogSink.OnEntry += OnLogEntry;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    // ── Log entry handler ────────────────────────────────────────────────────

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < _minLevel) return;

        Dispatcher.BeginInvoke(() =>
        {
            var color = entry.Level switch
            {
                LogEventLevel.Warning                        => WarnBrush,
                LogEventLevel.Error or LogEventLevel.Fatal   => ErrBrush,
                LogEventLevel.Debug or LogEventLevel.Verbose => VerbBrush,
                _                                            => FgBrush,
            };

            var tag = entry.Level switch
            {
                LogEventLevel.Verbose => "VRB",
                LogEventLevel.Debug   => "DBG",
                LogEventLevel.Warning => "WRN",
                LogEventLevel.Error   => "ERR",
                LogEventLevel.Fatal   => "FTL",
                _                     => "INF",
            };

            var para = new Paragraph { Margin = new Thickness(0) };
            para.Inlines.Add(new Run($"[{entry.Timestamp:HH:mm:ss}] [{tag}] ") { Foreground = DimBrush });
            para.Inlines.Add(new Run(entry.Message)                             { Foreground = color });

            if (entry.Exception != null)
            {
                var ex = entry.Exception;
                // Walk inner exceptions, collecting their messages
                var parts = new List<string>();
                while (ex != null)
                {
                    var firstLine = ex.Message.Split('\n')[0].Trim();
                    if (!string.IsNullOrEmpty(firstLine))
                        parts.Add(firstLine);
                    ex = ex.InnerException;
                }
                var exMsg = string.Join(" → ", parts);
                para.Inlines.Add(new Run($"\n    ↳ {exMsg}") { Foreground = new SolidColorBrush(WpfColor.FromArgb(180, 239, 68, 68)) });
            }

            LogBox.Document.Blocks.Add(para);

            // Cap at 3 000 paragraphs
            while (LogBox.Document.Blocks.Count > 3_000)
                LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);

            LineCount.Text = $"{LogBox.Document.Blocks.Count} líneas";
            LogBox.ScrollToEnd();
        });
    }

    // ── Toolbar actions ───────────────────────────────────────────────────────

    private void LevelFilter_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _minLevel = LevelFilter.SelectedIndex switch
        {
            1 => LogEventLevel.Debug,
            2 => LogEventLevel.Information,
            3 => LogEventLevel.Warning,
            4 => LogEventLevel.Error,
            _ => LogEventLevel.Verbose,
        };
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        LineCount.Text = "0 líneas";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var block in LogBox.Document.Blocks.OfType<Paragraph>())
        {
            foreach (var run in block.Inlines.OfType<Run>())
                sb.Append(run.Text);
            sb.AppendLine();
        }
        if (sb.Length > 0)
            System.Windows.Clipboard.SetText(sb.ToString());
    }

    protected override void OnClosed(EventArgs e)
    {
        LogSink.OnEntry -= OnLogEntry;
        base.OnClosed(e);
    }
}
