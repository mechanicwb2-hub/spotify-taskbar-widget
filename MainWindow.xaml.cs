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
    private readonly LyricsService _lyricsService = new();
    private List<LyricLine>? _currentLyrics;
    private string _currentTrackName = "";
    private string _currentArtistName = "";
    private bool? _liked;

    /// <summary>Barra desta janela (0 = principal, 1+ = secundárias). Cada
    /// monitor selecionado nas definições tem a sua própria instância.</summary>
    public int TrayIndex { get; set; }

    /// <summary>Fecho programático (sincronização de monitores) — não recriar.</summary>
    internal bool ClosedByApp;

    private bool _closed;
    private Action? _mediaChanged;
    private Action? _mediaTimeline;

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
        // A escolha de barra de cada janela pode ter mudado (ex.: o órfão que
        // tinha recuado para a principal tem de a largar JÁ, não daqui a 2s)
        foreach (var win in Instances)
            win._trayCache = IntPtr.Zero;
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
    private DateTime _shuffleToggledAt = DateTime.MinValue;
    private DateTime _seekAt = DateTime.MinValue;

    private bool AcceptPlayingState(bool incoming) =>
        incoming == _isPlayingUi || DateTime.UtcNow - _playToggledAt > TimeSpan.FromSeconds(2);

    // Âncoras da barra (píxeis físicos), atualizadas em background via UI Automation
    // Hook de foreground: o delegate tem de ficar referenciado (senão o GC apanha-o)
    // Um só hook de foreground para o processo inteiro: com N janelas, N hooks
    // davam N callbacks e N pipelines completos por CADA mudança de foco no OS
    private static Interop.WinEventDelegate? _fgProc;
    private static IntPtr _fgHook;

    private static void EnsureForegroundHook()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, _, _, _, _, _) =>
        {
            foreach (var win in Instances)
                win.Dispatcher.BeginInvoke((Action)win.UpdatePosition);
        };
        _fgHook = Interop.SetWinEventHook(
            Interop.EVENT_SYSTEM_FOREGROUND, Interop.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgProc, 0, 0, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private readonly object _anchorLock = new();
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
        OpacityMenuItem.Header = L.OpacityMenu;
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
        LyricsMenu.Header = L.BtnLyrics;
        LyricsStyleMenu.Header = L.LyricsStyle;
        LyricsStyleDefault.Header = L.LyricsStyle1Line;
        LyricsStyleScroll.Header = L.LyricsStyle2Lines;
        LauncherMenu.Header = L.ShowLauncher;
        LauncherMenu.ToolTip = L.ShowLauncherTip;
        AutoStartMenu.Header = L.AutoStart;
        OpenSpotifyMenu.Header = L.OpenSpotify;
        ExitMenu.Header = L.Exit;

        PrevButton.ToolTip = L.TipPrev;
        PlayPauseButton.ToolTip = L.TipPlayPause;
        NextButton.ToolTip = L.TipNext;
        VolumeButton.ToolTip = L.TipVolume;
        RepeatButton.ToolTip = L.TipRepeat;
        ShuffleButton.ToolTip = L.TipShuffle;
        LikeButton.ToolTip = L.TipLikeAdd;
        LyricsToggleButton.ToolTip = L.TipLyrics;
        LauncherPanel.ToolTip = L.TipOpenSpotify;
        LauncherText.Text = L.OpenSpotify;
        ArtistText.Text = L.NothingPlaying;
    }

    private bool _uiReady;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _uiReady = true; // InitializeComponent terminou: elementos nomeados existem
        ApplyLanguage();
        if (PackagedApp.IsPackaged)
        {
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

        // Captura de rato roubada a meio de um arrasto (menu, overlay do
        // sistema): sem isto _dragging ficava presa e o widget deixava de se
        // reposicionar; o próximo clique ainda gravava uma posição fantasma
        Root.LostMouseCapture += (_, _) =>
        {
            // Só quando roubada a MEIO do arrasto — no largar normal o
            // _dragging já está falso e a gravação da posição segue intacta
            if (_dragging)
            {
                _dragging = false;
                _dragMoved = false;
            }
        };

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
            _wheelAccum = 0;
            if (VolumePopup.Child != null &&
                PresentationSource.FromVisual(VolumePopup.Child) is HwndSource src)
                Interop.EnsureTopmost(src.Handle);

            // Aberto via roda, o rato pode nunca ENTRAR no popup — o MouseLeave
            // nunca dispararia e ele ficava aberto para sempre (suprimindo o
            // ReassertTopmost). Watchdog: fechar quando o rato não está nem no
            // popup nem no botão.
            _volPopupWatchdog?.Stop();
            _volPopupWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _volPopupWatchdog.Tick += (_, _) =>
            {
                if (!VolumePopup.IsOpen ||
                    (!VolumePopupBorder.IsMouseOver && !VolumeButton.IsMouseOver))
                {
                    _volPopupWatchdog?.Stop();
                    VolumePopup.IsOpen = false;
                }
            };
            _volPopupWatchdog.Start();
        };

        UpdatePosition();
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePosition();
            UpdateProgressUi();
            UpdateLyricsText();
            ApplyThemeIfChanged();
        };
        _positionTimer.Start();

        // Clicar na taskbar põe a barra por cima do widget; re-afirmar o topmost
        // no instante da mudança de janela ativa (o timer sozinho deixava flicker)
        EnsureForegroundHook();
        Closed += (_, _) =>
        {
            if (_trayLocHook != IntPtr.Zero) Interop.UnhookWinEvent(_trayLocHook);
        };

        // Subscrever ANTES de aguardar a inicialização: com o retry do SMTC, o
        // init pode demorar minutos — o widget tem de reagir logo que ele pegue
        _mediaChanged = () =>
        {
            _artDirty = true;
            _uiaDirty = true;
            Dispatcher.InvokeAsync(() => _ = RefreshTrackAsync());
        };
        _mediaTimeline = () => Dispatcher.InvokeAsync(RefreshTimeline);
        _media.Changed += _mediaChanged;
        _media.TimelineChanged += _mediaTimeline;

        _trackTimer.Tick += (_, _) => _ = RefreshTrackAsync();
        _trackTimer.Start();

        var mediaInit = _media.InitializeAsync();


        await mediaInit;
        if (_closed)
            return; // fechada durante o await (sync de monitores / restart do Explorer)
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
        LyricsMenu.IsChecked = _settings.ShowLyrics;
        LyricsStyleDefault.IsChecked = _settings.LyricsStyle == 0;
        LyricsStyleScroll.IsChecked = _settings.LyricsStyle == 1;

        LyricsToggleButton.Visibility = _settings.ShowLyrics ? Visibility.Visible : Visibility.Collapsed;

        ApplyScale();
        UpdateSizeChecks();
        ApplyOpacity();
        UpdateOpacityChecks();
        ApplyLyricsVisibility();
    }

    private void OnSettingsChanged()
    {
        ApplySettingsUi();
        // Reposicionar só DEPOIS do layout assentar: mudar tamanho/escala e
        // medir a janela no mesmo instante usava as dimensões antigas e o
        // widget aterrava desalinhado até ao tick seguinte
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)UpdatePosition);
        _ = RefreshTrackAsync();
    }

    // ---------- Posicionamento na barra de tarefas ----------

    private IntPtr _trayCache;
    private DateTime _trayCacheAt;

    /// <summary>Cache curta do handle da barra: enumerar e ordenar as barras
    /// todas em CADA evento de posição (dezenas/s durante um deslize) era
    /// trabalho repetido para rederivar um handle que quase nunca muda.</summary>
    private IntPtr GetTargetTray()
    {
        if (_trayCache != IntPtr.Zero && Interop.IsWindow(_trayCache)
            && (DateTime.UtcNow - _trayCacheAt).TotalSeconds < 2)
            return _trayCache;
        _trayCache = ResolveTargetTray();
        _trayCacheAt = DateTime.UtcNow;
        return _trayCache;
    }

    /// <summary>Barra de tarefas desta janela (principal ou secundária). Se a
    /// barra alvo não existir (monitor desligado), UMA janela órfã — a de menor
    /// índice — recua para a barra principal, desde que nenhuma outra lá viva;
    /// as restantes escondem-se. Garante que a app nunca fica toda invisível
    /// (sem widget não há menu de contexto para recuperar).</summary>
    private IntPtr ResolveTargetTray()
    {
        if (TrayIndex > 0)
        {
            var secondaries = Interop.GetSecondaryTrays();
            if (TrayIndex <= secondaries.Count)
                return secondaries[TrayIndex - 1];
            // O recuo só serve para a app não ficar TODA invisível: se outra
            // janela ainda tem barra (principal ou secundária viva), ou há um
            // órfão de índice menor que recua primeiro, esta esconde-se
            bool someoneVisible = Instances.Any(w => w != this &&
                (w.TrayIndex == 0 || w.TrayIndex <= secondaries.Count));
            bool lowerOrphan = Instances.Any(w => w != this &&
                w.TrayIndex > secondaries.Count && w.TrayIndex < TrayIndex);
            if (someoneVisible || lowerOrphan)
                return IntPtr.Zero;
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
        if (_closed)
            return; // dispatch em atraso numa janela fechada: sem isto podia
                    // re-registar hooks num hwnd morto (crash em callback GC'd)
        IntPtr tray = GetTargetTray();
        if (tray == IntPtr.Zero || !Interop.GetWindowRect(tray, out var r))
        {
            // Barra alvo indisponível (monitor desligado / Explorer a reiniciar):
            // esconder e limpar o estado de deslize — sem isto, a reaparição
            // tocava uma animação de subida espúria
            CancelRide();
            _barWasHidden = false;
            HideWidget();
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
            ReassertTopmost();
        }

        // Ocultação automática: o widget "cavalga" a barra — o hook de movimento
        // chama isto a cada frame da animação e o Y segue o rect atual, por isso
        // ele desce e sobe colado à barra. Só se esconde quando ela assenta fora
        // do ecrã; âncoras ficam intactas (o X não muda num deslize vertical).
        int trayHeightPx = r.Bottom - r.Top;
        int visiblePx = Interop.GetTaskbarVisiblePx(r, out int monitorBottomPx, out int workAreaBottomPx);

        if (!Interop.GetWindowRect(_hwnd, out var w))
            return;
        int winWidth = w.Right - w.Left;
        int winHeight = w.Bottom - w.Top;

        // Centrar na banda DESENHADA da barra, não no rect da janela dela (no
        // 25H2 o rect é mais alto e o widget flutuava — issue #9). A reserva de
        // área de trabalho dá a altura real desenhada em qualquer barra
        // (incluindo multi-linha); com ocultação automática não há reserva e
        // vale a heurística dos 48 DIP do Win11.
        int reservedPx = monitorBottomPx - workAreaBottomPx;
        int barBandPx = reservedPx > 8
            ? Math.Min(trayHeightPx, reservedPx)
            : Math.Min(trayHeightPx, (int)Math.Round(48 * Interop.GetDpiForWindow(tray) / 96.0));
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

        // Interação em curso "pina" o widget: esconder a meio de um arrasto do
        // slider/menu fechava-lhe o popup nas mãos (e um arrasto de move-mode
        // escondido perdia a captura do rato e ficava _dragging preso). Quando
        // a interação acabar, o tick seguinte esconde normalmente.
        bool pinned = _dragging || VolumePopup.IsOpen ||
                      (Root.ContextMenu?.IsOpen ?? false);

        if (visiblePx <= 8)
        {
            if (pinned)
                return;
            // Assente fora do ecrã. Se o widget ainda está à vista, o esconder
            // aconteceu num salto único — animar a descida na mesma.
            if (Visibility == Visibility.Visible && Interop.IsAutoHideEnabled())
            {
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
                return;
            }
            _barWasHidden = true;
            HideWidget();
            return;
        }

        if (visiblePx < trayHeightPx - 4)
        {
            // A deslizar. Widget visível = a barra começou a esconder-se: animar
            // a nossa descida (seguir os passos grossos da janela dela ficava
            // aos solavancos). Invisível = revelação em curso: esperar que
            // assente — a subida anima nessa altura.
            if (Visibility == Visibility.Visible && !pinned && Interop.IsAutoHideEnabled())
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
        if (_settings.ManualX.TryGetValue(TrayIndex, out double manualX))
        {
            // Posição manual DESTA barra (por monitor — arrastar um widget não
            // pode arrastar os dos outros ecrãs)
            leftPx = (int)Math.Max(r.Left + 4, Math.Min(manualX, r.Right - winWidth - 4));
            rightLimitPx = r.Right - 4;
        }
        else if (!IsTaskbarLeftAligned())
        {
            // Numa barra centrada o botão Iniciar existe sempre — âncora nula
            // significa que a leitura ainda não chegou ou falhou.
            if (!startLeftPx.HasValue && Visibility == Visibility.Visible)
            {
                // Já estamos bem posicionados: FICAR QUIETO até as âncoras
                // voltarem — esconder e reaparecer na borda esquerda (por cima
                // do botão do tempo) era exatamente o salto reportado
                leftPx = w.Left;
                rightLimitPx = r.Right - 4;
            }
            else if (!startLeftPx.HasValue)
            {
                // Ainda sem posição (arranque / primeiro reveal): esperar em
                // vez de posicionar às cegas; após o limite, fallback à esquerda
                if (_anchorsMissingSince == DateTime.MinValue)
                    _anchorsMissingSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _anchorsMissingSince < TimeSpan.FromSeconds(4))
                {
                    HideWidget();
                    return;
                }
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : r.Left + 12;
                rightLimitPx = r.Right - 4;
            }
            else
            {
                _anchorsMissingSince = DateTime.MinValue;
                // Ícones centrados (em qualquer barra/monitor): o espaço livre
                // está à esquerda — alinhar a seguir ao botão de widgets/tempo;
                // sem ele, à borda esquerda. Nunca invadir o botão Iniciar.
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : r.Left + 12;
                rightLimitPx = (int)startLeftPx.Value - 8;
            }
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
                HideWidget();
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
            HideWidget();
            return;
        }

        // Revelação da barra: o Windows teleporta a janela dela para o destino
        // e anima só o visual — não há frames para seguir. Animamos nós a
        // subida, a emergir da borda do ecrã em sincronia com a barra.
        // (Só faz sentido com ocultação automática — sem ela, um rect
        // transitório degenerado da barra armava uma subida espúria.)
        if (_barWasHidden && !_dragging && Interop.IsAutoHideEnabled())
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
        ReassertTopmost();
    }

    private bool _tooltipOpen;

    /// <summary>Esconde o widget e fecha SEMPRE o popup de volume e o menu de
    /// contexto — todos os caminhos de esconder passam por aqui para não
    /// divergirem (havia caminhos que deixavam popups órfãos a flutuar).</summary>
    private void HideWidget()
    {
        VolumePopup.IsOpen = false;
        if (Root.ContextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        if (Visibility != Visibility.Hidden)
            Visibility = Visibility.Hidden;
    }

    /// <summary>Re-afirma o widget no topo, EXCETO com um tooltip/popup nosso
    /// aberto — re-afirmar por cima deles empurra-os para trás da barra (que
    /// em ocultação automática também vive na banda topmost).</summary>
    private void ReassertTopmost()
    {
        if (!_tooltipOpen && !VolumePopup.IsOpen)
            Interop.EnsureTopmost(_hwnd);
    }

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
        ReassertTopmost();

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
    private int _startMissingReads;

    private void RefreshAnchors(IntPtr tray)
    {
        if ((DateTime.UtcNow - _lastAnchorQuery).TotalSeconds < 5)
            return;
        // Watchdog: se uma query pendurou (UIA contra um Explorer moribundo), a
        // flag não pode prender as âncoras para sempre — após 15s arranca outra
        if (_anchorQueryRunning && (DateTime.UtcNow - _lastAnchorQuery).TotalSeconds < 15)
            return;

        _anchorQueryRunning = true;
        _lastAnchorQuery = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                var (ok, widgetsRight, startLeft, taskButtonsRight) = TaskbarAnchors.Get(tray);
                lock (_anchorLock)
                {
                    // A barra alvo mudou enquanto a query corria: estes valores
                    // são coordenadas do monitor errado — deitar fora
                    if (tray != _anchorsTray)
                        return;
                    // Leitura falhada → mantêm-se TODAS as âncoras anteriores
                    // (falha transitória de UIA ≠ layout da barra mudou; era
                    // isto que punha o widget em cima do botão do tempo)
                    if (!ok)
                        return;
                    // O Iniciar existe sempre — uma leitura OK sem ele é suspeita;
                    // mas aceitar à 3ª seguida, senão um Iniciar realmente
                    // escondido (shells modificadas) congelava as âncoras todas
                    if (!startLeft.HasValue && _startLeftPx.HasValue && ++_startMissingReads < 3)
                        return;
                    bool startVanished = !startLeft.HasValue && _startLeftPx.HasValue;
                    _startMissingReads = 0;
                    // Se o Iniciar sumiu (estado anómalo da shell), as outras
                    // âncoras nulas provavelmente só falharam JUNTAS — manter as
                    // antigas; com leitura completa, null = desativado mesmo
                    _widgetsRightPx = startVanished ? (widgetsRight ?? _widgetsRightPx) : widgetsRight;
                    _taskEndPx = startVanished ? (taskButtonsRight ?? _taskEndPx) : taskButtonsRight;
                    _startLeftPx = startLeft;
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
        if (_refreshing || _closed) return;
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
                // Após um seek, ignorar posições fotografadas ANTES do salto
                if (!(DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && posAt < _seekAt))
                {
                    _basePosition = pos;
                    _basePositionAt = posAt;
                }
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
                _currentTrackName = "";
                _currentArtistName = "";
                _currentLyrics = null;
                UpdateLyricsText();
                UpdateMarquee();
                return;
            }

            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist;
            UpdateMarquee();
            SetPlayPauseIcon(_isPlayingUi);

            if (_currentTrackName != track.Title || _currentArtistName != track.Artist)
            {
                _currentTrackName = track.Title;
                _currentArtistName = track.Artist;
                _currentLyrics = null;
                UpdateLyricsText();
                _ = FetchLyricsAsync(_currentTrackName, _currentArtistName);
            }

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
            // A rede de segurança do SMTC atrasa-se vários segundos após um
            // clique — sobrepor-se a uma leitura UIA fresca fazia o ícone
            // piscar On→Off→On; janela de graça como no play/pause
            if (DateTime.UtcNow - _shuffleToggledAt > TimeSpan.FromSeconds(4))
            {
                if (track.IsShuffle == false && mode != ShuffleMode.Unknown)
                    mode = ShuffleMode.Off;
                else if (track.IsShuffle == true && mode is ShuffleMode.Off or ShuffleMode.Unknown)
                    mode = ShuffleMode.On;
            }

            ApplyShuffleVisual(mode);

            if (keyChanged || _artDirty)
            {
                _lastTrackKey = key;
                _artDirty = false;
                byte[]? bytes = null;
                try { bytes = await _media.GetThumbnailAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                BitmapImage? art = null;
                if (bytes != null)
                {
                    // Miniaturas truncadas/corrompidas acontecem em transições
                    // de faixa — não podem rebentar o refresh inteiro
                    try { art = ToBitmap(bytes); } catch { }
                }
                if (art != null)
                {
                    ArtBrush.ImageSource = art;
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
        // Depois de um seek, snapshots ANTERIORES ao salto ainda chegam durante
        // uns segundos — aplicá-los fazia a barra recuar e voltar a saltar
        if (DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && tl.PositionAtUtc < _seekAt)
            return;
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
        _shuffleToggledAt = DateTime.UtcNow;

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
        if (_volLoading)
            return; // já há uma abertura em curso — outra sobrescreveria o
                    // ajuste do utilizador com o volume antigo ao completar

        CaptureForeground();
        _volLoading = true;
        try
        {
            // O recurso CoreAudio também fora da thread de UI — é uma RPC ao
            // serviço de áudio e chegava a bloquear a interface
            double? current = await Task.Run(() => _uia.GetVolume() ?? SpotifyVolume.GetVolume());
            if (_closed || Visibility != Visibility.Visible)
                return; // o widget escondeu-se durante a leitura: não abrir
                        // um popup órfão a flutuar sobre a barra
            // Atribuir COM _volLoading ainda ativo: senão o ValueChanged ecoava
            // a leitura de volta ao Spotify em cada abertura — e uma leitura
            // falhada (null → 100%) rebentava o volume só por abrir o popup
            if (current is double v)
                VolumeSlider.Value = v * 100;
        }
        finally
        {
            _volLoading = false;
        }
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

    private int _wheelAccum;

    /// <summary>Roda do rato sobre o botão/popup de volume: fechado abre (com o
    /// volume atual carregado), aberto ajusta ±5 — como no próprio Spotify.</summary>
    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!VolumePopup.IsOpen)
        {
            Volume_Click(sender, null!); // o guard de _volLoading evita reentradas
            return;
        }
        if (_volLoading) return;
        // Acumular o delta em vez de ±5 por EVENTO: touchpads de precisão
        // mandam dezenas de eventos pequenos por gesto (o volume ia de 50 a 0
        // num toque) e rodas rápidas juntam vários notches num evento só
        // Inverter o sentido descarta o resto acumulado — senão o primeiro
        // notch da direção contrária era "engolido" a cancelar o resíduo
        if (_wheelAccum != 0 && Math.Sign(_wheelAccum) != Math.Sign(e.Delta))
            _wheelAccum = 0;
        _wheelAccum += e.Delta;
        int steps = _wheelAccum / 120;
        if (steps == 0) return;
        _wheelAccum -= steps * 120;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + 5 * steps, 0, 100);
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
        // O DPI entra na chave: a largura renderizada muda com a escala do
        // monitor e a decisão de scroll ficava obsoleta ao mudar de ecrã
        string key = $"{TitleText.Text}|{clipWidth:0}|{VisualTreeHelper.GetDpi(this).PixelsPerDip:0.##}";
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
            var anim = new DoubleAnimationUsingKeyFrames();
            if (!_settings.ScrollTitleOnce)
                anim.RepeatBehavior = RepeatBehavior.Forever;
            else
                anim.FillBehavior = FillBehavior.Stop;
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

        // Atualização otimista para a barra responder já; a janela de graça
        // impede snapshots pré-salto de a fazer recuar nos segundos seguintes
        _seekAt = DateTime.UtcNow;
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



    private void VolumePopup_Closed(object sender, EventArgs e)
    {
        _volPopupWatchdog?.Stop();
        RestoreForeground();
    }

    private DispatcherTimer? _volPopupWatchdog;

    // ---------- Preservação do foco ----------
    // O popup do volume e o menu de contexto ativam janelas próprias do WPF;
    // quando fecham, o foco pode ficar órfão e atalhos globais (PrintScreen,
    // Win+Shift+S) deixam de responder até se clicar noutra janela.

    private IntPtr _fgBeforeUi;

    private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        CaptureForeground();
        Interop.SetForegroundWindow(_hwnd); // Fix: o WPF não fecha o menu se a janela não tiver foco
        RebuildMonitorMenu();
        // Estado global — outra janela (ou o Gestor de Tarefas) pode tê-lo mudado
        if (!PackagedApp.IsPackaged)
            AutoStartMenu.IsChecked = IsAutoStartEnabled();
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
                    // Tem de sobrar pelo menos um monitor QUE EXISTA — entradas
                    // de monitores desligados não contam, senão a seleção podia
                    // ficar só com barras inexistentes e a app toda invisível
                    if (!monitors.Any(m => m != index && m <= count))
                    {
                        ((MenuItem)s).IsChecked = true;
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
                // Se esta própria janela vai ser removida, fechar o menu antes —
                // um menu StaysOpen órfão numa janela destruída fica pendurado
                if (!monitors.Contains(TrayIndex) && Root.ContextMenu is { IsOpen: true } cm)
                    cm.IsOpen = false;
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
                // Fica bloqueado nesta posição (modo manual) SÓ nesta barra;
                // px físicos, indexados pelo monitor desta janela
                if (Interop.GetWindowRect(_hwnd, out var w))
                    _settings.ManualX[TrayIndex] = w.Left;
                _settings.Save();
            }
        }
        else if (_pressed)
        {
            _pressed = false;
        }
    }

    private void Art_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SpotifyActions.OpenSpotifyWindow();
        e.Handled = true;
    }

    // ---------- Menu de contexto ----------

    private void MoveMode_Click(object sender, RoutedEventArgs e)
    {
        _moveMode = MoveMenu.IsChecked;
        Root.Cursor = _moveMode ? Cursors.SizeAll : Cursors.Hand;
    }

    private void ResetPos_Click(object sender, RoutedEventArgs e)
    {
        // Repõe as POSIÇÕES automáticas em todas as barras; a seleção de
        // monitores fica como está (limpá-la destruía a escolha do utilizador)
        _settings.AutoPosition = true;
        _settings.ManualX.Clear();
        _settings.Save();
        MoveMenu.IsChecked = false;
        _moveMode = false;
        Root.Cursor = Cursors.Hand;
        UpdatePosition();
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        _settings.Scale = double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        // O Save propaga a TODAS as janelas via Changed (ApplySettingsUi +
        // reposicionamento adiado para depois do layout) — nada a fazer aqui
        _settings.Save();
    }

    private void ApplyScale() => Root.LayoutTransform = new ScaleTransform(_settings.Scale, _settings.Scale);

    private void UpdateSizeChecks()
    {
        SizeSmall.IsChecked = Math.Abs(_settings.Scale - 0.8) < 0.01;
        SizeNormal.IsChecked = Math.Abs(_settings.Scale - 1.0) < 0.01;
        SizeLarge.IsChecked = _settings.Scale > 1.05;
    }

    /// <summary>Brilho/opacidade do widget — pedido de utilizadores OLED que
    /// escurecem a barra: um widget a brilho total destoa e marca o painel.</summary>
    private bool _opacityLoading;

    /// <summary>Slider de brilho 20–100% (pedido da comunidade OLED). Pré-visualiza
    /// ao vivo ao arrastar; grava no fim, uma vez, para não inundar o disco nem
    /// o evento Changed com um Save por cada passo do arrasto.</summary>
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Durante o carregamento do XAML, definir Minimum=20 coage o Value de 0
        // para 20 e dispara este evento ANTES de Root/OpacityValueText existirem
        // — sem este guard era NRE e a janela nunca se construía (widget sumia)
        if (_opacityLoading || !_uiReady) return;
        _settings.Opacity = Math.Clamp(e.NewValue / 100.0, 0.2, 1.0);
        ApplyOpacity();
        OpacityValueText.Text = $"{Math.Round(e.NewValue)}%";
        // Adiar o Save até o slider assentar (sem eventos por ~400ms)
        _opacitySaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _opacitySaveTimer.Stop();
        _opacitySaveTimer.Tick -= OpacitySaveTick;
        _opacitySaveTimer.Tick += OpacitySaveTick;
        _opacitySaveTimer.Start();
    }

    private DispatcherTimer? _opacitySaveTimer;

    private void OpacitySaveTick(object? sender, EventArgs e)
    {
        _opacitySaveTimer?.Stop();
        _settings.Save(); // propaga a todas as janelas via Changed
    }

    private int _opacityWheelAccum;

    /// <summary>Roda do rato sobre o slider de brilho: ±5% por notch, igual ao
    /// volume. Acumula o delta (touchpads de precisão mandam muitos eventos
    /// pequenos) e descarta o resto ao inverter o sentido.</summary>
    private void Opacity_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_opacityWheelAccum != 0 && Math.Sign(_opacityWheelAccum) != Math.Sign(e.Delta))
            _opacityWheelAccum = 0;
        _opacityWheelAccum += e.Delta;
        int steps = _opacityWheelAccum / 120;
        if (steps == 0) return;
        _opacityWheelAccum -= steps * 120;
        // Mexer no Value dispara o ValueChanged, que aplica e agenda o Save
        OpacitySlider.Value = Math.Clamp(OpacitySlider.Value + 5 * steps, 20, 100);
    }

    private void ApplyOpacity() => Root.Opacity = _settings.Opacity;

    private void UpdateOpacityChecks()
    {
        // Refletir o valor guardado no slider sem redisparar o Save
        _opacityLoading = true;
        OpacitySlider.Value = Math.Round(_settings.Opacity * 100);
        OpacityValueText.Text = $"{Math.Round(_settings.Opacity * 100)}%";
        _opacityLoading = false;
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLauncher = LauncherMenu.IsChecked;
        _settings.Save();
        _ = RefreshTrackAsync();
        UpdatePosition();
    }

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.LyricsActive = !_settings.LyricsActive;
        _settings.Save();
        ApplyLyricsVisibility();
        UpdatePosition();
    }

    private void ApplyLyricsVisibility()
    {
        if (_settings.LyricsActive)
        {
            LyricsPanel.Visibility = Visibility.Visible;
            LyricsToggleIcon.Fill = (Brush)new BrushConverter().ConvertFrom("#1ED760");
            UpdateLyricsText();
        }
        else
        {
            LyricsPanel.Visibility = Visibility.Collapsed;
            LyricsToggleIcon.Fill = (Brush)new BrushConverter().ConvertFrom("#B3B3B3");
        }
    }

    private async Task FetchLyricsAsync(string title, string artist)
    {
        var lyrics = await _lyricsService.GetLyricsAsync(title, artist);
        if (_currentTrackName == title && _currentArtistName == artist)
        {
            _currentLyrics = lyrics ?? new List<LyricLine>();
            _currentLyricIndex = -1;
            UpdateLyricsText();
        }
    }

    private int _currentLyricIndex = -1;

    private void UpdateLyricsText()
    {
        if (!_settings.LyricsActive) return;

        if (!_spotifyPresent || string.IsNullOrWhiteSpace(_currentTrackName))
        {
            LyricsText.Text = L.NothingPlaying;
            LyricsTextNext.Visibility = Visibility.Collapsed;
            _currentLyricIndex = -1;
            return;
        }

        if (_currentLyrics == null || _currentLyrics.Count == 0)
        {
            LyricsText.Text = "";
            LyricsTextNext.Visibility = Visibility.Collapsed;
            _currentLyricIndex = -1;
            return;
        }

        TimeSpan currentPos = _isPlayingUi ? _basePosition + (DateTime.UtcNow - _basePositionAt) : _basePosition;
        
        int newIndex = -1;
        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            if (currentPos >= _currentLyrics[i].Time) newIndex = i;
            else break;
        }

        if (newIndex == -1)
        {
            if (_currentLyricIndex != -1 || LyricsText.Text != "♪")
            {
                LyricsText.Text = "♪";
                LyricsTextNext.Visibility = Visibility.Collapsed;
                LyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
                LyricsScrollTransform.Y = 0;
                LyricsText.BeginAnimation(UIElement.OpacityProperty, null);
                LyricsText.Opacity = 1.0;
                _currentLyricIndex = -1;
            }
            return;
        }

        if (newIndex != _currentLyricIndex)
        {
            bool animate = _settings.LyricsStyle == 1 && newIndex == _currentLyricIndex + 1 && _currentLyricIndex != -1;
            _currentLyricIndex = newIndex;
            
            string currentLine = newIndex >= 0 && !string.IsNullOrWhiteSpace(_currentLyrics[newIndex].Text) 
                ? _currentLyrics[newIndex].Text : "♪";
            
            string nextLine = "";
            if (_settings.LyricsStyle == 1 && newIndex >= 0)
            {
                for (int i = newIndex + 1; i < _currentLyrics.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(_currentLyrics[i].Text))
                    {
                        nextLine = _currentLyrics[i].Text;
                        break;
                    }
                }
            }

            if (_settings.LyricsStyle == 1)
            {
                LyricsScrollPanel.VerticalAlignment = VerticalAlignment.Top;
                LyricsText.TextAlignment = TextAlignment.Center;
                LyricsText.HorizontalAlignment = HorizontalAlignment.Center;
                LyricsTextNext.TextAlignment = TextAlignment.Center;
                LyricsTextNext.HorizontalAlignment = HorizontalAlignment.Center;
                
                LyricsTextNext.Visibility = string.IsNullOrEmpty(nextLine) ? Visibility.Collapsed : Visibility.Visible;
                
                if (animate)
                {
                    double oldHeight = LyricsText.ActualHeight > 0 ? LyricsText.ActualHeight : 20;
                    LyricsText.Text = currentLine;
                    LyricsTextNext.Text = nextLine;
                    
                    var yAnim = new DoubleAnimation(oldHeight, 0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
                    LyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, yAnim);
                    
                    var opAnim1 = new DoubleAnimation(0.533, 1.0, TimeSpan.FromMilliseconds(400));
                    LyricsText.BeginAnimation(UIElement.OpacityProperty, opAnim1);
                    
                    if (!string.IsNullOrEmpty(nextLine))
                    {
                        var opAnim2 = new DoubleAnimation(0.0, 0.533, TimeSpan.FromMilliseconds(400));
                        LyricsTextNext.BeginAnimation(UIElement.OpacityProperty, opAnim2);
                    }
                }
                else
                {
                    LyricsText.Text = currentLine;
                    LyricsTextNext.Text = nextLine;
                    LyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
                    LyricsScrollTransform.Y = 0;
                    LyricsText.BeginAnimation(UIElement.OpacityProperty, null);
                    LyricsText.Opacity = 1.0;
                    LyricsTextNext.BeginAnimation(UIElement.OpacityProperty, null);
                    LyricsTextNext.Opacity = 0.533;
                }
            }
            else
            {
                LyricsScrollPanel.VerticalAlignment = VerticalAlignment.Center;
                LyricsText.TextAlignment = TextAlignment.Left;
                LyricsText.HorizontalAlignment = HorizontalAlignment.Left;
                
                LyricsText.Text = currentLine;
                LyricsTextNext.Visibility = Visibility.Collapsed;
                LyricsScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
                LyricsScrollTransform.Y = 0;
                LyricsText.BeginAnimation(UIElement.OpacityProperty, null);
                LyricsText.Opacity = 1.0;
            }
        }
    }

    private void Buttons_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLike = BtnLikeMenu.IsChecked;
        _settings.ShowShuffle = BtnShuffleMenu.IsChecked;
        _settings.ShowPrev = BtnPrevMenu.IsChecked;
        _settings.ShowNext = BtnNextMenu.IsChecked;
        _settings.ShowRepeat = BtnRepeatMenu.IsChecked;
        _settings.ShowVolume = BtnVolumeMenu.IsChecked;
        _settings.ShowLyrics = LyricsMenu.IsChecked;
        _settings.Save();
        UpdatePosition();
    }

    private void LyricsStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender == LyricsStyleDefault)
            _settings.LyricsStyle = 0;
        else if (sender == LyricsStyleScroll)
            _settings.LyricsStyle = 1;

        _settings.Save();
        _currentLyricIndex = -1;
        UpdateLyricsText();
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



    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.IntentionalExit = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true; // trava a continuação do OnLoaded se ainda estiver no await
        _positionTimer.Stop();
        _trackTimer.Stop();
        CancelRide(); // o tick do ride não pode continuar num hwnd morto
        if (_mediaChanged != null) _media.Changed -= _mediaChanged;
        if (_mediaTimeline != null) _media.TimelineChanged -= _mediaTimeline;
        _media.Shutdown(); // solta as subscrições WinRT que prendiam a janela
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
