using System.Windows;
using System.Windows.Media;

namespace AIHub.Desktop;

/// <summary>Semantic resources shared by XAML and programmatically rendered documents/dialogs.</summary>
internal static class Theme
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);
}
