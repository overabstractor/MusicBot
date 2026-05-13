using System.Windows;
using Markdig;

namespace MusicBot.Desktop;

/// <summary>
/// Shown when a new version is ready. Works in two modes:
///   – Installed (Velopack): "Actualizar ahora" queues the update and triggers a clean shutdown.
///   – Portable (ZIP): "Actualizar ahora" opens the browser to the ZIP download URL.
/// "Más tarde" always closes without doing anything — the update is never forced.
/// </summary>
public partial class UpdateReadyDialog : Window
{
    private readonly Action  _onConfirm;
    private readonly string  _notes;

    public UpdateReadyDialog(string version, string notes, Action onConfirm)
    {
        InitializeComponent();
        _onConfirm = onConfirm;
        _notes     = notes;

        VersionLabel.Text = $"MusicBot v{version} lista para instalar";

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await NotesWebView.EnsureCoreWebView2Async();

            // Disable navigation so links don't open inside the dialog
            NotesWebView.CoreWebView2.NewWindowRequested    += (_, ev) => ev.Handled = true;
            NotesWebView.CoreWebView2.NavigationStarting    += (_, ev) =>
            {
                if (ev.Uri != "about:blank" && !ev.Uri.StartsWith("data:"))
                    ev.Cancel = true;
            };

            NotesWebView.CoreWebView2.NavigateToString(BuildHtml(_notes));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "UpdateReadyDialog: no se pudo inicializar WebView2");
        }
    }

    private static string BuildHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var body     = Markdown.ToHtml(string.IsNullOrWhiteSpace(markdown)
            ? "_Sin notas de versión disponibles._"
            : markdown, pipeline);

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8"/>
            <style>
              * { box-sizing: border-box; margin: 0; padding: 0; }
              body {
                font-family: 'Segoe UI', system-ui, sans-serif;
                font-size: 13px;
                line-height: 1.6;
                color: #334155;
                background: #ffffff;
                padding: 14px 16px;
              }
              h1,h2,h3,h4 {
                color: #0f172a;
                font-weight: 600;
                margin: 14px 0 6px;
              }
              h1 { font-size: 16px; }
              h2 { font-size: 14px; }
              h3 { font-size: 13px; }
              p  { margin: 6px 0; }
              ul, ol { padding-left: 20px; margin: 6px 0; }
              li { margin: 3px 0; }
              code {
                font-family: 'Cascadia Code', Consolas, monospace;
                font-size: 12px;
                background: #f1f5f9;
                border: 1px solid #e2e8f0;
                border-radius: 4px;
                padding: 1px 5px;
                color: #7c3aed;
              }
              pre {
                background: #f8fafc;
                border: 1px solid #e2e8f0;
                border-radius: 6px;
                padding: 10px 12px;
                overflow-x: auto;
                margin: 8px 0;
              }
              pre code {
                background: none;
                border: none;
                padding: 0;
                color: #334155;
              }
              a { color: #8b5cf6; text-decoration: none; }
              hr { border: none; border-top: 1px solid #e2e8f0; margin: 10px 0; }
              blockquote {
                border-left: 3px solid #8b5cf6;
                margin: 8px 0;
                padding: 4px 12px;
                color: #64748b;
              }
            </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Close();
        _onConfirm();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
