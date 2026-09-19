using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClientBase;

public partial class SettingsWindow : Window
{
    const string AppTitle = "База клиентов";

    readonly Database _db;

    public SettingsWindow(Database db)
    {
        InitializeComponent();
        Branding.Apply(this);
        _db = db;
        DataPathText.Text = db.DataDirectory;
        VersionText.Text = $"Версия программы: {UpdateChecker.CurrentVersion}";
        Reload();
    }

    /// <summary>Путь к скачанному установщику новой версии, если пользователь согласился обновиться.</summary>
    public string? UpdateSetupPath { get; private set; }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            UpdateSetupPath = await UpdateUi.CheckManuallyAsync(this);
            if (UpdateSetupPath != null) Close();   // главное окно закроется и запустит установщик
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    void Reload(int? selectId = null)
    {
        var list = _db.GetManufacturers();
        MakersList.ItemsSource = list;
        MakersList.SelectedItem = selectId is int id ? list.FirstOrDefault(m => m.Id == id) : null;
    }

    void MakersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MakersList.SelectedItem is Manufacturer m) NameBox.Text = m.Name;
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Try(() =>
        {
            var id = _db.AddManufacturer(NameBox.Text);
            NameBox.Clear();
            Reload(id);
        });
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (MakersList.SelectedItem is not Manufacturer m)
        {
            Info("Выберите изготовителя в списке, затем измените название в поле ниже.");
            return;
        }
        Try(() =>
        {
            _db.RenameManufacturer(m.Id, NameBox.Text);
            Reload(m.Id);
        });
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MakersList.SelectedItem is not Manufacturer m)
        {
            Info("Выберите изготовителя в списке.");
            return;
        }

        var used = _db.CountManufacturerUsage(m.Id);
        if (used > 0)
        {
            Info($"«{m.Name}» указан в заказах в производство (подзаказов: {used}), поэтому удалить его нельзя.\n\n" +
                 "Его можно переименовать: новое название сразу появится во всех заказах.");
            return;
        }

        if (!Confirm.Delete(this, $"Удалить изготовителя «{m.Name}» из списка?")) return;

        _db.DeleteManufacturer(m.Id);
        NameBox.Clear();
        Reload();
    }

    void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_db.DataDirectory}\"") { UseShellExecute = true });

    /// <summary>Пользователь согласился перезапустить программу, чтобы она начала работать с новой папкой.</summary>
    public bool RestartRequested { get; private set; }

    void ChangeData_Click(object sender, RoutedEventArgs e)
    {
        var current = _db.DataDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка для данных программы",
            InitialDirectory = Directory.Exists(current) ? Path.GetDirectoryName(current) : null,
        };
        if (dialog.ShowDialog(this) != true) return;
        var target = dialog.FolderName;

        var problem = DataFolder.Validate(current, target);
        if (problem.Length > 0)
        {
            Info(problem);
            return;
        }

        // Если в выбранной папке уже лежит база (например, перенесённая раньше), переключаемся на неё, ничего не копируя.
        var useExisting = DataFolder.ContainsDatabase(target);
        var question = useExisting
            ? $"В папке\n{target}\nуже есть база данных программы.\n\nПереключиться на неё? Текущие данные останутся на месте и копироваться не будут."
            : $"Скопировать все данные (база клиентов, фото, резервные копии) в папку\n{target}\nи дальше работать с ней?\n\n" +
              "Старая папка останется на месте: удалите её сами, когда убедитесь, что всё работает.";
        if (MessageBox.Show(this, question, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (!useExisting) DataFolder.Copy(current, target);
            AppSettings.SetDataDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Info($"Не удалось перенести данные:\n{ex.Message}\n\nПрограмма продолжает работать со старой папкой.");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        DataPathText.Text = $"{target}\n(вступит в силу после перезапуска программы)";
        var restart = MessageBox.Show(this,
            "Папка с данными изменена. Чтобы программа начала работать с новой папкой, её нужно перезапустить.\n\n" +
            "Перезапустить сейчас? Всё, что вы измените до перезапуска, попадёт ещё в старую папку.",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (restart == MessageBoxResult.Yes)
        {
            RestartRequested = true;
            Close();
        }
    }

    void Try(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { Info(ex.Message); }
    }

    void Info(string text) => MessageBox.Show(this, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
}
