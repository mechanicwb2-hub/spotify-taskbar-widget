using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

    // Cores do Spotify
    private static readonly Brush SpotifyGreen = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
    private static readonly Brush Subdued = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
    private static readonly Brush DimWhite = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

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
    private bool _spotifyRunning = true;

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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AutoStartMenu.IsChecked = IsAutoStartEnabled();
        LauncherMenu.IsChecked = _settings.ShowLauncher;
        BtnLikeMenu.IsChecked = _settings.ShowLike;
        BtnShuffleMenu.IsChecked = _settings.ShowShuffle;
        BtnPrevMenu.IsChecked = _settings.ShowPrev;
        BtnNextMenu.IsChecked = _settings.ShowNext;
        BtnVolumeMenu.IsChecked = _settings.ShowVolume;
        ApplyScale();
        UpdateSizeChecks();

        UpdatePosition();
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _positionTimer.Start();

        await _media.InitializeAsync();
        _media.Changed += () =>
        {
            _artDirty = true;
            Dispatcher.InvokeAsync(() => _ = RefreshTrackAsync());
        };

        _trackTimer.Tick += (_, _) => _ = RefreshTrackAsync();
        _trackTimer.Start();

        _ = CheckUpdatesQuietlyAsync();

        await RefreshTrackAsync();
    }

    // ---------- Posicionamento na barra de tarefas ----------

    private void UpdatePosition()
    {
        IntPtr tray = Interop.FindWindow("Shell_TrayWnd", null);
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

        double left, rightLimit;
        if (_settings.AutoPosition)
        {
            // Alinhar a seguir ao botão de widgets/tempo; sem ele, à borda esquerda.
            // Limite direito: nunca invadir os ícones centrados (botão Iniciar).
            left = widgetsRightPx.HasValue
                ? m.Transform(new Point(widgetsRightPx.Value, 0)).X + 6
                : tl.X + 12;
            rightLimit = startLeftPx.HasValue
                ? m.Transform(new Point(startLeftPx.Value, 0)).X - 6
                : br.X - 4;
        }
        else
        {
            left = Math.Max(tl.X + 4, Math.Min(_settings.X, br.X - ActualWidth - 4));
            rightLimit = br.X - 4;
        }

        if (_spotifyRunning)
            ApplyResponsiveLayout(rightLimit - left);

        if (!_dragging)
        {
            if (Math.Abs(Top - top) > 0.5) Top = top;
            if (Math.Abs(Left - left) > 0.5) Left = left;
        }

        // Esconder quando há uma app em ecrã inteiro (jogos, vídeos), ou quando
        // o Spotify está fechado (a menos que o botão de abrir esteja ativado)
        bool hide = Interop.IsForegroundFullscreen(_hwnd, tray)
                    || (!_spotifyRunning && !_settings.ShowLauncher);
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

        bool prev = false, next = false, like = false, shuffle = false, volume = false;
        Take(ref prev, _settings.ShowPrev);
        Take(ref next, _settings.ShowNext);
        Take(ref like, _settings.ShowLike);
        Take(ref shuffle, _settings.ShowShuffle);
        Take(ref volume, _settings.ShowVolume);

        double text = Math.Max(MinTextWidth, Math.Min(MaxTextWidth, MinTextWidth + (avail - used)));

        SetVis(PrevButton, prev);
        SetVis(NextButton, next);
        SetVis(LikeButton, like);
        SetVis(ShuffleButton, shuffle);
        SetVis(VolumeButton, volume);
        if (Math.Abs(TextStack.Width - text) > 1)
            TextStack.Width = text;

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
            _spotifyRunning = Process.GetProcessesByName("Spotify").Length > 0;

            // Spotify fechado: ou o widget está escondido (UpdatePosition trata),
            // ou mostra apenas o botão de abrir
            var launcherWanted = !_spotifyRunning ? Visibility.Visible : Visibility.Collapsed;
            var contentWanted = !_spotifyRunning ? Visibility.Collapsed : Visibility.Visible;
            if (LauncherPanel.Visibility != launcherWanted)
            {
                LauncherPanel.Visibility = launcherWanted;
                ContentPanel.Visibility = contentWanted;
            }
            if (!_spotifyRunning)
            {
                _lastTrackKey = "";
                _liked = null;
                VolumePopup.IsOpen = false;
                return;
            }

            TrackInfo? track = await _media.GetTrackAsync();

            if (track == null || string.IsNullOrWhiteSpace(track.Title))
            {
                TitleText.Text = "Spotify";
                ArtistText.Text = "nada a tocar";
                PlayPauseIcon.Data = PlayGeo;
                ShuffleIcon.Fill = DimWhite;
                ShuffleDot.Visibility = Visibility.Collapsed;
                ShuffleSmartStar.Visibility = Visibility.Collapsed;
                LikeIcon.Data = AddCircleGeo;
                LikeIcon.Fill = DimWhite;
                _liked = null;
                ArtImage.Visibility = Visibility.Collapsed;
                ArtPlaceholder.Visibility = Visibility.Visible;
                _lastTrackKey = "";
                return;
            }

            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist;
            PlayPauseIcon.Data = track.IsPlaying ? PauseGeo : PlayGeo;

            // Estado real (favoritos + modo aleatório) da árvore de acessibilidade
            // do Spotify; o SMTC serve de rede de segurança para o on/off.
            var (liked, uiaMode) = await Task.Run(() => _uia.GetState());
            _liked = liked;

            LikeIcon.Data = liked == true ? CheckCircleGeo : AddCircleGeo;
            LikeIcon.Fill = liked == true ? SpotifyGreen : (liked == false ? Subdued : DimWhite);
            LikeButton.ToolTip = liked == true ? "Já está nos favoritos" : "Adicionar aos favoritos do Spotify";

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
                ShuffleMode.Smart => "Modo aleatório inteligente ativo",
                ShuffleMode.On => "Modo aleatório ativo",
                _ => "Modo aleatório",
            };

            string key = track.Title + "|" + track.Artist;
            if (key != _lastTrackKey || _artDirty)
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

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        // Clique no botão do próprio Spotify: cicla desligado → aleatório → inteligente
        bool ok = await Task.Run(() => _uia.CycleShuffle());
        if (!ok)
            await _media.ToggleShuffleAsync(); // sem janela do Spotify: só liga/desliga
        await Task.Delay(400);
        await RefreshTrackAsync();
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        if (_liked == true) return; // já está nos favoritos

        bool ok = await Task.Run(() => _uia.AddToFavorites());
        if (!ok)
            await Task.Run(SpotifyActions.LikeCurrentTrack); // recurso: atalho de teclado
        await Task.Delay(400);
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

    private void VolumePopup_Closed(object sender, EventArgs e) => RestoreForeground();

    // ---------- Preservação do foco ----------
    // O popup do volume e o menu de contexto ativam janelas próprias do WPF;
    // quando fecham, o foco pode ficar órfão e atalhos globais (PrintScreen,
    // Win+Shift+S) deixam de responder até se clicar noutra janela.

    private IntPtr _fgBeforeUi;

    private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e) => CaptureForeground();

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
        SizeLarge.IsChecked = Math.Abs(_settings.Scale - 1.15) < 0.01;
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
            MessageBox.Show(
                $"Versão atual: v{UpdateService.CurrentVersion}\n\nAs atualizações automáticas ainda não estão configuradas (falta definir o repositório GitHub em UpdateService.cs).",
                "Spotify Taskbar Widget");
            return;
        }

        UpdateMenu.IsEnabled = false;
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update == null)
            {
                MessageBox.Show($"Estás na versão mais recente (v{UpdateService.CurrentVersion}).",
                    "Spotify Taskbar Widget");
                return;
            }

            var answer = MessageBox.Show(
                $"Nova versão v{update.Value.Version} disponível (atual: v{UpdateService.CurrentVersion}).\n\nAtualizar agora? O widget reinicia sozinho.",
                "Spotify Taskbar Widget", MessageBoxButton.YesNo);
            if (answer == MessageBoxResult.Yes)
                await UpdateService.DownloadAndApplyAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível verificar atualizações: " + ex.Message,
                "Spotify Taskbar Widget");
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
                    UpdateMenu.Header = $"⬤ Atualizar para v{update.Value.Version}");
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
