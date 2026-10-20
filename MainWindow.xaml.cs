using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AmpPlayer;

/// <summary>
/// The player window (Milestones 4, 5 and 6 together).
///
/// Five lists share the playlist deck: Playlist, Queue ("up next"), Library, Radio and
/// Podcasts. Songs come from the playlist after the queue runs out. The next song is always
/// opened in advance, so the engine can start it with no gap, or crossfade into it.
///
/// Read the sections from top to bottom; each explains the code below it.
/// </summary>
public partial class MainWindow : Window
{
    // ================= Parts of the player =================

    private readonly LibraryDatabase _database = new();
    private readonly AudioEngine _engine = new();
    private MediaControls? _media;
    private AppSettings _settings = new();

    private enum ListKind { Playlist, Queue, Library, Radio, Podcasts }
    private ListKind _view = ListKind.Playlist;

    private enum RepeatMode { Off, All, One }
    private RepeatMode _repeat = RepeatMode.Off;
    private bool _shuffle;
    private List<PlaylistItem> _shuffleOrder = new();
    private static readonly Random Random = new();

    // The lists. The list on screen shows one of them and updates by itself.
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private readonly ObservableCollection<PlaylistItem> _queue = new();
    private readonly ObservableCollection<PlaylistItem> _library = new();
    private readonly ObservableCollection<PlaylistItem> _radio = new();
    private readonly ObservableCollection<PlaylistItem> _podcasts = new();
    private bool _libraryLoaded, _libraryOutOfDate;
    private bool _radioShowsFavourites = true;
    private string? _openPodcastFeed;      // set while the Podcasts list shows one podcast's episodes

    // What's playing, and the last playlist song that played (where the playlist continues).
    private PlaylistItem? _current;
    private PlaylistItem? _playlistPosition;
    private List<Chapter> _chapters = new();
    private int _reconnectAttempts;

    // Saved-playlist bookkeeping.
    private bool _playlistChanged;
    private bool _ignoreComboChanges;
    private bool _ignorePresetChanges;
    private bool _bypass;
    private bool _seeking;

    // Timers: the screen (VU needles, time) about 30 times a second, a pause before
    // filtering while you type, a pause before remembering the lists, and status messages.
    private readonly DispatcherTimer _screenTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private DateTime _lastMediaTimeline = DateTime.MinValue;
    private DateTime _lastTimeUpdate = DateTime.MinValue;

    // VU needle movement: a short history of levels (to delay them for Bluetooth headphones),
    // and where each needle is now.
    private readonly Queue<(DateTime time, float left, float right)> _levelHistory = new();
    private double _needleLeft = -40, _needleRight = -40;
    private DateTime _lastScreenUpdate = DateTime.UtcNow;

    // The EQ presets in the drop-down (dB for 31 Hz … 16 kHz).
    private static readonly Dictionary<string, float[]> Presets = new()
    {
        ["Flat"] = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Loudness"] = new float[] { 6, 4, 1.5f, 0, -1, -1, 0, 1.5f, 4, 6 },
        ["Bass boost"] = new float[] { 7, 6, 4, 2, 0, 0, 0, 0, 0, 0 },
        ["Treble boost"] = new float[] { 0, 0, 0, 0, 0, 1, 2, 4, 6, 7 },
        ["Vocal"] = new float[] { -2, -2, -1, 1, 3, 4, 3, 1, 0, -1 },
        ["Rock"] = new float[] { 5, 3, 1, -1, -2, -1, 1, 3, 4, 5 },
        ["Jazz"] = new float[] { 3, 2, 1, 2, -1, -1, 0, 1, 2, 3 },
        ["Classical"] = new float[] { 4, 3, 2, 1, 0, 0, 0, 1, 2, 3 },
        ["Electronic"] = new float[] { 5, 4, 1, 0, -2, 1, 0, 1, 4, 5 },
    };
    private const string CustomPreset = "Custom";

    // ================= Starting up =================

    public MainWindow()
    {
        InitializeComponent();

        if (!StartDatabase()) return; // the app is closing

        _settings = AppSettings.Load(_database);
        StartEngine();
        StartMediaKeys();
        RestoreSettingsToScreen();
        RestoreLastSession();

        // Engine events arrive on the audio thread; InvokeAsync hands them to the screen thread.
        _engine.AdvancedTo += item => Dispatcher.InvokeAsync(() => OnAdvanced(item));
        _engine.Finished += () => Dispatcher.InvokeAsync(async () => await PlayNextAsync(automatic: true));
        _engine.StreamDropped += item => Dispatcher.InvokeAsync(async () => await ReconnectAsync(item));
        _engine.StreamTitleChanged += title => Dispatcher.InvokeAsync(() => OnStreamTitle(title));
        _engine.Buffering += stalled => Dispatcher.InvokeAsync(() => { if (stalled) ShowStatus("BUFFERING…", 3); });
        _engine.DeviceProblem += kind => Dispatcher.InvokeAsync(() => OnDeviceProblem(kind));

        // Whenever a list changes: renumber it, remember it soon, and re-prepare the next song.
        _playlist.CollectionChanged += (_, _) => { Renumber(_playlist); _playlistChanged = true; RebuildShuffle(); ListsChanged(); };
        _queue.CollectionChanged += (_, _) => { Renumber(_queue); UpdateQueueInfo(); ListsChanged(); };
        foreach (var list in new[] { _library, _radio, _podcasts })
            list.CollectionChanged += (_, _) => { Renumber(list); UpdateEmptyText(); };

        foreach (var list in new[] { _playlist, _queue, _library, _radio, _podcasts })
            CollectionViewSource.GetDefaultView(list).Filter = MatchesSearch;

        _screenTimer.Tick += (_, _) => UpdateScreen();
        _screenTimer.Start();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshFilter(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); RememberLists(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); RestoreStatusLine(); };

