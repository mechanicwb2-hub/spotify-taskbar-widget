using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpotifyTaskbarWidget;

public record LyricLine(TimeSpan Time, string Text);

public class LrcLibResponse
{
    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; set; }
}

public class LyricsService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    
    private string _lastTitle = "";
    private string _lastArtist = "";
    private List<LyricLine>? _lastLyrics;
    
    static LyricsService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget/1.2.9 (https://github.com/mechanicwb2/spotify-taskbar-widget)");
    }

    public async Task<List<LyricLine>?> GetLyricsAsync(string title, string artist)
    {
        if (title == _lastTitle && artist == _lastArtist)
            return _lastLyrics;
            
        _lastTitle = title;
        _lastArtist = artist;
        _lastLyrics = null;
        
        try
        {
            string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) 
            {
                return new List<LyricLine> { new LyricLine(TimeSpan.Zero, "[Servidor indisponível. Tente novamente mais tarde.]") };
            }
            
            var json = await response.Content.ReadAsStringAsync();
            var lrc = JsonSerializer.Deserialize<LrcLibResponse>(json);
            if (string.IsNullOrWhiteSpace(lrc?.SyncedLyrics)) 
            {
                return new List<LyricLine> { new LyricLine(TimeSpan.Zero, "[Sem letras sincronizadas]") };
            }
            
            _lastLyrics = ParseLrc(lrc.SyncedLyrics);
            if (_lastLyrics.Count == 0)
            {
                return new List<LyricLine> { new LyricLine(TimeSpan.Zero, "[Erro ao processar as letras]") };
            }

            return _lastLyrics;
        }
        catch (Exception ex)
        {
            Diag.Once("lyrics-fetch", $"Error fetching lyrics: {ex.Message}");
            return new List<LyricLine> { new LyricLine(TimeSpan.Zero, "[Servidor indisponível. Tente novamente mais tarde.]") };
        }
    }
    
    private static List<LyricLine> ParseLrc(string lrcText)
    {
        var lines = new List<LyricLine>();
        var regex = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d+)?)\](.*)");
        
        foreach (var line in lrcText.Split('\n'))
        {
            var match = regex.Match(line.Trim());
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int min) && 
                    double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec))
                {
                    lines.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), match.Groups[3].Value.Trim()));
                }
            }
        }
        var processedLines = new List<LyricLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Text.Length > 55)
            {
                var words = line.Text.Split(' ');
                var currentPart = "";
                var parts = new List<string>();
                foreach(var w in words)
                {
                    if (currentPart.Length + w.Length + 1 > 55 && currentPart.Length > 0)
                    {
                        parts.Add(currentPart.Trim());
                        currentPart = w;
                    }
                    else
                    {
                        currentPart += (currentPart.Length > 0 ? " " : "") + w;
                    }
                }
                if (currentPart.Length > 0) parts.Add(currentPart.Trim());

                TimeSpan endTime = i + 1 < lines.Count ? lines[i+1].Time : line.Time + TimeSpan.FromSeconds(parts.Count * 3);
                TimeSpan totalDuration = endTime - line.Time;
                
                if (totalDuration.TotalSeconds <= 0) totalDuration = TimeSpan.FromSeconds(parts.Count * 2);

                TimeSpan chunkDuration = TimeSpan.FromSeconds(totalDuration.TotalSeconds / parts.Count);
                for (int j = 0; j < parts.Count; j++)
                {
                    processedLines.Add(new LyricLine(line.Time + TimeSpan.FromSeconds(chunkDuration.TotalSeconds * j), parts[j]));
                }
            }
            else
            {
                processedLines.Add(line);
            }
        }
        return processedLines;
    }
}
