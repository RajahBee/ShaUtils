using System;
using System.Linq;
using System.Windows;

namespace ShaUtils;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        var mainWindow = new MainWindow(args);
        mainWindow.Show();
    }
}