using System.ComponentModel;
using System.Windows;
using FlyBrain.App.ViewModels;

namespace FlyBrain.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private bool _shutdownComplete;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        // Release brain sessions and stop the brain process before the window goes away.
        e.Cancel = true;
        IsEnabled = false;
        Title = "FlyBrain - stopping the brain...";
        try
        {
            await _shell.DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
