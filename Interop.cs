using System.Runtime.InteropServices;
using System.Text;

namespace SpotifyTaskbarWidget;

internal static class Interop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    /// <summary>Barras de tarefas dos monitores secundários (Win11: Shell_SecondaryTrayWnd),
    /// ordenadas pela POSIÇÃO do monitor (esquerda→direita, cima→baixo). A enumeração
    /// crua vem por z-order, que muda a todo o momento — sem ordenação estável,
    /// "Monitor 2" e "Monitor 3" trocavam de identidade e o widget saltava entre eles.</summary>
    public static List<IntPtr> GetSecondaryTrays()
    {
        var list = new List<(IntPtr Handle, int Left, int Top)>();
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            GetWindowRect(h, out RECT r);
            list.Add((h, r.Left, r.Top));
        }
        return list.OrderBy(t => t.Left).ThenBy(t => t.Top).Select(t => t.Handle).ToList();
    }

    /// <summary>Limite esquerdo (px físicos) da área de ícones do sistema (relógio, rede…).</summary>
    public static int? GetTrayNotifyLeft(IntPtr tray)
    {
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out RECT r))
            return null;
        return r.Left;
    }

    /// <summary>Píxeis da barra atualmente dentro do ecrã (para saber se está
    /// assente, escondida, ou a meio da animação de revelar/esconder).
    /// Devolve também o fundo do monitor "casa" da barra, para o recorte.</summary>
    public static int GetTaskbarVisiblePx(RECT trayRect, out int monitorBottomPx)
    {
        IntPtr mon = GetTrayMonitor(trayRect);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi))
        {
            monitorBottomPx = trayRect.Bottom;
            return trayRect.Bottom - trayRect.Top; // sem info: assumir assente
        }
        monitorBottomPx = mi.rcMonitor.Bottom;
        return Math.Min(trayRect.Bottom, mi.rcMonitor.Bottom) - Math.Max(trayRect.Top, mi.rcMonitor.Top);
    }

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private static bool _windowClipped;

    /// <summary>Recorta a janela ao que cabe acima do fundo do ecrã enquanto a
    /// barra desliza (ocultação automática). Sem recorte, a parte que já "saiu"
    /// continuava a desenhar-se — visível num monitor disposto abaixo.
    /// O sistema fica dono da região após SetWindowRgn (não a libertar).</summary>
    public static void ClipWindowBottom(IntPtr hwnd, int widthPx, int heightPx, int visibleHeightPx)
    {
        if (visibleHeightPx >= heightPx)
        {
            if (_windowClipped)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                _windowClipped = false;
            }
            return;
        }
        SetWindowRgn(hwnd, CreateRectRgn(0, 0, widthPx, Math.Max(0, visibleHeightPx)), true);
        _windowClipped = true;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>True se a ocultação automática da barra estiver ativa. Nesse modo,
    /// as janelas maximizadas ocupam o ecrã todo e "parecem" fullscreen — o teste
    /// de ecrã inteiro deixa de ser fiável (e é redundante: a visibilidade do
    /// widget já segue a da barra).</summary>
    public static bool IsAutoHideEnabled()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return ((ulong)SHAppBarMessage(4 /*ABM_GETSTATE*/, ref data) & 1 /*ABS_AUTOHIDE*/) != 0;
    }

    public const byte VK_SHIFT = 0x10;
    public const byte VK_MENU = 0x12;
    public const byte VK_B = 0x42;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>Monitor "casa" da barra de tarefas. Com ocultação automática, o
    /// rect da barra desliza para FORA do ecrã — se houver um monitor disposto
    /// abaixo, MonitorFromWindow devolve esse vizinho e as contas de visibilidade
    /// saem contra o ecrã errado. O ponto logo acima da barra não sofre disso.</summary>
    private static IntPtr GetTrayMonitor(RECT trayRect)
    {
        var pt = new POINT { X = (trayRect.Left + trayRect.Right) / 2, Y = trayRect.Top - 10 };
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void EnsureTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    private const uint SWP_NOZORDER = 0x0004;

    /// <summary>Move a janela para coordenadas FÍSICAS (px). Posicionar em px puros
    /// evita as contas erradas de DIP entre monitores com escalas diferentes.</summary>
    public static void MoveWindowTo(IntPtr hwnd, int xPx, int yPx) =>
        SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // Hook de eventos do sistema: reagir no instante em que a janela ativa muda
    // (clicar na taskbar traz a barra para cima do widget até re-afirmarmos)
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>
    /// True se a janela em primeiro plano estiver em ecrã inteiro no mesmo monitor
    /// da barra de tarefas (jogos, vídeos) — nesse caso o widget deve esconder-se.
    /// </summary>
    public static bool IsForegroundFullscreen(IntPtr self, IntPtr tray)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || fg == tray)
            return false;

        var sb = new StringBuilder(256);
        GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow")
            return false;

        // CoreWindow só é inocente quando pertence à shell (menu Iniciar,
        // pesquisa, centro de notificações). Jogos/apps UWP em ecrã inteiro
        // (ex.: Forza da Store) usam a MESMA classe — excluí-la às cegas
        // deixava o widget visível por cima do jogo (issue #5).
        if (cls is "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
        {
            GetWindowThreadProcessId(fg, out uint pid);
            try
            {
                string proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                if (proc is "explorer" or "StartMenuExperienceHost" or "SearchHost"
                    or "ShellExperienceHost" or "ShellHost" or "SearchApp" or "LockApp")
                    return false;
            }
            catch
            {
                return false; // processo já morreu: tratar como shell (inócuo)
            }
        }

        IntPtr monFg = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        IntPtr monTray = GetWindowRect(tray, out RECT tr)
            ? GetTrayMonitor(tr)
            : MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
        if (monFg != monTray)
            return false;

        if (!GetWindowRect(fg, out RECT wr))
            return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monFg, ref mi))
            return false;

        return wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
            && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
    }
}
