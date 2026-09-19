using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace ClientBase;

/// <summary>Строка из таблицы: название и количество с единицей («80 шт»); у материалов количество может быть пустым.</summary>
public record ImportedMaterial(string Name, string Amount);

/// <param name="Materials">Материалы (плиты, декоры) — перечислены правее подписи «Материалы» в шапке таблицы.</param>
/// <param name="Hardware">Фурнитура — таблица «Наименование / Ед. изм. / К-во» под шапкой.</param>
public record FurnitureTable(List<ImportedMaterial> Materials, List<ImportedMaterial> Hardware);

/// <summary>
/// Таблица «Расчёт комплектующих», которую Базис-Мебельщик выгружает в папку «Фурник» (.xls и .xlsx):
///   • в шапке подпись «Материалы», а правее (одним столбцом вниз или одной строкой) — сами материалы;
///   • ниже таблица фурнитуры со столбцами «Наименование материала», «Ед. изм.», «К-во».
/// </summary>
public static class MaterialImport
{
    static readonly string[] SpreadsheetExt = { ".xls", ".xlsx" };

    /// <summary>Сколько подряд пустых строк допускается внутри перечня материалов.</summary>
    const int MaterialGapRows = 2;

    /// <summary>Ищет таблицы в папке «Фурник» проекта.</summary>
    public static List<string> FindSpreadsheets(string projectFolder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(projectFolder, "*.*", options)
            .Where(f => SpreadsheetExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .Where(f => Path.GetRelativePath(projectFolder, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => part.Contains("Фурник", StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static FurnitureTable Read(string projectFolder)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // нужен для старых .xls

        var materials = new List<ImportedMaterial>();
        var hardware = new List<ImportedMaterial>();
        foreach (var file in FindSpreadsheets(projectFolder))
        {
            // FileShare.ReadWrite: файл может быть открыт в Excel.
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            do
            {
                var rows = LoadRows(reader);
                var tableRow = FindTableHeader(rows, out var nameCol, out var unitCol, out var qtyCol);
                ReadMaterials(rows, tableRow < 0 ? rows.Count : tableRow, materials);
                if (tableRow >= 0) ReadHardware(rows, tableRow, nameCol, unitCol, qtyCol, hardware);
            } while (reader.NextResult());
        }
        return new FurnitureTable(materials, hardware);
    }

    static List<object?[]> LoadRows(IExcelDataReader reader)
    {
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var c = 0; c < row.Length; c++) row[c] = reader.GetValue(c);
            rows.Add(row);
        }
        return rows;
    }

    // ---------- таблица фурнитуры ----------

    /// <summary>Строка-заголовок: «Наименование …», «Ед. изм.», «К-во». Возвращает индекс строки или -1.</summary>
    static int FindTableHeader(List<object?[]> rows, out int nameCol, out int unitCol, out int qtyCol)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            int name = -1, unit = -1, qty = -1;
            for (var c = 0; c < rows[r].Length; c++)
            {
                var text = Text(rows[r], c).ToLowerInvariant();
                if (text.StartsWith("наименование") || text.StartsWith("название")) name = c;
                else if (text.StartsWith("ед")) unit = c;
                else if (text.StartsWith("к-во") || text.StartsWith("кол")) qty = c;
            }
            if (name >= 0 && qty >= 0)
            {
                (nameCol, unitCol, qtyCol) = (name, unit, qty);
                return r;
            }
        }
        (nameCol, unitCol, qtyCol) = (-1, -1, -1);
        return -1;
    }

    static void ReadHardware(List<object?[]> rows, int headerRow, int nameCol, int unitCol, int qtyCol,
        List<ImportedMaterial> result)
    {
        for (var r = headerRow + 1; r < rows.Count; r++)
        {
            var name = Clean(Text(rows[r], nameCol));
            if (name.Length == 0 || !TryNumber(Cell(rows[r], qtyCol), out var qty) || qty <= 0) continue;

            var unit = unitCol >= 0 ? Text(rows[r], unitCol) : "";
            result.Add(new ImportedMaterial(name, FormatQuantity(qty) + (unit.Length == 0 ? "" : " " + unit)));
        }
    }

    // ---------- материалы в шапке ----------

    /// <summary>
    /// Находит подпись «Материалы» выше таблицы фурнитуры и забирает всё, что записано правее неё:
    /// каждая текстовая ячейка — отдельный материал (число рядом — его количество).
    /// </summary>
    static void ReadMaterials(List<object?[]> rows, int limitRow, List<ImportedMaterial> result)
    {
        var (labelRow, labelCol) = FindMaterialsLabel(rows, limitRow);
        if (labelRow < 0) return;

        var emptyInARow = 0;
        for (var r = labelRow; r < limitRow; r++)
        {
            var found = false;
            string? name = null;
            var amount = "";
            for (var c = labelCol + 1; c < rows[r].Length; c++)
            {
                var value = Cell(rows[r], c);
                var text = Clean(Text(rows[r], c));
                if (text.Length == 0) continue;
                found = true;

                if (TryNumber(value, out var number) && name != null)
                    amount = FormatQuantity(number);            // число после названия — количество
                else
                {
                    if (name != null) result.Add(new ImportedMaterial(name, amount));
                    (name, amount) = (text, "");
                }
            }
            if (name != null) result.Add(new ImportedMaterial(name, amount));

            emptyInARow = found ? 0 : emptyInARow + 1;
            if (emptyInARow >= MaterialGapRows) break;
        }
    }

    /// <summary>
    /// Подпись «Материалы» (или «Материал», с двоеточием или без) — в любой ячейке выше таблицы фурнитуры.
    /// Заголовок «Наименование материала» подписью не считается.
    /// </summary>
    static (int Row, int Col) FindMaterialsLabel(List<object?[]> rows, int limitRow)
    {
        for (var r = 0; r < limitRow; r++)
            for (var c = 0; c < rows[r].Length; c++)
            {
                var text = Text(rows[r], c).ToLowerInvariant();
                if (text.StartsWith("материал") && text.Length <= 12) return (r, c);
            }
        return (-1, -1);
    }

    // ---------- вспомогательное ----------

    static object? Cell(object?[] row, int column) => column >= 0 && column < row.Length ? row[column] : null;

    static string Text(object?[] row, int column) =>
        Convert.ToString(Cell(row, column), CultureInfo.InvariantCulture)?.Trim() ?? "";

    /// <summary>Убирает лишние пробелы внутри названия («Н=45    (450мм)» → «Н=45 (450мм)»).</summary>
    static string Clean(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string FormatQuantity(double qty) =>
        qty % 1 == 0 ? qty.ToString("0", CultureInfo.InvariantCulture) : qty.ToString("0.##", Money.Ru);

    static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case double d: number = d; return true;
            case int i: number = i; return true;
            case string s:
                return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default: number = 0; return false;
        }
    }
}
