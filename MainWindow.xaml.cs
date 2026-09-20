using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClientBase;

public partial class MainWindow : Window
{
    const string AppTitle = "База клиентов";

    readonly Database _db;
    List<ClientListItem> _all = new();
    Client? _client;
    bool _dirty;
    bool _editing;
    bool _suppressSelection;
    (string File, string Arguments)? _launchAfterClose;

    public MainWindow(Database db)
    {
        InitializeComponent();
        Branding.Apply(this);
        WindowMemory.Restore(this, "main");
        _db = db;
        RefreshList();
        ShowClient(null);
    }

    // ---------- список и поиск ----------

    void RefreshList(int? selectId = null)
    {
        _all = _db.GetClients();
        ApplyFilter(selectId ?? _client?.Id);
    }

    void ApplyFilter(int? selectId)
    {
        var q = SearchBox.Text.Trim();
        var qDigits = Digits(q);
        IEnumerable<ClientListItem> items = _all;
        if (q.Length > 0)
            items = items.Where(c =>
                Has(c.Name, q) || Has(c.Email, q) || Has(c.Address, q) || Has(c.PhonesText, q) ||
                (qDigits.Length >= 3 && Digits(c.PhonesText).Contains(qDigits)));

        var list = items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _suppressSelection = true;
        ClientList.ItemsSource = list;
        ClientList.SelectedItem = selectId is int id ? list.FirstOrDefault(c => c.Id == id) : null;
        _suppressSelection = false;

        CountText.Text = q.Length > 0
            ? $"Найдено: {list.Count} из {_all.Count}"
            : $"Всего клиентов: {_all.Count}";
    }

    static bool Has(string source, string part) => source.Contains(part, StringComparison.CurrentCultureIgnoreCase);
    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(_client?.Id);

