using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sil.App.ViewModels;

namespace Sil.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Display refresh rate. The simulation may cycle at up to 1 kHz; the shell samples it on a
    /// timer instead of following every step, so a fast run never floods the UI thread.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += OnRefreshTick;
        _timer.Start();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnRefreshTick;
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Refresh();
        }

        this.FindControl<TraceChart>("Chart")?.Refresh();
    }
}
