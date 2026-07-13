using System.IO;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

public class WidgetSettings
{
    /// <summary>Alinhar automaticamente a seguir ao botão de widgets/tempo.</summary>
    public bool AutoPosition { get; set; } = true;

    /// <summary>Posição horizontal manual (px físicos de ecrã), usada quando AutoPosition = false.</summary>
    public double X { get; set; } = 150;

    /// <summary>Escala do widget (0.8 = pequeno, 1.0 = normal, 1.1 = grande).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Compatibilidade com versões antigas — hoje vale <see cref="Monitors"/>.</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Barras de tarefas com widget (0 = principal, 1+ = secundárias).
    /// Uma janela por entrada; lista vazia = ficheiro antigo, migra de MonitorIndex.</summary>
    public List<int> Monitors { get; set; } = new();

    /// <summary>Com o Spotify fechado: true mostra um botão "Abrir Spotify"; false esconde o widget.</summary>
    public bool ShowLauncher { get; set; } = false;

    /// <summary>Barra de progresso da música no fundo do widget (clique para saltar).</summary>
    public bool ShowProgress { get; set; } = true;

    // Botões visíveis (em ecrãs pequenos, os menos importantes escondem-se sozinhos)
    public bool ShowPrev { get; set; } = true;
    public bool ShowNext { get; set; } = true;
    public bool ShowLike { get; set; } = true;
    public bool ShowShuffle { get; set; } = true;
    public bool ShowRepeat { get; set; } = true;
    public bool ShowVolume { get; set; } = true;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarWidget");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>Instância única partilhada por todas as janelas do widget.</summary>
    public static WidgetSettings Shared => _shared ??= Load();
    private static WidgetSettings? _shared;

    /// <summary>Disparado depois de cada Save — as outras janelas re-aplicam a UI.</summary>
    public static event Action? Changed;

    public static WidgetSettings Load()
    {
        WidgetSettings s;
        try
        {
            s = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new WidgetSettings();
        }
        catch
        {
            s = new WidgetSettings();
        }
        if (s.Monitors.Count == 0)
            s.Monitors.Add(s.MonitorIndex); // migração do formato antigo
        s.Monitors = s.Monitors.Distinct().OrderBy(i => i).ToList();
        return s;
    }

    public void Save()
    {
        MonitorIndex = Monitors.Count > 0 ? Monitors[0] : 0; // downgrade-safe
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
        Changed?.Invoke();
    }
}
