using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClientBase;

public partial class OrderWindow : Window
{
    const string AppTitle = Confirm.AppTitle;

    readonly Database _db;
    readonly int _clientId;
    readonly string _clientName;

    Order _order = null!;
    HashSet<string> _savedPhotos = new();          // фото, которые сейчас записаны в базе
    readonly HashSet<string> _addedPhotos = new(); // фото, добавленные в этом сеансе (до сохранения)

    /// <summary>Список изготовителей для выпадающих списков (из настроек).</summary>
    public List<Manufacturer> Manufacturers { get; }

    public OrderWindow(Database db, int clientId, string clientName, int? orderId)
    {
        _db = db;
        _clientId = clientId;
        _clientName = clientName;
        Manufacturers = db.GetManufacturers();

        InitializeComponent();
        Branding.Apply(this);
        WindowMemory.Restore(this, "order");
        ClientText.Text = clientName;
        ShowOrder(orderId);
    }

    // ---------- загрузка и сохранение ----------

    void ShowOrder(int? orderId)
    {
        _order = orderId is int id
            ? _db.LoadOrder(id) ?? throw new InvalidOperationException("Заказ не найден в базе.")
            : new Order { ClientId = _clientId, PlannedNumber = _db.NextOrderNumber() };

        // Выбор «＋ Изготовитель — новый заказ» обрабатывается, когда WPF закончит обновлять привязку.
        _order.Defer = action => Dispatcher.BeginInvoke(action);
        _order.SetManufacturers(Manufacturers);

        _savedPhotos = PhotoNames(_order).ToHashSet();
        NumberText.Text = $"№ {_order.Number}";
        Title = $"Заказ № {_order.Number} — {_clientName}";
        DataContext = _order;
        ShowItemPhoto(null);
    }

    static IEnumerable<string> PhotoNames(Order order) =>
        order.Items.Select(i => i.Photo).Prepend(order.ProjectPhoto).Where(n => n.Length > 0);

    bool Save()
    {
        CommitEdits();

        if (_order.Date is null)
        {
            Warn("Укажите дату заказа.");
            return false;
        }

        // Строки, которые начали вводить и бросили, тихо убираем.
        foreach (var blank in _order.Items.Where(i => string.IsNullOrWhiteSpace(i.ProductName)).ToList())
            _order.Items.Remove(blank);
        foreach (var blank in _order.Materials.Where(m => string.IsNullOrWhiteSpace(m.Name) && string.IsNullOrWhiteSpace(m.Amount)).ToList())
            _order.Materials.Remove(blank);
        foreach (var blank in _order.Hardware.Where(h => string.IsNullOrWhiteSpace(h.Name) && string.IsNullOrWhiteSpace(h.Amount)).ToList())
            _order.Hardware.Remove(blank);

        if (_order.Items.Count == 0)
        {
            Warn("Добавьте в заказ хотя бы одно изделие.");
            Tabs.SelectedIndex = 0;
            return false;
        }
        if (_order.Items.Any(i => i.Quantity <= 0))
        {
            Warn("Количество у каждого изделия должно быть больше нуля.");
            Tabs.SelectedIndex = 0;
            return false;
        }
        if (_order.Materials.Any(m => string.IsNullOrWhiteSpace(m.Name)))
        {
            Warn("У одного из материалов не указано название.");
            Tabs.SelectedIndex = 1;
            return false;
        }
        if (_order.Hardware.Any(h => string.IsNullOrWhiteSpace(h.Name)))
        {
            Warn("У одной из позиций фурнитуры не указано название.");
            Tabs.SelectedIndex = 2;
            return false;
        }

        var id = _db.SaveOrder(_order);

        // Фото, которых больше нет в заказе (заменённые или убранные), удаляем из хранилища.
        var keep = PhotoNames(_order).ToHashSet();
        foreach (var name in _savedPhotos.Concat(_addedPhotos).Where(n => !keep.Contains(n)).ToList())
            PhotoStore.Delete(name);
        _addedPhotos.Clear();

        var tab = Tabs.SelectedIndex;
        ShowOrder(id);
        Tabs.SelectedIndex = tab;
        SavedText.Text = $"Сохранено в {DateTime.Now:HH:mm:ss}";
        return true;
    }

