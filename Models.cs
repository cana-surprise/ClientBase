using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ClientBase;

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class Money
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(decimal value) =>
        (value % 1 == 0 ? value.ToString("N0", Ru) : value.ToString("N2", Ru)) + " BYN";

    /// <summary>Понимает "12500", "12 500", "12500,50", "12500.50", "12 500 BYN"</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = new string(text.Where(ch => char.IsDigit(ch) || ch == ',' || ch == '.').ToArray())
            .Trim(',', '.')   // хвостовая точка после подписи валюты и т.п.
            .Replace('.', ',');
        if (t.Count(ch => ch == ',') > 1) return false;
        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, Ru, out var v) || v < 0) return false;
        value = Math.Round(v, 2);
        return true;
    }
}

// ---------- клиенты ----------

public class Phone : Notify
{
    string _number = "";
    string _note = "";

    public int Id { get; set; }
    public string Number { get => _number; set => Set(ref _number, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
}

public class Client : Notify
{
    string _name = "";
    string _email = "";
    string _address = "";
    string _notes = "";

    public int Id { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Email { get => _email; set => Set(ref _email, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public ObservableCollection<Phone> Phones { get; } = new();
}

public class ClientListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string PhonesText { get; set; } = "";
}

// ---------- производство ----------

/// <summary>Изготовитель (цех, производитель). Список ведётся в настройках.</summary>
public record Manufacturer(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Палитра цветов заказов в производство: каждому новому заказу достаётся случайный, ещё не занятый цвет.</summary>
public static class ProductionColors
{
    public static readonly string[] Palette =
    {
        "#3E7CB1", "#2A9D8F", "#8E6BBF", "#C8547A", "#7F9A2B",
        "#5C6BC0", "#A0522D", "#3F9E6A", "#B8860B", "#607D8B",
        "#D0544B", "#1F8FA8",
    };

    static readonly Random Rng = new();

    public static string Pick(IEnumerable<string> used)
    {
        var taken = used.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = Palette.Where(c => !taken.Contains(c)).ToArray();
        var source = free.Length > 0 ? free : Palette;
        return source[Rng.Next(source.Length)];
    }
}

/// <summary>
/// Заказ в производство («подзаказ») — часть общего заказа: «12-1», «12-2»… (номер заказа + подномер).
/// В него отправляются материалы; у подзаказа свой изготовитель, номер в производстве и даты.
/// Подзаказ «Готов», когда отмечены все его материалы.
/// </summary>
public class ProductionOrder : Notify
{
    int _sub;
    int _orderNumber;
    Manufacturer? _manufacturer;
    string _productionNumber = "";
    DateTime? _sentDate;
    DateTime? _readyDate;

    public int Id { get; set; }

    /// <summary>Служебная запись «— не назначено —» для выпадающих списков.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Служебная запись «＋ Изготовитель — новый заказ»: при выборе подзаказ создаётся автоматически.</summary>
    public bool IsVirtual { get; init; }

    public int Sub { get => _sub; set { if (Set(ref _sub, value)) RaiseLabels(); } }
    public int OrderNumber { get => _orderNumber; set { if (Set(ref _orderNumber, value)) RaiseLabels(); } }

    public Manufacturer? Manufacturer
    {
        get => _manufacturer;
        set { if (Set(ref _manufacturer, value)) Raise(nameof(Label)); }
    }

    public string ProductionNumber { get => _productionNumber; set => Set(ref _productionNumber, value); }

    /// <summary>Цвет заказа в производство («#RRGGBB»): назначается случайно и сохраняется.</summary>
    public string ColorHex { get => _colorHex; set => Set(ref _colorHex, value); }
    string _colorHex = "";

    public DateTime? SentDate
    {
        get => _sentDate;
        set { if (Set(ref _sentDate, value)) Raise(nameof(StatusText)); }
    }

    public DateTime? ReadyDate { get => _readyDate; set => Set(ref _readyDate, value); }

    /// <summary>Материалы, отправленные в этот подзаказ (поддерживается заказом автоматически).</summary>
    public ObservableCollection<Material> Lines { get; } = new();

    public bool HasLines => Lines.Count > 0;
    public int MaterialsReady => Lines.Count(m => m.IsReady);

    public string MaterialsText => Lines.Count == 0
        ? "материалов пока нет"
        : $"материалов: {Lines.Count} · готово {MaterialsReady}";

    /// <summary>Галочка «всё готово»: true — все материалы готовы, null — часть, false — ничего.</summary>
    public bool? AllDone
    {
        get
        {
            if (Lines.Count == 0) return false;
            var ready = MaterialsReady;
            return ready == Lines.Count ? true : ready == 0 ? false : null;
        }
        set
        {
            foreach (var material in Lines.ToList()) material.IsReady = value == true;
        }
    }

    public string Code => IsPlaceholder || IsVirtual
        ? ""
        : OrderNumber > 0 ? $"{OrderNumber}-{Sub}" : $"нов-{Sub}";

    public string Label =>
        IsPlaceholder ? "— не назначено —"
        : IsVirtual ? $"＋ {Manufacturer?.Name} — новый заказ"
        : Manufacturer is null ? Code : $"{Code} · {Manufacturer.Name}";

    public string StatusText => StatusFor(SentDate, Lines.Count, MaterialsReady);

    /// <summary>Не отправлен → В производстве → Готов (когда отмечены все материалы).</summary>
    public static string StatusFor(DateTime? sentDate, int materialsTotal, int materialsReady)
    {
        if (materialsTotal > 0 && materialsReady == materialsTotal) return "Готов";
        return sentDate is null ? "Не отправлен" : "В производстве";
    }

    public void RaiseLines()
    {
        Raise(nameof(HasLines));
        Raise(nameof(MaterialsText));
        Raise(nameof(StatusText));
        Raise(nameof(AllDone));
    }

    void RaiseLabels()
    {
        Raise(nameof(Code));
        Raise(nameof(Label));
    }
}

// ---------- заказ ----------

public class OrderItem : Notify
{
    string _productName = "";
    int _quantity = 1;
    string _photo = "";
    int? _sourceNo;

    public int Id { get; set; }

    /// <summary>
    /// Номер изделия — из имени файла в папке проекта Базис («7) Пенал…»). Показывается в таблице,
    /// а при повторном «подтянуть» по нему находятся уже добавленные изделия.
    /// </summary>
    public int? SourceNo
    {
        get => _sourceNo;
        set { if (Set(ref _sourceNo, value)) Raise(nameof(NumberText)); }
    }

    /// <summary>Текстовый «двойник» номера для таблицы: пустая строка — номера нет.</summary>
    public string NumberText
    {
        get => SourceNo?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            SourceNo = digits.Length > 0 && int.TryParse(digits, out var n) ? n : null;
        }
    }

    public string ProductName { get => _productName; set => Set(ref _productName, value); }

    public int Quantity { get => _quantity; set => Set(ref _quantity, Math.Max(0, value)); }

    /// <summary>Имя файла фото в папке «Данные\Фото» (пусто — фото нет).</summary>
    public string Photo
    {
        get => _photo;
        set { if (Set(ref _photo, value ?? "")) Raise(nameof(PhotoMark)); }
    }

    public string PhotoMark => Photo.Length > 0 ? "📷" : "";

    // Текстовый «двойник» для таблицы: терпимо относится к пробелам и лишним символам.
    // Намеренно не сообщает об изменении самого себя, чтобы не мешать вводу.
    public string QuantityText
    {
        get => Quantity.ToString(CultureInfo.InvariantCulture);
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length == 0) Quantity = 0;
            else if (int.TryParse(digits, out var q)) Quantity = q;
        }
    }
}

/// <summary>Материал (плита, кромка, фурнитура…), который отправляется в производство.</summary>
public class Material : Notify
{
    string _name = "";
    string _amount = "";
    bool _isReady;
    ProductionOrder? _assigned;

    public int Id { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Amount { get => _amount; set => Set(ref _amount, value); }

    /// <summary>Материал готов (выполнен на производстве).</summary>
    public bool IsReady { get => _isReady; set => Set(ref _isReady, value); }

    public ProductionOrder? Assigned { get => _assigned; set => Set(ref _assigned, value); }
}

/// <summary>Фурнитура (крепёж, петли, направляющие…) — строка таблицы «Фурник». Просто перечень, без отметок.</summary>
public class HardwareItem : Notify
{
    string _name = "";
    string _amount = "";

    public int Id { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Amount { get => _amount; set => Set(ref _amount, value); }
}

public class Order : Notify
{
    /// <summary>Номер, который получит ещё не сохранённый заказ (задаёт окно заказа).</summary>
    public int PlannedNumber { get; set; }

    /// <summary>Номер заказа: у сохранённого — его Id, у нового — ожидаемый следующий номер.</summary>
    public int Number => Id > 0 ? Id : PlannedNumber;

    string _name = "";
    string _notes = "";
    string _projectPhoto = "";
    string _projectFolder = "";
    DateTime? _date = DateTime.Today;
    decimal _cost;
    decimal _price;

    List<Manufacturer> _manufacturers = new();
    readonly Dictionary<int, ProductionOrder> _virtuals = new();

    public int Id { get; set; }
    public int ClientId { get; set; }

    public DateTime? Date { get => _date; set => Set(ref _date, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }

    /// <summary>Общее фото проекта (расстановка изделий): имя файла в «Данные\Фото».</summary>
    public string ProjectPhoto { get => _projectPhoto; set => Set(ref _projectPhoto, value ?? ""); }

    /// <summary>Папка проекта Базис-Мебельщика, откуда подтягиваются изделия и материалы.</summary>
    public string ProjectFolder { get => _projectFolder; set => Set(ref _projectFolder, value ?? ""); }

    public ObservableCollection<OrderItem> Items { get; } = new();
    public ObservableCollection<Material> Materials { get; } = new();
    public ObservableCollection<HardwareItem> Hardware { get; } = new();
    public ObservableCollection<ProductionOrder> Productions { get; } = new();

    /// <summary>
    /// Варианты выпадающего списка «Производство» у материала: «— не назначено —», существующие подзаказы
    /// и «＋ Изготовитель — новый заказ» для тех изготовителей, у которых подзаказа ещё нет.
    /// </summary>
    public ObservableCollection<ProductionOrder> ProductionChoices { get; } = new();
    public ProductionOrder NoProduction { get; } = new() { IsPlaceholder = true };

    /// <summary>
    /// Как отложить действие, пока WPF завершает обновление привязки (в окне — Dispatcher.BeginInvoke).
    /// По умолчанию выполняется сразу.
    /// </summary>
    public Action<Action> Defer { get; set; } = action => action();

    /// <summary>Есть несохранённые изменения.</summary>
    public bool IsDirty { get; private set; }
    public void AcceptChanges() => IsDirty = false;

    /// <summary>Себестоимость заказа — вводится вручную.</summary>
    public decimal Cost
    {
        get => _cost;
        set { if (Set(ref _cost, value)) Raise(nameof(CostText)); }
    }

    /// <summary>Конечная цена заказа для клиента — вводится вручную.</summary>
    public decimal Price
    {
        get => _price;
        set { if (Set(ref _price, value)) Raise(nameof(PriceText)); }
    }

    // Поля ввода: понимают «12500», «12 500», «12500,50», «12500.50». Пустое поле — 0.
    // Обновление привязки при потере фокуса, поэтому после ввода текст приводится к аккуратному виду
    // (а непонятный ввод возвращается к прежнему значению).
    public string CostText
    {
        get => FormatInput(Cost);
        set
        {
            Cost = ParseInput(value, Cost);
            Raise();
        }
    }

    public string PriceText
    {
        get => FormatInput(Price);
        set
        {
            Price = ParseInput(value, Price);
            Raise();
        }
    }

    static string FormatInput(decimal value) => value == 0 ? "" : value.ToString("0.##", Money.Ru);

    static decimal ParseInput(string? text, decimal previous)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Money.TryParse(text, out var value) ? value : previous;
    }

    public string ReadySummary =>
        $"Материалы: готово {Materials.Count(m => m.IsReady)} из {Materials.Count}   ·   " +
        $"Фурнитура: {Hardware.Count} поз.   ·   " +
        $"В производстве: {Productions.Count} (готово {Productions.Count(p => p.StatusText == "Готов")})";

    public Order()
    {
        ProductionChoices.Add(NoProduction);
        Items.CollectionChanged += OnItemsChanged;
        Materials.CollectionChanged += OnMaterialsChanged;
        Hardware.CollectionChanged += OnHardwareChanged;
        Productions.CollectionChanged += OnProductionsChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Name) or nameof(Notes) or nameof(ProjectPhoto)
                or nameof(ProjectFolder) or nameof(Date) or nameof(Cost) or nameof(Price))
                IsDirty = true;
        };
    }

    // ---------- изготовители и автосоздание подзаказов ----------

    /// <summary>Список изготовителей из настроек — для пунктов «＋ Изготовитель — новый заказ».</summary>
    public void SetManufacturers(IEnumerable<Manufacturer> manufacturers)
    {
        _manufacturers = manufacturers.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        RefreshChoices();
    }

    /// <summary>
    /// Превращает выбранный пункт списка в реальный заказ в производство: «＋ Изготовитель — новый заказ»
    /// каждый раз создаёт НОВЫЙ заказ этого изготовителя — в том числе когда у него уже есть заказ.
    /// Чтобы добавить материал в существующий заказ, выбирают сам заказ («12-1 · Цех А»).
    /// </summary>
    public ProductionOrder? ResolveChoice(ProductionOrder? choice)
    {
        if (choice is not { IsVirtual: true }) return choice;

        var created = new ProductionOrder
        {
            Sub = Productions.Select(p => p.Sub).DefaultIfEmpty(0).Max() + 1,
            OrderNumber = Number,
            Manufacturer = choice.Manufacturer,
        };
        Productions.Add(created);
        return created;
    }

    /// <summary>Пункты «＋ Изготовитель — новый заказ» есть у каждого изготовителя из настроек, всегда.</summary>
    public void RefreshChoices()
    {
        for (var i = ProductionChoices.Count - 1; i >= 0; i--)
            if (ProductionChoices[i].IsVirtual && _manufacturers.All(m => m.Id != ProductionChoices[i].Manufacturer!.Id))
                ProductionChoices.RemoveAt(i);

        foreach (var m in _manufacturers)
        {
            if (ProductionChoices.Any(c => c.IsVirtual && c.Manufacturer!.Id == m.Id)) continue;
            if (!_virtuals.TryGetValue(m.Id, out var virtualChoice))
                _virtuals[m.Id] = virtualChoice = new ProductionOrder { IsVirtual = true, Manufacturer = m };
            ProductionChoices.Add(virtualChoice);
        }
    }

    void ResolveAssigned(Material material)
    {
        if (material.Assigned is not { IsVirtual: true } choice) return;
        material.Assigned = ResolveChoice(choice);
    }

    // ---------- реакция на изменения ----------

    static void Rehook(NotifyCollectionChangedEventArgs e, PropertyChangedEventHandler handler)
    {
        if (e.OldItems != null)
            foreach (Notify n in e.OldItems) n.PropertyChanged -= handler;
        if (e.NewItems != null)
            foreach (Notify n in e.NewItems) n.PropertyChanged += handler;
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rehook(e, OnItemChanged);
        IsDirty = true;
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    void OnMaterialsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rehook(e, OnMaterialChanged);
        IsDirty = true;
        if (e.NewItems != null)
            foreach (Material m in e.NewItems) m.Assigned ??= NoProduction;
        RefreshProduction();
    }

    void OnMaterialChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDirty = true;
        var material = (Material)sender!;
        switch (e.PropertyName)
        {
            case nameof(Material.Assigned) when material.Assigned is { IsVirtual: true }:
                Defer(() => ResolveAssigned(material));
                break;
            case nameof(Material.Assigned):
            case nameof(Material.IsReady):
                RefreshProduction();
                break;
        }
    }

