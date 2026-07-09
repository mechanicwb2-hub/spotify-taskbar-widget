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
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private bool? _liked;

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

    // Âncoras da barra (píxeis físicos), atualizadas em background via UI Automation
    private readonly object _anchorLock = new();
    private double? _widgetsRightPx;
    private double? _startLeftPx;
    private DateTime _lastAnchorQuery = DateTime.MinValue;
    private bool _anchorQueryRunning;

    private const double MaxTextWidth = 150;
    private const double MinTextWidth = 60;

    public MainWindow()
    {
        InitializeComponent();
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
        AutoStartMenu.IsChecked = IsAutoStartEnabled();
        LauncherMenu.IsChecked = _settings.ShowLauncher;
        ProgressMenu.IsChecked = _settings.ShowProgress;
        ApplyThemeIfChanged();
        RebuildMonitorMenu();
        BtnLikeMenu.IsChecked = _settings.ShowLike;
        BtnShuffleMenu.IsChecked = _settings.ShowShuffle;
        BtnPrevMenu.IsChecked = _settings.ShowPrev;
        BtnNextMenu.IsChecked = _settings.ShowNext;
        BtnRepeatMenu.IsChecked = _settings.ShowRepeat;
        BtnVolumeMenu.IsChecked = _settings.ShowVolume;
        ApplyScale();
        UpdateSizeChecks();

        UpdatePosition();
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePosition();
            UpdateProgressUi();
            ApplyThemeIfChanged();
        };
        _positionTimer.Start();

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

        _ = CheckUpdatesQuietlyAsync();

        await RefreshTrackAsync();
    }

    // ---------- Posicionamento na barra de tarefas ----------

    /// <summary>Barra de tarefas escolhida nas definições (principal ou secundária).</summary>
    private IntPtr GetTargetTray()
    {
        if (_settings.MonitorIndex > 0)
        {
            var secondaries = Interop.GetSecondaryTrays();
            if (_settings.MonitorIndex <= secondaries.Count)
                return secondaries[_settings.MonitorIndex - 1];
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

    private void UpdatePosition()
    {
        IntPtr tray = GetTargetTray();
        if (tray == IntPtr.Zero || !Interop.GetWindowRect(tray, out var r))
            return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
            return;

        // píxeis físicos -> unidades WPF (DIP)
        Matrix m = source.CompositionTarget.TransformFromDevice;
        Point tl = m.Transform(new Point(r.Left, r.Top));
        Point br = m.Transform(new Point(r.Right, r.Bottom));

        double trayHeight = br.Y - tl.Y;
        double top = tl.Y + (trayHeight - ActualHeight) / 2;

        RefreshAnchors(tray);
        double? widgetsRightPx, startLeftPx;
        lock (_anchorLock)
        {
            widgetsRightPx = _widgetsRightPx;
            startLeftPx = _startLeftPx;
        }

        bool primaryTray = _settings.MonitorIndex == 0;
        bool rightAnchored = false;
        double left, rightLimit;
        if (!_settings.AutoPosition)
        {
            left = Math.Max(tl.X + 4, Math.Min(_settings.X, br.X - ActualWidth - 4));
            rightLimit = br.X - 4;
        }
        else if (primaryTray && !IsTaskbarLeftAligned())
        {
            // Ícones centrados: alinhar a seguir ao botão de widgets/tempo; sem
            // ele, à borda esquerda. Nunca invadir os ícones (botão Iniciar).
            left = widgetsRightPx.HasValue
                ? m.Transform(new Point(widgetsRightPx.Value, 0)).X + 6
                : tl.X + 12;
            rightLimit = startLeftPx.HasValue
                ? m.Transform(new Point(startLeftPx.Value, 0)).X - 6
                : br.X - 4;
        }
        else
        {
            // Ícones alinhados à esquerda (ou barra secundária): o espaço vazio
            // está à direita — encostar antes dos ícones do sistema/relógio
            rightAnchored = true;
            int? notifyLeftPx = Interop.GetTrayNotifyLeft(tray);
            rightLimit = notifyLeftPx.HasValue
                ? m.Transform(new Point(notifyLeftPx.Value, 0)).X - 8
                : br.X - 220;
            left = rightLimit - ActualWidth;
        }

        if (_spotifyPresent)
            ApplyResponsiveLayout(rightAnchored ? double.PositiveInfinity : rightLimit - left);

        if (!_dragging)
        {
            if (Math.Abs(Top - top) > 0.5) Top = top;
            if (Math.Abs(Left - left) > 0.5) Left = left;
        }

        // Esconder quando: app em ecrã inteiro; Spotify fechado (sem botão de
        // abrir); ou a barra deslizou para fora (ocultação automática)
        bool hide = Interop.IsForegroundFullscreen(_hwnd, tray)
                    || (!_spotifyPresent && !_settings.ShowLauncher)
                    || Interop.IsTaskbarHidden(tray, r);
        var wanted = hide ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != wanted)
        {
            Visibility = wanted;
            if (hide) VolumePopup.IsOpen = false;
        }

        if (!hide)
            Interop.EnsureTopmost(_hwnd);
    }

    /// <summary>
    /// Encaixa o widget no espaço disponível: primeiro encolhe o texto até um
    /// mínimo; se mesmo assim não couber, esconde os botões menos importantes
    /// (volume → aleatório → favoritos → seguinte → anterior).
    /// </summary>
    private void ApplyResponsiveLayout(double availableDip)
    {
        double s = _settings.Scale;
        double avail = availableDip / s; // trabalhar em unidades pré-escala

        const double IconBtn = 28;            // 26 + margens
        const double PlayBtn = 34;            // 30 + margens
        const double BasePart = 16 + 34 + 15; // padding + capa + margens do texto

        double used = BasePart + PlayBtn + MinTextWidth;

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
                var (widgetsRight, startLeft) = TaskbarAnchors.Get(tray);
                lock (_anchorLock)
                {
                    _widgetsRightPx = widgetsRight;
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
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            bool processAlive = Process.GetProcessesByName("Spotify").Length > 0;
            TrackInfo? track = processAlive ? await _media.GetTrackAsync() : null;

            // Perder a sessão com o processo ainda vivo = fecho do Spotify em curso
            // (o processo demora 1-2 s a morrer). Esconder já, sem estados intermédios.
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
            _basePosition = track?.Position ?? TimeSpan.Zero;
            _basePositionAt = DateTime.UtcNow;
            _isPlayingUi = track?.IsPlaying == true;
            UpdateProgressUi();

            if (track == null || string.IsNullOrWhiteSpace(track.Title))
            {
                TitleText.Text = "Spotify";
                ArtistText.Text = L.NothingPlaying;
                PlayPauseIcon.Data = PlayGeo;
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
            PlayPauseIcon.Data = track.IsPlaying ? PauseGeo : PlayGeo;

            // Estado real (favoritos + aleatório + repetição) da árvore de
            // acessibilidade do Spotify; o SMTC serve de rede de segurança.
            string key = track.Title + "|" + track.Artist;
            bool keyChanged = key != _lastTrackKey;
            if (keyChanged || _uiaDirty || DateTime.UtcNow - _lastUiaStateAt > TimeSpan.FromSeconds(5))
            {
                _uiaDirty = false;
                _uiaState = await Task.Run(() => _uia.GetState());
                _lastUiaStateAt = DateTime.UtcNow;
            }
            var (liked, uiaMode, repeatMode) = _uiaState;
            _liked = liked;

            RepeatIcon.Fill = repeatMode is RepeatMode.Context or RepeatMode.Track
                ? SpotifyGreen
                : (repeatMode == RepeatMode.Off ? Subdued : DimWhite);
            RepeatDot.Visibility = repeatMode is RepeatMode.Context or RepeatMode.Track
                ? Visibility.Visible : Visibility.Collapsed;
            RepeatOneBadge.Visibility = repeatMode == RepeatMode.Track
                ? Visibility.Visible : Visibility.Collapsed;

            LikeIcon.Data = liked == true ? CheckCircleGeo : AddCircleGeo;
            LikeIcon.Fill = liked == true ? SpotifyGreen : (liked == false ? Subdued : DimWhite);
            LikeButton.ToolTip = liked == true ? L.TipLiked : L.TipLikeAdd;

            ShuffleMode mode = uiaMode;
            if (track.IsShuffle == false && mode != ShuffleMode.Unknown)
                mode = ShuffleMode.Off;
            else if (track.IsShuffle == true && mode is ShuffleMode.Off or ShuffleMode.Unknown)
                mode = ShuffleMode.On;

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

            if (keyChanged || _artDirty)
            {
                _lastTrackKey = key;
                _artDirty = false;
                byte[]? bytes = await _media.GetThumbnailAsync();
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

    /// <summary>Atualização leve disparada pelos eventos de timeline (frequentes).</summary>
    private void RefreshTimeline()
    {
        if (_media.GetTimeline() is not { } tl) return;
        _duration = tl.Duration;
        _basePosition = tl.Position;
        _basePositionAt = DateTime.UtcNow;
        _isPlayingUi = tl.IsPlaying;
        PlayPauseIcon.Data = tl.IsPlaying ? PauseGeo : PlayGeo;
        UpdateProgressUi();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // Feedback imediato; os eventos do SMTC corrigem se o comando falhar
        _isPlayingUi = !_isPlayingUi;
        PlayPauseIcon.Data = _isPlayingUi ? PauseGeo : PlayGeo;
        if (!_isPlayingUi)
            _basePosition += DateTime.UtcNow - _basePositionAt; // congelar posição
        _basePositionAt = DateTime.UtcNow;
        await _media.TogglePlayPauseAsync();
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        // Clique no botão do próprio Spotify: cicla desligado → aleatório → inteligente
        bool ok = await Task.Run(() => _uia.CycleShuffle());
        if (!ok)
            await _media.ToggleShuffleAsync(); // sem janela do Spotify: só liga/desliga
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
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

        bool ok = await Task.Run(() => _uia.AddToFavorites());
        if (!ok)
            await Task.Run(SpotifyActions.LikeCurrentTrack); // recurso: atalho de teclado
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
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
        TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TitleText.DesiredSize.Width;
        double clipWidth = TextStack.Width;

        string key = $"{TitleText.Text}|{clipWidth:0}";
        if (key == _marqueeKey) return;
        _marqueeKey = key;

        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);
        TitleShift.X = 0;

        double overflow = textWidth - clipWidth;
        if (overflow > 4)
        {
            TitleText.TextTrimming = TextTrimming.None;
            TitleText.Width = textWidth;

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
        else
        {
            TitleText.Width = double.NaN;
            TitleText.TextTrimming = TextTrimming.CharacterEllipsis;
        }
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

    /// <summary>Um item por barra de tarefas existente (a lista pode mudar ao ligar/desligar monitores).</summary>
    private void RebuildMonitorMenu()
    {
        MonitorMenu.Items.Clear();
        int count = Interop.GetSecondaryTrays().Count;
        for (int i = 0; i <= count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = i == 0 ? L.MonitorPrimary : L.MonitorN(i + 1),
                IsCheckable = true,
                IsChecked = _settings.MonitorIndex == i,
            };
            item.Click += (_, _) =>
            {
                _settings.MonitorIndex = index;
                _settings.Save();
                UpdatePosition();
            };
            MonitorMenu.Items.Add(item);
        }
        MonitorMenu.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
                // Fica bloqueado nesta posição (modo manual)
                _settings.X = Left;
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

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
