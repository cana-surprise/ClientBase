using System.IO;
using System.Text.Json;

namespace ClientBase;

/// <summary>
/// Настройка, которая нужна до открытия базы: в какой папке лежат данные программы
/// (база клиентов, фото, резервные копии). Хранится в файле рядом с программой.
/// </summary>
public static class AppSettings
{
    record Data(string? DataDirectory, DateTime? LastUpdateCheck = null);

    /// <summary>
    /// Файл настроек лежит в профиле пользователя (туда всегда можно писать, в том числе когда программа
    /// установлена в «Program Files» и обновляется установщиком).
    /// </summary>
    public static string SettingsFile { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClientBase", "settings.json");

    /// <summary>Файл настроек старых версий — рядом с программой. Читается, если нового ещё нет.</summary>
    public static string LegacySettingsFile { get; set; } = Path.Combine(AppContext.BaseDirectory, "ClientBase.settings.json");

    /// <summary>
    /// Стандартная папка данных. Если рядом с программой уже есть папка «Данные» (программа работала «из папки»),
    /// используется она. Иначе — «Документы\База клиентов»: так данные не пропадают при обновлении и удалении программы.
    /// </summary>
    public static string DefaultDataDirectory { get; set; } = ChooseDefaultDataDirectory();

    static string ChooseDefaultDataDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Данные");
        return Directory.Exists(beside)
            ? beside
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "База клиентов");
    }

    static Data Load()
    {
        try
        {
            var file = File.Exists(SettingsFile) ? SettingsFile
                : File.Exists(LegacySettingsFile) ? LegacySettingsFile
                : null;
            return file == null ? new Data(null) : JsonSerializer.Deserialize<Data>(File.ReadAllText(file)) ?? new Data(null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Data(null);
        }
    }

    static void Save(Data data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(data));
    }

    /// <summary>Папка, выбранная пользователем; null — значит стандартная.</summary>
    public static string? ConfiguredDataDirectory =>
        string.IsNullOrWhiteSpace(Load().DataDirectory) ? null : Load().DataDirectory;

    /// <summary>Где программа хранит данные сейчас.</summary>
    public static string DataDirectory => ConfiguredDataDirectory ?? DefaultDataDirectory;

    public static void SetDataDirectory(string? path) => Save(Load() with { DataDirectory = path });

    /// <summary>Когда в последний раз успешно проверяли обновления (автопроверка идёт раз в сутки).</summary>
    public static DateTime? LastUpdateCheck => Load().LastUpdateCheck;

    public static void SetLastUpdateCheck(DateTime when)
    {
        try { Save(Load() with { LastUpdateCheck = when }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // не мешаем работе программы
    }
}

/// <summary>Проверка и перенос папки с данными.</summary>
public static class DataFolder
{
    public const string DatabaseFile = "clients.db";

    static readonly string[] Files = { DatabaseFile, "window.json" };
    static readonly string[] Folders = { "Фото", "Резервные копии" };

    /// <summary>В папке уже есть база программы.</summary>
    public static bool ContainsDatabase(string folder) => File.Exists(Path.Combine(folder, DatabaseFile));

    /// <summary>Пустая строка — папку можно выбрать; иначе — понятное объяснение, почему нельзя.</summary>
    public static string Validate(string current, string target)
    {
        string cur, tgt;
        try
        {
            cur = Normalize(current);
            tgt = Normalize(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Некорректный путь к папке.";
        }

        if (string.Equals(cur, tgt, StringComparison.OrdinalIgnoreCase))
            return "Это и есть текущая папка с данными.";
        if (tgt.StartsWith(cur + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Нельзя выбрать папку внутри текущей папки с данными. Выберите другое место.";

        try
        {
            // В папку должно быть можно писать: пробуем создать и удалить временный файл.
            Directory.CreateDirectory(tgt);
            var probe = Path.Combine(tgt, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"В эту папку нельзя записывать данные:\n{ex.Message}";
        }
        return "";
    }

    /// <summary>
    /// Копирует данные (базу, фото, резервные копии) в новую папку. Ничего не удаляет и не перезаписывает:
    /// если файл уже есть в новой папке, он остаётся как есть. Исходная папка не меняется.
    /// </summary>
    public static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Files)
        {
            var source = Path.Combine(from, file);
            var destination = Path.Combine(to, file);
            if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination);
        }
        foreach (var folder in Folders)
            CopyDirectory(Path.Combine(from, folder), Path.Combine(to, folder));

        // Проверка: база скопировалась целиком.
        var db = Path.Combine(from, DatabaseFile);
        var copy = Path.Combine(to, DatabaseFile);
        if (File.Exists(db) && (!File.Exists(copy) || new FileInfo(copy).Length != new FileInfo(db).Length))
            throw new IOException("База данных скопировалась не полностью. Старая папка осталась без изменений.");
    }

    static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var destination = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
        foreach (var directory in Directory.GetDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
