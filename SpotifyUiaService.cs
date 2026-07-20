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

    // Favoritos: quando a faixa está guardada, o botão passa a referir a
    // "playlist" (o menu "adicionar a playlist"). Muitos idiomas mantêm o
    // anglicismo (PT/ES/IT/DE/FR) — e "playlista" (PL) contém "playlist" —,
    // mas os nórdicos e o neerlandês traduzem. Casado com Contains.
    private static readonly string[] PlaylistTerms =
        { "playlist", "spellista", "spilleliste", "soittolista", "afspeellijst" };

    private static bool IsLikedName(string name) =>
        PlaylistTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Regista UMA vez cada nome distinto dos botões de favoritos e
    /// aleatório, mas só em Windows não-inglês — a população afetada pelo #16.
    /// Assim recolhemos os nomes reais que o Spotify expõe nesses idiomas e
    /// expandimos as listas de termos com dados, em vez de adivinhar. Sem ruído
    /// para os utilizadores ingleses (a maioria, e onde a deteção já acerta).</summary>
    private static void LogI18nNames(string likeName, string shuffleName)
    {
        if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("en", StringComparison.OrdinalIgnoreCase))
            return;
        if (likeName.Length > 0)
            Diag.Once("i18n-like:" + likeName, "[i18n] like button name: " + likeName);
        if (shuffleName.Length > 0)
            Diag.Once("i18n-shuffle:" + shuffleName, "[i18n] shuffle button name: " + shuffleName);
    }

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

    // Só o grupo de CONTROLOS fica em cache (estável entre faixas). O grupo do
    // título é recriado a cada faixa — o Chromium mantém o nó antigo legível
    // ("zombie") durante segundos, pelo que tem de ser derivado fresco a cada
    // leitura, e validado contra o título atual (o nome do grupo contém-no).
    private readonly object _lock = new();
    private AutomationElement? _controlsGroup;   // aleatório/anterior/play/seguinte + checkbox de repetição

    private RangeValuePattern? _volumePattern;
    private double _volMin;
    private double _volMax = 1;

    // ---------- Estado ----------

    /// <summary>Lê o estado. <paramref name="expectedTitle"/> (vindo do SMTC, que
    /// atualiza no instante) valida que o grupo do título já é o da faixa atual;
    /// Fresh=false indica leitura possivelmente obsoleta (o chamador re-tenta).</summary>
    public (bool? Liked, ShuffleMode Shuffle, RepeatMode Repeat, bool Fresh) GetState(string? expectedTitle = null)
    {
        // TryEnter com timeout em vez de lock: uma chamada UIA pendurada num
        // Spotify a morrer segurava o lock para SEMPRE (o WaitAsync do chamador
        // abandona a espera mas não solta o lock) e todos os botões morriam
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return (null, ShuffleMode.Unknown, RepeatMode.Unknown, false);
        try
        {
            try
            {
                EnsureGroups();

                bool fresh = true;
                bool? liked = null;
                var trackInfo = FindTrackInfoGroup();
                if (expectedTitle is { Length: > 0 } && trackInfo != null)
                {
                    string groupName = trackInfo.Current.Name ?? "";
                    fresh = groupName.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
                }
                var like = FindLikeButton(trackInfo);
                string likeName = like?.Current.Name ?? "";
                if (like != null)
                    liked = IsLikedName(likeName);

                var shuffleMode = ShuffleMode.Unknown;
                var shuffle = FindShuffleButton();
                string shuffleName = shuffle?.Current.Name ?? "";
                if (shuffle != null)
                {
                    string n = shuffleName.ToLowerInvariant();
                    bool smart = SmartTerms.Any(n.Contains);
                    // Contains (não StartsWith): idiomas com o verbo no fim
                    // ("… deaktivieren", "… kapat") não começam pelo termo
                    bool disable = DisableTerms.Any(n.Contains);
                    shuffleMode = smart ? (disable ? ShuffleMode.Smart : ShuffleMode.On) : ShuffleMode.Off;
                }

                // Recolher os nomes reais dos botões nos idiomas afetados (#16),
                // para expandir as listas de termos com dados em vez de adivinhar
                LogI18nNames(likeName, shuffleName);

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

                return (liked, shuffleMode, repeatMode, fresh);
            }
            catch
            {
                Invalidate();
                return (null, ShuffleMode.Unknown, RepeatMode.Unknown, false);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Ações ----------

    /// <summary>Adiciona aos favoritos e CONFIRMA que ficou guardado (o nome do
    /// botão passa a referir "playlist"). Sem confirmação devolve false, para o
    /// chamador usar o atalho de teclado como recurso. Não invoca quando já
    /// está guardado (nesse estado o botão do Spotify abre um menu).</summary>
    public bool AddToFavorites() => DoWithRetry(() =>
    {
        var like = FindLikeButton(FindTrackInfoGroup());
        if (like == null) return (bool?)null; // contentor obsoleto → repetir após rebuild

        string name = like.Current.Name ?? "";
        if (IsLikedName(name))
            return true; // já está nos favoritos

        // O botão + atual do Spotify tem aria-haspopup, por isso o Chromium só
        // expõe ExpandCollapse — e Expand() dispara o clique (kDoDefault), que
        // numa faixa por guardar ADICIONA aos favoritos (verificado ao vivo).
        // Toggle/Invoke ficam para versões antigas/futuras do Spotify.
        if (like.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
            ((ExpandCollapsePattern)expand).Expand();
        else if (like.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
            ((TogglePattern)toggle).Toggle();
        else if (like.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
            ((InvokePattern)invoke).Invoke();
        else
            return false; // sem padrão utilizável → clique real como recurso

        // Não esperar pela confirmação aqui: o Spotify demora vários segundos a
        // atualizar o texto do botão (mesmo com a ação já aplicada), e esperar
        // segurava o lock. O chamador reconcilia o estado mais tarde.
        return true;
    });

    /// <summary>Recurso quando os padrões de acessibilidade falham: restaura a
    /// janela do Spotify por instantes e faz um clique de rato real no botão de
    /// favoritos, confirmando o resultado no fim.</summary>
    public bool AddToFavoritesByClick()
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            try
            {
                EnsureGroups();
                var like = FindLikeButton(FindTrackInfoGroup());
                if (like == null) return false;
                if (IsLikedName(like.Current.Name ?? ""))
                    return true; // já está nos favoritos

                var proc = Process.GetProcessesByName("Spotify")
                    .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (proc == null) return false;

                IntPtr wnd = proc.MainWindowHandle;
                IntPtr prevFg = Interop.GetForegroundWindow();
                bool wasMinimized = Interop.IsIconic(wnd);
                Interop.GetCursorPos(out var prevCursor);
                try
                {
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_RESTORE);
                    Interop.SetForegroundWindow(wnd);
                    Thread.Sleep(450);

                    like = FindLikeButton(FindTrackInfoGroup()); // retângulos frescos com a janela visível
                    if (like == null) return false;
                    var r = like.Current.BoundingRectangle;
                    if (r.IsEmpty) return false;

                    Interop.SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
                    Thread.Sleep(60);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(350);

                    string after = FindLikeButton(FindTrackInfoGroup())?.Current.Name ?? "";
                    return IsLikedName(after);
                }
                finally
                {
                    Interop.SetCursorPos(prevCursor.X, prevCursor.Y);
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_MINIMIZE);
                    if (prevFg != IntPtr.Zero)
                        Interop.SetForegroundWindow(prevFg);
                }
            }
            catch
            {
                Invalidate();
                return false;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

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
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
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
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Volume ----------

    /// <summary>Volume atual do slider do próprio Spotify, 0..1.</summary>
    public double? GetVolume()
    {
        // Snapshot local do trio (padrão+min+max): um rebuild concorrente podia
        // emparelhar o padrão novo com limites antigos e dar contas erradas
        var pattern = _volumePattern;
        double min = _volMin, max = _volMax;
        if (pattern != null && max > min)
        {
            try
            {
                return (pattern.Current.Value - min) / (max - min);
            }
            catch { _volumePattern = null; }
        }

        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return null;
        try
        {
            EnsureGroups();
            if (_volumePattern == null)
            {
                // O Spotify pode recriar SÓ o slider (mudança de dispositivo de
                // saída) com o resto dos controlos vivo — sem isto, o rebuild
                // preguiçoso nunca corria e o volume ficava morto para sempre
                Invalidate();
                EnsureGroups();
            }
            pattern = _volumePattern;
            if (pattern == null || _volMax <= _volMin) return null;
            return (pattern.Current.Value - _volMin) / (_volMax - _volMin);
        }
        catch
        {
            Invalidate();
            return null;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Define o volume no slider do próprio Spotify (a UI dele atualiza), 0..1.
    /// Caminho rápido fora do lock: chamado repetidamente ao arrastar o slider.</summary>
    public bool SetVolume(double fraction)
    {
        var pattern = _volumePattern;
        double min = _volMin, max = _volMax;
        if (pattern != null && max > min)
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                pattern.SetValue(min + Math.Clamp(fraction, 0, 1) * (max - min));
                if (fg != IntPtr.Zero && Interop.GetForegroundWindow() != fg)
                    Interop.SetForegroundWindow(fg);
                return true;
            }
            catch
            {
                _volumePattern = null; // reconstruir abaixo
            }
        }

        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                EnsureGroups();
                if (_volumePattern == null)
                {
                    Invalidate(); // slider recriado sozinho — forçar rebuild completo
                    EnsureGroups();
                }
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
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Localização dos elementos ----------

    // Seleção pela ORDEM DO DOM (FindAll devolve em ordem do documento): imune
    // aos retângulos obsoletos/vazios de janelas minimizadas. No grupo do título,
    // o botão de favoritos é o último (capa → links → favoritos); nos controlos,
    // o aleatório é o primeiro (aleatório → anterior → play → seguinte).

    /// <summary>Grupo do título/artista, derivado FRESCO a cada leitura a partir
    /// do grupo de controlos (o Spotify recria-o a cada faixa).</summary>
    private AutomationElement? FindTrackInfoGroup()
    {
        var controls = _controlsGroup;
        if (controls == null) return null;
        var bar = TreeWalker.ControlViewWalker.GetParent(controls);
        if (bar == null) return null;
        var siblings = bar.FindAll(TreeScope.Children, GroupCond);
        foreach (AutomationElement sibling in siblings)
        {
            if (sibling.FindFirst(TreeScope.Descendants, HyperlinkCond) == null) continue;
            if (sibling.FindFirst(TreeScope.Descendants, ButtonCond) == null) continue;
            return sibling;
        }
        return null;
    }

    private static AutomationElement? FindLikeButton(AutomationElement? trackInfo)
    {
        if (trackInfo == null) return null;
        var buttons = trackInfo.FindAll(TreeScope.Descendants, ButtonCond);
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
        _volumePattern = null;
    }

    private void EnsureGroups()
    {
        if (_controlsGroup != null)
        {
            try
            {
                _ = _controlsGroup.Current.ControlType;
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
