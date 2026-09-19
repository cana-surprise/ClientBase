using System.IO;

namespace ClientBase;

/// <summary>
/// Хранилище фото: все картинки копируются в папку «Данные\Фото» под уникальными именами,
/// поэтому перенос или удаление исходных файлов ничего не ломает, а копирование папки «Данные» = полная резервная копия.
/// </summary>
public static class PhotoStore
{
    public static string Root { get; set; } = "";

    public static string FullPath(string name) => Path.Combine(Root, name);

    public static bool Exists(string name) => name.Length > 0 && File.Exists(FullPath(name));

    static string NewName(string extension)
    {
        Directory.CreateDirectory(Root);
        return Guid.NewGuid().ToString("N") + extension.ToLowerInvariant();
    }

    /// <summary>Копирует файл в хранилище, возвращает имя.</summary>
    public static string Add(string sourceFile)
    {
        var ext = Path.GetExtension(sourceFile);
        var name = NewName(string.IsNullOrEmpty(ext) ? ".jpg" : ext);
        File.Copy(sourceFile, FullPath(name));
        return name;
    }

    public static string AddBytes(byte[] data, string extension)
    {
        var name = NewName(extension);
        File.WriteAllBytes(FullPath(name), data);
        return name;
    }

    /// <summary>Резервирует имя для нового файла (для сохранения картинки из буфера обмена).</summary>
    public static string Reserve(string extension) => NewName(extension);

    public static void Delete(string? name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) return;
        try { File.Delete(FullPath(name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
