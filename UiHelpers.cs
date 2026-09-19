using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;

namespace ClientBase;

/// <summary>Показывает элемент, только когда число (например, количество записей) равно нулю.</summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Единое подтверждение для любого удаления.</summary>
public static class Confirm
{
    public const string AppTitle = "База клиентов";

    public static bool Delete(Window? owner, string question)
    {
        var text = question + "\n\nЭто действие нельзя отменить.";
        var answer = owner is null
            ? MessageBox.Show(text, "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, text, "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }
}

/// <summary>
/// Фирменная окраска заголовка и рамки окна (Windows 11): приложение сразу видно среди других открытых окон.
/// На старых версиях Windows вызов молча ничего не делает.
/// </summary>
public static class Branding
{
    const int DwmwaBorderColor = 34;
    const int DwmwaCaptionColor = 35;
    const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // COLORREF = 0x00BBGGRR
    static int Rgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Set(handle, DwmwaCaptionColor, Rgb(0x1E, 0x2A, 0x32));   // графит
            Set(handle, DwmwaTextColor, Rgb(0xFF, 0xFF, 0xFF));
            Set(handle, DwmwaBorderColor, Rgb(0xE0, 0x7B, 0x25));    // дубовый акцент
        };
    }

    static void Set(IntPtr handle, int attribute, int value)
    {
        try { DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

/// <summary>Запоминает положение и размер окон между запусками (файл «Данные\window.json»).</summary>
public static class WindowMemory
{
    record Bounds(double Left, double Top, double Width, double Height, bool Maximized);

    public static string? Directory { get; set; }

    static string? FilePath => Directory is null ? null : Path.Combine(Directory, "window.json");

    public static void Restore(Window window, string key)
    {
        try
        {
            if (FilePath is not { } path || !File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Bounds>>(File.ReadAllText(path));
            if (all is null || !all.TryGetValue(key, out var b)) return;

            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var rect = new Rect(b.Left, b.Top, Math.Max(b.Width, window.MinWidth), Math.Max(b.Height, window.MinHeight));
            // Окно должно оказаться на экране (монитор мог быть отключён).
            if (!screen.IntersectsWith(new Rect(rect.Left + 40, rect.Top + 20, Math.Max(rect.Width - 80, 1), 40))) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = rect.Left;
            window.Top = rect.Top;
            window.Width = rect.Width;
            window.Height = rect.Height;
            if (b.Maximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public static void Save(Window window, string key)
    {
        try
        {
            if (FilePath is not { } path) return;
            var all = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Bounds>>(File.ReadAllText(path)) ?? new()
                : new Dictionary<string, Bounds>();
            var r = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            all[key] = new Bounds(r.Left, r.Top, r.Width, r.Height, window.WindowState == WindowState.Maximized);
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
