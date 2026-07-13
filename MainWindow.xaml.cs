using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget;

public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SpotifyTaskbarWidget";

    // Link de donativos; vazio = item de menu oculto
    private const string DonateUrl = "https://ko-fi.com/mechanicwb2";

    // Ícones do Spotify (paths 16x16 do leitor web)
    private static readonly Geometry PlayGeo = Geometry.Parse("M3 1.713a.7.7 0 0 1 1.05-.607l10.89 6.288a.7.7 0 0 1 0 1.212L4.05 14.894A.7.7 0 0 1 3 14.288V1.713z");
    private static readonly Geometry PauseGeo = Geometry.Parse("M2.7 1a.7.7 0 0 0-.7.7v12.6a.7.7 0 0 0 .7.7h2.6a.7.7 0 0 0 .7-.7V1.7a.7.7 0 0 0-.7-.7H2.7zm8 0a.7.7 0 0 0-.7.7v12.6a.7.7 0 0 0 .7.7h2.6a.7.7 0 0 0 .7-.7V1.7a.7.7 0 0 0-.7-.7h-2.6z");
    private static readonly Geometry AddCircleGeo = Geometry.Parse("M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8z M11.75 8a.75.75 0 0 1-.75.75H8.75V11a.75.75 0 0 1-1.5 0V8.75H5a.75.75 0 0 1 0-1.5h2.25V5a.75.75 0 0 1 1.5 0v2.25H11a.75.75 0 0 1 .75.75z");
    private static readonly Geometry CheckCircleGeo = Geometry.Parse("M0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8zm11.748-1.97a.75.75 0 0 0-1.06-1.06l-4.47 4.44-1.405-1.406a.75.75 0 1 0-1.061 1.06l2.466 2.467 5.53-5.5z");

    // Cores do Spotify; as neutras dependem do tema da barra (claro/escuro)
    private static readonly Brush SpotifyGreen = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
    private Brush Subdued = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
    private Brush DimWhite = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private Brush _progressFillNormal = Brushes.White;
    private bool? _lightTheme;

    private readonly MediaService _media = new();
    private readonly SpotifyUiaService _uia = new();
    private readonly WidgetSettings _settings = WidgetSettings.Shared;
    private bool? _liked;

    /// <summary>Barra desta janela (0 = principal, 1+ = secundárias). Cada
    /// monitor selecionado nas definições tem a sua própria instância.</summary>
    public int TrayIndex { get; set; }

    /// <summary>Fecho programático (sincronização de monitores) — não recriar.</summary>
    internal bool ClosedByApp;

    private static readonly List<MainWindow> Instances = new();
    private static bool _updateCheckStarted;
    private static bool _recreatePending;

    /// <summary>Garante uma janela por barra selecionada nas definições:
    /// cria as que faltam, fecha as que sobram.</summary>
    public static void SyncToMonitors()
    {
        var wanted = WidgetSettings.Shared.Monitors;
        foreach (var win in Instances.Where(w => !wanted.Contains(w.TrayIndex)).ToList())
        {
            win.ClosedByApp = true;
            win.Close();
        }
        foreach (int idx in wanted)
            if (!Instances.Any(w => w.TrayIndex == idx))
                new MainWindow { TrayIndex = idx }.Show();
    }

    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _trackTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private IntPtr _hwnd;
    private bool _moveMode;
    private bool _dragging;
    private bool _dragMoved;
    private bool _pressed;
    private Point _dragStartScreen;
    private double _dragStartLeft;

    private string _lastTrackKey = "";
    private bool _artDirty = true;
    private bool _refreshing;
    private bool _volLoading;
    private bool _spotifyPresent = true;
    private DateTime _sessionLostAt = DateTime.MinValue;
    private DateTime _trackNullSince = DateTime.MinValue;

    // Progresso: última posição conhecida + instante em que foi lida (interpolação)
    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private DateTime _basePositionAt;
    private bool _isPlayingUi;

    // Estado via acessibilidade (favorito/aleatório/repetição): caro de ler,
    // por isso só em mudanças de faixa, após cliques, ou a cada 5 s
    private (bool? Liked, ShuffleMode Shuffle, RepeatMode Repeat) _uiaState =
        (null, ShuffleMode.Unknown, RepeatMode.Unknown);
    private DateTime _lastUiaStateAt = DateTime.MinValue;
    private bool _uiaDirty = true;

    // Depois de um clique otimista, leituras antigas (pré-clique) chegam durante
    // ~2 s e contradizem o novo estado — são ignoradas nesse intervalo
    private DateTime _playToggledAt = DateTime.MinValue;
    private DateTime _likedOptimisticAt = DateTime.MinValue;

    private bool AcceptPlayingState(bool incoming) =>
        incoming == _isPlayingUi || DateTime.UtcNow - _playToggledAt > TimeSpan.FromSeconds(2);

    // Âncoras da barra (píxeis físicos), atualizadas em background via UI Automation
    // Hook de foreground: o delegate tem de ficar referenciado (senão o GC apanha-o)
    private Interop.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;

    private readonly object _anchorLock = new();
    private int _widgetsMisses;
    private double? _widgetsRightPx;
    private double? _startLeftPx;
    private double? _taskEndPx;
    private DateTime _lastAnchorQuery = DateTime.MinValue;
    private bool _anchorQueryRunning;
    private IntPtr _anchorsTray;

    private const double MaxTextWidth = 150;
    private const double MinTextWidth = 60;

    public MainWindow()
    {
        InitializeComponent();
        Instances.Add(this);
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Não roubar o foco ao clicar e não aparecer no Alt+Tab
        int ex = Interop.GetWindowLong(_hwnd, Interop.GWL_EXSTYLE);
        Interop.SetWindowLong(_hwnd, Interop.GWL_EXSTYLE, ex | Interop.WS_EX_TOOLWINDOW | Interop.WS_EX_NOACTIVATE);
    }

    /// <summary>Aplica os textos no idioma do Windows (PT ou EN).</summary>
    private void ApplyLanguage()
    {
        MoveMenu.Header = L.MoveWidget;
        MoveMenu.ToolTip = L.MoveWidgetTip;
        ResetPosMenu.Header = L.ResetAutoPos;
        MonitorMenu.Header = L.MonitorMenu;
        SizeMenuItem.Header = L.SizeMenu;
        SizeSmall.Header = L.SizeSmall;
        SizeNormal.Header = L.SizeNormal;
        SizeLarge.Header = L.SizeLarge;
        ButtonsMenuItem.Header = L.ButtonsMenu;
        BtnLikeMenu.Header = L.BtnLike;
        BtnShuffleMenu.Header = L.BtnShuffle;
        BtnPrevMenu.Header = L.BtnPrev;
        BtnNextMenu.Header = L.BtnNext;
        BtnRepeatMenu.Header = L.BtnRepeat;
        BtnVolumeMenu.Header = L.BtnVolume;
        ProgressMenu.Header = L.ProgressBar;
        LauncherMenu.Header = L.ShowLauncher;
        LauncherMenu.ToolTip = L.ShowLauncherTip;
        AutoStartMenu.Header = L.AutoStart;
        OpenSpotifyMenu.Header = L.OpenSpotify;
        UpdateMenu.Header = L.CheckUpdates;
        DonateMenu.Header = L.Donate;
        DonateMenu.Visibility = DonateUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExitMenu.Header = L.Exit;

        PrevButton.ToolTip = L.TipPrev;
        PlayPauseButton.ToolTip = L.TipPlayPause;
        NextButton.ToolTip = L.TipNext;
        VolumeButton.ToolTip = L.TipVolume;
        RepeatButton.ToolTip = L.TipRepeat;
        ShuffleButton.ToolTip = L.TipShuffle;
        LikeButton.ToolTip = L.TipLikeAdd;
        LauncherPanel.ToolTip = L.TipOpenSpotify;
        LauncherText.Text = L.OpenSpotify;
        ArtistText.Text = L.NothingPlaying;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLanguage();
        if (PackagedApp.IsPackaged)
        {
            UpdateMenu.Visibility = Visibility.Collapsed; // a Store trata das atualizações
            _ = InitStartupTaskStateAsync();
        }
        else
        {
            AutoStartMenu.IsChecked = IsAutoStartEnabled();
        }
        ApplyThemeIfChanged();
        RebuildMonitorMenu();
        ApplySettingsUi();
        // As definições são partilhadas: quando outra janela grava, re-aplicar
        WidgetSettings.Changed += OnSettingsChanged;

        // Re-afirmar o topmost por cima de um tooltip aberto empurra-o para
        // trás da barra (report da comunidade) — suspender enquanto durar
        AddHandler(ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((_, _) => _tooltipOpen = true), true);
        AddHandler(ToolTipService.ToolTipClosingEvent,
            new ToolTipEventHandler((_, _) => _tooltipOpen = false), true);

        // O popup do volume tem de ganhar à barra (que também é topmost com a
        // ocultação automática): re-afirmá-lo no instante em que abre
        VolumePopup.Opened += (_, _) =>
        {
            if (VolumePopup.Child != null &&
                PresentationSource.FromVisual(VolumePopup.Child) is HwndSource src)
                Interop.EnsureTopmost(src.Handle);
        };

        UpdatePosition();
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePosition();
            UpdateProgressUi();
            ApplyThemeIfChanged();
        };
        _positionTimer.Start();

        // Clicar na taskbar põe a barra por cima do widget; re-afirmar o topmost
        // no instante da mudança de janela ativa (o timer sozinho deixava flicker)
        _winEventProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(UpdatePosition);
        _winEventHook = Interop.SetWinEventHook(
            Interop.EVENT_SYSTEM_FOREGROUND, Interop.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Interop.WINEVENT_OUTOFCONTEXT);
        Closed += (_, _) =>
        {
            if (_winEventHook != IntPtr.Zero) Interop.UnhookWinEvent(_winEventHook);
            if (_trayLocHook != IntPtr.Zero) Interop.UnhookWinEvent(_trayLocHook);
        };

        await _media.InitializeAsync();
        _media.Changed += () =>
        {
            _artDirty = true;
            _uiaDirty = true;
            Dispatcher.InvokeAsync(() => _ = RefreshTrackAsync());
        };
        _media.TimelineChanged += () => Dispatcher.InvokeAsync(RefreshTimeline);

        _trackTimer.Tick += (_, _) => _ = RefreshTrackAsync();
        _trackTimer.Start();

        if (!PackagedApp.IsPackaged && !_updateCheckStarted)
        {
            _updateCheckStarted = true; // com várias janelas, só uma verifica
            _ = CheckUpdatesQuietlyAsync();
        }

        await RefreshTrackAsync();
    }

    /// <summary>Estado da UI que espelha as definições partilhadas (menus,
    /// escala) — chamado no arranque e sempre que qualquer janela grava.</summary>
    private void ApplySettingsUi()
    {
        LauncherMenu.IsChecked = _settings.ShowLauncher;
        ProgressMenu.IsChecked = _settings.ShowProgress;
        BtnLikeMenu.IsChecked = _settings.ShowLike;
        BtnShuffleMenu.IsChecked = _settings.ShowShuffle;
        BtnPrevMenu.IsChecked = _settings.ShowPrev;
        BtnNextMenu.IsChecked = _settings.ShowNext;
        BtnRepeatMenu.IsChecked = _settings.ShowRepeat;
        BtnVolumeMenu.IsChecked = _settings.ShowVolume;
        ApplyScale();
        UpdateSizeChecks();
    }

    private void OnSettingsChanged()
    {
        ApplySettingsUi();
        UpdatePosition();
        _ = RefreshTrackAsync();
    }

    // ---------- Posicionamento na barra de tarefas ----------

    /// <summary>Barra de tarefas desta janela (principal ou secundária).
    /// Zero quando a barra alvo não existe (monitor desligado) — a janela
    /// esconde-se e volta quando ela reaparecer.</summary>
    private IntPtr GetTargetTray()
    {
        if (TrayIndex > 0)
        {
            var secondaries = Interop.GetSecondaryTrays();
            return TrayIndex <= secondaries.Count ? secondaries[TrayIndex - 1] : IntPtr.Zero;
        }
        return Interop.FindWindow("Shell_TrayWnd", null);
    }

    private static bool IsTaskbarLeftAligned()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr _ownerTray;
    private DateTime _anchorsMissingSince = DateTime.MinValue;

    // Hook de movimento da barra: quando ela desliza (ocultação automática),
    // os eventos chegam ao milissegundo e o widget "cavalga" a animação
    private IntPtr _trayLocHook;
    private IntPtr _hookedTray;
    private Interop.WinEventDelegate? _trayLocProc;
    private bool _updateQueued;

    private void EnsureTrayLocationHook(IntPtr tray)
    {
        if (tray == _hookedTray) return;
        if (_trayLocHook != IntPtr.Zero)
        {
            Interop.UnhookWinEvent(_trayLocHook);
            _trayLocHook = IntPtr.Zero;
        }
        _hookedTray = tray;
        uint tid = Interop.GetWindowThreadProcessId(tray, out uint pid);
        _trayLocProc ??= (_, _, hwnd, idObject, _, _, _) =>
        {
            if (hwnd != _hookedTray || idObject != 0 || _updateQueued) return;
            _updateQueued = true;
            // Prioridade alta: cada ms conta para apanhar o início do deslize
            Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)UpdatePosition);
        };
        _trayLocHook = Interop.SetWinEventHook(
            Interop.EVENT_OBJECT_LOCATIONCHANGE, Interop.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _trayLocProc, pid, tid, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private void UpdatePosition()
    {
        _updateQueued = false;
        IntPtr tray = GetTargetTray();
        if (tray == IntPtr.Zero || !Interop.GetWindowRect(tray, out var r))
        {
            // Barra alvo indisponível (monitor desligado / Explorer a reiniciar)
            if (Visibility != Visibility.Hidden)
            {
                Visibility = Visibility.Hidden;
                VolumePopup.IsOpen = false;
            }
            return;
        }

        EnsureTrayLocationHook(tray);

        // Janela "owned" pela taskbar: o gestor de janelas mantém-na SEMPRE acima
        // do dono — elimina o flicker de z-order por construção. Se o Explorer
        // reiniciar, a janela morre com a barra e o OnClosed recria o widget.
        if (tray != _ownerTray && _hwnd != IntPtr.Zero)
        {
            Interop.SetWindowLongPtr(_hwnd, Interop.GWLP_HWNDPARENT, tray);
            _ownerTray = tray;
            Interop.EnsureTopmost(_hwnd);
        }

        // Ocultação automática: o widget "cavalga" a barra — o hook de movimento
        // chama isto a cada frame da animação e o Y segue o rect atual, por isso
        // ele desce e sobe colado à barra. Só se esconde quando ela assenta fora
        // do ecrã; âncoras ficam intactas (o X não muda num deslize vertical).
        int trayHeightPx = r.Bottom - r.Top;
        int visiblePx = Interop.GetTaskbarVisiblePx(r, out int monitorBottomPx);

        if (!Interop.GetWindowRect(_hwnd, out var w))
            return;
        int winWidth = w.Right - w.Left;
        int winHeight = w.Bottom - w.Top;

        // Centrar na banda VISÍVEL inferior da barra (48 DIP no Win11): no
        // 25H2 a janela da barra ficou mais alta do que a barra desenhada e a
        // centragem no rect todo deixava o widget a flutuar acima (issue #9)
        int barBandPx = Math.Min(trayHeightPx, (int)Math.Round(48 * Interop.GetDpiForWindow(tray) / 96.0));
        // Onde o widget "estaria" com a barra assente fora do ecrã (mesmo offset
        // vertical dentro dela): ponto de partida da subida e destino da descida
        int belowEdgeTopPx = monitorBottomPx - 2 + (barBandPx - winHeight) / 2;

        if (_rideAnimating)
        {
            // A animação é dona da posição; se a barra inverter a meio,
            // invertemos também, a partir de onde o widget está
            if (_rideDown && visiblePx >= trayHeightPx - 4)
            {
                CancelRide();
                StartRide(w.Left, w.Top, r.Bottom - barBandPx + (barBandPx - winHeight) / 2,
                    down: false, winWidth, winHeight, monitorBottomPx);
            }
            else if (!_rideDown && visiblePx <= 8)
            {
                CancelRide();
                StartRide(w.Left, w.Top, belowEdgeTopPx,
                    down: true, winWidth, winHeight, monitorBottomPx);
            }
            return;
        }

        // Fração do caminho de esconder que a barra já percorreu — o widget
        // entra na animação neste ponto para ficar em sincronia com ela
        double hiddenPhase = 1.0 - Math.Clamp((double)visiblePx / trayHeightPx, 0, 1);

        if (visiblePx <= 8)
        {
            // Assente fora do ecrã. Se o widget ainda está à vista, o esconder
            // aconteceu num salto único — animar a descida na mesma.
            if (Visibility == Visibility.Visible && !_dragging && Interop.IsAutoHideEnabled())
            {
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
                return;
            }
            _barWasHidden = true;
            if (Visibility != Visibility.Hidden)
            {
                Visibility = Visibility.Hidden;
                VolumePopup.IsOpen = false;
            }
            return;
        }

        if (visiblePx < trayHeightPx - 4)
        {
            // A deslizar. Widget visível = a barra começou a esconder-se: animar
            // a nossa descida (seguir os passos grossos da janela dela ficava
            // aos solavancos). Invisível = revelação em curso: esperar que
            // assente — a subida anima nessa altura.
            if (Visibility == Visibility.Visible && !_dragging && Interop.IsAutoHideEnabled())
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
            return;
        }

        if (tray != _anchorsTray)
        {
            // Mudou a barra alvo (outro monitor): descartar âncoras da anterior
            _anchorsTray = tray;
            _lastAnchorQuery = DateTime.MinValue;
            lock (_anchorLock)
            {
                _widgetsMisses = 0;
                _widgetsRightPx = null;
                _startLeftPx = null;
                _taskEndPx = null;
            }
        }
        // Daqui para baixo a barra está assente no ecrã — âncoras fiáveis
        RefreshAnchors(tray);
        double? widgetsRightPx, startLeftPx, taskEndPx;
        lock (_anchorLock)
        {
            widgetsRightPx = _widgetsRightPx;
            startLeftPx = _startLeftPx;
            taskEndPx = _taskEndPx;
        }

        // Toda a matemática de posicionamento é feita em PÍXEIS FÍSICOS da barra
        // alvo: converter para DIP usava a escala do monitor ATUAL da janela e,
        // ao mudar para um monitor com DPI diferente, a conta saía errada e o
        // widget aterrava a meio do ecrã.
        double windowScale = Interop.GetDpiForWindow(_hwnd) / 96.0; // px por DIP, no monitor atual

        int topPx = r.Bottom - barBandPx + (barBandPx - winHeight) / 2;

        bool rightAnchored = false;
        int rightAnchorLeftLimitPx = r.Left + 12;
        int leftPx, rightLimitPx;
        if (!_settings.AutoPosition)
        {
            leftPx = (int)Math.Max(r.Left + 4, Math.Min(_settings.X, r.Right - winWidth - 4));
            rightLimitPx = r.Right - 4;
        }
        else if (!IsTaskbarLeftAligned())
        {
            // Numa barra centrada o botão Iniciar existe sempre — âncora nula
            // significa que a leitura ainda não chegou (arranque / primeiro
            // reveal do auto-hide): esperar em vez de posicionar às cegas
            // (o widget aparecia encostado à esquerda e "saltava" depois)
            if (!startLeftPx.HasValue)
            {
                if (_anchorsMissingSince == DateTime.MinValue)
                    _anchorsMissingSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _anchorsMissingSince < TimeSpan.FromSeconds(4))
                {
                    if (Visibility != Visibility.Hidden)
                        Visibility = Visibility.Hidden;
                    return;
                }
            }
            else
            {
                _anchorsMissingSince = DateTime.MinValue;
            }

            // Ícones centrados (em qualquer barra/monitor): o espaço livre está
            // à esquerda — alinhar a seguir ao botão de widgets/tempo; sem ele,
            // à borda esquerda. Nunca invadir os ícones (botão Iniciar).
            leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : r.Left + 12;
            rightLimitPx = startLeftPx.HasValue ? (int)startLeftPx.Value - 8 : r.Right - 4;
        }
        else
        {
            // Ícones alinhados à esquerda: o espaço vazio está à direita —
            // encostar antes dos ícones do sistema/relógio, sem nunca tapar
            // a fila de ícones das apps
            rightAnchored = true;
            int? notifyLeftPx = Interop.GetTrayNotifyLeft(tray);
            rightLimitPx = notifyLeftPx ?? (r.Right - 220);
            rightLimitPx -= 8;
            if (taskEndPx.HasValue)
                rightAnchorLeftLimitPx = (int)taskEndPx.Value + 8;
            leftPx = Math.Max(rightAnchorLeftLimitPx, rightLimitPx - winWidth);
        }

        if (_spotifyPresent)
        {
            int availPx = rightAnchored ? rightLimitPx - rightAnchorLeftLimitPx : rightLimitPx - leftPx;
            if (!ApplyResponsiveLayout(availPx / windowScale))
            {
                // Numa barra lotada nem a versão mínima cabe: esconder em vez
                // de transbordar para cima do relógio/ícones (issue #10)
                _barWasHidden = false;
                if (Visibility != Visibility.Hidden)
                {
                    Visibility = Visibility.Hidden;
                    VolumePopup.IsOpen = false;
                }
                return;
            }
        }

        // Esconder quando: app em ecrã inteiro (irrelevante com auto-hide — as
        // janelas maximizadas ocupam o ecrã todo e dariam falsos positivos; a
        // visibilidade já segue a da barra); ou Spotify fechado sem botão de abrir.
        bool hide = (!Interop.IsAutoHideEnabled() && Interop.IsForegroundFullscreen(_hwnd, tray))
                    || (!_spotifyPresent && !_settings.ShowLauncher);
        if (hide)
        {
            _barWasHidden = false;
            if (Visibility != Visibility.Hidden)
            {
                Visibility = Visibility.Hidden;
                VolumePopup.IsOpen = false;
            }
            return;
        }

        // Revelação da barra: o Windows teleporta a janela dela para o destino
        // e anima só o visual — não há frames para seguir. Animamos nós a
        // subida, a emergir da borda do ecrã em sincronia com a barra.
        if (_barWasHidden && !_dragging)
        {
            _barWasHidden = false;
            StartRide(leftPx, belowEdgeTopPx, topPx, down: false, winWidth, winHeight, monitorBottomPx);
            return;
        }
        _barWasHidden = false;

        if (!_dragging && (Math.Abs(w.Left - leftPx) > 1 || Math.Abs(w.Top - topPx) > 1))
            Interop.MoveWindowTo(_hwnd, leftPx, topPx);

        // Durante o deslize, recortar a parte do widget que já saiu do ecrã —
        // sem isto, o excedente aparecia a atravessar um monitor disposto abaixo
        Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - topPx);

        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
        // Re-afirmar por cima de um tooltip/popup aberto empurra-o para trás
        // da barra (a barra em ocultação automática também vive no topmost)
        if (!_tooltipOpen && !VolumePopup.IsOpen)
            Interop.EnsureTopmost(_hwnd);
    }

    private bool _tooltipOpen;

    // ---------- Animação de deslize (ocultação automática da barra) ----------

    private bool _barWasHidden;
    private bool _rideAnimating;
    private bool _rideDown;
    private DispatcherTimer? _rideTimer;

    /// <summary>Anima o widget entre a posição assente e o fundo do ecrã (nos
    /// dois sentidos), com o recorte a fazê-lo emergir/submergir na borda.
    /// Movimento Fluent: entradas desaceleram, saídas aceleram.
    /// startPhase (0..1): fração do caminho que a barra JÁ percorreu quando o
    /// gatilho chegou — o primeiro passo da janela dela vem atrasado, e entrar
    /// na curva a meio mantém o widget em sincronia em vez de a trás dela.</summary>
    private void StartRide(int leftPx, int fromTopPx, int toTopPx, bool down,
        int winWidth, int winHeight, int monitorBottomPx, double startPhase = 0)
    {
        var sw = Stopwatch.StartNew();
        const double DurationMs = 220; // aproxima a animação da própria barra
        startPhase = Math.Clamp(startPhase, 0, 1);
        // Instante da curva cujo easing corresponde à fase pedida
        double t0 = down ? Math.Cbrt(startPhase) : 1 - Math.Cbrt(1 - startPhase);
        double t0Ms = t0 * DurationMs;

        _rideAnimating = true;
        _rideDown = down;
        if (down)
            VolumePopup.IsOpen = false;

        double eased0 = down ? t0 * t0 * t0 : 1 - Math.Pow(1 - t0, 3);
        int startTopPx = (int)Math.Round(fromTopPx + (toTopPx - fromTopPx) * eased0);
        Interop.MoveWindowTo(_hwnd, leftPx, startTopPx);
        Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - startTopPx);
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
        Interop.EnsureTopmost(_hwnd);

        _rideTimer?.Stop();
        _rideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _rideTimer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, (t0Ms + sw.ElapsedMilliseconds) / DurationMs);
            double eased = down ? t * t * t : 1 - Math.Pow(1 - t, 3);
            int topPx = (int)Math.Round(fromTopPx + (toTopPx - fromTopPx) * eased);
            Interop.MoveWindowTo(_hwnd, leftPx, topPx);
            Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - topPx);
            if (t >= 1.0)
            {
                _rideTimer?.Stop();
                _rideAnimating = false;
                if (down)
                {
                    _barWasHidden = true;
                    Visibility = Visibility.Hidden;
                }
            }
        };
        _rideTimer.Start();
    }

    private void CancelRide()
    {
        _rideTimer?.Stop();
        _rideAnimating = false;
    }

    /// <summary>
    /// Encaixa o widget no espaço disponível: primeiro encolhe o texto até um
    /// mínimo; se mesmo assim não couber, esconde os botões menos importantes
    /// (volume → aleatório → favoritos → seguinte → anterior).
    /// Devolve false quando nem a versão mínima cabe no espaço dado.
    /// </summary>
    private bool ApplyResponsiveLayout(double availableDip)
    {
        double s = _settings.Scale;
        double avail = availableDip / s; // trabalhar em unidades pré-escala

        const double IconBtn = 28;            // 26 + margens
        const double PlayBtn = 34;            // 30 + margens
        const double BasePart = 16 + 34 + 15; // padding + capa + margens do texto

        double used = BasePart + PlayBtn + MinTextWidth;
        if (avail < used)
            return false;

        bool prev = false, next = false, like = false, shuffle = false, repeat = false, volume = false;
        Take(ref prev, _settings.ShowPrev);
        Take(ref next, _settings.ShowNext);
        Take(ref like, _settings.ShowLike);
        Take(ref shuffle, _settings.ShowShuffle);
        Take(ref repeat, _settings.ShowRepeat);
        Take(ref volume, _settings.ShowVolume);

        double text = Math.Max(MinTextWidth, Math.Min(MaxTextWidth, MinTextWidth + (avail - used)));

        SetVis(PrevButton, prev);
        SetVis(NextButton, next);
        SetVis(LikeButton, like);
        SetVis(ShuffleButton, shuffle);
        SetVis(RepeatButton, repeat);
        SetVis(VolumeButton, volume);
        if (Math.Abs(TextStack.Width - text) > 1)
        {
            TextStack.Width = text;
            UpdateMarquee();
        }
        return true;

        void Take(ref bool flag, bool wanted)
        {
            if (wanted && used + IconBtn <= avail)
            {
                flag = true;
                used += IconBtn;
            }
        }

        static void SetVis(UIElement el, bool show)
        {
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (el.Visibility != v) el.Visibility = v;
        }
    }

    /// <summary>
    /// Atualiza as âncoras da barra (botão de widgets e botão Iniciar) em background,
    /// no máximo a cada 5 segundos — as consultas de UI Automation não são gratuitas.
    /// </summary>
    private void RefreshAnchors(IntPtr tray)
    {
        if (_anchorQueryRunning || (DateTime.UtcNow - _lastAnchorQuery).TotalSeconds < 5)
            return;

        _anchorQueryRunning = true;
        _lastAnchorQuery = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                var (widgetsRight, startLeft, taskButtonsRight) = TaskbarAnchors.Get(tray);
                lock (_anchorLock)
                {
                    // Âncora do botão de widgets "pegajosa": as leituras de UIA
                    // falham transitoriamente (shell ocupada, pós-reveal) e cair
                    // logo para null punha o widget EM CIMA do botão do tempo.
                    // Só aceitar o desaparecimento ao fim de 3 falhas seguidas
                    // (~15s) — aí sim, foi mesmo desativado nas definições.
                    if (widgetsRight.HasValue)
                    {
                        _widgetsRightPx = widgetsRight;
                        _widgetsMisses = 0;
                    }
                    else if (_widgetsRightPx.HasValue && ++_widgetsMisses >= 3)
                    {
                        _widgetsRightPx = null;
                        _widgetsMisses = 0;
                    }
                    _startLeftPx = startLeft;
                    _taskEndPx = taskButtonsRight;
                }
            }
            finally
            {
                _anchorQueryRunning = false;
            }
        });
    }

    // ---------- Atualização da faixa ----------

    private async Task RefreshTrackAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            bool processAlive = Process.GetProcessesByName("Spotify").Length > 0;

            // As chamadas SMTC podem ficar penduradas para sempre numa sessão em
            // teardown (Spotify a fechar/reabrir) — sem timeout, a flag _refreshing
            // ficava presa e o widget congelava até reiniciar
            TrackInfo? track = null;
            if (processAlive)
            {
                try { track = await _media.GetTrackAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }

            // Nas mudanças de faixa, a sessão/título ficam vazios por umas centenas
            // de ms — manter o estado atual no ecrã e re-verificar já, em vez de
            // piscar placeholders ou esconder o widget
            bool noData = track == null || string.IsNullOrWhiteSpace(track.Title);
            if (processAlive && noData && _lastTrackKey.Length > 0)
            {
                if (_trackNullSince == DateTime.MinValue)
                    _trackNullSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _trackNullSince < TimeSpan.FromSeconds(1.2))
                {
                    _ = QuickRecheckAsync();
                    return;
                }
            }
            else if (!noData)
            {
                _trackNullSince = DateTime.MinValue;
            }

            // Perder a sessão com o processo ainda vivo (de forma persistente) =
            // fecho do Spotify em curso. Esconder, sem estados intermédios.
            if (processAlive && track == null && _lastTrackKey.Length > 0)
            {
                _sessionLostAt = DateTime.UtcNow;
                _lastTrackKey = "";
            }
            bool closing = processAlive && track == null &&
                           DateTime.UtcNow - _sessionLostAt < TimeSpan.FromSeconds(6);
            _spotifyPresent = processAlive && !closing;

            var launcherWanted = !_spotifyPresent && _settings.ShowLauncher
                ? Visibility.Visible : Visibility.Collapsed;
            var contentWanted = _spotifyPresent ? Visibility.Visible : Visibility.Collapsed;
            if (LauncherPanel.Visibility != launcherWanted) LauncherPanel.Visibility = launcherWanted;
            if (ContentPanel.Visibility != contentWanted) ContentPanel.Visibility = contentWanted;

            if (!_spotifyPresent)
            {
                _liked = null;
                _duration = TimeSpan.Zero;
                VolumePopup.IsOpen = false;
                UpdateProgressUi();
                UpdatePosition(); // esconder/mostrar imediatamente, sem esperar o timer
                return;
            }

            _duration = track?.Duration ?? TimeSpan.Zero;
            if (AcceptPlayingState(track?.IsPlaying == true))
            {
                // Âncora no LastUpdatedTime do Windows (não no momento da leitura):
                // re-ler um snapshot antigo dá o mesmo valor interpolado — sem saltos
                TimeSpan pos = track?.Position ?? TimeSpan.Zero;
                DateTime posAt = track?.PositionAtUtc ?? DateTime.UtcNow;
                // Snapshot da faixa anterior (posição > duração, ou muito antigo
                // numa faixa acabada de mudar): mostrar do início até assentar
                bool stale = pos > _duration ||
                             (track != null && track.Title + "|" + track.Artist != _lastTrackKey &&
                              DateTime.UtcNow - posAt > TimeSpan.FromSeconds(5));
                if (stale)
                {
                    pos = TimeSpan.Zero;
                    posAt = DateTime.UtcNow;
                }
                _basePosition = pos;
                _basePositionAt = posAt;
                _isPlayingUi = track?.IsPlaying == true;
            }
            UpdateProgressUi();

            if (track == null || string.IsNullOrWhiteSpace(track.Title))
            {
                TitleText.Text = "Spotify";
                ArtistText.Text = L.NothingPlaying;
                SetPlayPauseIcon(false);
                ShuffleIcon.Fill = DimWhite;
                ShuffleDot.Visibility = Visibility.Collapsed;
                ShuffleSmartStar.Visibility = Visibility.Collapsed;
                RepeatIcon.Fill = DimWhite;
                RepeatDot.Visibility = Visibility.Collapsed;
                RepeatOneBadge.Visibility = Visibility.Collapsed;
                LikeIcon.Data = AddCircleGeo;
                LikeIcon.Fill = DimWhite;
                _liked = null;
                ArtImage.Visibility = Visibility.Collapsed;
                ArtPlaceholder.Visibility = Visibility.Visible;
                _lastTrackKey = "";
                UpdateMarquee();
                return;
            }

            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist;
            UpdateMarquee();
            SetPlayPauseIcon(_isPlayingUi);

            // Estado real (favoritos + aleatório + repetição) da árvore de
            // acessibilidade do Spotify; o SMTC serve de rede de segurança.
            string key = track.Title + "|" + track.Artist;
            bool keyChanged = key != _lastTrackKey;
            if (keyChanged || _uiaDirty || DateTime.UtcNow - _lastUiaStateAt > TimeSpan.FromSeconds(5))
            {
                _uiaDirty = false;
                var state = (Liked: (bool?)null, Shuffle: ShuffleMode.Unknown, Repeat: RepeatMode.Unknown, Fresh: false);
                try { state = await Task.Run(() => _uia.GetState(track.Title)).WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException) { }
                _lastStateFresh = state.Fresh;
                // Grupo ainda da faixa anterior (zombie): não mostrar o tick antigo
                _uiaState = (state.Fresh ? state.Liked : null, state.Shuffle, state.Repeat);
                _lastUiaStateAt = DateTime.UtcNow;
            }
            if (keyChanged)
                _ = SettleStateAsync(); // re-ler até o Spotify renderizar a barra da faixa nova
            var (liked, uiaMode, repeatMode) = _uiaState;
            // Depois de adicionar aos favoritos, ignorar "não gostado" antigo — o
            // texto do botão do Spotify pode demorar vários segundos a atualizar
            if (liked == false && DateTime.UtcNow - _likedOptimisticAt < TimeSpan.FromSeconds(8))
                liked = true;
            _liked = liked;

            ApplyRepeatVisual(repeatMode);

            LikeIcon.Data = liked == true ? CheckCircleGeo : AddCircleGeo;
            LikeIcon.Fill = liked == true ? SpotifyGreen : (liked == false ? Subdued : DimWhite);
            LikeButton.ToolTip = liked == true ? L.TipLiked : L.TipLikeAdd;

            ShuffleMode mode = uiaMode;
            if (track.IsShuffle == false && mode != ShuffleMode.Unknown)
                mode = ShuffleMode.Off;
            else if (track.IsShuffle == true && mode is ShuffleMode.Off or ShuffleMode.Unknown)
                mode = ShuffleMode.On;

            ApplyShuffleVisual(mode);

            if (keyChanged || _artDirty)
            {
                _lastTrackKey = key;
                _artDirty = false;
                byte[]? bytes = null;
                try { bytes = await _media.GetThumbnailAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                if (bytes != null)
                {
                    ArtBrush.ImageSource = ToBitmap(bytes);
                    ArtImage.Visibility = Visibility.Visible;
                    ArtPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ArtImage.Visibility = Visibility.Collapsed;
                    ArtPlaceholder.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static BitmapImage ToBitmap(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    // ---------- Controlos ----------

    /// <summary>O triângulo de play centrado geometricamente parece deslocado à
    /// esquerda (a massa visual fica à esquerda) — compensação ótica de 1,5px.</summary>
    private void SetPlayPauseIcon(bool playing)
    {
        PlayPauseIcon.Data = playing ? PauseGeo : PlayGeo;
        PlayPauseIcon.Margin = playing ? new Thickness(0) : new Thickness(1.5, 0, 0, 0);
    }

    private void ApplyShuffleVisual(ShuffleMode mode)
    {
        ShuffleIcon.Fill = mode switch
        {
            ShuffleMode.On or ShuffleMode.Smart => SpotifyGreen,
            ShuffleMode.Off => Subdued,
            _ => DimWhite,
        };
        ShuffleDot.Visibility = mode is ShuffleMode.On or ShuffleMode.Smart
            ? Visibility.Visible : Visibility.Collapsed;
        ShuffleSmartStar.Visibility = mode == ShuffleMode.Smart ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.ToolTip = mode switch
        {
            ShuffleMode.Smart => L.TipShuffleSmart,
            ShuffleMode.On => L.TipShuffleOn,
            _ => L.TipShuffle,
        };
    }

    private void ApplyRepeatVisual(RepeatMode mode)
    {
        RepeatIcon.Fill = mode is RepeatMode.Context or RepeatMode.Track
            ? SpotifyGreen
            : (mode == RepeatMode.Off ? Subdued : DimWhite);
        RepeatDot.Visibility = mode is RepeatMode.Context or RepeatMode.Track
            ? Visibility.Visible : Visibility.Collapsed;
        RepeatOneBadge.Visibility = mode == RepeatMode.Track
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Atualização leve disparada pelos eventos de timeline (frequentes).
    /// Fora da thread de UI e com timeout: o SMTC pode pendurar em sessões mortas.</summary>
    private async void RefreshTimeline()
    {
        (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? tlMaybe = null;
        try { tlMaybe = await Task.Run(() => _media.GetTimeline()).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        if (tlMaybe is not { } tl) return;
        _duration = tl.Duration;
        if (AcceptPlayingState(tl.IsPlaying) && tl.Position <= tl.Duration)
        {
            _basePosition = tl.Position;
            _basePositionAt = tl.PositionAtUtc;
            _isPlayingUi = tl.IsPlaying;
            SetPlayPauseIcon(tl.IsPlaying);
        }
        UpdateProgressUi();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // Feedback imediato; leituras antigas são ignoradas por 2 s (grace) e
        // depois disso o estado real do SMTC volta a mandar
        _isPlayingUi = !_isPlayingUi;
        _playToggledAt = DateTime.UtcNow;
        SetPlayPauseIcon(_isPlayingUi);
        if (!_isPlayingUi)
            _basePosition += DateTime.UtcNow - _basePositionAt; // congelar posição
        _basePositionAt = DateTime.UtcNow;
        await _media.TogglePlayPauseAsync();
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        // Feedback imediato: cicla localmente; a leitura de estado corrige se preciso
        var next = _uiaState.Shuffle switch
        {
            ShuffleMode.Off => ShuffleMode.On,
            ShuffleMode.On => ShuffleMode.Smart,
            ShuffleMode.Smart => ShuffleMode.Off,
            _ => ShuffleMode.Unknown,
        };
        if (next != ShuffleMode.Unknown)
        {
            _uiaState = (_uiaState.Liked, next, _uiaState.Repeat);
            ApplyShuffleVisual(next);
        }

        bool ok = await Task.Run(() => _uia.CycleShuffle());
        if (!ok)
            await _media.ToggleShuffleAsync(); // sem janela do Spotify: só liga/desliga
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        // Feedback imediato: cicla localmente; a leitura de estado corrige se preciso
        var next = _uiaState.Repeat switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Unknown,
        };
        if (next != RepeatMode.Unknown)
        {
            _uiaState = (_uiaState.Liked, _uiaState.Shuffle, next);
            ApplyRepeatVisual(next);
        }

        bool ok = await Task.Run(() => _uia.CycleRepeat());
        if (!ok)
            await _media.CycleRepeatAsync(); // sem janela do Spotify: tentar via SMTC
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        if (_liked == true) return; // já está nos favoritos

        // Feedback imediato; se falhar, a leitura de estado seguinte corrige
        _liked = true;
        _likedOptimisticAt = DateTime.UtcNow;
        LikeIcon.Data = CheckCircleGeo;
        LikeIcon.Fill = SpotifyGreen;
        LikeButton.ToolTip = L.TipLiked;

        bool ok = await Task.Run(() => _uia.AddToFavorites());
        if (!ok)
            await Task.Run(() => _uia.AddToFavoritesByClick()); // recurso raro: sem verbo disponível

        // O texto do botão do Spotify demora segundos a refletir a adição —
        // reconciliar mais tarde em vez de concluir já que falhou
        _ = ReconcileLikeLaterAsync();
    }

    private async Task ReconcileLikeLaterAsync()
    {
        await Task.Delay(4000);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    /// <summary>Re-verificação rápida durante o vazio transitório das mudanças de faixa.</summary>
    private async Task QuickRecheckAsync()
    {
        await Task.Delay(350);
        await RefreshTrackAsync();
    }

    private int _settleToken;
    private bool _lastStateFresh = true;

    /// <summary>Depois de uma mudança de faixa, re-ler a cada 500 ms até o grupo
    /// do título no Spotify já ser o da faixa nova (validado pelo título do SMTC).
    /// Uma nova mudança de faixa cancela a série anterior.</summary>
    private async Task SettleStateAsync()
    {
        int token = ++_settleToken;
        for (int i = 0; i < 12; i++) // máx. ~6 s
        {
            await Task.Delay(500);
            if (token != _settleToken) return;
            _uiaDirty = true;
            await RefreshTrackAsync();
            if (_lastStateFresh) return;
        }
    }

    private async void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumePopup.IsOpen)
        {
            VolumePopup.IsOpen = false;
            return;
        }

        CaptureForeground();
        _volLoading = true;
        double? current = await Task.Run(() => _uia.GetVolume()) ?? SpotifyVolume.GetVolume();
        VolumeSlider.Value = (current ?? 1.0) * 100;
        _volLoading = false;
        VolumePopup.IsOpen = true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volLoading) return;
        ApplyVolume(e.NewValue / 100.0);
    }

    private double? _pendingVolume;
    private bool _volApplying;

    /// <summary>
    /// Aplica o volume no slider do próprio Spotify (a UI dele acompanha),
    /// com o mixer do Windows como recurso. Serializa os pedidos para o
    /// arrastar do slider não acumular chamadas.
    /// </summary>
    private async void ApplyVolume(double fraction)
    {
        _pendingVolume = fraction;
        if (_volApplying) return;
        _volApplying = true;
        try
        {
            while (_pendingVolume is double v)
            {
                _pendingVolume = null;
                await Task.Run(() =>
                {
                    if (!_uia.SetVolume(v))
                        SpotifyVolume.SetVolume((float)v);
                });
            }
        }
        finally
        {
            _volApplying = false;
        }
    }

    private void VolumePopup_MouseLeave(object sender, MouseEventArgs e) => VolumePopup.IsOpen = false;

    /// <summary>Roda do rato sobre o botão/popup de volume: fechado abre (com o
    /// volume atual carregado), aberto ajusta ±5 — como no próprio Spotify.</summary>
    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!VolumePopup.IsOpen)
        {
            Volume_Click(sender, null!);
            return;
        }
        if (_volLoading) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
    }

    // ---------- Tema (barra clara/escura) ----------

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A barra de tarefas segue o tema do SISTEMA (não o das apps);
    /// numa barra clara os textos/ícones têm de escurecer.</summary>
    private void ApplyThemeIfChanged()
    {
        bool light = IsSystemLightTheme();
        if (_lightTheme == light) return;
        _lightTheme = light;

        Subdued = new SolidColorBrush(light ? Color.FromRgb(0x48, 0x48, 0x48) : Color.FromRgb(0xB3, 0xB3, 0xB3));
        DimWhite = new SolidColorBrush(light ? Color.FromArgb(0x66, 0x00, 0x00, 0x00) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        _progressFillNormal = light ? Brushes.Black : Brushes.White;

        TitleText.Foreground = light ? Brushes.Black : Brushes.White;
        ArtistText.Foreground = Subdued;
        LauncherText.Foreground = Subdued;
        PrevIcon.Fill = Subdued;
        NextIcon.Fill = Subdued;
        VolumeIcon.Fill = Subdued;
        ArtPlaceholder.Foreground = DimWhite;
        ProgressTrack.Background = new SolidColorBrush(light ? Color.FromArgb(0x2E, 0x00, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        if (!ProgressTrack.IsMouseOver)
            ProgressFill.Background = _progressFillNormal;

        // Botão play: círculo branco no escuro, preto no claro (como o Spotify)
        PlayPauseButton.Background = light ? Brushes.Black : Brushes.White;
        PlayPauseIcon.Fill = light ? Brushes.White : Brushes.Black;

        _marqueeKey = ""; // sem efeito no texto, mas força refresh coerente
        _ = RefreshTrackAsync();
    }

    // ---------- Marquee do título ----------

    private string _marqueeKey = "";

    /// <summary>Título maior do que a coluna de texto → scroll contínuo com pausas,
    /// como no Spotify; caso contrário fica estático.</summary>
    private void UpdateMarquee()
    {
        double clipWidth = TextStack.Width;
        string key = $"{TitleText.Text}|{clipWidth:0}";
        if (key == _marqueeKey) return;
        _marqueeKey = key;

        // Medir a largura REALMENTE renderizada. A janela usa
        // TextFormattingMode=Display (avanços ajustados ao píxel) e o
        // FormattedText mede em modo Ideal — a diferença varia com a escala do
        // ecrã e o tipo de letra, e nalgumas máquinas passava da tolerância:
        // títulos que cabiam faziam scroll na mesma (report da comunidade).
        // Medir o próprio TextBlock sem restrições dá o valor exato do que se
        // desenha (está num Canvas, o layout já não o constrange).
        TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = Math.Ceiling(TitleText.DesiredSize.Width);

        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);
        TitleShift.X = 0;

        double overflow = textWidth - clipWidth;
        if (overflow > 4)
        {
            double scrollSeconds = Math.Max(1.5, overflow / 25.0);
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            double t = 2.5; // pausa inicial
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            t += scrollSeconds;
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-(overflow + 12), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            t += 1.5; // pausa no fim antes de recomeçar
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-(overflow + 12), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            anim.Duration = TimeSpan.FromSeconds(t);
            TitleShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        // Sem overflow: texto parado em X=0 (o Canvas nunca trunca a renderização)
    }

    // ---------- Barra de progresso ----------

    /// <summary>O Spotify só publica a posição de vez em quando; entre leituras,
    /// a posição é interpolada com o relógio local enquanto está a tocar.</summary>
    private void UpdateProgressUi()
    {
        bool show = _settings.ShowProgress && _spotifyPresent && _duration > TimeSpan.Zero;
        var wanted = show ? Visibility.Visible : Visibility.Collapsed;
        if (ProgressTrack.Visibility != wanted)
            ProgressTrack.Visibility = wanted;
        if (!show) return;

        TimeSpan pos = _basePosition;
        if (_isPlayingUi)
            pos += DateTime.UtcNow - _basePositionAt;

        double fraction = Math.Clamp(pos.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        ProgressFill.Width = fraction * ProgressTrack.ActualWidth;
    }

    private async void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode) return; // em modo mover, o arrasto tem prioridade
        e.Handled = true;      // não tratar como clique para abrir o Spotify

        if (_duration <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;
        double fraction = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
        var target = TimeSpan.FromTicks((long)(_duration.Ticks * fraction));

        // Atualização otimista para a barra responder já
        _basePosition = target;
        _basePositionAt = DateTime.UtcNow;
        UpdateProgressUi();

        await _media.SeekAsync(target);
    }

    private void Progress_MouseEnter(object sender, MouseEventArgs e) => ProgressFill.Background = SpotifyGreen;
    private void Progress_MouseLeave(object sender, MouseEventArgs e) => ProgressFill.Background = _progressFillNormal;

    private void Progress_MenuClick(object sender, RoutedEventArgs e)
    {
        _settings.ShowProgress = ProgressMenu.IsChecked;
        _settings.Save();
        UpdateProgressUi();
    }

    private void VolumePopup_Closed(object sender, EventArgs e) => RestoreForeground();

    // ---------- Preservação do foco ----------
    // O popup do volume e o menu de contexto ativam janelas próprias do WPF;
    // quando fecham, o foco pode ficar órfão e atalhos globais (PrintScreen,
    // Win+Shift+S) deixam de responder até se clicar noutra janela.

    private IntPtr _fgBeforeUi;

    private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        CaptureForeground();
        RebuildMonitorMenu();
    }

    /// <summary>Um item POR CADA barra de tarefas existente, com seleção
    /// múltipla — cada monitor marcado tem a sua instância do widget
    /// (pedido da comunidade). Pelo menos um fica sempre marcado.</summary>
    private void RebuildMonitorMenu()
    {
        MonitorMenu.Items.Clear();
        int count = Interop.GetSecondaryTrays().Count;
        var monitors = _settings.Monitors;
        for (int i = 0; i <= count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = i == 0 ? L.MonitorPrimary : L.MonitorN(i + 1),
                IsCheckable = true,
                IsChecked = monitors.Contains(i),
                StaysOpenOnClick = true, // marcar vários sem o menu fechar
            };
            item.Click += (s, _) =>
            {
                if (monitors.Contains(index))
                {
                    if (monitors.Count == 1)
                    {
                        ((MenuItem)s).IsChecked = true; // pelo menos um monitor
                        return;
                    }
                    monitors.Remove(index);
                }
                else
                {
                    monitors.Add(index);
                    monitors.Sort();
                }
                _settings.Save();
                SyncToMonitors();
            };
            MonitorMenu.Items.Add(item);
        }
        if (count == 0)
        {
            // Sem barras secundárias o Windows não tem onde ancorar o widget
            // noutro ecrã — explicar como ativar em vez de esconder o menu
            // (utilizadores achavam que a funcionalidade não existia, issue #11)
            MonitorMenu.Items.Add(new MenuItem
            {
                Header = L.MonitorHint,
                IsEnabled = false,
            });
        }
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => RestoreForeground();

    private void CaptureForeground()
    {
        IntPtr fg = Interop.GetForegroundWindow();
        _fgBeforeUi = fg == _hwnd ? IntPtr.Zero : fg;
    }

    private void RestoreForeground()
    {
        if (_fgBeforeUi == IntPtr.Zero) return;
        IntPtr target = _fgBeforeUi;
        _fgBeforeUi = IntPtr.Zero;
        if (Interop.GetForegroundWindow() != target)
            Interop.SetForegroundWindow(target);
    }

    // ---------- Mover (só quando ativado no menu) / clique para abrir o Spotify ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode)
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartLeft = Left;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            Root.CaptureMouse();
        }
        else
        {
            _pressed = true;
        }
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point cur = PointToScreen(e.GetPosition(this));
        double dxDevice = cur.X - _dragStartScreen.X;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        double dx = source.CompositionTarget.TransformFromDevice.Transform(new Vector(dxDevice, 0)).X;

        if (Math.Abs(dx) > 3) _dragMoved = true;
        if (_dragMoved)
            Left = _dragStartLeft + dx;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
            if (_dragMoved)
            {
                // Fica bloqueado nesta posição (modo manual); guardar em px físicos
                if (Interop.GetWindowRect(_hwnd, out var w))
                    _settings.X = w.Left;
                _settings.AutoPosition = false;
                _settings.Save();
            }
        }
        else if (_pressed)
        {
            _pressed = false;
            SpotifyActions.OpenSpotifyWindow();
        }
    }

    // ---------- Menu de contexto ----------

    private void MoveMode_Click(object sender, RoutedEventArgs e)
    {
        _moveMode = MoveMenu.IsChecked;
        Root.Cursor = _moveMode ? Cursors.SizeAll : Cursors.Hand;
    }

    private void ResetPos_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoPosition = true;
        _settings.Monitors.Clear();
        _settings.Monitors.Add(0);
        _settings.Save();
        SyncToMonitors();
        MoveMenu.IsChecked = false;
        _moveMode = false;
        Root.Cursor = Cursors.Hand;
        UpdatePosition();
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        _settings.Scale = double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        _settings.Save();
        ApplyScale();
        UpdateSizeChecks();
        UpdatePosition();
    }

    private void ApplyScale() => Root.LayoutTransform = new ScaleTransform(_settings.Scale, _settings.Scale);

    private void UpdateSizeChecks()
    {
        SizeSmall.IsChecked = Math.Abs(_settings.Scale - 0.8) < 0.01;
        SizeNormal.IsChecked = Math.Abs(_settings.Scale - 1.0) < 0.01;
        SizeLarge.IsChecked = _settings.Scale > 1.05;
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLauncher = LauncherMenu.IsChecked;
        _settings.Save();
        _ = RefreshTrackAsync();
        UpdatePosition();
    }

    private void Buttons_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLike = BtnLikeMenu.IsChecked;
        _settings.ShowShuffle = BtnShuffleMenu.IsChecked;
        _settings.ShowPrev = BtnPrevMenu.IsChecked;
        _settings.ShowNext = BtnNextMenu.IsChecked;
        _settings.ShowRepeat = BtnRepeatMenu.IsChecked;
        _settings.ShowVolume = BtnVolumeMenu.IsChecked;
        _settings.Save();
        UpdatePosition();
    }

    private const string StartupTaskId = "SpotifyTaskbarWidgetStartup";

    private async Task InitStartupTaskStateAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            AutoStartMenu.IsChecked = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { }
    }

    private async void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (PackagedApp.IsPackaged)
        {
            try
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                if (AutoStartMenu.IsChecked)
                {
                    var state = await task.RequestEnableAsync();
                    AutoStartMenu.IsChecked = state == Windows.ApplicationModel.StartupTaskState.Enabled;
                }
                else
                {
                    task.Disable();
                }
            }
            catch { }
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (AutoStartMenu.IsChecked)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e) => SpotifyActions.OpenSpotifyWindow();

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.IsConfigured)
        {
            MessageBox.Show(L.UpdateNotConfigured(UpdateService.CurrentVersion), L.AppTitle);
            return;
        }

        UpdateMenu.IsEnabled = false;
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update == null)
            {
                MessageBox.Show(L.UpdateLatest(UpdateService.CurrentVersion), L.AppTitle);
                return;
            }

            var answer = MessageBox.Show(
                L.UpdatePrompt(update.Value.Version, UpdateService.CurrentVersion),
                L.AppTitle, MessageBoxButton.YesNo);
            if (answer == MessageBoxResult.Yes)
                await UpdateService.DownloadAndApplyAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.UpdateError(ex.Message), L.AppTitle);
        }
        finally
        {
            UpdateMenu.IsEnabled = true;
        }
    }

    /// <summary>Verificação silenciosa ao arrancar: se houver update, realça o item do menu.</summary>
    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            var update = await UpdateService.CheckAsync();
            if (update != null)
                await Dispatcher.InvokeAsync(() =>
                    UpdateMenu.Header = L.UpdateAvailable(update.Value.Version));
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.IntentionalExit = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _positionTimer.Stop();
        _trackTimer.Stop();
        WidgetSettings.Changed -= OnSettingsChanged;
        Instances.Remove(this);
        if (!App.IntentionalExit && !ClosedByApp && !_recreatePending)
        {
            // Explorer reiniciou e levou as janelas (são owned pelas barras):
            // um só waiter recria o conjunto todo quando a barra voltar
            _recreatePending = true;
            _ = RecreateAfterTaskbarRestartAsync();
        }
    }

    /// <summary>Como o widget é owned pela taskbar, um reinício do Explorer
    /// destrói a(s) janela(s) — esperar pela barra nova e recriar o conjunto.</summary>
    private static async Task RecreateAfterTaskbarRestartAsync()
    {
        try
        {
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (Interop.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    await Task.Delay(2000); // deixar a barra (e as secundárias) assentar
                    SyncToMonitors();
                    return;
                }
            }
            App.IntentionalExit = true;
            Application.Current.Shutdown();
        }
        finally
        {
            _recreatePending = false;
        }
    }
}
