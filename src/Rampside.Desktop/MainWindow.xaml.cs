using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Rampside.Desktop.ViewModels;

namespace Rampside.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _viewModel;
    private bool _followLog = true;
    private bool _logScrollPending;
    private double? _logIndent;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        foreach (var entry in viewModel.Log)
        {
            LogDocument.Blocks.Add(CreateLogParagraph(entry));
        }

        viewModel.Log.CollectionChanged += OnLogChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this, ((SolidColorBrush)FindResource("WindowBackground")).Color);
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // The app shuts down explicitly (see App.xaml) once the host and its simulator connection are stopped.
        // A hanging simulator connection must not keep an invisible process alive.
        try
        {
            await Task.WhenAny(_viewModel.DisposeAsync().AsTask(), Task.Delay(ShutdownTimeout));
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>Keeps the log document in step with the view model (lines are added at the end, old ones removed at the start).</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (LogEntry entry in e.NewItems)
                {
                    LogDocument.Blocks.Add(CreateLogParagraph(entry));
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex == 0:
                for (var i = 0; i < e.OldItems.Count && LogDocument.Blocks.FirstBlock is { } first; i++)
                {
                    LogDocument.Blocks.Remove(first);
                }

                break;

            default:
                LogDocument.Blocks.Clear();
                foreach (var entry in _viewModel.Log)
                {
                    LogDocument.Blocks.Add(CreateLogParagraph(entry));
                }

                break;
        }

        if (_followLog)
        {
            ScrollLogToEndLater();
        }
    }

    /// <summary>
    /// One line "HH:mm:ss  text": the time in a dim colour, the text coloured by kind. Wrapped lines are indented
    /// under the text, and copying gives the same "time  text" format as before.
    /// </summary>
    private Paragraph CreateLogParagraph(LogEntry entry)
    {
        var indent = _logIndent ??= MeasureLogText("00:00:00  ");
        var paragraph = new Paragraph { Margin = new Thickness(indent, 0, 0, 1), TextIndent = -indent };
        paragraph.Inlines.Add(new Run($"{entry.TimeText}  ") { Foreground = (Brush)FindResource("TextDisabled") });
        paragraph.Inlines.Add(new Run(entry.Text)
        {
            Foreground = (Brush)FindResource(entry.Kind switch
            {
                LogKind.Warning => "Warn",
                LogKind.Accepted => "Accent",
                LogKind.Rejected => "Bad",
                _ => "TextPrimary",
            }),
        });
        return paragraph;
    }

    private double MeasureLogText(string text) => new FormattedText(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface(LogBox.FontFamily, LogBox.FontStyle, LogBox.FontWeight, LogBox.FontStretch),
        LogBox.FontSize,
        Brushes.White,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Follow new lines unless the user scrolled up to read older ones. Only scrolling by the user
        // changes that; new lines and resizing keep the end in view while following.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _followLog = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
        }
        else if (_followLog)
        {
            ScrollLogToEndLater();
        }
    }

    // Deferred until layout is done: scrolling from inside ScrollChanged re-enters layout.
    private void ScrollLogToEndLater()
    {
        if (_logScrollPending)
        {
            return;
        }

        _logScrollPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _logScrollPending = false;
            LogBox.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
