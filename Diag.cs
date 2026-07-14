using System.IO;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Diagnóstico mínimo para falhas silenciosas: escreve UMA vez por causa no
/// errors.log (o mesmo do handler global), para os utilizadores afetados
/// poderem colar o conteúdo num report. Em inglês — é o que viaja no Reddit.
/// </summary>
internal static class Diag
{
    private static readonly HashSet<string> Seen = new();
    private static string? _lastWrite;
    private static DateTime _lastWriteAt;

    public static void Once(string key, string message)
    {
        lock (Seen)
        {
            if (!Seen.Add(key)) return;
        }
        Log(message);
    }

    /// <summary>Escrita partilhada no errors.log, com teto de tamanho (uma
    /// exceção em loop enchia o disco) e dedup de repetições consecutivas.</summary>
    public static void Log(string message)
    {
        try
        {
            lock (Seen)
            {
                // Um erro num timer repete-se várias vezes por segundo — não
                // escrever a mesma mensagem outra vez dentro de 30s
                if (message == _lastWrite && DateTime.UtcNow - _lastWriteAt < TimeSpan.FromSeconds(30))
                    return;
                _lastWrite = message;
                _lastWriteAt = DateTime.UtcNow;

                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpotifyTaskbarWidget");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "errors.log");
                // Teto: recomeçar quando passa de 1 MB (o valor está nos casos
                // recentes; histórico antigo não ajuda em reports)
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                    File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch { }
    }
}
