using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace OsuInstafade.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // osu! 느낌의 분홍색 강조 색
        ApplicationAccentColorManager.Apply(Color.FromRgb(0xC2, 0x18, 0x5B), ApplicationTheme.Dark);
        var w = new MainWindow(e.Args);
        w.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("예상하지 못한 오류가 발생했습니다.\n\n" + e.Exception.Message,
            "osu! Instafade by LTH", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