    void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ClientList.SelectedItem is not ClientListItem item) return;
        if (_client != null && item.Id == _client.Id) return;

        if (!ConfirmLeave())
        {
            _suppressSelection = true;
            ClientList.SelectedItem = (ClientList.ItemsSource as IEnumerable<ClientListItem>)
                ?.FirstOrDefault(c => c.Id == _client?.Id);
            _suppressSelection = false;
            return;
        }
        ShowClient(_db.LoadClient(item.Id));
    }

    // ---------- карточка клиента ----------

    void ShowClient(Client? client)
    {
        if (_client != null) Unhook(_client);
        _client = client;
        if (client != null && client.Phones.Count == 0) client.Phones.Add(new Phone());

        ClientPanel.DataContext = client;
        ClientPanel.Visibility = client == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = client == null ? Visibility.Visible : Visibility.Collapsed;
        SavedText.Text = "";
        _dirty = false;

        // Сохранённый клиент открывается компактно, новый — сразу для заполнения.
        SetEditing(client != null && client.Id == 0);
        CancelEditButton.Visibility = client is { Id: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        if (client != null) Hook(client);
        LoadOrders();
    }

    void SetEditing(bool editing)
    {
        _editing = editing;
        EditPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ViewPanel.Visibility = editing || _client == null ? Visibility.Collapsed : Visibility.Visible;
        if (!editing) FillView();
    }

    /// <summary>Заполняет компактный вид карточки; пустые строки скрываются.</summary>
    void FillView()
    {
        if (_client == null) return;
        ViewName.Text = _client.Name;

        var phones = _client.Phones
            .Where(p => !string.IsNullOrWhiteSpace(p.Number))
            .Select(p => string.IsNullOrWhiteSpace(p.Note) ? p.Number.Trim() : $"{p.Number.Trim()} ({p.Note.Trim()})");
        Row(ViewPhonesLabel, ViewPhones, string.Join("   ·   ", phones));
        Row(ViewEmailLabel, ViewEmail, _client.Email.Trim());
        Row(ViewAddressLabel, ViewAddress, _client.Address.Trim());
        Row(ViewNotesLabel, ViewNotesScroll, _client.Notes.Trim());
        ViewNotes.Text = _client.Notes.Trim();

        static void Row(UIElement label, UIElement value, string text)
        {
            if (value is TextBlock tb) tb.Text = text;
            var v = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = v;
            value.Visibility = v;
        }
    }

    void EditClient_Click(object sender, RoutedEventArgs e)
    {
        SetEditing(true);
        SavedText.Text = "";
        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text.Length;
    }

    void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null || _client.Id == 0) return;
        if (_dirty && Dialogs.Show(this, "Отменить внесённые изменения?", AppTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ShowClient(_db.LoadClient(_client.Id));
    }

    void NotesGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        NotesBox.Height = Math.Clamp(NotesBox.ActualHeight + e.VerticalChange, 54, 420);

    void Hook(Client c)
    {
        c.PropertyChanged += MarkDirty;
        c.Phones.CollectionChanged += OnPhonesChanged;
        foreach (var p in c.Phones) p.PropertyChanged += MarkDirty;
    }

    void Unhook(Client c)
    {
        c.PropertyChanged -= MarkDirty;
        c.Phones.CollectionChanged -= OnPhonesChanged;
        foreach (var p in c.Phones) p.PropertyChanged -= MarkDirty;
    }

    void MarkDirty(object? sender, EventArgs e)
    {
        _dirty = true;
        SavedText.Text = "";
    }

    void OnPhonesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Phone p in e.OldItems) p.PropertyChanged -= MarkDirty;
        if (e.NewItems != null)
            foreach (Phone p in e.NewItems) p.PropertyChanged += MarkDirty;
        MarkDirty(sender, e);
    }

    bool SaveClient()
    {
        if (_client == null) return true;
        if (string.IsNullOrWhiteSpace(_client.Name))
        {
            Info("Введите имя клиента.");
            NameBox.Focus();
            return false;
        }

        var id = _db.SaveClient(_client);
        ShowClient(_db.LoadClient(id));
        RefreshList(id);
        SavedText.Text = $"Сохранено в {DateTime.Now:HH:mm:ss}";
        return true;
    }

    bool ConfirmLeave()
    {
        if (!_dirty || _client == null) return true;
        var name = string.IsNullOrWhiteSpace(_client.Name) ? "новый клиент" : _client.Name;
        var answer = Dialogs.Show(this, $"Сохранить изменения в карточке «{name}»?", AppTitle,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveClient(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void NewClient_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        ShowClient(new Client());
        ApplyFilter(null);
        NameBox.Focus();
    }

    void SaveClient_Click(object sender, RoutedEventArgs e) => SaveClient();

    void DeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        if (_client.Id == 0)
        {
            ShowClient(null);
            return;
        }

        if (!Confirm.Delete(this, $"Удалить клиента «{_client.Name}» вместе со всей историей его заказов, фото и материалов?"))
            return;

        var photos = _db.GetClientPhotos(_client.Id);
        _db.DeleteClient(_client.Id);
        photos.ForEach(PhotoStore.Delete);
        ShowClient(null);
        RefreshList();
    }

    void AddPhone_Click(object sender, RoutedEventArgs e) => _client?.Phones.Add(new Phone());

    void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null || (sender as FrameworkElement)?.DataContext is not Phone p) return;

        // Пустую строку (только что добавленную и не заполненную) убираем без вопросов.
        var isEmpty = string.IsNullOrWhiteSpace(p.Number) && string.IsNullOrWhiteSpace(p.Note);
        if (isEmpty || Confirm.Delete(this, $"Убрать телефон {p.Number}?"))
            _client.Phones.Remove(p);
    }

    // ---------- заказы ----------

    void LoadOrders()
    {
        if (_client == null || _client.Id == 0)
        {
            OrdersGrid.ItemsSource = null;
            OrdersSummary.Text = _client == null ? "" : "Сохраните клиента, чтобы добавлять заказы.";
            return;
        }

        var orders = _db.GetOrders(_client.Id);
        OrdersGrid.ItemsSource = orders;
        OrdersSummary.Text = orders.Count == 0
            ? "Заказов пока нет"
            : $"Заказов: {orders.Count}     Сумма по ценам заказов: {Money.Format(orders.Sum(o => o.Price))}";
    }

    void NewOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        if ((_client.Id == 0 || _dirty) && !SaveClient()) return;
        ShowOrder(null);
    }

    void OpenOrder_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is OrderSummary o) ShowOrder(o.Id);
        else Info("Выберите заказ в списке.");
    }

    void OrdersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var onRow = ItemsControl.ContainerFromElement(OrdersGrid, e.OriginalSource as DependencyObject) is DataGridRow;
        if (onRow && OrdersGrid.SelectedItem is OrderSummary o) ShowOrder(o.Id);
    }

    void ShowOrder(int? orderId)
    {
        if (_client == null) return;
        new OrderWindow(_db, _client.Id, _client.Name, orderId) { Owner = this }.ShowDialog();
        LoadOrders();   // заказ мог быть сохранён и без закрытия окна
    }

    void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not OrderSummary o)
        {
            Info("Выберите заказ в списке.");
            return;
        }

        if (!Confirm.Delete(this, $"Удалить заказ № {o.Number} от {o.DateText}" + (o.Price > 0 ? $" на {o.PriceText}" : "") + "?"))
            return;

        var photos = _db.GetOrderPhotos(o.Id);
        _db.DeleteOrder(o.Id);
        photos.ForEach(PhotoStore.Delete);
        LoadOrders();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_db) { Owner = this };
        settings.ShowDialog();

        // Папка данных изменена — перезапускаем программу; скачано обновление — запускаем установщик.
        // Оба действия выполняются после закрытия главного окна (если в карточке клиента остались
        // несохранённые правки, программа сначала о них спросит).
        if (settings.UpdateSetupPath is { } setup) CloseAndRun(setup, UpdateUi.InstallerArguments);
        else if (settings.RestartRequested && Environment.ProcessPath is { } exe) CloseAndRun(exe, "");
    }

    /// <summary>Закрывает программу и запускает файл (установщик обновления или новый экземпляр программы).</summary>
    public void CloseAndRun(string file, string arguments)
    {
        _launchAfterClose = (file, arguments);
        Close();
        _launchAfterClose = null;   // если закрытие отменили (несохранённые правки), ничего не запускаем
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_launchAfterClose is var (file, arguments))
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
    }

    // ---------- прочее ----------

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmLeave())
        {
            e.Cancel = true;
            return;
        }
        WindowMemory.Save(this, "main");
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.S when _client != null && _editing:
                SaveClient();
                break;
            case Key.N:
                NewClient_Click(this, e);
                break;
            case Key.F:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>В поиске: Esc — очистить, Enter или стрелка вниз — к списку клиентов.</summary>
    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SearchBox.Clear();
                e.Handled = true;
                break;
            case Key.Enter or Key.Down when ClientList.Items.Count > 0:
                if (ClientList.SelectedIndex < 0) ClientList.SelectedIndex = 0;
                (ClientList.ItemContainerGenerator.ContainerFromIndex(Math.Max(ClientList.SelectedIndex, 0)) as UIElement)?.Focus();
                e.Handled = true;
                break;
        }
    }

    void Info(string text) => Dialogs.Show(this, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
}
