using System.IO;
using System.Windows;

namespace ClientBase;

/// <summary>Диалоги обновления: вопрос пользователю, загрузка, ручная и автоматическая проверка.</summary>
public static class UpdateUi
{
    /// <summary>Тихая установка с индикатором; после неё установщик снова запускает программу.</summary>
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Автоматическая проверка на старте разрешена не чаще раза в сутки.</summary>
    public static bool AutoCheckDue() =>
        AppSettings.LastUpdateCheck is not { } last || DateTime.Now - last >= AutoCheckInterval;

    /// <summary>
    /// Проверка при запуске: раз в сутки, без вмешательства, если интернета нет или новой версии нет.
    /// Возвращает путь к скачанному установщику, если пользователь согласился обновиться.
    /// </summary>
    public static async Task<string?> CheckInBackgroundAsync(Window owner, bool force = false)
    {
        if (!force && !AutoCheckDue()) return null;
        try
        {
            var info = await UpdateChecker.CheckAsync(UpdateChecker.CurrentVersion, TimeSpan.FromSeconds(8));
            if (!force) AppSettings.SetLastUpdateCheck(DateTime.Now);
            return info == null ? null : OfferAndDownload(owner, info);
        }
        catch (Exception)
        {
            return null;   // нет интернета или GitHub недоступен — попробуем при следующем запуске
        }
    }

    /// <summary>Проверка по кнопке: о результате (в том числе об ошибке) сообщаем пользователю.</summary>
    public static async Task<string?> CheckManuallyAsync(Window owner)
    {
        try
        {
            var info = await UpdateChecker.CheckAsync(UpdateChecker.CurrentVersion, TimeSpan.FromSeconds(15));
            AppSettings.SetLastUpdateCheck(DateTime.Now);
            if (info == null)
            {
                MessageBox.Show(owner, $"У вас последняя версия программы ({UpdateChecker.CurrentVersion}).",
                    Confirm.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return OfferAndDownload(owner, info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                "Не удалось проверить обновления. Проверьте подключение к интернету и повторите попытку.\n\n" + ex.Message,
                Confirm.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>Спрашивает, обновляться ли, и скачивает установщик. Null — отказ, отмена или ошибка.</summary>
    public static string? OfferAndDownload(Window owner, UpdateInfo info)
    {
        var notes = info.Notes.Trim();
        if (notes.Length > 700) notes = notes[..700].TrimEnd() + "…";

        var question = $"Доступна новая версия {info.Version} (у вас {UpdateChecker.CurrentVersion}).\n\n" +
                       (notes.Length > 0 ? $"Что нового:\n{notes}\n\n" : "") +
                       "Обновить сейчас? Программа закроется, установится новая версия и запустится снова. " +
                       "Ваши данные не затрагиваются.";
        if (MessageBox.Show(owner, question, "Обновление программы", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return null;

        var target = Path.Combine(Path.GetTempPath(), "ClientBase-update", info.SetupName);
        var window = new DownloadWindow(info, target) { Owner = owner };
        if (window.ShowDialog() == true) return target;

        if (window.ErrorMessage != null)
            MessageBox.Show(owner, "Не удалось скачать обновление:\n" + window.ErrorMessage,
                Confirm.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }
}