    void OnHardwareChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rehook(e, OnHardwareItemChanged);
        IsDirty = true;
        Raise(nameof(ReadySummary));
    }

    void OnHardwareItemChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    void OnProductionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rehook(e, OnProductionChanged);
        IsDirty = true;

        if (e.OldItems != null)
            foreach (ProductionOrder p in e.OldItems)
            {
                foreach (var m in Materials.Where(m => ReferenceEquals(m.Assigned, p))) m.Assigned = NoProduction;
                ProductionChoices.Remove(p);
            }
        if (e.NewItems != null)
            foreach (ProductionOrder p in e.NewItems)
            {
                if (p.ColorHex.Length == 0)
                    p.ColorHex = ProductionColors.Pick(Productions.Where(x => !ReferenceEquals(x, p)).Select(x => x.ColorHex));
                ProductionChoices.Insert(Math.Min(1 + Productions.IndexOf(p), ProductionChoices.Count), p);
            }

        RefreshProduction();
    }

    void OnProductionChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    /// <summary>Обновляет списки материалов в подзаказах («Не назначено» тоже считается списком).</summary>
    void RefreshProduction()
    {
        foreach (var p in Productions)
            SyncLines(p, Materials.Where(m => ReferenceEquals(m.Assigned, p)));
        SyncLines(NoProduction, Materials.Where(m => m.Assigned == null || ReferenceEquals(m.Assigned, NoProduction)));
        Raise(nameof(ReadySummary));
    }

    static void SyncLines(ProductionOrder target, IEnumerable<Material> wanted)
    {
        var want = wanted.ToList();

        for (var i = target.Lines.Count - 1; i >= 0; i--)
            if (!want.Contains(target.Lines[i]))
                target.Lines.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
            if (i >= target.Lines.Count || !ReferenceEquals(target.Lines[i], want[i]))
                target.Lines.Insert(i, want[i]);

        target.RaiseLines();   // число готовых меняется и при том же составе
    }
}

public class OrderSummary
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Name { get; set; } = "";
    public string Products { get; set; } = "";
    public decimal Cost { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = StatusInDevelopment;

    public const string StatusInDevelopment = "В разработке";
    public const string StatusInProduction = "В производстве";
    public const string StatusDone = "Готово";

    /// <summary>
    /// Статус заказа по его заказам в производство: пока ничего не отправлено (или производства нет) —
    /// «В разработке»; отправлено хотя бы что-то — «В производстве»; всё готово — «Готово».
    /// </summary>
    public static string StatusFor(IReadOnlyCollection<string> productionStatuses)
    {
        if (productionStatuses.Count == 0) return StatusInDevelopment;
        if (productionStatuses.All(s => s == "Готов")) return StatusDone;
        return productionStatuses.Any(s => s != "Не отправлен") ? StatusInProduction : StatusInDevelopment;
    }

    public string DateText => Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public string CostText => Cost == 0 ? "—" : Money.Format(Cost);
    public string PriceText => Price == 0 ? "—" : Money.Format(Price);

    /// <summary>Название заказа; если не заполнено — перечень изделий.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? Products : Name;
}
