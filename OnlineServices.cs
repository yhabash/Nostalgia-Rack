using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AmpPlayer;

/// <summary>One shared web client for everything online. It follows redirects and gives up after 20 seconds.</summary>
public static class Web
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AmpPlayer/0.6 (hobby music player)");
        return client;
    }

    /// <summary>Turns web errors into a short message a person can understand.</summary>
    public static string Explain(Exception ex) => ex switch
    {
        TaskCanceledException => "the server took too long to answer",
        HttpRequestException { StatusCode: { } code } => $"the server said {(int)code} {code}",
        HttpRequestException h when h.InnerException is System.Security.Authentication.AuthenticationException
            => "the site's security certificate isn't valid",
        HttpRequestException => "couldn't reach the server (check your internet connection)",
        _ => ex.Message
    };
}

// =====================================================================================
// Internet radio: the free Radio Browser directory (radio-browser.info)
// =====================================================================================

public static class RadioBrowser
{
    // Radio Browser runs several mirror servers; if one is down we try the next.
    private static readonly string[] Servers =
    {
        "https://de1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
        "https://all.api.radio-browser.info"
    };

    /// <summary>Stations whose name matches the text (or the most popular ones, for empty text).</summary>
    public static async Task<List<PlaylistItem>> SearchAsync(string text)
    {
        string path = string.IsNullOrWhiteSpace(text)
            ? "/json/stations/topclick/100"
            : $"/json/stations/search?name={Uri.EscapeDataString(text.Trim())}&limit=150&hidebroken=true&order=clickcount&reverse=true";

        Exception? last = null;
        foreach (var server in Servers)
        {
            try
            {
                string json = await Web.Client.GetStringAsync(server + path);
                return Parse(json);
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException($"Radio Browser didn't answer: {Web.Explain(last!)}");
    }

    private static List<PlaylistItem> Parse(string json)
    {
        var result = new List<PlaylistItem>();
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            string url = Str(s, "url_resolved");
            if (url.Length == 0) url = Str(s, "url");
            if (url.Length == 0) continue;

            string codec = Str(s, "codec").ToUpperInvariant();
            int bitrate = s.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            string country = Str(s, "country");
            string tags = string.Join(", ", Str(s, "tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));

            result.Add(new PlaylistItem
            {
                Path = url,
                Title = Str(s, "name").Trim(),
                Artist = country,
                Album = tags,
                Kind = TrackKind.Radio,
                Format = bitrate > 0 ? $"{codec} {bitrate}" : codec
            });
        }
        return result;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

// =====================================================================================
// .pls and .m3u radio playlists: find the real stream address inside
// =====================================================================================

public static class StreamPlaylists
{
    public static bool LooksLikePlaylist(string url)
    {
        string path = url.Split('?')[0];
        return path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads a .pls / .m3u file and returns the first stream address in it.</summary>
    public static async Task<string?> ResolveAsync(string url)
    {
        string text = await Web.Client.GetStringAsync(url);
        return ParseFirstUrl(text);
    }

    public static string? ParseFirstUrl(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            // .pls: "File1=http://…"
            int eq = line.IndexOf('=');
            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && eq > 0) line = line[(eq + 1)..].Trim();
            // .m3u: plain lines; "#…" lines are comments
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }
}

// =====================================================================================
// Podcasts: RSS feeds (with the Apple iTunes and Podcasting 2.0 extras) and Podcast Index search
// =====================================================================================

public sealed record PodcastFeed(string FeedUrl, string Title, string Author, string ImageUrl, List<PlaylistItem> Episodes);

public sealed record Chapter(double StartSeconds, string Title)
{
    public override string ToString()
    {
        var t = TimeSpan.FromSeconds(StartSeconds);
        string time = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        return $"{time} · {Title}";
    }
}

public static class Podcasts
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Pod = "https://podcastindex.org/namespace/1.0";

    /// <summary>Downloads and reads a podcast's RSS feed: its name and all its episodes, newest first.</summary>
    public static async Task<PodcastFeed> LoadFeedAsync(string feedUrl)
    {
        string xml = await Web.Client.GetStringAsync(feedUrl);
        return ParseFeed(feedUrl, xml);
    }

    public static PodcastFeed ParseFeed(string feedUrl, string xml)
    {
        var doc = XDocument.Parse(xml);
        var channel = doc.Root?.Element("channel") ?? throw new InvalidOperationException("This isn't a podcast feed (no RSS channel).");

        string podcastTitle = channel.Element("title")?.Value.Trim() ?? feedUrl;
        string author = channel.Element(Itunes + "author")?.Value.Trim() ?? "";
        string image = channel.Element(Itunes + "image")?.Attribute("href")?.Value
                       ?? channel.Element("image")?.Element("url")?.Value ?? "";

        var episodes = new List<(DateTimeOffset date, PlaylistItem item)>();
        foreach (var item in channel.Elements("item"))
        {
            string? audio = item.Element("enclosure")?.Attribute("url")?.Value;
            if (string.IsNullOrWhiteSpace(audio)) continue; // not an audio episode

            DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AllowWhiteSpaces, out var date);

            // Prefer a readable transcript (HTML or plain text), else subtitles.
            var transcripts = item.Elements(Pod + "transcript").ToList();
            var transcript = transcripts.FirstOrDefault(t => (t.Attribute("type")?.Value ?? "").Contains("html"))
                             ?? transcripts.FirstOrDefault(t => (t.Attribute("type")?.Value ?? "").StartsWith("text/plain"))
                             ?? transcripts.FirstOrDefault();

            episodes.Add((date, new PlaylistItem
            {
                Path = audio.Trim(),
                Title = item.Element("title")?.Value.Trim() ?? "Episode",
                Artist = podcastTitle,
                Album = date == default ? "" : date.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                Duration = ParseDuration(item.Element(Itunes + "duration")?.Value),
                Kind = TrackKind.Episode,
                Format = "PODCAST",
                ChaptersUrl = item.Element(Pod + "chapters")?.Attribute("url")?.Value ?? "",
                TranscriptUrl = transcript?.Attribute("url")?.Value ?? ""
            }));
        }

        return new PodcastFeed(feedUrl, podcastTitle, author, image,
                               episodes.OrderByDescending(e => e.date).Select(e => e.item).ToList());
    }

    // itunes:duration can be "3600", "60:00" or "1:00:00".
    private static TimeSpan ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return TimeSpan.Zero;
            seconds = seconds * 60 + value;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Reads Podcasting 2.0 chapters (a small JSON file with start times and titles).</summary>
    public static async Task<List<Chapter>> LoadChaptersAsync(string url)
    {
        string json = await Web.Client.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var result = new List<Chapter>();
        if (!doc.RootElement.TryGetProperty("chapters", out var chapters)) return result;
        foreach (var c in chapters.EnumerateArray())
        {
            if (c.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.False) continue; // hidden chapter
            double start = c.TryGetProperty("startTime", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            string title = c.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            result.Add(new Chapter(start, title));
        }
        return result.OrderBy(c => c.StartSeconds).ToList();
    }

    /// <summary>
    /// Searches the Podcast Index directory. It needs a free API key and secret
    /// (from api.podcastindex.org), entered in Advanced settings.
    /// </summary>
    public static async Task<List<PlaylistItem>> SearchAsync(string text, string key, string secret)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "To search for podcasts, add a free Podcast Index API key and secret in Advanced settings " +
                "(get them at api.podcastindex.org). You can also paste a feed address into the search box.");

        // Podcast Index signs each request: SHA-1 of key + secret + the current time.
        string date = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key + secret + date))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.podcastindex.org/api/1.0/search/byterm?q={Uri.EscapeDataString(text.Trim())}&max=60");
        request.Headers.Add("X-Auth-Date", date);
        request.Headers.Add("X-Auth-Key", key);
        request.Headers.Add("Authorization", hash);

        using var response = await Web.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var result = new List<PlaylistItem>();
        if (!doc.RootElement.TryGetProperty("feeds", out var feeds)) return result;
        foreach (var f in feeds.EnumerateArray())
        {
            string url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0) continue;
            result.Add(new PlaylistItem
            {
                Path = url,
                Title = f.TryGetProperty("title", out var t) ? t.GetString()?.Trim() ?? url : url,
                Artist = f.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
                Kind = TrackKind.Podcast,
                Format = "FEED"
            });
        }
        return result;
    }
}

