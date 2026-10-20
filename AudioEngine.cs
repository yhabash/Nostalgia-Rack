using System.Runtime.InteropServices;
using System.Text;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;

namespace AmpPlayer;

/// <summary>How the sound card should be used (from the Advanced settings).</summary>
public sealed record OutputOptions(
    string DeviceId,        // "" = whatever Windows uses as the default
    bool Exclusive,         // true = only Amp Player uses the device (bit-perfect)
    int SampleRate,         // 0 = automatic (exclusive mode only; shared uses Windows' rate)
    int BufferMs);          // 0 = automatic

/// <summary>One output device for the settings list.</summary>
public sealed record OutputDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>
/// The sound engine. How the sound flows:
///
///   song file / radio ──► fade ──┐
///                                ├─► MIXER ──► preamp ─► EQ ─► meter/volume/soft clip ──► WASAPI ──► speakers
///   next song (crossfade) ► fade ┘
///
/// - Every song is opened as a "decoding" stream and added to one mixer. The mixer never
///   stops, so the next song can be added at the exact moment the last one ends: gapless.
///   For a crossfade, the next song is added a few seconds early while both fade.
/// - The EQ and output stage sit on the mixer, so they keep working from song to song.
/// - WASAPI is Windows' modern sound system. Shared mode mixes with other apps; exclusive
///   mode hands the sound card to Amp Player alone (the purest, "bit-perfect" path).
///
/// Timing: BASS calls the "sync" methods below on its own audio thread, at the exact moment
/// in the sound. They must be quick, so anything slow is sent away to another thread, and
/// events tell the window, which hands them to the screen thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    // ---------------- One playing song ----------------

    private sealed class Source
    {
        public required int Handle;
        public required PlaylistItem Item;
        public required Fader Fader;
        public double LengthSeconds;
        public bool IsLive => Item.Kind == TrackKind.Radio;
    }

    /// <summary>
    /// Fades one song in or out, sample by sample, so crossfades are perfectly smooth.
    /// It runs as a tiny DSP on the song's own stream, before the mixer.
    /// </summary>
    private sealed class Fader
    {
        private readonly DSPProcedure _procedure;
        private float[] _scratch = Array.Empty<float>();
        private readonly int _channels;
        private readonly int _sampleRate;
        private float _gain = 1f, _target = 1f, _stepPerFrame;

        public Fader(int handle)
        {
            var info = Bass.ChannelGetInfo(handle);
            _channels = Math.Max(1, info.Channels);
            _sampleRate = Math.Max(1, info.Frequency);
            _procedure = Process;
            Bass.ChannelSetDSP(handle, _procedure, IntPtr.Zero, 10);
        }

        public void SetGain(float gain) { lock (this) { _gain = _target = gain; _stepPerFrame = 0; } }

        public void FadeTo(float target, double seconds)
        {
            lock (this)
            {
                _target = target;
                double frames = Math.Max(1, seconds * _sampleRate);
                _stepPerFrame = (float)((target - _gain) / frames);
            }
        }

        private void Process(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            float gain, target, step;
            lock (this) { gain = _gain; target = _target; step = _stepPerFrame; }
            if (gain == 1f && target == 1f) return; // not fading: leave the sound alone

            int count = length / sizeof(float);
            if (count == 0) return;
            if (_scratch.Length < count) _scratch = new float[count];
            Marshal.Copy(buffer, _scratch, 0, count);

            for (int i = 0; i < count; i += _channels)
            {
                if (step != 0)
                {
                    gain += step;
                    if ((step > 0 && gain >= target) || (step < 0 && gain <= target)) { gain = target; step = 0; }
                }
                for (int c = 0; c < _channels && i + c < count; c++)
                    _scratch[i + c] *= gain;
            }

            Marshal.Copy(_scratch, 0, buffer, count);
            lock (this) { _gain = gain; if (step == 0) _stepPerFrame = 0; }
        }
    }

    // ---------------- State ----------------

    private readonly object _lock = new();
    private int _mixer;
    private int _rate = 48000, _channels = 2;
    private Source? _current;     // the song you hear (or the one fading in)
    private Source? _fadingOut;   // during a crossfade: the song fading out
    private Source? _prepared;    // the next song, opened and waiting
    private bool _paused;
    private bool _outputRunning;
    private OutputOptions _options = new("", false, 0, 0);
    private int _wasapiDevice = -1;

    // BASS keeps pointers to these methods, so we keep them in fields (so .NET never throws them away).
    private readonly WasapiProcedure _outputProcedure;
    private readonly WasapiNotifyProcedure _notifyProcedure;
    private readonly SyncProcedure _endSync, _crossfadeSync, _metaSync, _stallSync;

    public AudioEngine()
    {
        _outputProcedure = FeedSpeakers;
        _notifyProcedure = OnDeviceNotification;
        _endSync = OnSongEnd;
        _crossfadeSync = OnCrossfadePoint;
        _metaSync = OnStreamTitle;
        _stallSync = OnStall;
    }

    /// <summary>The 10-band EQ and preamp (Equalizer.cs).</summary>
    public Equalizer Equalizer { get; } = new();

    /// <summary>Meter, volume, balance and soft clipper (OutputStage.cs).</summary>
    public OutputStage Output { get; } = new();

    /// <summary>Crossfade length in seconds. 0 = gapless (songs follow each other with no gap).</summary>
    public double CrossfadeSeconds { get; set; }

    // ---------------- Events (raised on the audio thread!) ----------------

    /// <summary>The prepared next song has started (gapless or crossfade).</summary>
    public event Action<PlaylistItem>? AdvancedTo;

    /// <summary>The song ended and nothing was prepared: the window should decide what's next.</summary>
    public event Action? Finished;

    /// <summary>A radio stream stopped unexpectedly (the window can reconnect).</summary>
    public event Action<PlaylistItem>? StreamDropped;

    /// <summary>A radio station sent a new "now playing" title.</summary>
    public event Action<string>? StreamTitleChanged;

    /// <summary>A radio stream ran out of data (true) or got going again (false).</summary>
    public event Action<bool>? Buffering;

    /// <summary>The sound device went away or changed. The text explains what happened.</summary>
    public event Action<string>? DeviceProblem;

    // ---------------- Starting up ----------------

    /// <summary>
    /// Starts BASS (with "no sound", because WASAPI does the sound output) and the mixer.
    /// Returns an error message, or null when everything worked.
    /// </summary>
    public string? Start(OutputOptions options, int resamplerQuality, int netBufferMs, int netPreBufferPercent)
    {
        try
        {
            ApplyTuning(resamplerQuality, netBufferMs, netPreBufferPercent);
            Bass.NetAgent = "AmpPlayer/0.6";
            Bass.NetPlaylist = 1; // let BASS follow .pls/.m3u links inside streams too

            if (!Bass.Init(Bass.NoSoundDevice))
                return $"Could not start the audio engine ({Bass.LastError}).";
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return MissingDll("bass.dll", "bass24.zip");
        }

        try { _ = ManagedBass.Fx.BassFx.Version; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        { return MissingDll("bass_fx.dll", "bass_fx24.zip"); }

        try { _ = BassMix.Version; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        { return MissingDll("bassmix.dll", "bassmix24.zip"); }

        try { _ = BassWasapi.Version; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        { return MissingDll("basswasapi.dll", "basswasapi24.zip"); }

        BassWasapi.SetNotify(_notifyProcedure);
        return OpenOutput(options);
    }

    private static string MissingDll(string dll, string zip) =>
        $"{dll} is missing or is the 32-bit version.\n\n" +
        $"Copy {dll} from the x64 folder of {zip} (from un4seen.com) into the project folder, " +
        "then choose Build → Rebuild Solution.";

    /// <summary>Resampler quality (0–4) and internet radio buffering, from Advanced settings.</summary>
    public static void ApplyTuning(int resamplerQuality, int netBufferMs, int netPreBufferPercent)
    {
        Bass.SRCQuality = Math.Clamp(resamplerQuality, 0, 4);
        Bass.NetBufferLength = Math.Clamp(netBufferMs, 1000, 30000);
        Bass.NetPreBuffer = Math.Clamp(netPreBufferPercent, 0, 100);
    }

    /// <summary>All the output devices Windows knows about (speakers, headphones, USB DACs…).</summary>
    public static List<OutputDevice> GetOutputDevices()
    {
        var result = new List<OutputDevice>();
        try
        {
            for (int i = 0; BassWasapi.GetDeviceInfo(i, out var info); i++)
            {
                if (info.IsInput || info.IsLoopback || !info.IsEnabled) continue;
                result.Add(new OutputDevice(info.ID ?? "", info.Name ?? $"Device {i}", info.IsDefault));
            }
        }
        catch (Exception) { /* basswasapi.dll missing: no list */ }
        return result;
    }

    /// <summary>
    /// Opens (or re-opens) the sound device. If exclusive mode is refused (another app is
    /// using the device that way), it falls back to shared mode and says so.
    /// Returns a message for the user, or null when it all went as asked.
    /// </summary>
    public string? OpenOutput(OutputOptions options)
    {
        _options = options;
        string? message = null;

        // Free the old output first, without holding our lock: the audio thread may be
        // waiting for that lock inside a sync, and Free waits for the audio thread.
        BassWasapi.Free();

        lock (_lock)
        {
            _outputRunning = false;

            _wasapiDevice = FindDevice(options.DeviceId);
            if (_wasapiDevice == -1 && options.DeviceId.Length > 0)
                message = "The chosen output device isn't available. Using the Windows default.";

            float buffer = options.BufferMs > 0 ? options.BufferMs / 1000f : 0f;
            var baseFlags = WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat;

            bool ok = false;
            if (options.Exclusive)
            {
                ok = BassWasapi.Init(_wasapiDevice, options.SampleRate, 2, baseFlags | WasapiInitFlags.Exclusive,
                                     buffer, 0, _outputProcedure);
                if (!ok)
                    message = $"Exclusive mode isn't available right now ({Bass.LastError}), " +
                              "probably because another app is using the device that way. Using shared mode.";
            }
            if (!ok)
                ok = BassWasapi.Init(_wasapiDevice, 0, 2, baseFlags | WasapiInitFlags.Shared, buffer, 0, _outputProcedure);
            if (!ok)
                return $"Could not open the sound device ({Bass.LastError}).";

            BassWasapi.GetInfo(out var info);
            if (_mixer == 0 || info.Frequency != _rate || info.Channels != _channels)
                RebuildMixer(info.Frequency, info.Channels);

            if (!_paused) StartOutput();
        }
        return message;
    }

    // Looks up a device by its Windows ID; -1 = the Windows default.
    private static int FindDevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; BassWasapi.GetDeviceInfo(i, out var info); i++)
            if (!info.IsInput && !info.IsLoopback && info.IsEnabled && info.ID == id) return i;
        return -1;
    }

    // Creates the mixer at the device's sample rate and moves any playing songs onto it.
    // Songs at other sample rates are converted by the mixer with the best-quality resampler.
    private void RebuildMixer(int rate, int channels)
    {
        _rate = rate;
        _channels = channels;

        int oldMixer = _mixer;
        _mixer = BassMix.CreateMixerStream(rate, channels, BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);

        Equalizer.Attach(_mixer);
        Output.Attach(_mixer);

        foreach (var source in new[] { _fadingOut, _current })
        {
            if (source == null) continue;
            if (oldMixer != 0) BassMix.MixerRemoveChannel(source.Handle);
            AddToMixer(source);
        }

        if (oldMixer != 0) Bass.StreamFree(oldMixer);
    }

    private void StartOutput()
    {
        if (_outputRunning) return;
        _outputRunning = BassWasapi.Start();
    }

    // WASAPI calls this when the speakers need more sound: we hand over the mixer's output.
    private int FeedSpeakers(IntPtr buffer, int length, IntPtr user)
    {
        int mixer = _mixer;
        if (mixer == 0) return 0;
        int got = Bass.ChannelGetData(mixer, buffer, length);
        return got < 0 ? 0 : got;
    }

    // Windows says a device changed: unplugged, disabled, or a new default was chosen.
    private void OnDeviceNotification(WasapiNotificationType notify, int device, IntPtr user)
    {
        bool ours = device == _wasapiDevice || (_wasapiDevice == -1 && notify == WasapiNotificationType.DefaultOutput);
        if (notify == WasapiNotificationType.Disabled && device == BassWasapiDeviceInUse())
            DeviceProblem?.Invoke("unplugged");
        else if (notify == WasapiNotificationType.DefaultOutput && _wasapiDevice == -1)
            DeviceProblem?.Invoke("default-changed");
        else if (notify == WasapiNotificationType.Fail && ours)
            DeviceProblem?.Invoke("failed");
    }

    private int BassWasapiDeviceInUse()
    {
        try { return BassWasapi.CurrentDevice; } catch { return _wasapiDevice; }
    }

    // ---------------- Playing ----------------

    /// <summary>True while a song is loaded (playing or paused).</summary>
    public bool IsLoaded { get { lock (_lock) return _current != null; } }

    /// <summary>True while a song is loaded and not paused.</summary>
    public bool IsPlaying { get { lock (_lock) return _current != null && !_paused; } }

    public bool IsPaused => _paused;

    /// <summary>The song you hear now.</summary>
    public PlaylistItem? CurrentItem { get { lock (_lock) return _current?.Item; } }

    /// <summary>The prepared next song (if any).</summary>
    public PlaylistItem? PreparedItem { get { lock (_lock) return _prepared?.Item; } }

    /// <summary>
    /// Opens a song and starts it now, stopping whatever was playing.
    /// Internet streams can take a few seconds to connect, so this runs in the background.
    /// Returns an error message, or null when it started.
    /// </summary>
    public async Task<string?> PlayAsync(PlaylistItem item)
    {
        var (source, error) = await Task.Run(() => Open(item));
        if (source == null) return error;

        List<Source> old;
        lock (_lock)
        {
            old = TakeAll();
            _current = source;
            _paused = false;
        }
        RemoveAndFree(old);
        AddToMixer(source);
        StartOutput();
        return null;
    }

    /// <summary>
    /// Opens the song that should come next, so it can start without a gap (or crossfade in).
    /// Passing null (or a live radio station) cancels the prepared song.
    /// </summary>
    public async Task PrepareNextAsync(PlaylistItem? item)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_prepared?.Item, item)) return;
            FreeLater(_prepared);
            _prepared = null;
        }
        if (item == null || item.Kind == TrackKind.Radio) return;

        var (source, _) = await Task.Run(() => Open(item));
        if (source == null) return; // it will fail again (and be skipped) when its turn comes

        lock (_lock)
        {
            if (_prepared != null) FreeLater(_prepared);
            _prepared = source;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_current == null || _paused) return;
            _paused = true;
            _outputRunning = false;
        }
        BassWasapi.Stop(false); // false = keep what's in the buffer, so resume is seamless
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_current == null) return;
            _paused = false;
        }
        StartOutput();
    }

    /// <summary>Stops and goes back to the start of the song (stays loaded).</summary>
    public void Stop()
    {
        Source? current, fading;
        lock (_lock)
        {
            if (_current == null) return;
            current = _current;
            fading = TakeFadingOut();
            _paused = true;
            _outputRunning = false;
        }
        BassWasapi.Stop(true);
        if (fading != null) RemoveAndFree(new() { fading });
        current.Fader.SetGain(1f);
        BassMix.ChannelSetPosition(current.Handle, 0);
    }

    /// <summary>Stops and releases everything.</summary>
    public void Unload()
    {
        List<Source> old;
        lock (_lock)
        {
            old = TakeAll();
            if (_prepared != null) old.Add(_prepared);
            _prepared = null;
        }
        RemoveAndFree(old);
    }

    public double PositionSeconds
    {
        get
        {
            int handle = CurrentHandle;
            if (handle == 0) return 0;
            long bytes = BassMix.ChannelGetPosition(handle);
            return bytes < 0 ? 0 : Bass.ChannelBytes2Seconds(handle, bytes);
        }
    }

    public double LengthSeconds { get { lock (_lock) return _current?.LengthSeconds ?? 0; } }

    /// <summary>Jumps to a time in the song (not possible on live radio).</summary>
    public void SeekTo(double seconds)
    {
        Source? current, fading;
        lock (_lock)
        {
            if (_current == null || _current.IsLive || _current.LengthSeconds <= 0) return;
            current = _current;
            fading = TakeFadingOut();
        }
        if (fading != null) RemoveAndFree(new() { fading });
        current.Fader.SetGain(1f);
        double target = Math.Clamp(seconds, 0, Math.Max(0, current.LengthSeconds - 0.5));
        BassMix.ChannelSetPosition(current.Handle, Bass.ChannelSeconds2Bytes(current.Handle, target));
    }

    public void Skip(double seconds) => SeekTo(PositionSeconds + seconds);

    /// <summary>How full a radio stream's buffer is, 0 to 100 (or null for files).</summary>
    public int? BufferPercent
    {
        get
        {
            Source? current;
            lock (_lock) current = _current;
            if (current == null || !current.Item.IsOnline) return null;
            long buffered = Bass.StreamGetFilePosition(current.Handle, FileStreamPosition.Buffer);
            long total = Bass.StreamGetFilePosition(current.Handle, FileStreamPosition.End);
            if (buffered < 0 || total <= 0) return null;
            return (int)Math.Clamp(buffered * 100 / total, 0, 100);
        }
    }

    /// <summary>The format of what's playing, e.g. "FLAC · 44.1 kHz · 16-BIT".</summary>
    public string DescribeCurrent()
    {
        Source? current;
        lock (_lock) current = _current;
        {
            if (current == null) return "";
            var info = Bass.ChannelGetInfo(current.Handle);
            string kind = current.Item.Format.Split(' ').FirstOrDefault() ?? "";
            string bits = info.OriginalResolution > 0 ? $" · {info.OriginalResolution}-BIT" : "";
            return $"{kind} · {info.Frequency / 1000.0:0.#} kHz{bits}".Trim(' ', '·');
        }
    }

    // ---------------- Opening songs ----------------

    private (Source? source, string error) Open(PlaylistItem item)
    {
        var flags = BassFlags.Decode | BassFlags.Float;
        int handle;

        if (item.IsOnline)
        {
            // Radio is streamed in small blocks (it never ends); episodes are downloaded so you can seek.
            if (item.Kind == TrackKind.Radio) flags |= BassFlags.StreamDownloadBlocks;
            handle = Bass.CreateStream(item.Path, 0, flags | BassFlags.StreamStatus, null, IntPtr.Zero);
        }
        else
        {
            // "Prescan" reads the whole MP3 once, so seeking is exact even without a seek table.
            if (item.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) flags |= BassFlags.Prescan;
            handle = Bass.CreateStream(item.Path, 0, 0, flags);
        }

        if (handle == 0) return (null, DescribeError(Bass.LastError));

        long lengthBytes = Bass.ChannelGetLength(handle);
        var source = new Source
        {
            Handle = handle,
            Item = item,
            Fader = new Fader(handle),
            LengthSeconds = lengthBytes > 0 ? Bass.ChannelBytes2Seconds(handle, lengthBytes) : 0
        };

        if (item.IsOnline)
        {
            // Radio: be told when the title changes or the stream stalls.
            Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0, _metaSync);
            Bass.ChannelSetSync(handle, SyncFlags.OggChange, 0, _metaSync);
            Bass.ChannelSetSync(handle, SyncFlags.Stalled, 0, _stallSync);
        }
        return (source, "");
    }

    private static string DescribeError(Errors error) => error switch
    {
        Errors.FileOpen => "file not found or can't be opened",
        Errors.FileFormat => "format not supported",
        Errors.Codec => "codec not available",
        Errors.NoInternet => "no internet connection",
        Errors.Timeout => "the server didn't answer",
        _ => error.ToString()
    };

    // Adds a song to the mixer and asks to be told when it ends (and when to start a crossfade).
    private void AddToMixer(Source source)
    {
        BassMix.MixerAddChannel(_mixer, source.Handle, BassFlags.MixerChanNoRampin | BassFlags.MixerChanDownMix);
        BassMix.ChannelSetSync(source.Handle, SyncFlags.End | SyncFlags.Mixtime, 0, _endSync, (IntPtr)source.Handle);

        double fade = CrossfadeSeconds;
        if (fade > 0 && source.LengthSeconds > fade * 2 + 1)
        {
            long at = Bass.ChannelSeconds2Bytes(source.Handle, source.LengthSeconds - fade);
            BassMix.ChannelSetSync(source.Handle, SyncFlags.Position | SyncFlags.Mixtime, at, _crossfadeSync, (IntPtr)source.Handle);
        }
    }

    // ---------------- Moving to the next song (audio thread) ----------------

    // A song reached its end.
    private void OnSongEnd(int sync, int channel, int data, IntPtr user)
    {
        int handle = (int)user;
        Source? ended = null, started = null;
        bool finished = false, dropped = false;

        lock (_lock)
        {
            if (_fadingOut?.Handle == handle)
            {
                ended = _fadingOut; // the crossfade is over
                _fadingOut = null;
            }
            else if (_current?.Handle == handle)
            {
                ended = _current;
                if (_current.IsLive)
                {
                    dropped = true; // radio should never end: the connection was lost
                    _current = null;
                }
                else if (_prepared != null)
                {
                    // Gapless: the next song starts in the very same instant.
                    started = _prepared;
                    _prepared = null;
                    started.Fader.SetGain(1f);
                    _current = started;
                    AddToMixer(started);
                }
                else
                {
                    _current = null;
                    finished = true;
                }
            }
        }

        FreeLater(ended); // freeing a stream also takes it out of the mixer
        if (started != null) Raise(() => AdvancedTo?.Invoke(started.Item));
        if (dropped && ended != null) Raise(() => StreamDropped?.Invoke(ended.Item));
        if (finished) Raise(() => Finished?.Invoke());
    }

    // A song reached "length minus crossfade": start the next one fading in, this one fading out.
    private void OnCrossfadePoint(int sync, int channel, int data, IntPtr user)
    {
        int handle = (int)user;
        Source? started = null;

        lock (_lock)
        {
            if (_current?.Handle != handle || _prepared == null || _fadingOut != null) return;
            if (!CrossfadeAllowed(_current.Item, _prepared.Item)) return; // gapless will happen at the end

            double fade = CrossfadeSeconds;
            started = _prepared;
            _prepared = null;

            started.Fader.SetGain(0f);
            started.Fader.FadeTo(1f, fade);
            _current.Fader.FadeTo(0f, fade);

            _fadingOut = _current;
            _current = started;
            AddToMixer(started);
        }

        Raise(() => AdvancedTo?.Invoke(started.Item));
    }

    /// <summary>
    /// No crossfade between two songs of the same album (live albums, classical, DJ mixes are
    /// meant to flow into each other), and none for radio.
    /// </summary>
    public static bool CrossfadeAllowed(PlaylistItem from, PlaylistItem to)
    {
        if (from.Kind == TrackKind.Radio || to.Kind == TrackKind.Radio) return false;
        bool sameAlbum = from.Album.Length > 0 &&
                         string.Equals(from.Album, to.Album, StringComparison.CurrentCultureIgnoreCase);
        return !sameAlbum;
    }

    // Helpers that only change our own fields (call them inside the lock). The BASS work
    // happens afterwards in RemoveAndFree, outside the lock.
    private Source? TakeFadingOut()
    {
        var fading = _fadingOut;
        _fadingOut = null;
        return fading;
    }

    private List<Source> TakeAll()
    {
        var all = new List<Source>();
        if (_fadingOut != null) all.Add(_fadingOut);
        if (_current != null) all.Add(_current);
        _fadingOut = null;
        _current = null;
        return all;
    }

    private static void RemoveAndFree(List<Source> sources)
    {
        foreach (var source in sources)
        {
            BassMix.MixerRemoveChannel(source.Handle);
            FreeLater(source);
        }
    }

    private int CurrentHandle { get { lock (_lock) return _current?.Handle ?? 0; } }

    // Streams are freed on a worker thread, never inside BASS's own callbacks.
    private static void FreeLater(Source? source)
    {
        if (source == null) return;
        int handle = source.Handle;
        ThreadPool.QueueUserWorkItem(_ => Bass.StreamFree(handle));
    }

    private static void Raise(Action action) => ThreadPool.QueueUserWorkItem(_ => action());

    // ---------------- Radio titles and buffering ----------------

    private void OnStreamTitle(int sync, int channel, int data, IntPtr user)
    {
        string? title = ReadStreamTitle(channel);
        if (!string.IsNullOrWhiteSpace(title)) Raise(() => StreamTitleChanged?.Invoke(title));
    }

    private void OnStall(int sync, int channel, int data, IntPtr user)
    {
        bool stalled = data == 0; // 0 = ran out of data, 1 = playing again
        Raise(() => Buffering?.Invoke(stalled));
    }

    /// <summary>The station's current title ("Artist - Song"), from Shoutcast/Icecast or Ogg tags.</summary>
    public string? CurrentStreamTitle()
    {
        int handle = CurrentHandle;
        return handle == 0 ? null : ReadStreamTitle(handle);
    }

    private static string? ReadStreamTitle(int handle)
    {
        // Shoutcast / Icecast: StreamTitle='Artist - Song';StreamUrl='…';
        IntPtr meta = Bass.ChannelGetTags(handle, TagType.META);
        if (meta != IntPtr.Zero)
        {
            string text = DecodeTagText(meta);
            const string key = "StreamTitle='";
            int start = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += key.Length;
                int end = text.IndexOf("';", start, StringComparison.Ordinal);
                if (end < 0) end = text.LastIndexOf('\'');
                if (end > start) return text[start..end].Trim();
            }
        }

        // Ogg/Opus streams: TITLE= and ARTIST= tags.
        IntPtr ogg = Bass.ChannelGetTags(handle, TagType.OGG);
        if (ogg != IntPtr.Zero)
        {
            var tags = Extensions.ExtractMultiStringUtf8(ogg);
            string? Get(string name) => tags?.FirstOrDefault(t => t.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
            string? artist = Get("ARTIST"), title = Get("TITLE");
            if (!string.IsNullOrEmpty(title)) return string.IsNullOrEmpty(artist) ? title : $"{artist} - {title}";
        }
        return null;
    }

    /// <summary>
    /// Reads a tag text from BASS. Stations send it in different text encodings: we try
    /// UTF-8 first, and if that's not valid, fall back to Latin-1 (Western European).
    /// </summary>
    private static string DecodeTagText(IntPtr pointer)
    {
        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0 && length < 16384) length++;
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);

        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    // ---------------- Shutting down ----------------

    public void Dispose()
    {
        try
        {
            BassWasapi.Free();
            lock (_lock)
            {
                foreach (var source in new[] { _fadingOut, _current, _prepared })
                    if (source != null) Bass.StreamFree(source.Handle);
                _fadingOut = _current = _prepared = null;
                if (_mixer != 0) Bass.StreamFree(_mixer);
                _mixer = 0;
            }
            Bass.Free();
        }
        catch (Exception) { /* closing anyway */ }
    }
}
