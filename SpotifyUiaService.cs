using System.Diagnostics;
using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

public enum ShuffleMode { Unknown, Off, On, Smart }
public enum RepeatMode { Unknown, Off, Context, Track }

/// <summary>
/// Lê e controla o estado dos favoritos, modo aleatório, repetição e volume
/// através da árvore de acessibilidade da janela do Spotify (Chromium).
///
/// O Spotify recria os botões no DOM a cada mudança de faixa, por isso só os
/// CONTENTORES estáveis ficam em cache (grupo de controlos do leitor e grupo
/// do título/artista); os botões são procurados frescos dentro deles a cada
/// uso — subárvores pequenas, milissegundos. A reconstrução completa (cara)
/// só acontece quando os próprios contentores morrem.
///
/// Estado a partir dos nomes/aria (independente do idioma sempre que possível):
/// - favoritos: guardado ⇔ o nome refere "playlist";
/// - aleatório: nome descreve a próxima ação (Ativar/Desativar + "inteligente");
/// - repetição: aria-checked false/true/mixed = desligado/playlist/faixa.
/// </summary>
public sealed class SpotifyUiaService
{
    private static readonly string[] SmartTerms =
        { "inteligente", "smart", "intelligent", "slim", "inteligentny", "akıllı" };

    private static readonly string[] DisableTerms =
        { "desativar", "disable", "desactivar", "désactiver", "deaktivieren",
          "disattiva", "uitschakelen", "wyłącz", "stäng av", "slå fra", "kapat" };

