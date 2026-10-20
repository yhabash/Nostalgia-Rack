using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace AmpPlayer;

/// <summary>What kind of thing a list entry is.</summary>
public enum TrackKind
{
    File = 0,     // a song file on your computer
    Radio = 1,    // an internet radio station (never ends, no length)
    Episode = 2,  // a podcast episode (a file on the internet)
    Podcast = 3   // a podcast you can subscribe to (opens its episodes)
}

/// <summary>
/// One entry in a list: where it is, plus the title, artist, album and length read from
/// its tags. The list on screen updates itself when a value changes
/// (that is what INotifyPropertyChanged is for).
/// </summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    // The file types we add when you drop a folder. Others are skipped.
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".aac", ".m4a", ".wma", ".aif", ".aiff"
    };

    private int _number;
    private bool _isCurrent;
    private bool _hasError;

    /// <summary>The file path, or the web address for radio, episodes and podcast feeds.</summary>
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public TrackKind Kind { get; init; } = TrackKind.File;

    /// <summary>Short format label for the list, e.g. "FLAC 24/96" or "MP3 320".</summary>
    public string Format { get; init; } = "";

    /// <summary>Podcast episodes only: links to chapters (JSON) and a transcript.</summary>
    public string ChaptersUrl { get; init; } = "";
    public string TranscriptUrl { get; init; } = "";

    /// <summary>The song's number in the library database (0 = not saved there yet).</summary>
    public long TrackId { get; set; }

    /// <summary>File size and last-changed time, so a rescan can skip files that didn't change.</summary>
    public long FileSize { get; init; }
    public long ModifiedTicks { get; init; }

    /// <summary>True for radio, episodes and feeds (anything on the internet).</summary>
    public bool IsOnline => Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Artist · Album", or just one of them if the other is missing.</summary>
    public string ArtistAlbum =>
        string.Join(" · ", new[] { Artist, Album }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Length as "3:42", "LIVE" for radio, or "" if unknown.</summary>
    public string DurationText => Kind == TrackKind.Radio ? "LIVE"
        : Duration <= TimeSpan.Zero ? ""
        : Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");

    /// <summary>Position in the list, starting at 1.</summary>
    public int Number { get => _number; set => Set(ref _number, value); }

    /// <summary>True for the song that is playing now (shown brighter, with a marker).</summary>
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    /// <summary>True if the file could not be played or is missing (shown greyed out).</summary>
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }

    /// <summary>
    /// A fresh copy for another list (the same song can be in the playlist and the queue at
    /// the same time, each with its own number and "playing" mark).
    /// </summary>
    public PlaylistItem Clone() => new()
    {
        Path = Path, Title = Title, Artist = Artist, Album = Album, Duration = Duration,
        Kind = Kind, Format = Format, ChaptersUrl = ChaptersUrl, TranscriptUrl = TranscriptUrl,
        TrackId = TrackId, FileSize = FileSize, ModifiedTicks = ModifiedTicks, HasError = HasError
    };

    /// <summary>
    /// Reads a file's tags with TagLib#. If the tags can't be read, the file name is used
    /// as the title, so the song still appears and can still be tried.
    /// </summary>
    public static PlaylistItem FromFile(string path)
    {
        string fallbackTitle = System.IO.Path.GetFileNameWithoutExtension(path);
        string extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        long size = 0, ticks = 0;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception) { /* missing or locked: leave 0 */ }

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            return new PlaylistItem
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title.Trim(),
                Artist = tag.JoinedPerformers?.Trim() ?? "",
                Album = tag.Album?.Trim() ?? "",
                Duration = file.Properties?.Duration ?? TimeSpan.Zero,
                Format = DescribeFormat(extension, file.Properties),
                FileSize = size,
                ModifiedTicks = ticks
            };
        }
        catch (Exception)
        {
            // Unsupported, corrupt, locked or missing file: show it by its name.
            return new PlaylistItem { Path = path, Title = fallbackTitle, Format = extension, FileSize = size, ModifiedTicks = ticks };
        }
    }

    // Lossless files show bits/kHz ("FLAC 24/96"); lossy files show the bitrate ("MP3 320").
    private static string DescribeFormat(string extension, TagLib.Properties? properties)
    {
        if (properties == null) return extension;
        bool lossless = extension is "FLAC" or "WAV" or "AIF" or "AIFF" || properties.BitsPerSample > 0 && extension == "M4A" && properties.AudioBitrate > 600;
        if (lossless && properties.BitsPerSample > 0 && properties.AudioSampleRate > 0)
            return $"{extension} {properties.BitsPerSample}/{properties.AudioSampleRate / 1000.0:0.#}";
        return properties.AudioBitrate > 0 ? $"{extension} {properties.AudioBitrate}" : extension;
    }

    /// <summary>
    /// Turns a mix of files and folders into a sorted list of audio files.
    /// Folders are searched with all their sub-folders.
    /// </summary>
    public static List<string> ExpandToAudioFiles(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                result.AddRange(Directory.EnumerateFiles(path, "*", options)
                    .Where(f => AudioExtensions.Contains(System.IO.Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path))
            {
                result.Add(path);
            }
        }
        return result;
    }

    // ---- Plumbing that tells the screen when a value changed ----

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
