using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace ClientBase;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Русские даты, числа и календарь во всём приложении.
        CultureInfo.DefaultThreadCurrentCulture = Money.Ru;
        CultureInfo.DefaultThreadCurrentUICulture = Money.Ru;
        Thread.CurrentThread.CurrentCulture = Money.Ru;
        Thread.CurrentThread.CurrentUICulture = Money.Ru;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Произошла ошибка:\n\n{args.Exception.Message}", "База клиентов",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            // Данные лежат в папке, которую выбрал пользователь (Настройки → Изменить папку),
            // по умолчанию — «Данные» рядом с программой. Для проверок можно передать --db <путь>.
            var dataDirectory = AppSettings.DataDirectory;
            if (AppSettings.ConfiguredDataDirectory != null && !Directory.Exists(dataDirectory))
            {
                var answer = MessageBox.Show(
                    $"Папка с данными программы сейчас недоступна:\n{dataDirectory}\n\n" +
                    "Возможно, отключён диск или папку переименовали или переместили.\n\n" +
                    "Да — открыть стандартную папку рядом с программой (там будет создана новая пустая база).\n" +
                    "Нет — закрыть программу, чтобы вы подключили диск или вернули папку.",
                    "База клиентов", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    Shutdown();
                    return;
                }
                dataDirectory = AppSettings.DefaultDataDirectory;   // настройку не меняем: когда диск вернётся, всё будет как раньше
            }

            var dbPath = Path.Combine(dataDirectory, DataFolder.DatabaseFile);
            var args = e.Args;
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "--db") dbPath = args[i + 1];

            var db = new Database(dbPath);
            PhotoStore.Root = Path.Combine(db.DataDirectory, "Фото");
            WindowMemory.Directory = db.DataDirectory;
            db.Backup();
            db.Init();
            var main = new MainWindow(db);
            main.Show();

            // Проверка обновлений: при запуске, не чаще раза в сутки. При запуске с --db (проверки разработчика)
            // не выполняется, если явно не указать --check-updates.
            var testRun = args.Contains("--db");
            var forceCheck = args.Contains("--check-updates");
            if (!testRun || forceCheck) _ = CheckUpdatesAtStartupAsync(main, forceCheck);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть базу данных:\n\n{ex.Message}", "База клиентов",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    static async Task CheckUpdatesAtStartupAsync(MainWindow main, bool force)
    {
        var setup = await UpdateUi.CheckInBackgroundAsync(main, force);
        if (setup != null) main.CloseAndRun(setup, UpdateUi.InstallerArguments);
    }
}