    private static readonly Condition ButtonCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
    private static readonly Condition CheckBoxCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);
    private static readonly Condition GroupCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group);
    private static readonly Condition HyperlinkCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
    private static readonly Condition SliderCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider);

    private readonly object _lock = new();
    private AutomationElement? _controlsGroup;   // aleatório/anterior/play/seguinte + checkbox de repetição
    private AutomationElement? _trackInfoGroup;  // título/artista + botão de favoritos

    private RangeValuePattern? _volumePattern;
    private double _volMin;
    private double _volMax = 1;

    // ---------- Estado ----------

    public (bool? Liked, ShuffleMode Shuffle, RepeatMode Repeat) GetState()
    {
        lock (_lock)
        {
            try
            {
                EnsureGroups();

                bool? liked = null;
                var like = FindLikeButton();
                if (like != null)
                {
                    string name = like.Current.Name ?? "";
                    liked = name.Contains("playlist", StringComparison.OrdinalIgnoreCase);
                }

                var shuffleMode = ShuffleMode.Unknown;
                var shuffle = FindShuffleButton();
                if (shuffle != null)
                {
                    string name = (shuffle.Current.Name ?? "").ToLowerInvariant();
                    bool smart = SmartTerms.Any(name.Contains);
                    bool disable = DisableTerms.Any(name.StartsWith);
                    shuffleMode = smart ? (disable ? ShuffleMode.Smart : ShuffleMode.On) : ShuffleMode.Off;
                }

                var repeatMode = RepeatMode.Unknown;
                var repeat = FindRepeatCheckbox();
                if (repeat != null)
                {
                    var toggle = (TogglePattern)repeat.GetCurrentPattern(TogglePattern.Pattern);
                    repeatMode = toggle.Current.ToggleState switch
                    {
                        ToggleState.Off => RepeatMode.Off,
                        ToggleState.On => RepeatMode.Context,
                        ToggleState.Indeterminate => RepeatMode.Track,
                        _ => RepeatMode.Unknown,
                    };
                }

                return (liked, shuffleMode, repeatMode);
            }
            catch
            {
                Invalidate();
                return (null, ShuffleMode.Unknown, RepeatMode.Unknown);
            }
        }
    }

    // ---------- Ações ----------

    /// <summary>Adiciona aos favoritos e CONFIRMA que ficou guardado (o nome do
    /// botão passa a referir "playlist"). Sem confirmação devolve false, para o
    /// chamador usar o atalho de teclado como recurso. Não invoca quando já
    /// está guardado (nesse estado o botão do Spotify abre um menu).</summary>
    public bool AddToFavorites() => DoWithRetry(() =>
    {
        var like = FindLikeButton();
        if (like == null) return (bool?)null; // contentor obsoleto → repetir após rebuild

        string name = like.Current.Name ?? "";
        if (name.Contains("playlist", StringComparison.OrdinalIgnoreCase))
            return true; // já está nos favoritos

        ((InvokePattern)like.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(250);
            try
            {
                string after = FindLikeButton()?.Current.Name ?? "";
                if (after.Contains("playlist", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false; // não confirmado → o chamador tenta o atalho de teclado
    });

    /// <summary>Um clique no botão do Spotify: desligado → aleatório → inteligente → desligado.</summary>
    public bool CycleShuffle() => DoWithRetry(() =>
    {
        var shuffle = FindShuffleButton();
        if (shuffle == null) return (bool?)null;
        ((InvokePattern)shuffle.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return true;
    });

    /// <summary>Um clique no botão do Spotify: desligado → playlist → faixa → desligado.</summary>
    public bool CycleRepeat() => DoWithRetry(() =>
    {
        var repeat = FindRepeatCheckbox();
        if (repeat == null) return (bool?)null;
        ((TogglePattern)repeat.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        return true;
    });

    /// <summary>Executa a ação com os contentores garantidos; se os elementos
    /// estiverem obsoletos, reconstrói uma vez e tenta de novo. Repõe a janela
    /// em primeiro plano (os cliques do Chromium podem roubá-la).</summary>
    private bool DoWithRetry(Func<bool?> action)
    {
        lock (_lock)
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureGroups();
                        bool? result = action();
                        if (result is bool ok) return ok;
                    }
                    catch { }
                    Invalidate();
                }
                return false;
            }
            finally
            {
                RestoreForeground(fg);
            }
        }
    }

    // ---------- Volume ----------

    /// <summary>Volume atual do slider do próprio Spotify, 0..1.</summary>
    public double? GetVolume()
    {
        var pattern = _volumePattern;
        if (pattern != null)
        {
            try
            {
                return _volMax <= _volMin ? null : (pattern.Current.Value - _volMin) / (_volMax - _volMin);
            }
            catch { _volumePattern = null; }
        }

        lock (_lock)
        {
            try
            {
                EnsureGroups();
                pattern = _volumePattern;
                if (pattern == null || _volMax <= _volMin) return null;
                return (pattern.Current.Value - _volMin) / (_volMax - _volMin);
            }
            catch
            {
                Invalidate();
                return null;
            }
        }
    }

    /// <summary>Define o volume no slider do próprio Spotify (a UI dele atualiza), 0..1.
    /// Caminho rápido fora do lock: chamado repetidamente ao arrastar o slider.</summary>
    public bool SetVolume(double fraction)
    {
        var pattern = _volumePattern;
        if (pattern != null)
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                pattern.SetValue(_volMin + Math.Clamp(fraction, 0, 1) * (_volMax - _volMin));
                if (fg != IntPtr.Zero && Interop.GetForegroundWindow() != fg)
                    Interop.SetForegroundWindow(fg);
                return true;
            }
            catch
            {
                _volumePattern = null; // reconstruir abaixo
            }
        }

        lock (_lock)
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                EnsureGroups();
                pattern = _volumePattern;
                if (pattern == null || _volMax <= _volMin) return false;
                pattern.SetValue(_volMin + Math.Clamp(fraction, 0, 1) * (_volMax - _volMin));
                return true;
            }
            catch
            {
                Invalidate();
                return false;
            }
            finally
            {
                if (fg != IntPtr.Zero && Interop.GetForegroundWindow() != fg)
                    Interop.SetForegroundWindow(fg);
            }
        }
    }

    // ---------- Localização dos elementos ----------

    // Seleção pela ORDEM DO DOM (FindAll devolve em ordem do documento): imune
    // aos retângulos obsoletos/vazios de janelas minimizadas. No grupo do título,
    // o botão de favoritos é o último (capa → links → favoritos); nos controlos,
    // o aleatório é o primeiro (aleatório → anterior → play → seguinte).

    private AutomationElement? FindLikeButton()
    {
        var group = _trackInfoGroup;
        if (group == null) return null;
        var buttons = group.FindAll(TreeScope.Descendants, ButtonCond);
        return buttons.Count > 0 ? buttons[buttons.Count - 1] : null;
    }

    private AutomationElement? FindShuffleButton()
    {
        var group = _controlsGroup;
        if (group == null) return null;
        var buttons = group.FindAll(TreeScope.Children, ButtonCond);
        return buttons.Count > 0 ? buttons[0] : null;
    }

    private AutomationElement? FindRepeatCheckbox() =>
        _controlsGroup?.FindFirst(TreeScope.Children, CheckBoxCond);

    private void Invalidate()
    {
        _controlsGroup = null;
        _trackInfoGroup = null;
        _volumePattern = null;
    }

    private void EnsureGroups()
    {
        if (_controlsGroup != null && _trackInfoGroup != null)
        {
            try
            {
                _ = _controlsGroup.Current.ControlType;
                _ = _trackInfoGroup.Current.ControlType;
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

    /// <summary>Reconstrução completa. Parte das CHECKBOXES (raras na árvore) em
    /// vez de percorrer todos os grupos: a checkbox de repetição identifica o
    /// grupo de controlos (4 botões + 1 checkbox) e daí sai o resto da barra.</summary>
    private bool FindInWindow(AutomationElement root)
    {
        var checkboxes = root.FindAll(TreeScope.Descendants, CheckBoxCond);
        foreach (AutomationElement checkbox in checkboxes)
        {
            AutomationElement? controls;
            try { controls = TreeWalker.ControlViewWalker.GetParent(checkbox); }
            catch { continue; }
            if (controls == null) continue;

            var buttons = controls.FindAll(TreeScope.Children, ButtonCond);
            if (buttons.Count != 4) continue;

            var bar = TreeWalker.ControlViewWalker.GetParent(controls);
            if (bar == null) continue;

            // Grupo do título/artista: irmão com hyperlinks e pelo menos um botão
            AutomationElement? trackInfo = null;
            var siblings = bar.FindAll(TreeScope.Children, GroupCond);
            foreach (AutomationElement sibling in siblings)
            {
                if (sibling.FindFirst(TreeScope.Descendants, HyperlinkCond) == null) continue;
                if (sibling.FindFirst(TreeScope.Descendants, ButtonCond) == null) continue;
                trackInfo = sibling;
                break;
            }
            if (trackInfo == null) continue;

            // Volume: o slider mais à direita da barra (pré-aquecido para o caminho rápido)
            try
            {
                var sliders = bar.FindAll(TreeScope.Descendants, SliderCond);
                var volume = sliders.Cast<AutomationElement>().OrderByDescending(SafeLeft).FirstOrDefault();
                if (volume != null)
                {
                    var rv = (RangeValuePattern)volume.GetCurrentPattern(RangeValuePattern.Pattern);
                    _volMin = rv.Current.Minimum;
                    _volMax = rv.Current.Maximum;
                    _volumePattern = rv;
                }
            }
            catch { _volumePattern = null; }

            _controlsGroup = controls;
            _trackInfoGroup = trackInfo;
            return true;
        }

        return false;
    }

    private static void RestoreForeground(IntPtr before)
    {
        if (before == IntPtr.Zero) return;
        Thread.Sleep(80); // o roubo de foco do Chromium é assíncrono
        if (Interop.GetForegroundWindow() != before)
            Interop.SetForegroundWindow(before);
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
