using System.Diagnostics;

namespace SpotifyTaskbarWidget;

internal static class SpotifyActions
{
    /// <summary>
    /// Adiciona/remove a faixa atual dos favoritos enviando o atalho oficial do
    /// Spotify (Alt+Shift+B) à janela dele. A API de media do Windows não expõe
    /// "guardar nos favoritos", por isso é preciso dar foco brevemente ao Spotify.
    /// Correr numa thread de background (usa Sleep).
    /// </summary>
    public static void LikeCurrentTrack()
    {
        var proc = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc == null) return;

        IntPtr spotify = proc.MainWindowHandle;
        IntPtr previous = Interop.GetForegroundWindow();

        // Minimizada, a janela pode não processar o atalho — restaurar por instantes
        bool wasMinimized = Interop.IsIconic(spotify);
        if (wasMinimized)
        {
            Interop.ShowWindow(spotify, Interop.SW_RESTORE);
            Thread.Sleep(250);
        }

        // "Toque" no Alt liberta a restrição do SetForegroundWindow
        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.SetForegroundWindow(spotify);
        Thread.Sleep(150);

        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(200);

        if (wasMinimized)
            Interop.ShowWindow(spotify, Interop.SW_MINIMIZE);
        if (previous != IntPtr.Zero)
            Interop.SetForegroundWindow(previous);
    }

    public static void OpenSpotifyWindow()
    {
        var proc = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc != null)
        {
            if (Interop.GetForegroundWindow() == proc.MainWindowHandle)
            {
                Interop.ShowWindow(proc.MainWindowHandle, Interop.SW_MINIMIZE);
            }
            else
            {
                if (Interop.IsIconic(proc.MainWindowHandle))
                    Interop.ShowWindow(proc.MainWindowHandle, Interop.SW_RESTORE);
                Interop.SetForegroundWindow(proc.MainWindowHandle);
            }
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