// =====================================================================================
// A web address dropped on the window (e.g. a Chrome tab): what is it?
// =====================================================================================

public enum DroppedUrlKind { Audio, RadioPlaylist, PodcastFeed, NotAudio }

public static class UrlInspector
{
    private static readonly string[] VideoSites =
        { "youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "tiktok.com", "soundcloud.com", "spotify.com", "music.apple.com", "tidal.com", "deezer.com" };

    /// <summary>
    /// Asks the server what the address is (its "content type") without downloading it all.
    /// Only real audio, radio playlists and podcast feeds are accepted; web pages and video
    /// sites get a friendly "can't play this" (no scraping of video sites, by design).
    /// </summary>
    public static async Task<(DroppedUrlKind kind, string message)> InspectAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return (DroppedUrlKind.NotAudio, "That isn't a web address Amp Player can open.");

        if (VideoSites.Any(site => uri.Host.EndsWith(site, StringComparison.OrdinalIgnoreCase)))
            return (DroppedUrlKind.NotAudio,
                    $"{uri.Host} is a streaming or video site. Amp Player only plays direct audio links, podcast feeds and radio streams.");

        if (StreamPlaylists.LooksLikePlaylist(url)) return (DroppedUrlKind.RadioPlaylist, "");
        if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return (DroppedUrlKind.NotAudio, "That's an HLS stream (.m3u8). Amp Player doesn't play those yet.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 4095); // just the start
            request.Headers.Add("Icy-MetaData", "1");
            using var response = await Web.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            bool isIcy = response.Headers.Contains("icy-name") || response.Headers.Contains("icy-metaint");

            if (type is "audio/x-scpls" or "audio/x-mpegurl" or "audio/mpegurl")
                return (DroppedUrlKind.RadioPlaylist, "");
            if (type.StartsWith("audio/") || type is "application/ogg" || isIcy)
                return (DroppedUrlKind.Audio, isIcy ? "radio" : "");

            if (type.Contains("rss") || type.Contains("xml") || type.Contains("atom"))
            {
                string start = await ReadStartAsync(response);
                if (start.Contains("<rss", StringComparison.OrdinalIgnoreCase)) return (DroppedUrlKind.PodcastFeed, "");
            }

            if (type.Contains("html"))
                return (DroppedUrlKind.NotAudio,
                        "That's a web page, not audio. Drop a direct audio link (.mp3, .flac…), a podcast feed or a radio stream instead.");

            return (DroppedUrlKind.NotAudio, $"Amp Player can't play this (it is \"{(type.Length > 0 ? type : "unknown")}\").");
        }
        catch (Exception ex)
        {
            return (DroppedUrlKind.NotAudio, $"Couldn't check that address: {Web.Explain(ex)}.");
        }
    }

    private static async Task<string> ReadStartAsync(HttpResponseMessage response)
    {
        var buffer = new byte[4096];
        await using var stream = await response.Content.ReadAsStreamAsync();
        int read = await stream.ReadAsync(buffer);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
