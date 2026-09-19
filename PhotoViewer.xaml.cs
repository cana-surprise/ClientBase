using System.Windows;
using System.Windows.Input;

namespace ClientBase;

public partial class PhotoViewer : Window
{
    public PhotoViewer(string photoName, string title)
    {
        InitializeComponent();
        Branding.Apply(this);
        Title = string.IsNullOrWhiteSpace(title) ? "Фото" : title;
        Img.Source = PhotoBox.LoadBitmap(photoName);
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    void Img_Click(object sender, MouseButtonEventArgs e) => Close();
}
