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
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
        {
            // Uma tag fora do padrão (ex.: "v1.3.0-fix") cortaria TODA a gente
            // das atualizações em silêncio — deixar rasto para diagnóstico
            Diag.Once("update-tag", "Could not parse the latest release tag as a version: " + tag);
            return null;
        }
        if (latest <= CurrentVersion)
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

    private static bool _updating;

    /// <summary>Descarrega a nova versão e termina a app; um script substitui o
    /// exe e reinicia. Endurecido para NUNCA deixar o utilizador sem app:
    /// valida o download (assinatura MZ + tamanho — um proxy/portal cativo pode
    /// devolver 200 com HTML), troca via copy+move com retries (antivírus
    /// seguram ficheiros; o exe antigo nunca é truncado) e, se a troca falhar,
    /// reinicia o exe antigo intacto. Espera pelo PID com limite (os PIDs são
    /// reciclados). Script escrito na codepage OEM — o cmd não lê UTF-8 e
    /// perfis com acentos ("João") davam caminhos estropiados.</summary>
    public static async Task DownloadAndApplyAsync(string url)
    {
        if (_updating) return; // há um item de menu por janela — só um aplica
        _updating = true;
        try
        {
            string target = Environment.ProcessPath!;
            string temp = Path.Combine(Path.GetTempPath(), "SpotifyTaskbarWidget.update.exe");
            string staged = target + ".new";

            byte[] bytes;
            using (var http = NewClient())
                bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length < 1_000_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                Diag.Once("update-invalid",
                    $"Update download rejected: {bytes.Length} bytes, not a PE executable (proxy/captive portal?)");
                return;
            }
            await File.WriteAllBytesAsync(temp, bytes);

            string script = Path.Combine(Path.GetTempPath(), "SpotifyTaskbarWidget.update.cmd");
            int pid = Environment.ProcessId;
            string body =
                "@echo off\r\n" +
                "set tries=0\r\n" +
                ":wait\r\n" +
                "set /a tries+=1\r\n" +
                "if %tries% gtr 60 goto apply\r\n" +
                $"tasklist /fi \"PID eq {pid}\" /fo csv /nh 2>nul | find \"\"\"{pid}\"\"\" >nul\r\n" +
                "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                ":apply\r\n" +
                "set ctries=0\r\n" +
                ":copyloop\r\n" +
                "set /a ctries+=1\r\n" +
                "if %ctries% gtr 12 goto fail\r\n" +
                $"copy /y \"{temp}\" \"{staged}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto copyloop)\r\n" +
                $"move /y \"{staged}\" \"{target}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto copyloop)\r\n" +
                $"del \"{temp}\" >nul 2>&1\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del \"%~f0\"\r\n" +
                "exit /b\r\n" +
                ":fail\r\n" +
                $"del \"{staged}\" >nul 2>&1\r\n" +
                $"del \"{temp}\" >nul 2>&1\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del \"%~f0\"\r\n";

            System.Text.Encoding enc;
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                enc = System.Text.Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                enc = System.Text.Encoding.Default;
            }
            await File.WriteAllTextAsync(script, body, enc);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });

            App.IntentionalExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        finally
        {
            _updating = false;
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget");
        return http;
    }
}
