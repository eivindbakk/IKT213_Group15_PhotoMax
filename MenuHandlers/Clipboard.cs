// File: MenuHandlers/Clipboard.cs
using System.Windows;

namespace PhotoMax
{
    public partial class MainWindow
    {
        private void Clipboard_Copy_Click(object sender, RoutedEventArgs e)
            => _img?.CopySelectionToClipboard();

        private void Clipboard_Paste_Click(object sender, RoutedEventArgs e)
            => _img?.PasteFromClipboard();

        private void Clipboard_Cut_Click(object sender, RoutedEventArgs e)
            => _img?.CutSelectionToClipboard();
    }
}