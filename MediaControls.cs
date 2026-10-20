namespace NostalgiaRack;

/// <summary>
/// Connects Nostalgia Rack to Windows' own media controls (SMTC): the play/pause/next keys on
/// your keyboard and headphones, and the little media panel Windows shows next to the
/// volume pop-up, with the song title. Because Windows routes the keys to whichever app
/// last played, this also stops browsers or other players from "stealing" the media keys.
///
/// Technique: a desktop app gets the controls through a Windows MediaPlayer object that we
/// never use for sound (its own automatic handling is switched off).
/// </summary>
public sealed class MediaControls : IDisposable
{
    private readonly global::Windows.Media.Playback.MediaPlayer? _player;
    private readonly global::Windows.Media.SystemMediaTransportControls? _controls;

    /// <summary>Raised when a media key is pressed (on a background thread).</summary>
    public event Action<string>? ButtonPressed; // "play", "pause", "next", "previous", "stop"

    public MediaControls()
    {
        try
        {
            _player = new global::Windows.Media.Playback.MediaPlayer();
            _player.CommandManager.IsEnabled = false; // we handle the buttons ourselves
            _controls = _player.SystemMediaTransportControls;
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.ButtonPressed += (_, args) =>
            {
                string? name = args.Button switch
                {
                    global::Windows.Media.SystemMediaTransportControlsButton.Play => "play",
                    global::Windows.Media.SystemMediaTransportControlsButton.Pause => "pause",
                    global::Windows.Media.SystemMediaTransportControlsButton.Next => "next",
                    global::Windows.Media.SystemMediaTransportControlsButton.Previous => "previous",
                    global::Windows.Media.SystemMediaTransportControlsButton.Stop => "stop",
                    _ => null
                };
                if (name != null) ButtonPressed?.Invoke(name);
            };
        }
        catch (Exception)
        {
            // Very old Windows or no media support: the player still works, just without these.
            _controls = null;
        }
    }

    /// <summary>Shows what's playing in Windows' media panel.</summary>
    public void ShowSong(string title, string artist, string album)
    {
        if (_controls == null) return;
        try
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = global::Windows.Media.MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            updater.Update();
        }
        catch (Exception) { }
    }

    public void SetState(bool playing, bool loaded)
    {
        if (_controls == null) return;
        try
        {
            _controls.PlaybackStatus = !loaded
                ? global::Windows.Media.MediaPlaybackStatus.Stopped
                : playing ? global::Windows.Media.MediaPlaybackStatus.Playing
                          : global::Windows.Media.MediaPlaybackStatus.Paused;
        }
        catch (Exception) { }
    }

    /// <summary>Updates the progress bar in Windows' media panel.</summary>
    public void SetTimeline(double positionSeconds, double lengthSeconds)
    {
        if (_controls == null || lengthSeconds <= 0) return;
        try
        {
            _controls.UpdateTimelineProperties(new global::Windows.Media.SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, lengthSeconds)),
                MaxSeekTime = TimeSpan.FromSeconds(lengthSeconds),
                EndTime = TimeSpan.FromSeconds(lengthSeconds)
            });
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        try { _player?.Dispose(); } catch (Exception) { }
    }
}
