using System.IO;
using System.Windows;

namespace SpotifyTaskbarWidget;

public partial class App : Application
{
    private static Mutex? _mutex;

    /// <summary>True apenas quando o utilizador saiu de propósito (ou update);
    /// false quando a janela morre com o Explorer e deve ser recriada.</summary>
    public static bool IntentionalExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SpotifyTaskbarWidget_SingleInstance", out bool isNew);
        if (!isNew)
        {
            IntentionalExit = true;
            Shutdown();
            return;
        }

        // A janela pode ser destruída por um reinício do Explorer (é owned pela
        // taskbar) e recriada — a app só termina quando o utilizador manda
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarWidget");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "errors.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n");
            }
            catch { }
            args.Handled = true;
        };

        base.OnStartup(e);

        // Uma janela de widget por barra selecionada nas definições
        SpotifyTaskbarWidget.MainWindow.SyncToMonitors();
    }
}
