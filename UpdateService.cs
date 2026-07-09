using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Atualizações via GitHub Releases: compara a versão da release mais recente
/// com a versão da app; se houver mais recente, descarrega o .exe publicado
/// como asset e substitui o atual (via script que espera a app fechar).
///
/// Para publicar uma atualização:
///  1. subir <Version> no .csproj e fazer publish;
///  2. criar uma release no GitHub com tag "vX.Y.Z" e anexar o SpotifyTaskbarWidget.exe.
/// </summary>
internal static class UpdateService
{
    // Repositório GitHub "dono/repo" das releases. Com "CHANGEME", a verificação fica desativada.
    public const string GitHubRepo = "mechanicwb2-hub/spotify-taskbar-widget";

    public static bool IsConfigured => !GitHubRepo.Contains("CHANGEME");

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static async Task<(Version Version, string Url)?> CheckAsync()
    {
        if (!IsConfigured) return null;

        using var http = NewClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // repositório ainda sem releases — não é um erro
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= CurrentVersion)
            return null;

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            // Nome EXATO: a release também tem o instalador (…-Setup.exe) e
            // "primeiro .exe" podia apanhá-lo — substituir-nos-íamos pelo setup
            if (name.Equals("SpotifyTaskbarWidget.exe", StringComparison.OrdinalIgnoreCase))
                return (latest, asset.GetProperty("browser_download_url").GetString() ?? "");
        }
        return null;
    }

    /// <summary>Descarrega a nova versão e termina a app; um script substitui o exe e reinicia.</summary>
    public static async Task DownloadAndApplyAsync(string url)
    {
        string target = Environment.ProcessPath!;
        string temp = Path.Combine(Path.GetTempPath(), "SpotifyTaskbarWidget.update.exe");

        using (var http = NewClient())
            await File.WriteAllBytesAsync(temp, await http.GetByteArrayAsync(url));

        string script = Path.Combine(Path.GetTempPath(), "SpotifyTaskbarWidget.update.cmd");
        int pid = Environment.ProcessId;
        await File.WriteAllTextAsync(script,
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"copy /y \"{temp}\" \"{target}\" >nul\r\n" +
            $"del \"{temp}\" >nul 2>&1\r\n" +
            $"start \"\" \"{target}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        });

        App.IntentionalExit = true;
        System.Windows.Application.Current.Shutdown();
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget");
        return http;
    }
}
