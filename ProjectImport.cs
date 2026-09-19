using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClientBase;

public record ImportedProduct(int? Number, string Name, int Quantity, string? SketchPath, string ModelPath);

/// <param name="Products">Изделия проекта.</param>
/// <param name="UnmatchedSketches">Эскизы, для которых не нашлось изделия.</param>
/// <param name="ProjectModelPath">Общий файл проекта (из него берётся общая фотография), если найден.</param>
public record ImportScan(List<ImportedProduct> Products, int UnmatchedSketches, string? ProjectModelPath);

/// <summary>
/// Чтение папки проекта Базис-Мебельщика. Понимает такую раскладку:
///   3Д\Проект.b3d                            — общий файл проекта (из него берётся общее фото);
///   3Д\Изделия\7) Тумба (450х500х650) 2шт.b3d — файлы изделий («номер) название (ШхГхВ) N шт»);
///   3Д\На раскрой\…                          — файлы для раскроя, в изделия не попадают;
///   Эскизы\7-1 Тумба….jpg                    — эскизы: «номер изделия-номер вида».
/// Более простая раскладка (файлы «N) …» прямо в 3Д) тоже работает.
/// Содержимое .b3d (закрытый формат) не разбирается, берётся только встроенная миниатюра.
/// </summary>
public static class ProjectImport
{
    static readonly Regex ModelName = new(@"^\s*(?<n>\d+)\s*[\)\.]\s*(?<name>.+?)\s*$", RegexOptions.Compiled);
    static readonly Regex SketchName = new(@"^\s*(?<n>\d+)\s*[-=–—]\s*(?<k>\d+)", RegexOptions.Compiled);
    static readonly Regex Qty = new(@"(?<q>\d+)\s*шт", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex QtyTail = new(@"\s*[\(\[]?\s*\d+\s*шт\.?\s*[\)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    /// <summary>Папки, содержимое которых не считается ни изделиями, ни общим проектом.</summary>
    static readonly string[] IgnoredFolders = { "На раскрой", "Фрагмент" };

    const string ItemsFolder = "Изделия";

    public static ImportScan Scan(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var root = Path.GetFullPath(folder);

        var sketches = new Dictionary<int, List<(int View, string Path)>>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", options))
        {
            if (!ImageExt.Contains(Path.GetExtension(file))) continue;
            var m = SketchName.Match(Path.GetFileNameWithoutExtension(file));
            if (!m.Success) continue;
            var n = int.Parse(m.Groups["n"].Value);
            if (!sketches.TryGetValue(n, out var list)) sketches[n] = list = new();
            list.Add((int.Parse(m.Groups["k"].Value), file));
        }

        var models = Directory.EnumerateFiles(root, "*.b3d", options)
            .Where(f => !IsIgnored(root, f))
            .ToList();

        var products = new List<ImportedProduct>();
        var usedNumbers = new HashSet<int>();
        var projectCandidates = new List<string>();
        foreach (var model in models)
        {
            var fileName = Path.GetFileNameWithoutExtension(model);
            var m = ModelName.Match(fileName);
            var inItemsFolder = string.Equals(Path.GetFileName(Path.GetDirectoryName(model)), ItemsFolder,
                StringComparison.CurrentCultureIgnoreCase);

            // Изделие — это файл «N) …» или любой файл из папки «Изделия». Остальное — общий проект.
            if (!m.Success && !inItemsFolder)
            {
                projectCandidates.Add(model);
                continue;
            }

            int? number = m.Success ? int.Parse(m.Groups["n"].Value) : null;
            var title = m.Success ? m.Groups["name"].Value : fileName.Trim();
            var quantity = QuantityIn(title);
            var name = QtyTail.Replace(title, "").Trim();

            string? sketch = null;
            if (number is int num && sketches.TryGetValue(num, out var views))
            {
                usedNumbers.Add(num);
                var ordered = views.OrderBy(v => v.View).ThenBy(v => v.Path, StringComparer.OrdinalIgnoreCase).ToList();
                sketch = ordered[0].Path;
                if (quantity == 1)
                    quantity = ordered.Select(v => QuantityIn(Path.GetFileNameWithoutExtension(v.Path))).FirstOrDefault(q => q > 1, 1);
            }
            products.Add(new ImportedProduct(number, name, quantity, sketch, model));
        }

        products = products
            .OrderBy(p => p.Number ?? int.MaxValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var unmatched = sketches.Where(s => !usedNumbers.Contains(s.Key)).Sum(s => s.Value.Count);
        return new ImportScan(products, unmatched, PickProjectModel(root, projectCandidates));
    }

    static bool IsIgnored(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Take(parts.Length - 1).Any(p => IgnoredFolders.Contains(p, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>Общий файл проекта: тот, что назван как папка проекта, иначе самый «верхний» из оставшихся.</summary>
    static string? PickProjectModel(string root, List<string> candidates)
    {
        if (candidates.Count == 0) return null;
        var folderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return candidates
            .OrderByDescending(f => string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(f => f.Count(ch => ch == Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .First();
    }

    static int QuantityIn(string text)
    {
        var m = Qty.Match(text);
        return m.Success && int.TryParse(m.Groups["q"].Value, out var q) && q > 0 ? q : 1;
    }

    /// <summary>Достаёт PNG-миниатюру, которую Базис вшивает в .b3d. Null, если её нет.</summary>
    public static byte[]? ExtractThumbnail(string modelPath)
    {
        try
        {
            var b = File.ReadAllBytes(modelPath);
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var start = b.AsSpan().IndexOf(signature);
            if (start < 0) return null;

            var pos = start + 8;
            while (pos + 12 <= b.Length)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4));
                var type = Encoding.ASCII.GetString(b, pos + 4, 4);
                var next = (long)pos + 12 + length;
                if (next > b.Length) return null;
                pos = (int)next;
                if (type == "IEND") return b[start..pos];
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
