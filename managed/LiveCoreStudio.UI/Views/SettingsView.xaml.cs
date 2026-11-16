using System.Windows;
using System.Windows.Controls;
using LiveCoreStudio.UI.ViewModels;
using Microsoft.Win32;

namespace LiveCoreStudio.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select LiveCore.MediaEngine",
            Filter = "Media engine|LiveCore.MediaEngine.exe|Executables|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) vm.ExecutablePath = dialog.FileName;
    }
}
