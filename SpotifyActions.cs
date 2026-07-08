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

        IntPtr previous = Interop.GetForegroundWindow();

        // "Toque" no Alt liberta a restrição do SetForegroundWindow
        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.SetForegroundWindow(proc.MainWindowHandle);
        Thread.Sleep(120);

        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(120);

        if (previous != IntPtr.Zero)
            Interop.SetForegroundWindow(previous);
    }

    public static void OpenSpotifyWindow()
    {
        var proc = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc != null)
        {
            Interop.ShowWindow(proc.MainWindowHandle, Interop.SW_RESTORE);
            Interop.SetForegroundWindow(proc.MainWindowHandle);
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
