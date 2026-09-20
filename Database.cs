using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ClientBase;

public class Database
{
    const string DateFormat = "yyyy-MM-dd";
    const int SchemaVersion = 5;

    readonly string _connectionString;

    public string FilePath { get; }
    public string DataDirectory => Path.GetDirectoryName(FilePath)!;

    public Database(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString();
    }

    // ---------- служебное ----------

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys = ON");
        return c;
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static void InTransaction(SqliteConnection c, Action work)
    {
        Exec(c, "BEGIN");
        try
        {
            work();
            Exec(c, "COMMIT");
        }
        catch
        {
            Exec(c, "ROLLBACK");
            throw;
        }
    }

    // ---------- схема и обновления ----------

    public void Init()
    {
        using var c = Open();
        var existing = Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Clients'") > 0;
        var version = (int)Scalar(c, "PRAGMA user_version");

        // Перед обновлением структуры уже существующей базы — отдельная копия «на всякий случай».
        if (existing && version < SchemaVersion) BackupBeforeUpgrade();

        // Версия 0: клиенты, телефоны, заказы, позиции.
        Exec(c, """
            CREATE TABLE IF NOT EXISTS Clients (
                Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                Name    TEXT NOT NULL,
                Email   TEXT NOT NULL DEFAULT '',
                Address TEXT NOT NULL DEFAULT '',
                Notes   TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Phones (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId INTEGER NOT NULL REFERENCES Clients(Id) ON DELETE CASCADE,
                Number   TEXT NOT NULL,
                Note     TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Orders (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId  INTEGER NOT NULL REFERENCES Clients(Id) ON DELETE CASCADE,
                OrderDate TEXT NOT NULL,
                Notes     TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS OrderItems (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId      INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
                ProductName  TEXT NOT NULL,
                Quantity     INTEGER NOT NULL,
                UnitPriceKop INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Phones_Client ON Phones(ClientId);
            CREATE INDEX IF NOT EXISTS IX_Orders_Client ON Orders(ClientId);
            CREATE INDEX IF NOT EXISTS IX_Items_Order   ON OrderItems(OrderId);
            """);

        // Версия 1: фото, название заказа, изготовители, подзаказы в производство, материалы.
        if (version < 1)
        {
            InTransaction(c, () => Exec(c, """
                CREATE TABLE Manufacturers (
                    Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL
                );
                CREATE TABLE ProductionOrders (
                    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId          INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
                    SubNumber        INTEGER NOT NULL,
                    ManufacturerId   INTEGER NULL REFERENCES Manufacturers(Id) ON DELETE SET NULL,
                    ProductionNumber TEXT NOT NULL DEFAULT '',
                    SentDate         TEXT NULL,
                    ReadyDate        TEXT NULL
                );
                CREATE TABLE Materials (
                    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId           INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
                    Name              TEXT NOT NULL,
                    Amount            TEXT NOT NULL DEFAULT '',
                    IsReady           INTEGER NOT NULL DEFAULT 0,
                    ProductionOrderId INTEGER NULL REFERENCES ProductionOrders(Id) ON DELETE SET NULL
                );
                CREATE INDEX IX_Prod_Order ON ProductionOrders(OrderId);
                CREATE INDEX IX_Materials_Order ON Materials(OrderId);

                ALTER TABLE Orders ADD COLUMN Name TEXT NOT NULL DEFAULT '';
                ALTER TABLE Orders ADD COLUMN ProjectPhoto TEXT NOT NULL DEFAULT '';
                ALTER TABLE Orders ADD COLUMN ProjectFolder TEXT NOT NULL DEFAULT '';
                ALTER TABLE OrderItems ADD COLUMN Photo TEXT NOT NULL DEFAULT '';
                ALTER TABLE OrderItems ADD COLUMN IsReady INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE OrderItems ADD COLUMN SourceNo INTEGER NULL;
                ALTER TABLE OrderItems ADD COLUMN ProductionOrderId INTEGER NULL REFERENCES ProductionOrders(Id) ON DELETE SET NULL;
                """));
        }

        // Версия 2: себестоимость и конечная цена заказа вводятся вручную (цен у изделий больше нет).
        // Чтобы не потерять уже введённое, прежний итог по изделиям становится начальной ценой заказа.
        if (version < 2)
        {
            InTransaction(c, () => Exec(c, """
                ALTER TABLE Orders ADD COLUMN CostKop  INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Orders ADD COLUMN PriceKop INTEGER NOT NULL DEFAULT 0;
                UPDATE Orders SET PriceKop = IFNULL(
                    (SELECT SUM(i.Quantity * i.UnitPriceKop) FROM OrderItems i WHERE i.OrderId = Orders.Id), 0);
                """));
        }

        // Версия 3: фурнитура (крепёж, петли, направляющие…) — отдельный список заказа, не «материалы».
        if (version < 3)
        {
            InTransaction(c, () => Exec(c, """
                CREATE TABLE Hardware (
                    Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId INTEGER NOT NULL REFERENCES Orders(Id) ON DELETE CASCADE,
                    Name    TEXT NOT NULL,
                    Amount  TEXT NOT NULL DEFAULT '',
                    IsReady INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IX_Hardware_Order ON Hardware(OrderId);
                """));
        }

        // Версия 4: цвет заказа в производство (подсветка в списках).
        if (version < 4)
            Exec(c, "ALTER TABLE ProductionOrders ADD COLUMN Color TEXT NOT NULL DEFAULT ''");

        // Версия 5: у каждого клиента своя нумерация заказов с 1. Прежний номер (общий Id) остаётся внутренним
        // ключом, а существующим заказам номера раздаются по порядку внутри клиента.
        if (version < 5)
        {
            InTransaction(c, () =>
            {
                Exec(c, "ALTER TABLE Orders ADD COLUMN Number INTEGER NOT NULL DEFAULT 0");
                Exec(c, "UPDATE Orders SET Number = (SELECT COUNT(*) FROM Orders o2 WHERE o2.ClientId = Orders.ClientId AND o2.Id <= Orders.Id)");
            });
        }

        // Колонки OrderItems.UnitPriceKop, OrderItems.IsReady и OrderItems.ProductionOrderId остались от ранних
        // версий и программой больше не используются (цены изделий убраны, в производство уходят материалы).
        Exec(c, $"PRAGMA user_version = {SchemaVersion}");
    }

