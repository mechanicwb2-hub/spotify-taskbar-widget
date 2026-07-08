using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyTaskbarWidget;

public sealed record TrackInfo(string Title, string Artist, bool IsPlaying, bool? IsShuffle);

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

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => PickSession();
        PickSession();
    }

    private void PickSession()
    {
        if (_manager == null) return;

        GlobalSystemMediaTransportControlsSession? chosen = null;
        try
        {
            var sessions = _manager.GetSessions();
            chosen = sessions.FirstOrDefault(s =>
                         (s.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase))
                     ?? _manager.GetCurrentSession();
        }
        catch { }

        var old = _session;
        if (old != null)
        {
            try
            {
                old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
        }

        _session = chosen;
        if (chosen != null)
        {
            chosen.MediaPropertiesChanged += OnMediaPropertiesChanged;
            chosen.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }

        Changed?.Invoke();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Changed?.Invoke();

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
            return new TrackInfo(props?.Title ?? "", props?.Artist ?? "", playing, pi?.IsShuffleActive);
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