    void CommitEdits()
    {
        foreach (var grid in new[] { ItemsGrid, MaterialsGrid, HardwareGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        // Поле, в котором сейчас курсор (например, цена), передаёт значение в заказ при потере фокуса —
        // при Ctrl+S или закрытии окна этого ещё не произошло.
        if (Keyboard.FocusedElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        CommitEdits();
        if (_order.IsDirty)
        {
            var answer = Dialogs.Show(this, "Сохранить изменения в заказе?", AppTitle,
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }

        // Фото, добавленные в этом сеансе, но так и не сохранённые, не должны копиться в папке.
        foreach (var name in _addedPhotos.Where(n => !_savedPhotos.Contains(n)).ToList())
            PhotoStore.Delete(name);
        WindowMemory.Save(this, "order");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
        }
    }

    // ---------- фото ----------

    void Photo_FileAdded(object? sender, string name) => _addedPhotos.Add(name);

    void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowItemPhoto(ItemsGrid.SelectedItem as OrderItem);

    void ShowItemPhoto(OrderItem? item)
    {
        ItemPhotoBox.DataContext = item;
        ItemPhotoBox.IsEnabled = item != null;
    }

    // ---------- добавление строк («+») и выделение строк ----------

    void AddItem_Click(object sender, RoutedEventArgs e) => AddRow(ItemsGrid, _order.Items, new OrderItem(), nameBoxIndex: 1);
    void AddMaterial_Click(object sender, RoutedEventArgs e) => AddRow(MaterialsGrid, _order.Materials, new Material(), nameBoxIndex: 0);
    void AddHardware_Click(object sender, RoutedEventArgs e) => AddRow(HardwareGrid, _order.Hardware, new HardwareItem(), nameBoxIndex: 0);

    /// <summary>Добавляет строку в конец списка и ставит курсор в поле названия — можно сразу печатать.</summary>
    void AddRow<T>(DataGrid grid, ObservableCollection<T> list, T item, int nameBoxIndex) where T : class
    {
        CommitEdits();
        list.Add(item);
        grid.SelectedItem = item;
        grid.UpdateLayout();
        grid.ScrollIntoView(item);

        Dispatcher.BeginInvoke(() =>
        {
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return;
            row.ApplyTemplate();
            var boxes = VisualChildren<TextBox>(row).ToList();
            if (boxes.Count <= nameBoxIndex) return;
            boxes[nameBoxIndex].Focus();
            boxes[nameBoxIndex].SelectAll();
        }, DispatcherPriority.Loaded);
    }

    static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// Tab / Shift+Tab в таблицах идёт по полям ввода (название → количество → производство → следующая строка).
    /// Сама таблица при Tab прыгает по служебным ячейкам, и текст уходил «в никуда».
    /// </summary>
    static bool MoveFocusWithTab(KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        if (Keyboard.FocusedElement is not UIElement current) return false;

        var direction = Keyboard.Modifiers == ModifierKeys.Shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        e.Handled = current.MoveFocus(new TraversalRequest(direction));
        return true;
    }

    /// <summary>Когда фокус приходит в строку (например, по Tab), строка выделяется — и фото изделия показывается.</summary>
    void Grid_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;   // мышью выделение делает Grid_PreviewMouseLeftButtonDown
        var grid = (DataGrid)sender;
        if (e.NewFocus is DependencyObject target
            && ItemsControl.ContainerFromElement(grid, target) is DataGridRow { IsSelected: false } row)
        {
            grid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    /// <summary>
    /// В ячейках таблицы — поля ввода, а клик по полю сам строку не выделяет. Поэтому выделение делаем здесь:
    /// клик — одна строка, Ctrl+клик — добавить или снять строку, Shift+клик — диапазон.
    /// </summary>
    void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;

        switch (Keyboard.Modifiers)
        {
            case ModifierKeys.None:
                if (!row.IsSelected || grid.SelectedItems.Count > 1)
                {
                    grid.SelectedItems.Clear();
                    row.IsSelected = true;
                }
                break;

            case ModifierKeys.Control:
                row.IsSelected = !row.IsSelected;
                e.Handled = true;
                break;

            case ModifierKeys.Shift:
                var anchor = grid.SelectedItem is { } selected ? grid.Items.IndexOf(selected) : row.GetIndex();
                var target = row.GetIndex();
                grid.SelectedItems.Clear();
                for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
                    grid.SelectedItems.Add(grid.Items[i]);
                e.Handled = true;
                break;
        }
    }

    // ---------- изделия: удаление (всегда с подтверждением) ----------

    void RemoveItemRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OrderItem item) DeleteItems(new List<OrderItem> { item });
    }

    /// <summary>Delete в таблице (не во время ввода текста в ячейке) удаляет выделенные строки — после подтверждения.</summary>
    void ItemsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MoveFocusWithTab(e)) return;
        if (e.Key != Key.Delete || e.OriginalSource is TextBoxBase) return;
        e.Handled = true;
        DeleteItems(ItemsGrid.SelectedItems.OfType<OrderItem>().ToList());
    }

    void DeleteItems(List<OrderItem> items)
    {
        if (items.Count == 0) return;
        var question = items.Count == 1
            ? $"Удалить изделие «{items[0].ProductName}»?"
            : $"Удалить выбранные изделия ({items.Count} шт.)?";
        if (!Confirm.Delete(this, question)) return;

        ItemsGrid.CancelEdit();
        foreach (var item in items) _order.Items.Remove(item);
    }

    // ---------- материалы ----------

    void RemoveMaterialRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Material material) DeleteMaterials(new List<Material> { material });
    }

    void MaterialsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MoveFocusWithTab(e)) return;
        if (e.Key != Key.Delete || e.OriginalSource is TextBoxBase) return;
        e.Handled = true;
        DeleteMaterials(MaterialsGrid.SelectedItems.OfType<Material>().ToList());
    }

    void DeleteMaterials(List<Material> materials)
    {
        if (materials.Count == 0) return;
        var question = materials.Count == 1
            ? $"Удалить материал «{materials[0].Name}»?"
            : $"Удалить выбранные материалы ({materials.Count} шт.)?";
        if (!Confirm.Delete(this, question)) return;

        MaterialsGrid.CancelEdit();
        foreach (var material in materials) _order.Materials.Remove(material);
    }

    // ---------- фурнитура ----------

    void RemoveHardwareRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HardwareItem item) DeleteHardware(new List<HardwareItem> { item });
    }

    void HardwareGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MoveFocusWithTab(e)) return;
        if (e.Key != Key.Delete || e.OriginalSource is TextBoxBase) return;
        e.Handled = true;
        DeleteHardware(HardwareGrid.SelectedItems.OfType<HardwareItem>().ToList());
    }

    void DeleteHardware(List<HardwareItem> items)
    {
        if (items.Count == 0) return;
        var question = items.Count == 1
            ? $"Удалить фурнитуру «{items[0].Name}»?"
            : $"Удалить выбранную фурнитуру ({items.Count} поз.)?";
        if (!Confirm.Delete(this, question)) return;

        HardwareGrid.CancelEdit();
        foreach (var item in items) _order.Hardware.Remove(item);
    }

    void AssignSelected_Click(object sender, RoutedEventArgs e)
    {
        if (AssignCombo.SelectedItem is not ProductionOrder choice)
        {
            Info("Выберите в списке заказ в производство или изготовителя («＋ … — новый заказ»), затем выделите материалы в таблице.");
            return;
        }
        var selected = MaterialsGrid.SelectedItems.OfType<Material>().ToList();
        if (selected.Count == 0)
        {
            Info("Выделите в таблице один или несколько материалов (Ctrl или Shift для нескольких).");
            return;
        }

        // Если выбран изготовитель без заказа — заказ в производство создастся сам.
        var target = _order.ResolveChoice(choice);
        foreach (var material in selected) material.Assigned = target;
        _order.RefreshChoices();
        AssignCombo.SelectedItem = target;
    }

    // ---------- заказы в производство ----------

    void AddProduction_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        var production = new ProductionOrder
        {
            Sub = _order.Productions.Select(p => p.Sub).DefaultIfEmpty(0).Max() + 1,
            OrderNumber = _order.Number,
        };
        _order.Productions.Add(production);

        var waiting = _order.NoProduction.Lines.Count;
        if (_order.Productions.Count == 1 && waiting > 0)
        {
            var answer = Dialogs.Show(this,
                $"Отправить в этот заказ в производство все материалы ({waiting})?\n\n" +
                "Потом любой материал можно перенести в другой заказ на вкладке «Материалы».",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                foreach (var material in _order.Materials.ToList()) material.Assigned = production;
        }
    }

    void RemoveProductionRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProductionOrder production) return;

        var count = production.Lines.Count;
        var question = $"Удалить заказ в производство {production.Code}" +
                       (production.Manufacturer is null ? "" : $" ({production.Manufacturer.Name})") + "?" +
                       (count > 0 ? $"\n\nМатериалы ({count}) останутся в заказе, но станут «не назначенными»." : "");
        if (Confirm.Delete(this, question)) _order.Productions.Remove(production);
    }

    // ---------- папка проекта Базис ----------

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка проекта Базис-Мебельщика" };
        var current = _order.ProjectFolder.Trim();
        if (Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) != true) return;
        _order.ProjectFolder = dialog.FolderName;

        var answer = Dialogs.Show(this,
            "Подтянуть из этой папки изделия, общее фото проекта, материалы и фурнитуру прямо сейчас?", AppTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        Import_Click(sender, e);
        ImportMaterials_Click(sender, e);
    }

    bool TryGetProjectFolder(out string folder)
    {
        folder = _order.ProjectFolder.Trim();
        if (Directory.Exists(folder)) return true;
        Warn("Укажите существующую папку проекта (кнопка «Выбрать…»).");
        return false;
    }

    /// <summary>Изделия (с эскизами и количеством из имён файлов) и общее фото проекта.</summary>
    void Import_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (!TryGetProjectFolder(out var folder)) return;

        ImportScan scan;
        try
        {
            scan = ProjectImport.Scan(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Не удалось прочитать папку:\n{ex.Message}");
            return;
        }

        if (scan.Products.Count == 0 && scan.ProjectModelPath == null)
        {
            Info("В папке не найдено ни одного файла модели (.b3d).\nПроверьте, что выбрана папка проекта с подпапкой «3Д».");
            return;
        }

        // Общее фото проекта — миниатюра общего файла проекта; уже загруженное вручную фото не заменяем.
        string projectPhotoNote;
        if (_order.ProjectPhoto.Length > 0)
            projectPhotoNote = "уже было, не менял";
        else if (scan.ProjectModelPath != null && ProjectImport.ExtractThumbnail(scan.ProjectModelPath) is { } png
                 && SaveProjectPhoto(png))
            projectPhotoNote = "загружено из общего файла проекта";
        else
            projectPhotoNote = "не найдено — загрузите вручную";

        int added = 0, existing = 0, photos = 0;
        foreach (var product in scan.Products)
        {
            // Изделие уже есть, если совпал его номер в папке проекта. По названию сверяем только строки, добавленные
            // вручную (без номера): у разных изделий проекта названия бывают одинаковыми («Шкаф-Витрина» левая и правая).
            var item = (product.Number is int number ? _order.Items.FirstOrDefault(i => i.SourceNo == number) : null)
                       ?? _order.Items.FirstOrDefault(i => i.SourceNo == null &&
                              string.Equals(i.ProductName.Trim(), product.Name, StringComparison.CurrentCultureIgnoreCase));
            if (item == null)
            {
                item = new OrderItem { ProductName = product.Name, Quantity = product.Quantity, SourceNo = product.Number };
                _order.Items.Add(item);
                added++;
            }
            else
            {
                existing++;
                item.SourceNo ??= product.Number;
            }

            if (item.Photo.Length == 0 && ImportPhoto(product) is { } photo)
            {
                item.Photo = photo;
                _addedPhotos.Add(photo);
                photos++;
            }
        }

        Info($"Изделия из папки проекта\n" +
             $"   добавлено: {added}\n   уже были в заказе: {existing}\n   фото привязано: {photos}\n\n" +
             $"Общее фото проекта: {projectPhotoNote}." +
             (scan.UnmatchedSketches > 0 ? $"\nЭскизов без изделия (пропущено): {scan.UnmatchedSketches}." : "") +
             "\n\nКоличество берётся из имён файлов («…5шт»), иначе 1 — проверьте. Цены из Базиса не читаются — проставьте вручную.");
    }

    /// <summary>
    /// Таблица из папки «Фурник»: материалы (правее подписи «Материалы») — во вкладку «Материалы»,
    /// таблица «Наименование / Ед. изм. / К-во» — во вкладку «Фурнитура». Положение ячеек не важно.
    /// </summary>
    void ImportMaterials_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        if (!TryGetProjectFolder(out var folder)) return;

        FurnitureTable table;
        try
        {
            if (MaterialImport.FindSpreadsheets(folder).Count == 0)
            {
                Info("В папке проекта не найдено таблицы в подпапке «Фурник» (.xls или .xlsx).");
                return;
            }
            table = MaterialImport.Read(folder);
        }
        catch (Exception ex)
        {
            Warn($"Не удалось прочитать таблицу из папки «Фурник»:\n{ex.Message}");
            return;
        }

        int materialsAdded = 0, materialsKnown = 0, hardwareAdded = 0, hardwareKnown = 0;
        foreach (var row in table.Materials)
        {
            if (_order.Materials.Any(m => SameName(m.Name, row.Name))) { materialsKnown++; continue; }
            _order.Materials.Add(new Material { Name = row.Name, Amount = row.Amount });
            materialsAdded++;
        }
        foreach (var row in table.Hardware)
        {
            if (_order.Hardware.Any(h => SameName(h.Name, row.Name))) { hardwareKnown++; continue; }
            _order.Hardware.Add(new HardwareItem { Name = row.Name, Amount = row.Amount });
            hardwareAdded++;
        }

        var report = $"Таблица из папки «Фурник»\n\n" +
                     $"Материалы:\n   добавлено: {materialsAdded}\n   уже были в заказе: {materialsKnown}\n\n" +
                     $"Фурнитура:\n   добавлено: {hardwareAdded}\n   уже были в заказе: {hardwareKnown}";
        if (table.Materials.Count == 0)
            report += "\n\nМатериалы не найдены: в таблице нет ячейки с подписью «Материалы», правее которой они перечислены.";
        if (table.Hardware.Count == 0)
            report += "\n\nФурнитура не найдена: в таблице нет строки-заголовка с «Наименование» и «К-во».";
        Info(report);

        if (materialsAdded > 0) Tabs.SelectedIndex = 1;
        else if (hardwareAdded > 0) Tabs.SelectedIndex = 2;
    }

    static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    bool SaveProjectPhoto(byte[] png)
    {
        try
        {
            var name = PhotoStore.AddBytes(png, ".png");
            _addedPhotos.Add(name);
            _order.ProjectPhoto = name;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string? ImportPhoto(ImportedProduct product)
    {
        try
        {
            if (product.SketchPath != null) return PhotoStore.Add(product.SketchPath);
            return ProjectImport.ExtractThumbnail(product.ModelPath) is { } png ? PhotoStore.AddBytes(png, ".png") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------- сообщения ----------

    void Warn(string text) => Dialogs.Show(this, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    void Info(string text) => Dialogs.Show(this, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
}