    void BackupBeforeUpgrade()
    {
        var dir = Path.Combine(DataDirectory, "Резервные копии");
        Directory.CreateDirectory(dir);
        File.Copy(FilePath, Path.Combine(dir, $"upgrade_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db"), overwrite: false);
    }

    /// <summary>Раз в день при запуске копирует базу в папку «Резервные копии» (хранится последние 30).</summary>
    public void Backup()
    {
        if (!File.Exists(FilePath)) return;
        var dir = Path.Combine(DataDirectory, "Резервные копии");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"clients_{DateTime.Now:yyyy-MM-dd}.db");
        if (!File.Exists(target)) File.Copy(FilePath, target);
        foreach (var old in Directory.GetFiles(dir, "clients_*.db").OrderByDescending(f => f).Skip(30))
            File.Delete(old);
    }

    // ---------- клиенты ----------

    public List<ClientListItem> GetClients()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT c.Id, c.Name, c.Email, c.Address, IFNULL(group_concat(p.Number, ', '), '')
            FROM Clients c LEFT JOIN Phones p ON p.ClientId = c.Id
            GROUP BY c.Id
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<ClientListItem>();
        while (r.Read())
            list.Add(new ClientListItem
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                Email = r.GetString(2),
                Address = r.GetString(3),
                PhonesText = r.GetString(4),
            });
        return list;
    }

    public Client? LoadClient(int id)
    {
        using var c = Open();
        Client client;
        using (var cmd = Cmd(c, "SELECT Id, Name, Email, Address, Notes FROM Clients WHERE Id = @id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            client = new Client
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                Email = r.GetString(2),
                Address = r.GetString(3),
                Notes = r.GetString(4),
            };
        }
        using (var cmd = Cmd(c, "SELECT Id, Number, Note FROM Phones WHERE ClientId = @id ORDER BY Id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                client.Phones.Add(new Phone { Id = r.GetInt32(0), Number = r.GetString(1), Note = r.GetString(2) });
        }
        return client;
    }

    /// <summary>Сохраняет клиента с телефонами (пустые номера пропускаются). Возвращает Id клиента.</summary>
    public int SaveClient(Client client)
    {
        using var c = Open();
        var id = client.Id;
        InTransaction(c, () =>
        {
            var args = new (string, object?)[]
            {
                ("@name", client.Name.Trim()), ("@email", client.Email.Trim()),
                ("@address", client.Address.Trim()), ("@notes", client.Notes.Trim()), ("@id", id),
            };
            if (id == 0)
            {
                Exec(c, "INSERT INTO Clients (Name, Email, Address, Notes) VALUES (@name, @email, @address, @notes)", args);
                id = (int)Scalar(c, "SELECT last_insert_rowid()");
            }
            else
            {
                Exec(c, "UPDATE Clients SET Name=@name, Email=@email, Address=@address, Notes=@notes WHERE Id=@id", args);
            }

            Exec(c, "DELETE FROM Phones WHERE ClientId = @id", ("@id", id));
            foreach (var p in client.Phones.Where(p => !string.IsNullOrWhiteSpace(p.Number)))
                Exec(c, "INSERT INTO Phones (ClientId, Number, Note) VALUES (@id, @number, @note)",
                    ("@id", id), ("@number", p.Number.Trim()), ("@note", p.Note.Trim()));
        });
        return id;
    }

    public void DeleteClient(int id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM Clients WHERE Id = @id", ("@id", id));
    }

    // ---------- изготовители ----------

    public List<Manufacturer> GetManufacturers()
    {
        using var c = Open();
        return ReadManufacturers(c);
    }

    static List<Manufacturer> ReadManufacturers(SqliteConnection c)
    {
        using var cmd = Cmd(c, "SELECT Id, Name FROM Manufacturers");
        using var r = cmd.ExecuteReader();
        var list = new List<Manufacturer>();
        while (r.Read()) list.Add(new Manufacturer(r.GetInt32(0), r.GetString(1)));
        return list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public int AddManufacturer(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Название изготовителя не может быть пустым.");
        using var c = Open();
        EnsureUnique(c, name, exceptId: 0);
        Exec(c, "INSERT INTO Manufacturers (Name) VALUES (@n)", ("@n", name));
        return (int)Scalar(c, "SELECT last_insert_rowid()");
    }

    public void RenameManufacturer(int id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Название изготовителя не может быть пустым.");
        using var c = Open();
        EnsureUnique(c, name, exceptId: id);
        Exec(c, "UPDATE Manufacturers SET Name = @n WHERE Id = @id", ("@n", name), ("@id", id));
    }

    static void EnsureUnique(SqliteConnection c, string name, int exceptId)
    {
        if (ReadManufacturers(c).Any(m => m.Id != exceptId &&
                string.Equals(m.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException($"Изготовитель «{name}» уже есть в списке.");
    }

    /// <summary>Сколько подзаказов в производство ссылаются на этого изготовителя.</summary>
    public int CountManufacturerUsage(int id)
    {
        using var c = Open();
        return (int)Scalar(c, "SELECT COUNT(*) FROM ProductionOrders WHERE ManufacturerId = @id", ("@id", id));
    }

    public void DeleteManufacturer(int id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM Manufacturers WHERE Id = @id", ("@id", id));
    }

    // ---------- заказы ----------

    public List<OrderSummary> GetOrders(int clientId)
    {
        using var c = Open();
        var list = new List<OrderSummary>();
        using (var cmd = Cmd(c, """
            SELECT o.Id, o.OrderDate, o.Name,
                   IFNULL(group_concat(i.ProductName, ', '), ''),
                   o.CostKop,
                   o.PriceKop,
                   o.Number
            FROM Orders o
            LEFT JOIN OrderItems i ON i.OrderId = o.Id
            WHERE o.ClientId = @c
            GROUP BY o.Id
            ORDER BY o.OrderDate DESC, o.Id DESC
            """, ("@c", clientId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                list.Add(new OrderSummary
                {
                    Id = r.GetInt32(0),
                    Date = ParseDate(r.GetString(1)),
                    Name = r.GetString(2),
                    Products = r.GetString(3),
                    Cost = r.GetInt64(4) / 100m,
                    Price = r.GetInt64(5) / 100m,
                    Number = r.GetInt32(6),
                });
        }

        // Статус заказа складывается из состояния его заказов в производство.
        var statuses = new Dictionary<int, List<string>>();
        using (var cmd = Cmd(c, """
            SELECT po.OrderId, po.SentDate,
                   (SELECT COUNT(*) FROM Materials x WHERE x.ProductionOrderId = po.Id),
                   (SELECT COUNT(*) FROM Materials x WHERE x.ProductionOrderId = po.Id AND x.IsReady = 1)
            FROM ProductionOrders po
            JOIN Orders o ON o.Id = po.OrderId
            WHERE o.ClientId = @c
            """, ("@c", clientId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var orderId = r.GetInt32(0);
                if (!statuses.TryGetValue(orderId, out var l)) statuses[orderId] = l = new();
                l.Add(ProductionOrder.StatusFor(ReadDate(r, 1), r.GetInt32(2), r.GetInt32(3)));
            }
        }
        foreach (var o in list)
            o.Status = OrderSummary.StatusFor(statuses.GetValueOrDefault(o.Id) ?? new());

        return list;
    }

    /// <summary>
    /// Номер, который получит следующий новый заказ этого клиента: у каждого клиента своя нумерация с 1
    /// (наибольший номер среди его заказов + 1).
    /// </summary>
    public int NextOrderNumber(int clientId)
    {
        using var c = Open();
        return (int)Scalar(c, "SELECT IFNULL(MAX(Number), 0) + 1 FROM Orders WHERE ClientId = @c", ("@c", clientId));
    }

    public Order? LoadOrder(int id)
    {
        using var c = Open();
        var makers = ReadManufacturers(c).ToDictionary(m => m.Id);

        Order order;
        using (var cmd = Cmd(c, "SELECT Id, ClientId, OrderDate, Name, Notes, ProjectPhoto, ProjectFolder, CostKop, PriceKop, Number FROM Orders WHERE Id = @id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            order = new Order
            {
                Id = r.GetInt32(0),
                ClientId = r.GetInt32(1),
                Date = ParseDate(r.GetString(2)),
                Name = r.GetString(3),
                Notes = r.GetString(4),
                ProjectPhoto = r.GetString(5),
                ProjectFolder = r.GetString(6),
                Cost = r.GetInt64(7) / 100m,
                Price = r.GetInt64(8) / 100m,
                Number = r.GetInt32(9),
            };
        }

        var productions = new Dictionary<int, ProductionOrder>();
        using (var cmd = Cmd(c, "SELECT Id, SubNumber, ManufacturerId, ProductionNumber, SentDate, ReadyDate, Color FROM ProductionOrders WHERE OrderId = @id ORDER BY SubNumber, Id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var p = new ProductionOrder
                {
                    Id = r.GetInt32(0),
                    Sub = r.GetInt32(1),
                    OrderNumber = order.Number,
                    Manufacturer = !r.IsDBNull(2) && makers.TryGetValue(r.GetInt32(2), out var m) ? m : null,
                    ProductionNumber = r.GetString(3),
                    SentDate = ReadDate(r, 4),
                    ReadyDate = ReadDate(r, 5),
                    ColorHex = r.GetString(6),
                };
                productions[p.Id] = p;
                order.Productions.Add(p);
            }
        }

        using (var cmd = Cmd(c, "SELECT Id, ProductName, Quantity, Photo, SourceNo FROM OrderItems WHERE OrderId = @id ORDER BY Id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                order.Items.Add(new OrderItem
                {
                    Id = r.GetInt32(0),
                    ProductName = r.GetString(1),
                    Quantity = r.GetInt32(2),
                    Photo = r.GetString(3),
                    SourceNo = r.IsDBNull(4) ? null : r.GetInt32(4),
                });
        }

        using (var cmd = Cmd(c, "SELECT Id, Name, Amount, IsReady, ProductionOrderId FROM Materials WHERE OrderId = @id ORDER BY Id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                order.Materials.Add(new Material
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    Amount = r.GetString(2),
                    IsReady = r.GetInt32(3) != 0,
                    Assigned = !r.IsDBNull(4) && productions.TryGetValue(r.GetInt32(4), out var p) ? p : null,
                });
        }

        using (var cmd = Cmd(c, "SELECT Id, Name, Amount FROM Hardware WHERE OrderId = @id ORDER BY Id", ("@id", id)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                order.Hardware.Add(new HardwareItem { Id = r.GetInt32(0), Name = r.GetString(1), Amount = r.GetString(2) });
        }

        order.SetManufacturers(makers.Values);
        order.AcceptChanges();
        return order;
    }

    /// <summary>Сохраняет заказ целиком: подзаказы, изделия, материалы. Возвращает Id заказа.</summary>
    public int SaveOrder(Order order)
    {
        using var c = Open();
        var id = order.Id;
        InTransaction(c, () =>
        {
            var args = new (string, object?)[]
            {
                ("@client", order.ClientId),
                ("@date", (order.Date ?? DateTime.Today).ToString(DateFormat, CultureInfo.InvariantCulture)),
                ("@name", order.Name.Trim()), ("@notes", order.Notes.Trim()),
                ("@photo", order.ProjectPhoto), ("@folder", order.ProjectFolder.Trim()),
                ("@cost", (long)Math.Round(order.Cost * 100m)), ("@price", (long)Math.Round(order.Price * 100m)),
                ("@id", id),
            };
            if (id == 0)
            {
                // Id — внутренний ключ (общий счётчик). Номер заказа у каждого клиента свой: наибольший номер
                // среди его заказов + 1 (1, если заказов нет); после удаления последнего заказа номер освобождается.
                id = (int)Scalar(c, "SELECT IFNULL(MAX(Id), 0) + 1 FROM Orders");
                var number = (int)Scalar(c, "SELECT IFNULL(MAX(Number), 0) + 1 FROM Orders WHERE ClientId = @client", ("@client", order.ClientId));
                Exec(c, "INSERT INTO Orders (Id, ClientId, Number, OrderDate, Name, Notes, ProjectPhoto, ProjectFolder, CostKop, PriceKop) VALUES (@newId, @client, @number, @date, @name, @notes, @photo, @folder, @cost, @price)",
                    args.Append(("@newId", (object?)id)).Append(("@number", (object?)number)).ToArray());
            }
            else
            {
                Exec(c, "UPDATE Orders SET OrderDate=@date, Name=@name, Notes=@notes, ProjectPhoto=@photo, ProjectFolder=@folder, CostKop=@cost, PriceKop=@price WHERE Id=@id", args);
            }

            Exec(c, "DELETE FROM OrderItems WHERE OrderId = @id", ("@id", id));
            Exec(c, "DELETE FROM Materials WHERE OrderId = @id", ("@id", id));
            Exec(c, "DELETE FROM Hardware WHERE OrderId = @id", ("@id", id));
            Exec(c, "DELETE FROM ProductionOrders WHERE OrderId = @id", ("@id", id));

            var ids = new Dictionary<ProductionOrder, int>();
            foreach (var p in order.Productions)
            {
                Exec(c, "INSERT INTO ProductionOrders (OrderId, SubNumber, ManufacturerId, ProductionNumber, SentDate, ReadyDate, Color) VALUES (@o, @sub, @m, @num, @sent, @ready, @color)",
                    ("@o", id), ("@sub", p.Sub), ("@m", p.Manufacturer?.Id), ("@num", p.ProductionNumber.Trim()),
                    ("@sent", FormatDate(p.SentDate)), ("@ready", FormatDate(p.ReadyDate)), ("@color", p.ColorHex));
                ids[p] = (int)Scalar(c, "SELECT last_insert_rowid()");
            }

            // «Не назначено» и служебные пункты «＋ Изготовитель» в базу не попадают.
            object? ProductionId(ProductionOrder? p) =>
                p is { IsPlaceholder: false, IsVirtual: false } && ids.TryGetValue(p, out var pid) ? pid : null;

            foreach (var i in order.Items)
                // UnitPriceKop — устаревшая колонка (NOT NULL), цен у изделий больше нет.
                Exec(c, "INSERT INTO OrderItems (OrderId, ProductName, Quantity, UnitPriceKop, Photo, SourceNo) VALUES (@o, @name, @qty, 0, @photo, @src)",
                    ("@o", id), ("@name", i.ProductName.Trim()), ("@qty", i.Quantity),
                    ("@photo", i.Photo), ("@src", i.SourceNo));

            foreach (var m in order.Materials)
                Exec(c, "INSERT INTO Materials (OrderId, Name, Amount, IsReady, ProductionOrderId) VALUES (@o, @name, @amount, @ready, @po)",
                    ("@o", id), ("@name", m.Name.Trim()), ("@amount", m.Amount.Trim()),
                    ("@ready", m.IsReady ? 1 : 0), ("@po", ProductionId(m.Assigned)));

            foreach (var h in order.Hardware)
                Exec(c, "INSERT INTO Hardware (OrderId, Name, Amount) VALUES (@o, @name, @amount)",
                    ("@o", id), ("@name", h.Name.Trim()), ("@amount", h.Amount.Trim()));
        });
        return id;
    }

    public void DeleteOrder(int id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM Orders WHERE Id = @id", ("@id", id));
    }

    // ---------- фото ----------

    /// <summary>Имена всех файлов фото, принадлежащих заказу (чтобы убрать их при удалении).</summary>
    public List<string> GetOrderPhotos(int orderId) => ReadPhotoNames("""
        SELECT ProjectPhoto FROM Orders WHERE Id = @id
        UNION ALL
        SELECT Photo FROM OrderItems WHERE OrderId = @id
        """, orderId);

    public List<string> GetClientPhotos(int clientId) => ReadPhotoNames("""
        SELECT ProjectPhoto FROM Orders WHERE ClientId = @id
        UNION ALL
        SELECT i.Photo FROM OrderItems i JOIN Orders o ON o.Id = i.OrderId WHERE o.ClientId = @id
        """, clientId);

    List<string> ReadPhotoNames(string sql, int id)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, ("@id", id));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
            if (r.GetString(0) is { Length: > 0 } name) list.Add(name);
        return list;
    }

    // ---------- даты ----------

    static string? FormatDate(DateTime? d) => d?.ToString(DateFormat, CultureInfo.InvariantCulture);

    static DateTime? ReadDate(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : ParseDate(r.GetString(index));

    static DateTime ParseDate(string s) => DateTime.ParseExact(s, DateFormat, CultureInfo.InvariantCulture);
}
