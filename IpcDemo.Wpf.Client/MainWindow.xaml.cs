using System.Windows;

namespace IpcDemo.Wpf.Client;

public partial class MainWindow : Window
{
    private readonly ClientViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_viewModel.ShouldAutoConnect)
        {
            await _viewModel.ConnectCommand.ExecuteAsync(null);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.DisposeAsync();
        base.OnClosed(e);
    }
}
