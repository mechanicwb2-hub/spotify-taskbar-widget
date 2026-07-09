using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyTaskbarWidget;

public sealed record TrackInfo(string Title, string Artist, bool IsPlaying, bool? IsShuffle,
    TimeSpan Position, TimeSpan Duration);

/// <summary>
/// Lê a música atual através da API de media do Windows (SMTC).
/// O Spotify desktop publica aqui a faixa em reprodução — não é preciso
/// login nem API do Spotify. Prefere a sessão do Spotify; se não existir,
/// usa a sessão de media ativa.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Disparado (em thread de background) quando a faixa ou o estado mudam.</summary>
    public event Action? Changed;

    /// <summary>Disparado só quando a posição/duração mudam — acontece a cada poucos
    /// segundos, por isso o tratamento tem de ser leve.</summary>
    public event Action? TimelineChanged;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => PickSession();
        PickSession();
    }

    private void PickSession()
    {
        if (_manager == null) return;

        // Apenas sessões do Spotify — sem fallback para outras apps (Chrome, etc.),
        // senão o widget mostra media de outros programas quando o Spotify fecha.
        GlobalSystemMediaTransportControlsSession? chosen = null;
        try
        {
            var sessions = _manager.GetSessions();
            chosen = sessions.FirstOrDefault(s =>
                (s.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        var old = _session;
        if (old != null)
        {
            try
            {
                old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                old.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            catch { }
        }

        _session = chosen;
        if (chosen != null)
        {
            chosen.MediaPropertiesChanged += OnMediaPropertiesChanged;
            chosen.PlaybackInfoChanged += OnPlaybackInfoChanged;
            chosen.TimelinePropertiesChanged += OnTimelineChanged;
        }

        Changed?.Invoke();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        TimelineChanged?.Invoke();

    /// <summary>Leitura rápida da timeline (sem propriedades da faixa nem capa).</summary>
    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying)? GetTimeline()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var tl = s.GetTimelineProperties();
            var pi = s.GetPlaybackInfo();
            return (tl?.Position ?? TimeSpan.Zero,
                    tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero,
                    pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var pi = s.GetPlaybackInfo();
            bool playing = pi?.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var tl = s.GetTimelineProperties();
            TimeSpan position = tl?.Position ?? TimeSpan.Zero;
            TimeSpan duration = tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero;

            return new TrackInfo(props?.Title ?? "", props?.Artist ?? "", playing, pi?.IsShuffleActive,
                position, duration);
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetThumbnailAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props?.Thumbnail == null) return null;

            using var stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size == 0) return null;

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    public async Task CycleRepeatAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var current = s.GetPlaybackInfo()?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
            var next = current switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            };
            await s.TryChangeAutoRepeatModeAsync(next);
        }
        catch { }
    }

    public async Task ToggleShuffleAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            bool current = s.GetPlaybackInfo()?.IsShuffleActive ?? false;
            await s.TryChangeShuffleActiveAsync(!current);
        }
        catch { }
    }
}
