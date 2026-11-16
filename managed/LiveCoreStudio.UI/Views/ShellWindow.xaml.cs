using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LiveCoreStudio.UI.ViewModels;

namespace LiveCoreStudio.UI.Views;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    // Marks the rail item of the page the shell opened on.
    private void NavItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && DataContext is ShellViewModel shell)
            rb.IsChecked = ReferenceEquals(rb.DataContext, shell.CurrentPage);
    }

    // Windows 10 20H1+ / Windows 11: dark caption to match the charcoal UI.
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        int caption = 0x00141211; // COLORREF (0x00BBGGRR) for #111214
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCaptionColor, ref caption, sizeof(int));
    }

    private static class NativeMethods
    {
        public const int DwmwaUseImmersiveDarkMode = 20;
        public const int DwmwaCaptionColor = 35;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
