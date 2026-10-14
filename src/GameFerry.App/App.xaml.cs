using System.Windows;
using GameFerry.App.ViewModels;

namespace GameFerry.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Optional overrides, mainly for trying things out: --source <folder> --target <drive|folder>
        string? source = Option(e.Args, "--source");
        string? target = Option(e.Args, "--target");

        MainViewModel viewModel = new();
        MainWindow window = new() { DataContext = viewModel };
        window.Show();
        _ = viewModel.InitializeAsync(source, target);
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
