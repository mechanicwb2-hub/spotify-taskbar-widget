using System.Globalization;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Textos da interface: português quando o Windows está em PT (PT/BR),
/// inglês para o resto do mundo.
/// </summary>
internal static class L
{
    private static readonly bool Pt =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);

    public const string AppTitle = "Taskbar Widget for Spotify";

    // Menu
    public static string MoveWidget => Pt ? "Mover widget" : "Move widget";
    public static string MoveWidgetTip => Pt
        ? "Arrasta o widget para onde quiseres; desmarca para bloquear nessa posição"
        : "Drag the widget wherever you want; untick to lock it in place";
    public static string ResetAutoPos => Pt ? "Repor posição automática" : "Reset to automatic position";
    public static string MonitorMenu => Pt ? "Monitor" : "Monitor";
    public static string MonitorPrimary => Pt ? "Principal" : "Primary";
    public static string MonitorN(int n) => Pt ? $"Monitor {n}" : $"Monitor {n}";
    public static string SizeMenu => Pt ? "Tamanho" : "Size";
    public static string SizeSmall => Pt ? "Pequeno" : "Small";
    public static string SizeNormal => Pt ? "Normal" : "Normal";
    public static string SizeLarge => Pt ? "Grande" : "Large";
    public static string ButtonsMenu => Pt ? "Botões" : "Buttons";
    public static string BtnLike => Pt ? "Adicionar aos favoritos (+)" : "Add to favorites (+)";
    public static string BtnShuffle => Pt ? "Modo aleatório" : "Shuffle";
    public static string BtnPrev => Pt ? "Anterior" : "Previous";
    public static string BtnNext => Pt ? "Seguinte" : "Next";
    public static string BtnRepeat => Pt ? "Repetição" : "Repeat";
    public static string BtnVolume => Pt ? "Volume" : "Volume";
    public static string ProgressBar => Pt ? "Barra de progresso" : "Progress bar";
    public static string ShowLauncher => Pt ? "Mostrar botão para abrir o Spotify" : "Show button to open Spotify";
    public static string ShowLauncherTip => Pt
        ? "Com o Spotify fechado, mostra um botão para o abrir em vez de esconder o widget"
        : "When Spotify is closed, show a button to open it instead of hiding the widget";
    public static string AutoStart => Pt ? "Iniciar com o Windows" : "Start with Windows";
    public static string OpenSpotify => Pt ? "Abrir Spotify" : "Open Spotify";
    public static string CheckUpdates => Pt ? "Procurar atualizações" : "Check for updates";
    public static string Exit => Pt ? "Sair" : "Quit";

    // Tooltips / estados
    public static string TipPrev => Pt ? "Anterior" : "Previous";
    public static string TipPlayPause => Pt ? "Reproduzir/Pausar" : "Play/Pause";
    public static string TipNext => Pt ? "Seguinte" : "Next";
    public static string TipVolume => Pt ? "Volume do Spotify" : "Spotify volume";
    public static string TipRepeat => Pt ? "Repetição" : "Repeat";
    public static string TipShuffle => Pt ? "Modo aleatório" : "Shuffle";
    public static string TipShuffleOn => Pt ? "Modo aleatório ativo" : "Shuffle on";
    public static string TipShuffleSmart => Pt ? "Modo aleatório inteligente ativo" : "Smart Shuffle on";
    public static string TipLikeAdd => Pt ? "Adicionar aos favoritos do Spotify" : "Add to your Spotify favorites";
    public static string TipLiked => Pt ? "Já está nos favoritos" : "Already in your favorites";
    public static string NothingPlaying => Pt ? "nada a tocar" : "nothing playing";
    public static string TipOpenSpotify => Pt ? "Abrir o Spotify" : "Open Spotify";

    // Atualizações
    public static string UpdateAvailable(Version v) => Pt ? $"⬤ Atualizar para v{v}" : $"⬤ Update to v{v}";
    public static string UpdateLatest(Version v) => Pt
        ? $"Estás na versão mais recente (v{v})."
        : $"You're on the latest version (v{v}).";
    public static string UpdateNotConfigured(Version v) => Pt
        ? $"Versão atual: v{v}\n\nAs atualizações automáticas ainda não estão configuradas (falta definir o repositório GitHub em UpdateService.cs)."
        : $"Current version: v{v}\n\nAutomatic updates are not configured yet (set the GitHub repository in UpdateService.cs).";
    public static string UpdatePrompt(Version latest, Version current) => Pt
        ? $"Nova versão v{latest} disponível (atual: v{current}).\n\nAtualizar agora? O widget reinicia sozinho."
        : $"New version v{latest} available (current: v{current}).\n\nUpdate now? The widget restarts by itself.";
    public static string UpdateError(string message) => Pt
        ? "Não foi possível verificar atualizações: " + message
        : "Could not check for updates: " + message;
}
