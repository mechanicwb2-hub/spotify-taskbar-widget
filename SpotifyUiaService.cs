using System.Diagnostics;
using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

public enum ShuffleMode { Unknown, Off, On, Smart }

/// <summary>
/// Lê e controla o estado dos favoritos e do modo aleatório através da árvore
/// de acessibilidade da janela do Spotify (Chromium). Ao contrário do SMTC,
/// isto expõe o estado "já está nos favoritos" e o modo aleatório inteligente,
/// e os cliques (InvokePattern) não roubam o foco à janela ativa.
///
/// Localização dos botões é estrutural (independente do idioma):
/// - grupo com 4 botões + 1 checkbox = controlos do leitor; o 1.º botão é o aleatório;
/// - grupo irmão com hyperlinks (título/artista) tem o botão de favoritos mais à direita.
///
/// Estado a partir do nome do botão:
/// - favoritos: quando a faixa já está guardada, o nome refere "playlist"
///   ("Adicionar à playlist" / "Add to playlist"); quando não está, refere as
///   músicas favoritas ("Adicionar a Músicas de que gostas" / "Add to Liked Songs").
/// - aleatório: o nome descreve a próxima ação; se menciona o modo inteligente
///   e começa por "Desativar/Disable", o modo inteligente está ativo; se menciona
///   e começa por "Ativar/Enable", o aleatório normal está ativo; caso contrário, desligado.
/// </summary>
public sealed class SpotifyUiaService
{
    private static readonly string[] SmartTerms =
        { "inteligente", "smart", "intelligent", "slim", "inteligentny", "akıllı" };

    private static readonly string[] DisableTerms =
        { "desativar", "disable", "desactivar", "désactiver", "deaktivieren",
          "disattiva", "uitschakelen", "wyłącz", "stäng av", "slå fra", "kapat" };

    private readonly object _lock = new();
    private AutomationElement? _likeButton;
    private AutomationElement? _shuffleButton;
    private AutomationElement? _volumeSlider;

    public (bool? Liked, ShuffleMode Shuffle) GetState()
    {
        lock (_lock)
        {
            try
            {
                EnsureElements();

                bool? liked = null;
                if (_likeButton != null)
                {
                    string name = _likeButton.Current.Name ?? "";
                    liked = name.Contains("playlist", StringComparison.OrdinalIgnoreCase);
                }

                var mode = ShuffleMode.Unknown;
                if (_shuffleButton != null)
                {
                    string name = (_shuffleButton.Current.Name ?? "").ToLowerInvariant();
                    bool smart = SmartTerms.Any(name.Contains);
                    bool disable = DisableTerms.Any(name.StartsWith);
                    mode = smart ? (disable ? ShuffleMode.Smart : ShuffleMode.On) : ShuffleMode.Off;
                }

                return (liked, mode);
            }
            catch
            {
                Invalidate();
                return (null, ShuffleMode.Unknown);
            }
        }
    }

    /// <summary>Adiciona aos favoritos. Não invoca quando já está guardado
    /// (nesse estado o botão do Spotify abre um menu de playlists).</summary>
    public bool AddToFavorites()
    {
        lock (_lock)
        {
            try
            {
                EnsureElements();
                if (_likeButton == null) return false;
                string name = _likeButton.Current.Name ?? "";
                if (name.Contains("playlist", StringComparison.OrdinalIgnoreCase))
                    return true; // já está nos favoritos

                ((InvokePattern)_likeButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                return true;
            }
            catch
            {
                Invalidate();
                return false;
            }
        }
    }

    /// <summary>Um clique no botão do Spotify: desligado → aleatório → inteligente → desligado.</summary>
    public bool CycleShuffle()
    {
        lock (_lock)
        {
            try
            {
                EnsureElements();
                if (_shuffleButton == null) return false;
                ((InvokePattern)_shuffleButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                return true;
            }
            catch
            {
                Invalidate();
                return false;
            }
        }
    }

    /// <summary>Volume atual do slider do próprio Spotify, 0..1.</summary>
    public double? GetVolume()
    {
        lock (_lock)
        {
            try
            {
                EnsureElements();
                if (_volumeSlider == null) return null;
                var rv = (RangeValuePattern)_volumeSlider.GetCurrentPattern(RangeValuePattern.Pattern);
                double min = rv.Current.Minimum, max = rv.Current.Maximum;
                if (max <= min) return null;
                return (rv.Current.Value - min) / (max - min);
            }
            catch
            {
                Invalidate();
                return null;
            }
        }
    }

    /// <summary>Define o volume no slider do próprio Spotify (a UI dele atualiza), 0..1.</summary>
    public bool SetVolume(double fraction)
    {
        lock (_lock)
        {
            try
            {
                EnsureElements();
                if (_volumeSlider == null) return false;
                var rv = (RangeValuePattern)_volumeSlider.GetCurrentPattern(RangeValuePattern.Pattern);
                double min = rv.Current.Minimum, max = rv.Current.Maximum;
                if (max <= min) return false;
                rv.SetValue(min + Math.Clamp(fraction, 0, 1) * (max - min));
                return true;
            }
            catch
            {
                Invalidate();
                return false;
            }
        }
    }

    private void Invalidate()
    {
        _likeButton = null;
        _shuffleButton = null;
        _volumeSlider = null;
    }

    private void EnsureElements()
    {
        if (_likeButton != null && _shuffleButton != null && _volumeSlider != null)
        {
            try
            {
                _ = _likeButton.Current.Name;
                _ = _shuffleButton.Current.Name;
                _ = _volumeSlider.Current.Name;
                return;
            }
            catch (ElementNotAvailableException)
            {
                Invalidate();
            }
        }

        foreach (var proc in Process.GetProcessesByName("Spotify"))
        {
            if (proc.MainWindowHandle == IntPtr.Zero) continue;
            try
            {
                var root = AutomationElement.FromHandle(proc.MainWindowHandle);
                if (FindInWindow(root)) return;
            }
            catch { }
        }
    }

    private bool FindInWindow(AutomationElement root)
    {
        var groups = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));

        foreach (AutomationElement group in groups)
        {
            var buttons = group.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (buttons.Count != 4) continue;

            var checkboxes = group.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            if (checkboxes.Count < 1) continue;

            // Controlos do leitor: aleatório, anterior, play/pausa, seguinte (+ checkbox de repetição)
            var shuffle = buttons.Cast<AutomationElement>().OrderBy(SafeLeft).First();

            // Grupo irmão com os links do título/artista → botão de favoritos mais à direita
            AutomationElement? like = null;
            var parent = TreeWalker.ControlViewWalker.GetParent(group);
            if (parent != null)
            {
                var siblings = parent.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
                foreach (AutomationElement sibling in siblings)
                {
                    var links = sibling.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
                    if (links.Count == 0) continue;

                    var sibButtons = sibling.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    like = sibButtons.Cast<AutomationElement>().OrderByDescending(SafeLeft).FirstOrDefault();
                    if (like != null) break;
                }
            }

            if (like == null) continue;

            // Volume: o slider mais à direita da barra de reprodução
            AutomationElement? volume = null;
            if (parent != null)
            {
                var sliders = parent.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider));
                volume = sliders.Cast<AutomationElement>().OrderByDescending(SafeLeft).FirstOrDefault();
            }

            _shuffleButton = shuffle;
            _likeButton = like;
            _volumeSlider = volume;
            return true;
        }

        return false;
    }

    private static double SafeLeft(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            return r.IsEmpty ? double.MaxValue : r.Left;
        }
        catch
        {
            return double.MaxValue;
        }
    }
}
