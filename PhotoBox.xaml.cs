using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ClientBase;

/// <summary>Блок с фото: показ, загрузка из файла / буфера обмена / перетаскиванием, увеличение по щелчку.</summary>
public partial class PhotoBox : UserControl
{
    const string AppTitle = "База клиентов";
    const string ImageFilter = "Фотографии|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|Все файлы|*.*";

    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(string), typeof(PhotoBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((PhotoBox)d).Refresh()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PhotoBox),
        new PropertyMetadata("", (d, e) => ((PhotoBox)d).TitleText.Text = e.NewValue as string ?? ""));

    /// <summary>Имя файла в хранилище фото (пусто — фото нет).</summary>
    public string Photo
    {
        get => GetValue(PhotoProperty) as string ?? "";
        set => SetValue(PhotoProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty) as string ?? "";
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Новый файл добавлен в хранилище (чтобы окно могло убрать его, если заказ не сохранят).</summary>
    public event EventHandler<string>? FileAdded;

    public PhotoBox()
    {
        InitializeComponent();
        IsEnabledChanged += (_, _) => Refresh();
        Refresh();
    }

    void Refresh()
    {
        var name = Photo;
        var image = LoadBitmap(name, 1600);
        Img.Source = image;
        Hint.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = !IsEnabled
            ? "Выберите изделие в таблице,\nчтобы посмотреть или добавить его фото"
            : name.Length > 0 ? "Файл фото не найден" : "Нет фото\nПеретащите файл сюда или нажмите «Загрузить»";
        RemoveButton.IsEnabled = name.Length > 0;
        Img.Cursor = image == null ? Cursors.Arrow : Cursors.Hand;
    }

    /// <summary>Читает картинку целиком в память (файл остаётся свободным для удаления).</summary>
    public static BitmapImage? LoadBitmap(string name, int decodeWidth = 0)
    {
        if (!PhotoStore.Exists(name)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(PhotoStore.FullPath(name));
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    void Load_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Выберите фото", Filter = ImageFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SetFromFile(dialog.FileName);
    }

    void Photo_Drop(object sender, DragEventArgs e)
    {
        if (!IsEnabled || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        SetFromFile(files[0]);
    }

    void SetFromFile(string path)
    {
        try
        {
            // Проверяем, что это действительно картинка, до копирования.
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth <= 0) throw new NotSupportedException();
            Accept(PhotoStore.Add(path));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            Dialogs.Show(Window.GetWindow(this), "Не удалось открыть этот файл как картинку.\nПоддерживаются JPG, PNG, BMP, GIF, TIFF.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
            {
                Dialogs.Show(Window.GetWindow(this),
                    "В буфере обмена нет картинки.\nСкопируйте изображение (например, скриншот) и нажмите кнопку снова.",
                    AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var name = PhotoStore.Reserve(".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(PhotoStore.FullPath(name))) encoder.Save(stream);
            Accept(name);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Dialogs.Show(Window.GetWindow(this), "Буфер обмена сейчас занят другой программой. Повторите попытку.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    void Accept(string name)
    {
        FileAdded?.Invoke(this, name);
        Photo = name;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm.Delete(Window.GetWindow(this), "Убрать это фото?")) Photo = "";
    }

    void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if (Img.Source == null) return;
        new PhotoViewer(Photo, Title) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
