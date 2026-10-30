using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rampside.Desktop.ViewModels;

namespace Rampside.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _viewModel;
    private ScrollViewer? _logViewer;
    private bool _followLog = true;
    private bool _logScrollPending;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Log.CollectionChanged += OnLogChanged;
        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, CopySelectedLogLines,
            (_, e) => e.CanExecute = LogList.SelectedItems.Count > 0));
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

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _followLog)
        {
            ScrollLogToEndLater();
        }
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _logViewer ??= e.OriginalSource as ScrollViewer;

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
            _logViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void CopySelectedLogLines(object sender, ExecutedRoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Cast<LogEntry>()
            .OrderBy(_viewModel.Log.IndexOf)
            .Select(entry => entry.ToString());
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (ExternalException)
        {
            // Clipboard is busy; the user can simply try again.
        }
    }
}
