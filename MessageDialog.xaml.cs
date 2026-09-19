using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClientBase;

/// <summary>
/// Окно сообщения в оформлении программы (вместо системного MessageBox): тот же фон, шрифт, цветные кнопки
/// и цветной заголовок, что и у остальных окон.
/// </summary>
public partial class MessageDialog : Window
{
    readonly MessageBoxButton _buttons;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public MessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult, bool destructive)
    {
        InitializeComponent();
        Branding.Apply(this);
        Title = caption;
        MessageText.Text = text;
        _buttons = buttons;
        ShowIcon(icon);
        BuildButtons(defaultResult, destructive);
    }

    void ShowIcon(MessageBoxImage icon)
    {
        // Значок: цветной кружок с символом. Для обычных сообщений без значка кружок не показывается.
        var (glyph, fill, ink) = icon switch
        {
            MessageBoxImage.Question => ("?", "AccentSoftBrush", "AccentBrush"),
            MessageBoxImage.Information => ("i", "AccentSoftBrush", "AccentBrush"),
            MessageBoxImage.Warning => ("!", "WarnSoftBrush", "WarnBrush"),
            MessageBoxImage.Error => ("✕", "DangerSoftBrush", "DangerBrush"),
            _ => ("", "", ""),
        };
        if (glyph.Length == 0) return;

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = (Brush)FindResource(ink);
        IconBadge.Background = (Brush)FindResource(fill);
        IconBadge.Visibility = Visibility.Visible;
    }

    void BuildButtons(MessageBoxResult defaultResult, bool destructive)
    {
        var results = _buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            _ => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
        };
        if (defaultResult == MessageBoxResult.None || !results.Contains(defaultResult)) defaultResult = results[0];

        foreach (var result in results)
        {
            var isDefault = result == defaultResult;
            var button = new Button
            {
                Content = result switch
                {
                    MessageBoxResult.Yes => "Да",
                    MessageBoxResult.No => "Нет",
                    MessageBoxResult.Cancel => "Отмена",
                    _ => "ОК",
                },
                MinWidth = 100,
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(results[0] == result ? 0 : 8, 0, 0, 0),
                IsDefault = isDefault,
            };

            // Главная кнопка (по умолчанию) — яркая. При удалении «Да» — красная, чтобы не нажать по привычке.
            if (destructive && result == MessageBoxResult.Yes)
                button.Style = (Style)FindResource("DangerButton");
            else if (isDefault)
                button.Style = (Style)FindResource("PrimaryButton");

            var captured = result;
            button.Click += (_, _) =>
            {
                Result = captured;
                Close();
            };
            ButtonsPanel.Children.Add(button);
        }

        Loaded += (_, _) =>
        {
            foreach (Button b in ButtonsPanel.Children)
                if (b.IsDefault) b.Focus();
        };
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc = «Отмена», а если её нет — «Нет», а если нет и её — «ОК» (как в системном окне).
            Result = _buttons switch
            {
                MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.OK,
            };
            Close();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try { Clipboard.SetText(MessageText.Text); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }
}

/// <summary>Замена MessageBox.Show: те же параметры, но окно в оформлении программы.</summary>
public static class Dialogs
{
    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, bool destructive = false)
    {
        var dialog = new MessageDialog(text, caption, button, icon, defaultResult, destructive);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }

    public static MessageBoxResult Show(string text, string caption,
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, bool destructive = false) =>
        Show(null, text, caption, button, icon, defaultResult, destructive);
}
