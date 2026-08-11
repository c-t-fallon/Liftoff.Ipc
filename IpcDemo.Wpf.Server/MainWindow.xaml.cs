using System.Windows;

namespace IpcDemo.Wpf.Server;

public partial class MainWindow : Window
{
    private readonly ServerViewModel _viewModel = new();
    public MainWindow() { InitializeComponent(); DataContext = _viewModel; }
    protected override async void OnClosed(EventArgs e) { await _viewModel.DisposeAsync(); base.OnClosed(e); }
}