        // Waking from sleep: re-open the sound device, which Windows may have reset.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        ShowList(ListKind.Playlist);
        ApplyFolding();
        UpdateTransportLights();
        Closed += OnClosed;
    }

    private bool StartDatabase()
    {
        try
        {
            _database.Initialize();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the library database:\n{_database.FilePath}\n\n{ex.Message}\n\n" +
                $"A backup copy may be at:\n{_database.BackupPath}",
                "Amp Player");
            Application.Current.Shutdown();
            return false;
        }
    }

    private void StartEngine()
    {
        _engine.CrossfadeSeconds = _settings.CrossfadeSeconds;
        string? problem = _engine.Start(OutputOptionsFromSettings(), _settings.ResamplerQuality,
                                        _settings.NetBufferMs, _settings.NetPreBufferPercent);
        if (problem != null)
            MessageBox.Show(problem, "Amp Player");
    }

    private OutputOptions OutputOptionsFromSettings() =>
        new(_settings.OutputDeviceId, _settings.ExclusiveMode, _settings.SampleRate, _settings.BufferMs);

    private void StartMediaKeys()
    {
        _media = new MediaControls();
        _media.ButtonPressed += name => Dispatcher.InvokeAsync(async () =>
        {
            switch (name)
            {
                case "play": await PlayOrResumeAsync(); break;
                case "pause": _engine.Pause(); UpdateTransportLights(); break;
                case "next": await PlayNextAsync(automatic: false); break;
                case "previous": await PreviousAsync(); break;
                case "stop": _engine.Stop(); UpdateTransportLights(); break;
            }
        });
    }

    // Puts the knobs, EQ, folds and window position back the way you left them.
    private void RestoreSettingsToScreen()
    {
        VolumeKnob.Value = _settings.Volume;
        SlimVolume.Value = _settings.Volume;
        BalanceKnob.Value = _settings.Balance;
        _engine.Output.SetVolume(_settings.Volume);
        _engine.Output.SetBalance(_settings.Balance);

        _ignorePresetChanges = true;
        PresetCombo.ItemsSource = Presets.Keys.Concat(new[] { CustomPreset }).ToList();
        PresetCombo.SelectedItem = Presets.ContainsKey(_settings.EqPreset) ? _settings.EqPreset : CustomPreset;
        PreampSlider.Value = _settings.Preamp;
        var bands = BandSliders().ToList();
        for (int i = 0; i < bands.Count && i < _settings.EqGains.Length; i++)
            bands[i].Value = _settings.EqGains[i];
        _ignorePresetChanges = false;

        _engine.Equalizer.SetEnabled(_settings.EqOn);
        _engine.Output.SoftClipEnabled = _settings.SoftClip;
        UpdateEqLights();
        UpdateSkipLabels();

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop) &&
            _settings.WindowLeft > SystemParameters.VirtualScreenLeft - 100 &&
            _settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            _settings.WindowTop > SystemParameters.VirtualScreenTop - 20 &&
            _settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
    }

    private void RestoreLastSession()
    {
        var (playlist, queue) = _database.LoadSession();
        foreach (var item in playlist) _playlist.Add(item);
        foreach (var item in queue) _queue.Add(item);
        Renumber(_playlist);
        Renumber(_queue);

        RefreshPlaylistCombo();
        if (long.TryParse(_database.GetSetting("open_playlist"), out long id))
            SelectPlaylistInCombo(id);
        _playlistChanged = _database.GetSetting("playlist_changed") == "1";

        _shuffle = _database.GetSetting("shuffle") == "1";
        _repeat = Enum.TryParse(_database.GetSetting("repeat"), out RepeatMode mode) ? mode : RepeatMode.Off;
        RebuildShuffle();
        UpdateQueueInfo();
    }

    private void RememberLists()
    {
        try
        {
            _database.SaveSession(_playlist, _queue);
            _database.SetSetting("open_playlist", (PlaylistCombo.SelectedItem as PlaylistInfo)?.Id.ToString());
            _database.SetSetting("playlist_changed", _playlistChanged ? "1" : "0");
            _database.SetSetting("shuffle", _shuffle ? "1" : "0");
            _database.SetSetting("repeat", _repeat.ToString());
        }
        catch (Exception ex)
        {
            ShowStatus($"COULDN'T SAVE THE LISTS ({ex.Message})");
        }
    }

    private void SaveSettings()
    {
        _settings.Volume = VolumeKnob.Value;
        _settings.Balance = BalanceKnob.Value;
        _settings.EqGains = _engine.Equalizer.Gains;
        _settings.Preamp = (float)PreampSlider.Value;
        _settings.EqPreset = PresetCombo.SelectedItem as string ?? CustomPreset;
        if (WindowState == WindowState.Normal) { _settings.WindowLeft = Left; _settings.WindowTop = Top; }
        try { _settings.Save(_database); } catch (Exception) { /* closing anyway */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _screenTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        RememberLists();
        SaveSettings();
        _media?.Dispose();
        _engine.Dispose();
    }

    // ================= Playing =================

    /// <summary>
    /// Plays one entry now. Radio playlists (.pls/.m3u) are opened to find the real stream first.
    /// Returns false if it couldn't be played (the entry is greyed out).
    /// </summary>
    private async Task<bool> PlayItemAsync(PlaylistItem item)
    {
        var toPlay = item;
        if (item.IsOnline) ShowStatus(item.Kind == TrackKind.Radio ? "CONNECTING…" : "LOADING…", 30);

        if (item.Kind == TrackKind.Radio && StreamPlaylists.LooksLikePlaylist(item.Path))
        {
            try
            {
                string? url = await StreamPlaylists.ResolveAsync(item.Path);
                if (url == null) throw new InvalidOperationException("no stream address inside");
                toPlay = new PlaylistItem
                {
                    Path = url, Title = item.Title, Artist = item.Artist, Album = item.Album,
                    Kind = TrackKind.Radio, Format = item.Format, TrackId = item.TrackId
                };
            }
            catch (Exception ex)
            {
                item.HasError = true;
                ShowStatus($"COULDN'T OPEN THE STATION ({Web.Explain(ex)})");
                return false;
            }
        }

        string? error = await _engine.PlayAsync(toPlay);
        if (error != null)
        {
            item.HasError = true;
            ShowStatus($"SKIPPED \"{item.Title}\" ({error})");
            return false;
        }

        item.HasError = false;
        _reconnectAttempts = 0;
        SetCurrent(item);
        RestoreStatusLine();
        UpdateTransportLights();
        await PrepareNextAsync();
        return true;
    }

    /// <summary>Plays the playlist from a position, skipping songs that can't be played.</summary>
    private async Task PlayFromPlaylistAsync(int index)
    {
        for (int tries = 0; tries < _playlist.Count && index >= 0 && index < _playlist.Count; tries++, index++)
        {
            var item = _playlist[index];
            _playlistPosition = item;
            if (await PlayItemAsync(item)) return;
        }
        StopAndClear();
    }

    /// <summary>
    /// Moves to the next song: repeat-one replays, then the queue (the song leaves the queue),
    /// then the playlist (in shuffle order if shuffle is on, round again if repeat is on).
    /// "automatic" = the song ended by itself (as opposed to pressing Next).
    /// </summary>
    private async Task PlayNextAsync(bool automatic)
    {
        if (automatic && _repeat == RepeatMode.One && _current != null && _current.Kind != TrackKind.Radio)
        {
            if (await PlayItemAsync(_current)) return;
        }

        while (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (await PlayItemAsync(next)) return;
        }

        var fromPlaylist = NextPlaylistItem();
        if (fromPlaylist != null)
            await PlayFromPlaylistAsync(_playlist.IndexOf(fromPlaylist));
        else if (automatic)
            StopAndClear(); // end of the playlist
    }

    // The playlist song after the last one that played (shuffle and repeat-all aware).
    private PlaylistItem? NextPlaylistItem()
    {
        if (_playlist.Count == 0) return null;
        var order = _shuffle ? _shuffleOrder : _playlist.ToList();
        if (order.Count == 0) return null;

        int index = _playlistPosition == null ? -1 : order.IndexOf(_playlistPosition);
        if (_playlistPosition != null && index < 0) return null; // it was removed from the list
        if (index + 1 < order.Count) return order[index + 1];
        return _repeat == RepeatMode.All ? order[0] : null;
    }

    private PlaylistItem? PreviousPlaylistItem()
    {
        var order = _shuffle ? _shuffleOrder : _playlist.ToList();
        int index = _playlistPosition == null ? -1 : order.IndexOf(_playlistPosition);
        if (index > 0) return order[index - 1];
        return _repeat == RepeatMode.All && order.Count > 0 && index == 0 ? order[^1] : null;
    }

    // What should come after the current song (without taking anything out of the queue yet).
    private PlaylistItem? PeekNext()
    {
        if (_current?.Kind == TrackKind.Radio) return null;
        if (_repeat == RepeatMode.One && _current != null) return _current;
        if (_queue.Count > 0) return _queue[0];
        return NextPlaylistItem();
    }

    /// <summary>Opens the next song in advance so it can start without a gap (or crossfade in).</summary>
    private Task PrepareNextAsync() => _engine.IsLoaded ? _engine.PrepareNextAsync(PeekNext()) : Task.CompletedTask;

    // The engine started the prepared song by itself (gapless or crossfade).
    private void OnAdvanced(PlaylistItem item)
    {
        if (_queue.Count > 0 && ReferenceEquals(_queue[0], item)) _queue.RemoveAt(0);
        else if (_playlist.Contains(item)) _playlistPosition = item;

        SetCurrent(item);
        RestoreStatusLine();
        _ = PrepareNextAsync();
    }

    // Any list change: remember it soon, and make sure the right song is prepared.
    private void ListsChanged()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateEmptyText();
        _ = PrepareNextAsync();
    }

    private async Task PlayOrResumeAsync()
    {
        if (_engine.IsLoaded)
        {
            _engine.Resume();
            UpdateTransportLights();
            return;
        }
        if (SongList.SelectedItem is PlaylistItem picked) { await PlayPickedAsync(picked); return; }
        if (_queue.Count > 0 || _playlist.Count > 0) { await PlayNextAsync(automatic: false); return; }
        AddFiles_Click(this, new RoutedEventArgs());
    }

    private async Task PreviousAsync()
    {
        if (_engine.IsLoaded && _engine.PositionSeconds > 3)
        {
            _engine.SeekTo(0);
            return;
        }
        var previous = PreviousPlaylistItem();
        if (previous != null) await PlayFromPlaylistAsync(_playlist.IndexOf(previous));
        else _engine.SeekTo(0);
    }

    private void StopAndClear()
    {
        _engine.Unload();
        SetCurrent(null);
        UpdateTransportLights();
    }

    /// <summary>Plays the entry you picked in the list on screen (double-click, Enter, "Play now").</summary>
    private async Task PlayPickedAsync(PlaylistItem item)
    {
        switch (_view)
        {
            case ListKind.Playlist:
                await PlayFromPlaylistAsync(_playlist.IndexOf(item));
                break;
            case ListKind.Queue:
                _queue.Remove(item); // playing a queued song takes it out of the queue
                if (!await PlayItemAsync(item)) await PlayNextAsync(automatic: false);
                break;
            case ListKind.Podcasts when item.Kind == TrackKind.Podcast:
                await OpenPodcastAsync(item.Path, item.Title);
                break;
            default:
                // Library, radio and episodes play straight away; afterwards the queue/playlist carry on.
                EnsureInDatabase(new[] { item });
                await PlayItemAsync(item);
                break;
        }
    }

    // Radio stations: if the stream drops, try again after 1, 2, 4, 8… seconds (at most 30).
    private async Task ReconnectAsync(PlaylistItem station)
    {
        if (!ReferenceEquals(_current, station) && _current?.Path != station.Path) return;
        _reconnectAttempts++;
        if (_reconnectAttempts > 8)
        {
            ShowStatus("THE STATION STOPPED. PRESS PLAY TO TRY AGAIN", 10);
            StopAndClear();
            return;
        }

        int delay = (int)Math.Min(30, Math.Pow(2, _reconnectAttempts - 1));
        ShowStatus($"RECONNECTING IN {delay} S… (TRY {_reconnectAttempts})", delay + 2);
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (_current != null && _current.Path != station.Path) return; // you chose something else meanwhile

        string? error = await _engine.PlayAsync(station);
        if (error != null) await ReconnectAsync(station);
        else { _reconnectAttempts = 0; SetCurrent(station); RestoreStatusLine(); }
    }

    // ================= What's playing (display, chapters, Windows media panel) =================

    private void SetCurrent(PlaylistItem? item)
    {
        if (_current != null) _current.IsCurrent = false;
        _current = item;
        _chapters = new List<Chapter>();
        ChaptersButton.Visibility = Visibility.Collapsed;
        TranscriptButton.Visibility = Visibility.Collapsed;

        if (item == null)
        {
            TitleText.Text = SlimTitle.Text = "NO SONG LOADED";
            ArtistText.Text = SlimArtist.Text = "";
            TimeText.Text = SlimTime.Text = "00:00";
            LengthText.Text = FormatText.Text = "";
            SeekBar.Value = 0;
            _media?.SetState(false, false);
            Taskbar.ProgressState = TaskbarItemProgressState.None;
            RestoreStatusLine();
            return;
        }

        item.IsCurrent = true;
        TitleText.Text = SlimTitle.Text = item.Title.ToUpperInvariant();
        ArtistText.Text = SlimArtist.Text = item.ArtistAlbum;
        FormatText.Text = item.Kind == TrackKind.Radio ? $"{item.Format} · LIVE".TrimStart(' ', '·') : _engine.DescribeCurrent();
        SeekBar.IsEnabled = item.Kind != TrackKind.Radio;
        Title = $"{item.Title} – Amp Player";
        Taskbar.Description = $"{item.Title} – {item.ArtistAlbum}";
        _media?.ShowSong(item.Title, item.Artist, item.Album);

        if (SongList.ItemsSource is IList<PlaylistItem> shown && shown.Contains(item))
            SongList.ScrollIntoView(item);

        if (item.Kind == TrackKind.Episode)
        {
            TranscriptButton.Visibility = item.TranscriptUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (item.ChaptersUrl.Length > 0) _ = LoadChaptersAsync(item);
        }
    }

    private async Task LoadChaptersAsync(PlaylistItem episode)
    {
        try
        {
            var chapters = await Podcasts.LoadChaptersAsync(episode.ChaptersUrl);
            if (!ReferenceEquals(_current, episode) || chapters.Count == 0) return;
            _chapters = chapters;
            ChaptersButton.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* chapters are optional */ }
    }

    private void ChaptersButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ChaptersButton, Placement = PlacementMode.Bottom };
        foreach (var chapter in _chapters)
        {
            var entry = new MenuItem { Header = chapter.ToString() };
            entry.Click += (_, _) => _engine.SeekTo(chapter.StartSeconds);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void TranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.TranscriptUrl is { Length: > 0 } url) OpenInBrowser(url);
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser */ }
    }

    // A radio station sent a new "now playing" title.
    private void OnStreamTitle(string title)
    {
        if (_current?.Kind != TrackKind.Radio) return;
        ArtistText.Text = SlimArtist.Text = title;
        _media?.ShowSong(title, _current.Title, "");
    }

    /// <summary>The first display line: "NOW PLAYING · 03 / 16", or a message for a few seconds.</summary>
    private void ShowStatus(string message, double seconds = 4)
    {
        StatusLine.Text = message.ToUpperInvariant();
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(seconds);
        _statusTimer.Start();
    }

    private void RestoreStatusLine()
    {
        _statusTimer.Stop();
        if (_current == null) { StatusLine.Text = _bypass ? "READY · BYPASS" : "READY"; return; }

        string where = _current.Kind switch
        {
            TrackKind.Radio => "RADIO · LIVE",
            TrackKind.Episode => "PODCAST",
            _ => _playlist.Contains(_current) ? $"NOW PLAYING · {_playlist.IndexOf(_current) + 1:00} / {_playlist.Count:00}" : "NOW PLAYING"
        };
        if (_engine.IsPaused) where += " · PAUSED";
        if (_bypass) where += " · BYPASS";
        StatusLine.Text = where;
    }

    // ================= The screen timer: time, seek bar, VU needles, taskbar =================

    private void UpdateScreen()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastScreenUpdate).TotalSeconds, 0.001, 0.2);
        _lastScreenUpdate = now;

        // --- VU needles ---
        var (left, right) = _engine.IsPlaying ? _engine.Output.Levels : (0f, 0f);
        _levelHistory.Enqueue((now, left, right));
        var delay = TimeSpan.FromMilliseconds(_settings.BluetoothLatencyMs);
        while (_levelHistory.Count > 1 && now - _levelHistory.Peek().time > delay)
            _levelHistory.Dequeue();
        var shown = _levelHistory.Peek();

        // 0 VU = −12 dBFS RMS: loud modern music swings around 0, quieter music lower.
        double targetLeft = ToVu(shown.left), targetRight = ToVu(shown.right);
        _needleLeft = Ballistics(_needleLeft, targetLeft, dt);
        _needleRight = Ballistics(_needleRight, targetRight, dt);
        if (MainDeck.Visibility == Visibility.Visible) { VuLeft.Level = _needleLeft; VuRight.Level = _needleRight; }
        else { SlimVuLeft.Level = _needleLeft; SlimVuRight.Level = _needleRight; }

        // --- Time and seek bar (4 times a second is plenty) ---
        if (now - _lastTimeUpdate < TimeSpan.FromMilliseconds(250)) return;
        _lastTimeUpdate = now;
        if (!_engine.IsLoaded) { Taskbar.ProgressState = TaskbarItemProgressState.None; return; }

        double position = _engine.PositionSeconds, length = _engine.LengthSeconds;
        TimeText.Text = SlimTime.Text = FormatTime(position);
        LengthText.Text = length > 0 ? $"/ {FormatTime(length)}" : "";
        if (!_seeking && length > 0) SeekBar.Value = Math.Clamp(position / length, 0, 1);

        if (_current?.Kind == TrackKind.Radio && _engine.BufferPercent is int buffer && buffer < 50 && !_statusTimer.IsEnabled)
            StatusLine.Text = $"RADIO · BUFFER {buffer}%";

        Taskbar.ProgressState = length <= 0 ? TaskbarItemProgressState.None
                              : _engine.IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = length > 0 ? position / length : 0;

        if (now - _lastMediaTimeline > TimeSpan.FromSeconds(1))
        {
            _lastMediaTimeline = now;
            _media?.SetTimeline(position, length);
        }
    }

    private static double ToVu(float rms) => rms <= 0.00001f ? -40 : 20 * Math.Log10(rms) + 12;

    // A VU needle rises in about 0.1 s and falls back in about 0.3 s, like a real meter.
    private static double Ballistics(double current, double target, double dt)
    {
        double tau = target > current ? 0.1 : 0.3;
        return current + (target - current) * (1 - Math.Exp(-dt / tau));
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    // ================= Transport buttons =================

    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayOrResumeAsync();

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _engine.Pause();
        UpdateTransportLights();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsPlaying) Pause_Click(sender, e);
        else await PlayOrResumeAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        UpdateTransportLights();
        TimeText.Text = SlimTime.Text = "00:00";
        SeekBar.Value = 0;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _engine.Skip(-_settings.SeekStepSeconds);
    private void Forward_Click(object sender, RoutedEventArgs e) => _engine.Skip(+_settings.SeekStepSeconds);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await PlayNextAsync(automatic: false);

    // Play is lit while music plays, Pause while paused; the taskbar and Windows follow.
    private void UpdateTransportLights()
    {
        bool playing = _engine.IsPlaying;
        bool paused = _engine.IsLoaded && _engine.IsPaused;
        SkinProps.SetLit(PlayButton, playing);
        SkinProps.SetLit(PauseButton, paused);
        SkinProps.SetLit(SlimPlay, playing);
        SlimPlayIcon.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
        ThumbPlay.ImageSource = (ImageSource)FindResource(playing ? "ThumbPauseImage" : "ThumbPlayImage");
        ThumbPlay.Description = playing ? "Pause" : "Play";
        _media?.SetState(playing, _engine.IsLoaded);
        RestoreStatusLine();
    }

    private void UpdateSkipLabels()
    {
        string step = $"{_settings.SeekStepSeconds:0}";
        BackStepText.Text = FwdStepText.Text = step;
        BackButton.ToolTip = $"Back {step} seconds (Shift+Left)";
        FwdButton.ToolTip = $"Forward {step} seconds (Shift+Right)";
    }

    // Seek bar: dragging the dot, or clicking anywhere on the line.
    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
        _engine.SeekTo(SeekBar.Value * _engine.LengthSeconds);
    }

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) _engine.SeekTo(SeekBar.Value * _engine.LengthSeconds);
    }

    // Knobs
    private void VolumeKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _engine.Output.SetVolume(e.NewValue);
        if (SlimVolume != null && Math.Abs(SlimVolume.Value - e.NewValue) > 0.0001) SlimVolume.Value = e.NewValue;
    }

    private void SlimVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeKnob != null && Math.Abs(VolumeKnob.Value - e.NewValue) > 0.0001) VolumeKnob.Value = e.NewValue;
    }

    private void BalanceKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => _engine.Output.SetBalance(e.NewValue);

    // Taskbar thumbnail buttons
    private async void ThumbPrev_Click(object? sender, EventArgs e) => await PreviousAsync();
    private void ThumbPlay_Click(object? sender, EventArgs e) => PlayPause_Click(this, new RoutedEventArgs());
    private async void ThumbNext_Click(object? sender, EventArgs e) => await PlayNextAsync(automatic: false);

    // ================= Equalizer deck =================

    private IEnumerable<Slider> BandSliders() =>
        BandPanel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Slider>())
                 .OrderBy(s => int.Parse(s.Tag?.ToString() ?? "0"));

    private void Band_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || !int.TryParse(slider.Tag?.ToString(), out int band)) return;
        _engine.Equalizer.SetBand(band, (float)e.NewValue);
        _engine.Output.BoostActive = _engine.Equalizer.IsBoosting;
        if (!_ignorePresetChanges) MarkPresetCustomIfChanged();
    }

    private void Preamp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _engine.Equalizer.SetPreamp((float)e.NewValue);
        _engine.Output.BoostActive = _engine.Equalizer.IsBoosting;
    }

    private void EqSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider) slider.Value = 0;
    }

    // Picking a preset moves all ten sliders.
    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignorePresetChanges || PresetCombo.SelectedItem is not string name || !Presets.TryGetValue(name, out var gains)) return;
        _ignorePresetChanges = true;
        var bands = BandSliders().ToList();
        for (int i = 0; i < bands.Count; i++) bands[i].Value = gains[i];
        _ignorePresetChanges = false;
        ShowStatus($"EQ PRESET · {name}");
    }

    // Moving a slider by hand turns the preset into "Custom" (unless it still matches one).
    private void MarkPresetCustomIfChanged()
    {
        if (PresetCombo?.ItemsSource == null) return;
        var gains = BandSliders().Select(s => (float)s.Value).ToArray();
        string match = Presets.FirstOrDefault(p => p.Value.Zip(gains).All(z => Math.Abs(z.First - z.Second) < 0.01f)).Key ?? CustomPreset;
        if (Equals(PresetCombo.SelectedItem, match)) return;
        _ignorePresetChanges = true;
        PresetCombo.SelectedItem = match;
        _ignorePresetChanges = false;
    }

    private void EqOnButton_Click(object sender, RoutedEventArgs e) => ToggleEq();

    private void ToggleEq()
    {
        if (_bypass) { SetBypass(false); return; }
        _settings.EqOn = !_settings.EqOn;
        _engine.Equalizer.SetEnabled(_settings.EqOn);
        UpdateEqLights();
        ShowStatus(_settings.EqOn ? "EQ ON" : "EQ OFF");
    }

    private void SoftClipKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.SoftClip = !_settings.SoftClip;
        if (!_bypass) _engine.Output.SoftClipEnabled = _settings.SoftClip;
        UpdateEqLights();
        ShowStatus(_settings.SoftClip ? "SOFT CLIP ON" : "SOFT CLIP OFF");
    }

    // Master bypass: no EQ, no preamp, no clipper. Only volume and balance stay.
    private void BypassButton_Click(object sender, RoutedEventArgs e) => SetBypass(!_bypass);

    private void SetBypass(bool on)
    {
        _bypass = on;
        _engine.Equalizer.SetEnabled(!on && _settings.EqOn);
        _engine.Output.SoftClipEnabled = !on && _settings.SoftClip;
        UpdateEqLights();
        RestoreStatusLine();
        ShowStatus(on ? "BYPASS · PURE SIGNAL" : "BYPASS OFF");
    }

    private void UpdateEqLights()
    {
        SkinProps.SetLit(EqOnButton, _settings.EqOn && !_bypass);
        SkinProps.SetLit(BypassButton, _bypass);
        SkinProps.SetLit(SoftClipKey, _settings.SoftClip && !_bypass);
        _engine.Output.BoostActive = _engine.Equalizer.IsBoosting;
    }

    // ================= Folding the decks, and the slim strip =================

    private void MainFold_Click(object sender, RoutedEventArgs e) { _settings.SlimMode = !_settings.SlimMode; ApplyFolding(); }
    private void EqFold_Click(object sender, RoutedEventArgs e) { _settings.EqFolded = !_settings.EqFolded; ApplyFolding(); }
    private void PlaylistFold_Click(object sender, RoutedEventArgs e) { _settings.PlaylistFolded = !_settings.PlaylistFolded; ApplyFolding(); }

    private void ApplyFolding()
    {
        bool slim = _settings.SlimMode;
        bool showEq = !slim && !_settings.EqFolded;
        bool showList = !slim && !_settings.PlaylistFolded;

        SlimDeck.Visibility = slim ? Visibility.Visible : Visibility.Collapsed;
        MainDeck.Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
        EqSeam.Visibility = EqDeck.Visibility = showEq ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSeam.Visibility = PlaylistDeck.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

        PullEq.Visibility = !slim && _settings.EqFolded ? Visibility.Visible : Visibility.Collapsed;
        PullPlaylist.Visibility = !slim && _settings.PlaylistFolded ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= The window frame =================

    // Drag the player by any empty bit of metal or wood.
    private void Case_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void CloseKey_Click(object sender, RoutedEventArgs e) => Close();
    private void MinimizeKey_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void SettingsKey_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ================= Keyboard shortcuts =================

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // While typing in a text box, plain keys (and Space) belong to the text box.
        if (Keyboard.FocusedElement is TextBox && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return;
        // Arrow keys on a focused knob or slider move it.
        if (Keyboard.FocusedElement is RangeBase && modifiers == ModifierKeys.None) return;

        var action = _settings.FindAction(modifiers, key);
        if (action == null) return;
        e.Handled = true;

        switch (action.Value)
        {
            case ShortcutAction.PlayPause:
                if (_engine.IsPlaying) { _engine.Pause(); UpdateTransportLights(); } else await PlayOrResumeAsync();
                break;
            case ShortcutAction.Stop: Stop_Click(this, e); break;
            case ShortcutAction.Next: await PlayNextAsync(automatic: false); break;
            case ShortcutAction.Previous: await PreviousAsync(); break;
            case ShortcutAction.SkipBack: _engine.Skip(-_settings.SeekStepSeconds); break;
            case ShortcutAction.SkipForward: _engine.Skip(+_settings.SeekStepSeconds); break;
            case ShortcutAction.VolumeUp: VolumeKnob.Value = Math.Min(1, VolumeKnob.Value + 0.05); break;
            case ShortcutAction.VolumeDown: VolumeKnob.Value = Math.Max(0, VolumeKnob.Value - 0.05); break;
            case ShortcutAction.ToggleEq: ToggleEq(); break;
            case ShortcutAction.FoldEq: EqFold_Click(this, e); break;
            case ShortcutAction.FoldPlaylist: PlaylistFold_Click(this, e); break;
            case ShortcutAction.SlimMode: MainFold_Click(this, e); break;
            case ShortcutAction.Search:
                if (_settings.SlimMode) { _settings.SlimMode = false; }
                _settings.PlaylistFolded = false;
                ApplyFolding();
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case ShortcutAction.Settings: OpenSettings(); break;
        }
    }

    // ================= Advanced settings =================

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings.Copy(), _database) { Owner = this };
        window.Applied += ApplySettings;   // APPLY and OK
        window.ShowDialog();
        if (window.LibraryFoldersChanged) _ = RescanAsync();
    }

    private void ApplySettings(AppSettings updated)
    {
        var old = _settings;

        // Keep the things the player remembers by itself (they may have changed meanwhile).
        updated.Volume = VolumeKnob.Value; updated.Balance = BalanceKnob.Value;
        updated.EqGains = _engine.Equalizer.Gains; updated.Preamp = (float)PreampSlider.Value;
        updated.EqOn = old.EqOn; updated.SoftClip = old.SoftClip; updated.EqPreset = PresetCombo.SelectedItem as string ?? old.EqPreset;
        updated.EqFolded = old.EqFolded; updated.PlaylistFolded = old.PlaylistFolded; updated.SlimMode = old.SlimMode;
        _settings = updated;

        _engine.CrossfadeSeconds = updated.CrossfadeSeconds;
        AudioEngine.ApplyTuning(updated.ResamplerQuality, updated.NetBufferMs, updated.NetPreBufferPercent);
        UpdateSkipLabels();

        bool outputChanged = old.OutputDeviceId != updated.OutputDeviceId || old.ExclusiveMode != updated.ExclusiveMode ||
                             old.SampleRate != updated.SampleRate || old.BufferMs != updated.BufferMs;
        if (outputChanged)
        {
            string? message = _engine.OpenOutput(OutputOptionsFromSettings());
            if (message != null) MessageBox.Show(message, "Amp Player");
            UpdateTransportLights();
        }

        SaveSettings();
        _ = PrepareNextAsync(); // the crossfade length may have changed
        ShowStatus("SETTINGS SAVED");
    }

    // ================= Sound device problems =================

    private void OnDeviceProblem(string kind)
    {
        switch (kind)
        {
            case "unplugged":
                // Headphones or USB DAC unplugged: pause, then fall back to the Windows default.
                _engine.Pause();
                UpdateTransportLights();
                string? message = _engine.OpenOutput(OutputOptionsFromSettings() with { DeviceId = "" });
                ShowStatus("OUTPUT UNPLUGGED · PAUSED", 8);
                if (message != null) MessageBox.Show(message, "Amp Player");
                break;
            case "default-changed":
            case "failed":
                _engine.OpenOutput(OutputOptionsFromSettings());
                ShowStatus("SOUND DEVICE CHANGED", 5);
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.InvokeAsync(() =>
        {
            _engine.OpenOutput(OutputOptionsFromSettings());
            UpdateTransportLights();
        });
    }

    // ================= Switching lists, and searching =================

    private async void SourceTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == PlaylistTab) ShowList(ListKind.Playlist);
        else if (sender == QueueTab) ShowList(ListKind.Queue);
        else if (sender == LibraryTab) { ShowList(ListKind.Library); if (!_libraryLoaded || _libraryOutOfDate) await LoadLibraryAsync(); }
        else if (sender == RadioTab) { ShowList(ListKind.Radio); if (_radio.Count == 0) LoadRadioFavourites(); }
        else if (sender == PodcastsTab) { ShowList(ListKind.Podcasts); if (_openPodcastFeed == null) LoadPodcastList(); }
    }

    private void ShowList(ListKind kind)
    {
        _view = kind;
        if (SearchBox.Text.Length > 0) SearchBox.Text = ""; // each list starts unfiltered
        PlaylistTools.Visibility = kind == ListKind.Playlist ? Visibility.Visible : Visibility.Collapsed;
        QueueTools.Visibility = kind == ListKind.Queue ? Visibility.Visible : Visibility.Collapsed;
        LibraryTools.Visibility = kind == ListKind.Library ? Visibility.Visible : Visibility.Collapsed;
        RadioTools.Visibility = kind == ListKind.Radio ? Visibility.Visible : Visibility.Collapsed;
        PodcastTools.Visibility = kind == ListKind.Podcasts ? Visibility.Visible : Visibility.Collapsed;

        bool editable = kind is ListKind.Playlist or ListKind.Queue;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = RemoveButton.IsEnabled = ClearButton.IsEnabled = editable;

        SearchBox.Tag = kind switch
        {
            ListKind.Radio => "SEARCH STATIONS ↵",
            ListKind.Podcasts => "SEARCH PODCASTS ↵",
            _ => "SEARCH"
        };

        SongList.ItemsSource = ListFor(kind);
        RefreshFilter();
        UpdateEmptyText();
        UpdateShuffleRepeatLights();
    }

    private ObservableCollection<PlaylistItem> ListFor(ListKind kind) => kind switch
    {
        ListKind.Queue => _queue,
        ListKind.Library => _library,
        ListKind.Radio => _radio,
        ListKind.Podcasts => _podcasts,
        _ => _playlist
    };

    private ObservableCollection<PlaylistItem> ShownList => ListFor(_view);

    private void UpdateQueueInfo()
    {
        QueueTab.Content = _queue.Count == 0 ? "QUEUE" : $"QUEUE {_queue.Count}";
        QueueInfo.Text = _queue.Count == 0 ? "THE QUEUE IS EMPTY" : $"UP NEXT · {_queue.Count} {(_queue.Count == 1 ? "SONG" : "SONGS")}";
    }

    // A friendly hint inside the empty glass, so you always know what to do next.
    private void UpdateEmptyText()
    {
        if (EmptyListText == null) return;

        // Songs are there, but the search hides them all: say so, rather than showing blank glass.
        string search = SearchBox?.Text.Trim() ?? "";
        if (ShownList.Count > 0 && search.Length > 0 && SongList.ItemsSource != null &&
            CollectionViewSource.GetDefaultView(SongList.ItemsSource).IsEmpty)
        {
            EmptyListText.Visibility = Visibility.Visible;
            EmptyListText.Text = $"Nothing here matches \"{search}\". Press Esc in the search box to clear it.";
            return;
        }

        bool empty = ShownList.Count == 0;
        EmptyListText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = _view switch
        {
            ListKind.Playlist => "Drop songs or folders here, or use the + buttons below.",
            ListKind.Queue => "Right-click any song and choose Play next or Add to queue.",
            ListKind.Library => "Click FOLDERS… to choose your music folders. The library fills itself.",
            ListKind.Radio => _radioShowsFavourites
                ? "No favourite stations yet. Type a station name in Search and press Enter, or click TOP STATIONS."
                : "No stations found.",
            ListKind.Podcasts => _openPodcastFeed != null ? "No episodes." :
                "Click ADD FEED… to paste a podcast feed address, or type a name in Search and press Enter.",
            _ => ""
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    // Enter in the search box: search Radio Browser or Podcast Index (in those lists).
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; SongList.Focus(); return; }
        if (e.Key != Key.Enter) return;
        string text = SearchBox.Text.Trim();
        if (_view == ListKind.Radio) await SearchRadioAsync(text);
        else if (_view == ListKind.Podcasts) await SearchPodcastsAsync(text);
    }

    private void RefreshFilter()
    {
        if (SongList.ItemsSource != null)
            CollectionViewSource.GetDefaultView(SongList.ItemsSource).Refresh();
        UpdateEmptyText();
    }

    // A song matches if every word you typed appears in its title, artist or album.
    private bool MatchesSearch(object obj)
    {
        string text = SearchBox?.Text?.Trim() ?? "";
        if (text.Length == 0 || obj is not PlaylistItem item) return true;
        string haystack = $"{item.Title} {item.Artist} {item.Album}";
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .All(word => haystack.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    // ================= The list on screen =================

    private async void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongList.SelectedItem is PlaylistItem item) await PlayPickedAsync(item);
    }

    private async void SongList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SongList.SelectedItem is PlaylistItem item)
        {
            e.Handled = true;
            await PlayPickedAsync(item);
        }
        else if (e.Key == Key.Delete && _view is ListKind.Playlist or ListKind.Queue)
        {
            e.Handled = true;
            RemoveSelected();
        }
    }

    private List<PlaylistItem> SelectedSongs()
    {
        var list = ShownList;
        return SongList.SelectedItems.Cast<PlaylistItem>().OrderBy(list.IndexOf).ToList();
    }

    // Right-click menu: only what makes sense for what you clicked on.
    private void SongMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSongs();
        bool any = selected.Count > 0;
        bool feeds = any && selected.All(s => s.Kind == TrackKind.Podcast);
        bool playable = any && !feeds;
        bool radio = any && selected.All(s => s.Kind == TrackKind.Radio);

        MenuPlayNow.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        MenuPlayNext.Visibility = MenuAddToQueue.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        MenuAddToPlaylist.Visibility = playable && _view != ListKind.Playlist ? Visibility.Visible : Visibility.Collapsed;
        MenuFavourite.Visibility = radio ? Visibility.Visible : Visibility.Collapsed;
        MenuFavourite.Header = _view == ListKind.Radio && _radioShowsFavourites ? "Remove from radio favourites" : "Add to radio favourites";
        MenuSubscribe.Visibility = feeds ? Visibility.Visible : Visibility.Collapsed;
        MenuSubscribe.Header = feeds && _database.IsSubscribed(selected[0].Path) ? "Unsubscribe" : "Subscribe";
        MenuOpenPodcast.Visibility = feeds ? Visibility.Visible : Visibility.Collapsed;
        MenuRemove.Visibility = _view is ListKind.Playlist or ListKind.Queue ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MenuPlayNow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSongs().FirstOrDefault() is PlaylistItem item) await PlayPickedAsync(item);
    }

    private void MenuPlayNext_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedSongs().Where(s => s.Kind != TrackKind.Podcast).ToList();
        EnsureInDatabase(songs);
        for (int i = 0; i < songs.Count; i++) _queue.Insert(i, songs[i].Clone());
        ShowStatus(songs.Count == 1 ? "PLAYS NEXT" : $"{songs.Count} SONGS PLAY NEXT");
    }

    private void MenuAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedSongs().Where(s => s.Kind != TrackKind.Podcast).ToList();
        EnsureInDatabase(songs);
        foreach (var song in songs) _queue.Add(song.Clone());
        ShowStatus(songs.Count == 1 ? "QUEUED" : $"QUEUED {songs.Count} SONGS");
    }

    private void MenuAddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedSongs().Where(s => s.Kind != TrackKind.Podcast).ToList();
        EnsureInDatabase(songs);
        foreach (var song in songs) _playlist.Add(song.Clone());
        ShowStatus(songs.Count == 1 ? "ADDED TO THE PLAYLIST" : $"ADDED {songs.Count} TO THE PLAYLIST");
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (_view is not (ListKind.Playlist or ListKind.Queue)) return;
        var list = ShownList;
        foreach (var item in SelectedSongs()) list.Remove(item); // your files are never touched
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_view == ListKind.Queue) { _queue.Clear(); ShowStatus("QUEUE CLEARED"); return; }
        if (_view != ListKind.Playlist) return;
        if (_playlist.Count > 0 && MessageBox.Show("Empty the playlist? (Saved playlists are not deleted.)", "Amp Player",
                                                   MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _playlist.Clear();
        _playlistPosition = null;
        SetComboWithoutLoading(null);
        _playlistChanged = false;
        ShowStatus("PLAYLIST CLEARED");
    }

    // Radio stations and episodes need a library number before they can be saved in a list.
    private void EnsureInDatabase(IEnumerable<PlaylistItem> items)
    {
        var missing = items.Where(i => i.TrackId == 0).ToList();
        if (missing.Count > 0) _database.UpsertTracks(missing);
    }

    private static void Renumber(IList<PlaylistItem> list)
    {
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
    }

    // ================= Shuffle and repeat =================

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        RebuildShuffle();
        UpdateShuffleRepeatLights();
        ShowStatus(_shuffle ? "SHUFFLE ON" : "SHUFFLE OFF");
        ListsChanged();
    }

    // Off → repeat all → repeat one → off
    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeat = _repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        UpdateShuffleRepeatLights();
        ShowStatus(_repeat switch { RepeatMode.All => "REPEAT ALL", RepeatMode.One => "REPEAT ONE", _ => "REPEAT OFF" });
        ListsChanged();
    }

    private void UpdateShuffleRepeatLights()
    {
        SkinProps.SetLit(ShuffleButton, _shuffle);
        SkinProps.SetLit(RepeatButton, _repeat != RepeatMode.Off);
        RepeatOneMark.Visibility = _repeat == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.ToolTip = _repeat switch { RepeatMode.All => "Repeat: all", RepeatMode.One => "Repeat: one song", _ => "Repeat: off" };
    }

    // A fresh random order, starting with the song that's playing.
    private void RebuildShuffle()
    {
        if (!_shuffle) { _shuffleOrder = new(); return; }
        var rest = _playlist.Where(p => !ReferenceEquals(p, _playlistPosition)).OrderBy(_ => Random.Next()).ToList();
        if (_playlistPosition != null && _playlist.Contains(_playlistPosition)) rest.Insert(0, _playlistPosition);
        _shuffleOrder = rest;
    }

    // ================= Adding songs (buttons and drag and drop) =================

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _view == ListKind.Queue ? "Add songs to the queue" : "Add songs to the playlist",
            Multiselect = true,
            Filter = "Audio files|*.mp3;*.flac;*.wav;*.ogg;*.aac;*.m4a;*.wma;*.aif;*.aiff|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of music", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FolderNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("UniformResourceLocatorW") ||
                  e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await AddPathsAsync(paths);
            return;
        }
        string? url = ReadDroppedUrl(e.Data);
        if (url != null) await HandleDroppedUrlAsync(url);
    }

    // A browser tab gives us its address as a "URL" or as plain text.
    private static string? ReadDroppedUrl(IDataObject data)
    {
        try
        {
            if (data.GetData("UniformResourceLocatorW") is MemoryStream stream)
            {
                string text = System.Text.Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0').Trim();
                if (text.Length > 0) return text;
            }
            if (data.GetData(DataFormats.UnicodeText) is string unicode) return unicode.Trim().Split('\n')[0].Trim();
            if (data.GetData(DataFormats.Text) is string plain) return plain.Trim().Split('\n')[0].Trim();
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// A web address was dropped (e.g. a Chrome tab). We check what it really is first.
    /// Audio and radio streams play; radio playlists are opened; podcast feeds are subscribed.
    /// Anything else gets a friendly "can't play this" message.
    /// </summary>
    private async Task HandleDroppedUrlAsync(string url)
    {
        ShowStatus("CHECKING THE ADDRESS…", 20);
        var (kind, message) = await UrlInspector.InspectAsync(url);
        RestoreStatusLine();

        switch (kind)
        {
            case DroppedUrlKind.Audio:
            case DroppedUrlKind.RadioPlaylist:
            {
                bool isRadio = kind == DroppedUrlKind.RadioPlaylist || message == "radio";
                var uri = new Uri(url);
                string name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
                var item = new PlaylistItem
                {
                    Path = url,
                    Title = string.IsNullOrWhiteSpace(name) ? uri.Host : name,
                    Artist = uri.Host,
                    Kind = isRadio ? TrackKind.Radio : TrackKind.Episode,
                    Format = isRadio ? "STREAM" : Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToUpperInvariant()
                };
                EnsureInDatabase(new[] { item });
                _playlist.Add(item);
                await PlayFromPlaylistAsync(_playlist.IndexOf(item));
                break;
            }
            case DroppedUrlKind.PodcastFeed:
                PodcastsTab.IsChecked = true;
                await SubscribeAsync(url);
                break;
            default:
                MessageBox.Show(message, "Can't play this", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    /// <summary>
    /// Adds files and folders to the end of the queue (if it's showing) or the playlist.
    /// Searching folders and reading tags runs in the background, so the window stays responsive.
    /// </summary>
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var target = _view == ListKind.Queue ? _queue : _playlist;
        var pathList = paths.ToList();
        ShowStatus("LOOKING FOR SONGS…", 60);

        List<PlaylistItem> items = await Task.Run(() =>
        {
            var found = PlaylistItem.ExpandToAudioFiles(pathList).Select(PlaylistItem.FromFile).ToList();
            _database.UpsertTracks(found);
            return found;
        });

        // A leftover search would hide the new songs, so clear it.
        if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; RefreshFilter(); }

        foreach (var item in items) target.Add(item);
        _libraryOutOfDate = true;
        ShowStatus(items.Count switch
        {
            0 => "NO SONGS FOUND THERE",
            1 => "ADDED 1 SONG",
            _ => $"ADDED {items.Count} SONGS"
        });
    }

    // ================= Saved playlists =================

    private void RefreshPlaylistCombo()
    {
        _ignoreComboChanges = true;
        var selectedId = (PlaylistCombo.SelectedItem as PlaylistInfo)?.Id;
        PlaylistCombo.ItemsSource = _database.GetPlaylists();
        if (selectedId != null) SelectPlaylistInCombo(selectedId.Value);
        _ignoreComboChanges = false;
    }

    private void SelectPlaylistInCombo(long id)
    {
        bool before = _ignoreComboChanges;
        _ignoreComboChanges = true;
        PlaylistCombo.SelectedItem = PlaylistCombo.Items.Cast<PlaylistInfo>().FirstOrDefault(p => p.Id == id);
        _ignoreComboChanges = before;
    }

    private void SetComboWithoutLoading(PlaylistInfo? info)
    {
        _ignoreComboChanges = true;
        PlaylistCombo.SelectedItem = info;
        _ignoreComboChanges = false;
    }

    private void PlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreComboChanges || PlaylistCombo.SelectedItem is not PlaylistInfo info) return;

        if (_playlistChanged && _playlist.Count > 0 &&
            MessageBox.Show($"Open \"{info.Name}\"? Changes to the current list that you haven't saved will be lost.",
                            "Amp Player", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            SetComboWithoutLoading(e.RemovedItems.Count > 0 ? e.RemovedItems[0] as PlaylistInfo : null);
            return;
        }

        _playlist.Clear();
        foreach (var item in _database.LoadPlaylistTracks(info.Id)) _playlist.Add(item);
        _playlistPosition = null;
        _playlistChanged = false;
        ShowStatus($"OPENED \"{info.Name}\"");
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistCombo.SelectedItem is PlaylistInfo info) SaveInto(info.Id, info.Name);
        else SavePlaylistAs_Click(sender, e);
    }

    private void SavePlaylistAs_Click(object sender, RoutedEventArgs e)
    {
        string? name = Dialogs.AskForName(this, "Save playlist", "Name for this playlist:",
                                          (PlaylistCombo.SelectedItem as PlaylistInfo)?.Name ?? "");
        if (name == null) return;

        var existing = _database.GetPlaylists().FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null &&
            MessageBox.Show($"A playlist called \"{existing.Name}\" already exists. Replace it?", "Amp Player",
                            MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        SaveInto(existing?.Id ?? _database.GetOrCreatePlaylist(name), existing?.Name ?? name);
    }

    private void SaveInto(long id, string name)
    {
        EnsureInDatabase(_playlist);
        _database.SavePlaylistTracks(id, _playlist);
        RefreshPlaylistCombo();
        SelectPlaylistInCombo(id);
        _playlistChanged = false;
        RememberLists();
        ShowStatus($"SAVED \"{name}\"");
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistCombo.SelectedItem is not PlaylistInfo info) return;
        if (MessageBox.Show($"Delete the saved playlist \"{info.Name}\"? Your music files are not touched, and the list on screen stays.",
                            "Amp Player", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _database.DeletePlaylist(info.Id);
        SetComboWithoutLoading(null);
        RefreshPlaylistCombo();
        _playlistChanged = true;
        ShowStatus($"DELETED \"{info.Name}\"");
    }

    // ================= Library =================

    private async Task LoadLibraryAsync()
    {
        LibraryInfo.Text = "READING…";
        var tracks = await Task.Run(() => _database.GetAllTracks());
        _library.Clear();
        foreach (var track in tracks) _library.Add(track);
        _libraryLoaded = true;
        _libraryOutOfDate = false;
        ShowLibraryInfo();
    }

    private void ShowLibraryInfo()
    {
        int folders = _database.GetLibraryFolders().Count;
        LibraryInfo.Text = $"{_library.Count:N0} SONGS · {folders} {(folders == 1 ? "FOLDER" : "FOLDERS")}";
    }

    private async void LibraryFolders_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.EditLibraryFolders(this, _database)) await RescanAsync();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync()
    {
        var progress = new Progress<string>(message => LibraryInfo.Text = message.ToUpperInvariant());
        var (added, updated, missing) = await Task.Run(() => _database.ScanLibrary(progress));
        await LoadLibraryAsync();
        ShowStatus($"LIBRARY · {added} NEW, {updated} CHANGED, {missing} MISSING", 6);
    }

    // ================= Radio =================

    private void LoadRadioFavourites()
    {
        _radioShowsFavourites = true;
        _radio.Clear();
        foreach (var station in _database.GetRadioFavorites()) _radio.Add(station);
        RadioInfo.Text = $"{_radio.Count} FAVOURITES";
        SkinProps.SetLit(FavouritesKey, true);
        SkinProps.SetLit(TopStationsKey, false);
        UpdateEmptyText();
    }

    private void RadioFavourites_Click(object sender, RoutedEventArgs e) => LoadRadioFavourites();
    private async void RadioTop_Click(object sender, RoutedEventArgs e) => await SearchRadioAsync("");

    // Searches the free Radio Browser directory (or shows the most popular stations).
    private async Task SearchRadioAsync(string text)
    {
        RadioInfo.Text = "SEARCHING…";
        try
        {
            var stations = await RadioBrowser.SearchAsync(text);
            _radioShowsFavourites = false;
            _radio.Clear();
            foreach (var station in stations) _radio.Add(station);
            RadioInfo.Text = text.Length == 0 ? "MOST POPULAR" : $"{stations.Count} FOUND";
            SkinProps.SetLit(FavouritesKey, false);
            SkinProps.SetLit(TopStationsKey, text.Length == 0);
            SearchBox.Text = "";
        }
        catch (Exception ex)
        {
            RadioInfo.Text = "NO ANSWER";
            ShowStatus(ex.Message, 8);
        }
        UpdateEmptyText();
    }

    private void MenuFavourite_Click(object sender, RoutedEventArgs e)
    {
        var stations = SelectedSongs().Where(s => s.Kind == TrackKind.Radio).ToList();
        if (_view == ListKind.Radio && _radioShowsFavourites)
        {
            foreach (var station in stations) { _database.RemoveRadioFavorite(station); _radio.Remove(station); }
            RadioInfo.Text = $"{_radio.Count} FAVOURITES";
        }
        else
        {
            foreach (var station in stations) _database.AddRadioFavorite(station);
            ShowStatus(stations.Count == 1 ? "ADDED TO FAVOURITES" : $"ADDED {stations.Count} TO FAVOURITES");
        }
    }

    // ================= Podcasts =================

    private void LoadPodcastList()
    {
        _openPodcastFeed = null;
        PodcastBackKey.Visibility = Visibility.Collapsed;
        _podcasts.Clear();
        foreach (var podcast in _database.GetPodcasts()) _podcasts.Add(podcast);
        PodcastInfo.Text = $"{_podcasts.Count} SUBSCRIBED";
        UpdateEmptyText();
    }

    private void PodcastBack_Click(object sender, RoutedEventArgs e) => LoadPodcastList();

    // Shows a podcast's episodes: the stored ones at once, then fresh ones from the internet.
    private async Task OpenPodcastAsync(string feedUrl, string title)
    {
        _openPodcastFeed = feedUrl;
        PodcastBackKey.Visibility = Visibility.Visible;
        _podcasts.Clear();
        foreach (var episode in _database.GetEpisodes(feedUrl)) _podcasts.Add(episode);
        PodcastInfo.Text = title.ToUpperInvariant();
        UpdateEmptyText();
        await RefreshPodcastAsync(feedUrl, quiet: _podcasts.Count > 0);
    }

    private async Task RefreshPodcastAsync(string feedUrl, bool quiet)
    {
        try
        {
            if (!quiet) PodcastInfo.Text = "LOADING…";
            var feed = await Podcasts.LoadFeedAsync(feedUrl);
            if (_database.IsSubscribed(feedUrl)) _database.SavePodcast(feed);
            else EnsureInDatabase(feed.Episodes);
            if (_openPodcastFeed != feedUrl) return; // you went elsewhere meanwhile

            _podcasts.Clear();
            foreach (var episode in feed.Episodes) _podcasts.Add(episode);
            PodcastInfo.Text = $"{feed.Title.ToUpperInvariant()} · {feed.Episodes.Count} EPISODES";
        }
        catch (Exception ex)
        {
            ShowStatus($"COULDN'T REFRESH ({Web.Explain(ex)})", 8);
        }
        UpdateEmptyText();
    }

    private async void PodcastRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_openPodcastFeed != null) { await RefreshPodcastAsync(_openPodcastFeed, quiet: false); return; }

        // On the subscriptions list: refresh them all.
        var feeds = _database.GetPodcasts();
        int done = 0;
        foreach (var podcast in feeds)
        {
            PodcastInfo.Text = $"REFRESHING {++done} OF {feeds.Count}…";
            try { _database.SavePodcast(await Podcasts.LoadFeedAsync(podcast.Path)); }
            catch (Exception) { /* skip feeds that don't answer */ }
        }
        LoadPodcastList();
    }

    private async void PodcastAddFeed_Click(object sender, RoutedEventArgs e)
    {
        string? url = Dialogs.AskForName(this, "Add a podcast", "Paste the podcast's feed address (RSS):");
        if (url != null) await SubscribeAsync(url);
    }

    private async Task SubscribeAsync(string feedUrl)
    {
        ShowStatus("READING THE FEED…", 20);
        try
        {
            var feed = await Podcasts.LoadFeedAsync(feedUrl);
            _database.SavePodcast(feed);
            ShowStatus($"SUBSCRIBED TO \"{feed.Title}\"");
            if (_view != ListKind.Podcasts) PodcastsTab.IsChecked = true;
            await OpenPodcastAsync(feed.FeedUrl, feed.Title);
        }
        catch (Exception ex)
        {
            ShowStatus($"COULDN'T SUBSCRIBE ({Web.Explain(ex)})", 8);
        }
    }

    // Enter in Search on the Podcasts list: a feed address subscribes; words search Podcast Index.
    private async Task SearchPodcastsAsync(string text)
    {
        if (text.Length == 0) { LoadPodcastList(); return; }
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SearchBox.Text = "";
            await SubscribeAsync(text);
            return;
        }

        PodcastInfo.Text = "SEARCHING…";
        try
        {
            var results = await Podcasts.SearchAsync(text, _settings.PodcastIndexKey, _settings.PodcastIndexSecret);
            _openPodcastFeed = null;
            PodcastBackKey.Visibility = Visibility.Visible;
            _podcasts.Clear();
            foreach (var podcast in results) _podcasts.Add(podcast);
            PodcastInfo.Text = $"{results.Count} FOUND · DOUBLE-CLICK TO OPEN";
            SearchBox.Text = "";
        }
        catch (Exception ex)
        {
            PodcastInfo.Text = "";
            MessageBox.Show(ex is InvalidOperationException ? ex.Message : $"Podcast search failed: {Web.Explain(ex)}",
                            "Podcasts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        UpdateEmptyText();
    }

    private async void MenuSubscribe_Click(object sender, RoutedEventArgs e)
    {
        var feed = SelectedSongs().FirstOrDefault(s => s.Kind == TrackKind.Podcast);
        if (feed == null) return;
        if (_database.IsSubscribed(feed.Path))
        {
            if (MessageBox.Show($"Unsubscribe from \"{feed.Title}\"?", "Podcasts", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _database.Unsubscribe(feed.Path);
            if (_openPodcastFeed == null) LoadPodcastList();
            ShowStatus("UNSUBSCRIBED");
        }
        else await SubscribeAsync(feed.Path);
    }

    private async void MenuOpenPodcast_Click(object sender, RoutedEventArgs e)
    {
        var feed = SelectedSongs().FirstOrDefault(s => s.Kind == TrackKind.Podcast);
        if (feed != null) await OpenPodcastAsync(feed.Path, feed.Title);
    }
}
