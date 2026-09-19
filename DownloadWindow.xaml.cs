using System.ComponentModel;
using System.Windows;

namespace ClientBase;

/// <summary>Окно с индикатором, пока скачивается установщик новой версии.</summary>
public partial class DownloadWindow : Window
{
    readonly UpdateInfo _info;
    readonly string _target;
    readonly CancellationTokenSource _cancellation = new();
    bool _finished;

    /// <summary>Причина неудачи (null — отменено пользователем или всё прошло успешно).</summary>
    public string? ErrorMessage { get; private set; }

    public DownloadWindow(UpdateInfo info, string target)
    {
        InitializeComponent();
        Branding.Apply(this);
        _info = info;
        _target = target;
        TitleText.Text = $"Загрузка версии {info.Version}…";
        Loaded += async (_, _) => await RunAsync();
    }

    async Task RunAsync()
    {
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total <= 0) return;
            Bar.IsIndeterminate = false;
            Bar.Value = (double)p.Done / p.Total;
            SizeText.Text = $"{p.Done / 1048576.0:0.0} из {p.Total / 1048576.0:0.0} МБ";
        });

        try
        {
            await UpdateChecker.DownloadAsync(_info, _target, progress, _cancellation.Token);
            Finish(true);
        }
        catch (OperationCanceledException)
        {
            Finish(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Finish(false);
        }
    }

    void Finish(bool success)
    {
        _finished = true;
        DialogResult = success;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation.Cancel();

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_finished) return;
        e.Cancel = true;            // закрыть крестиком = отменить загрузку; окно закроется само, когда она остановится
        _cancellation.Cancel();
    }
}
