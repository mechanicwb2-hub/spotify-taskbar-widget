using System.IO;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

public class WidgetSettings
{
    /// <summary>Alinhar automaticamente a seguir ao botão de widgets/tempo.</summary>
    public bool AutoPosition { get; set; } = true;

    /// <summary>Posição horizontal manual (em unidades WPF/DIP), usada quando AutoPosition = false.</summary>
    public double X { get; set; } = 150;

    /// <summary>Escala do widget (0.8 = pequeno, 1.0 = normal, 1.15 = grande).</summary>
    public double Scale { get; set; } = 1.0;

    // Botões visíveis (em ecrãs pequenos, os menos importantes escondem-se sozinhos)
    public bool ShowPrev { get; set; } = true;
    public bool ShowNext { get; set; } = true;
    public bool ShowLike { get; set; } = true;
    public bool ShowShuffle { get; set; } = true;
    public bool ShowVolume { get; set; } = true;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarWidget");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new WidgetSettings();
        }
        catch
        {
            return new WidgetSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
