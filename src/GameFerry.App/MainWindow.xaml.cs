using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using GameFerry.App.ViewModels;

namespace GameFerry.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // Coming back to the window is the moment a card may have been plugged in or pulled out.
    private void OnActivated(object? sender, EventArgs e) => ViewModel?.RefreshDrives();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        if (viewModel.HasWorkInProgress)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                "A transfer is still running. Stop it and close?\n\nThe game being copied right now will be removed from the target; finished games stay.",
                "Transfer in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        viewModel.CancelEverything();
    }

    /// <summary>Space ticks or unticks every highlighted row, like a file manager.</summary>
    private void OnSourceGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.OriginalSource is CheckBox)
            return;

        List<SourceItemViewModel> rows = [.. SourceGrid.SelectedItems.OfType<SourceItemViewModel>().Where(r => r.CanTransfer)];
        if (rows.Count == 0)
            return;

        bool tick = rows.Any(r => !r.IsSelected);
        foreach (SourceItemViewModel row in rows)
            row.IsSelected = tick;
        e.Handled = true;
    }

    /// <summary>
    /// Hovering over a cover shows it at its full size. That picture is only read at this point, so that a
    /// long list doesn't hold every cover in memory twice. Where there is no cover, no tooltip appears.
    /// </summary>
    private void OnCoverToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CoverImage cover, ToolTip: ToolTip { Content: Image picture } }
            && cover.LoadFullSize() is { } fullSize)
        {
            picture.Source = fullSize;
        }
        else
        {
            e.Handled = true;
        }
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
