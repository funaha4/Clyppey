using System.Windows;
using Clyppey.App.ViewModels;

namespace Clyppey.App;

/// <summary>Settings-page update actions.</summary>
public partial class MainWindow
{
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.CheckForUpdatesAsync();
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.ApplyUpdateAsync();
        }
    }
}