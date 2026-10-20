using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Fx;

namespace NostalgiaRack;

/// <summary>
/// A 10-band graphic equalizer plus a preamp, built on the BASS_FX add-on.
///
/// How it works:
/// - BASS plays a song as a "channel". We attach two things to that channel:
///   1. the preamp: our own small piece of code that multiplies every sample (runs first)
///   2. a Peaking EQ effect from BASS_FX with 10 bands, one per slider
/// - Every slider just changes the gain (in dB) of one band.
/// - When a new song is opened it is a new channel, so we attach everything again
///   with the same settings. That is what Attach() does.
/// - Boosts are never compensated by turning the music down. Anything that would go
///   over the maximum level is caught by the soft clipper in OutputStage.cs.
/// </summary>
public sealed class Equalizer
{
    // The centre frequency of each slider, in Hz.
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    // Sliders go from -12 dB to +12 dB.
    public const float MaxGainDb = 12f;

    // Each band is one octave wide, matching the one-octave spacing of the sliders.
    private const float BandwidthOctaves = 1f;

    private readonly float[] _gainsDb = new float[Frequencies.Length];
    private float _preampDb;

    private int _channel;          // the song BASS is playing (0 = none)
    private int _sampleRate = 44100;
    private int _channelCount = 2;
    private int _eqEffect;         // handle of the 10-band EQ effect (0 = not attached)
    private int _preampDsp;        // handle of our preamp code (0 = not attached)

    // The level the preamp should be at, and the level it is at right now.
    // The audio thread glides smoothly from one to the other, so changes never click.
    private volatile float _targetFactor = 1f;
    private float _currentFactor = 1f;

    // BASS calls this method on its audio thread with each small block of sound.
    // We keep it in a field so .NET never throws it away while BASS still uses it.
    private readonly DSPProcedure _preampProcedure;
    private float[] _scratch = Array.Empty<float>();

    public Equalizer()
    {
        _preampProcedure = ApplyPreampToBlock;
    }

    /// <summary>True when the EQ is switched on.</summary>
    public bool Enabled { get; private set; } = true;

    /// <summary>True when the EQ is on and boosts something, so peaks could go over the ceiling.</summary>
    public bool IsBoosting => Enabled && (_preampDb > 0 || _gainsDb.Any(g => g > 0));

    /// <summary>The current band gains and preamp in dB (for saving your settings).</summary>
    public float[] Gains => (float[])_gainsDb.Clone();
    public float PreampDb => _preampDb;

    /// <summary>
    /// Connects the EQ to the mixer, where all songs flow through (or to nothing, with 0).
    /// Because it sits on the mixer, it is attached once and keeps working from song to song.
    /// </summary>
    public void Attach(int channel)
    {
        if (_channel != 0) RemoveEffects();
        _channel = channel;
        _eqEffect = 0;
        _preampDsp = 0;
        _currentFactor = _targetFactor;

        if (_channel != 0)
        {
            var info = Bass.ChannelGetInfo(_channel);
            _sampleRate = info.Frequency > 0 ? info.Frequency : 44100;
            _channelCount = Math.Max(1, info.Channels);
        }

        if (Enabled && _channel != 0)
            AddEffects();
    }

    /// <summary>Switches the whole EQ on or off. Off = the music is untouched.</summary>
    public void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        if (_channel == 0) return;

        if (on) AddEffects();
        else RemoveEffects();
    }

    /// <summary>Sets one band (0 = 31 Hz … 9 = 16 kHz) to a gain in dB.</summary>
    public void SetBand(int band, float gainDb)
    {
        _gainsDb[band] = Math.Clamp(gainDb, -MaxGainDb, MaxGainDb);
        ApplyBand(band);
    }

    /// <summary>Sets the preamp in dB.</summary>
    public void SetPreamp(float gainDb)
    {
        _preampDb = Math.Clamp(gainDb, -MaxGainDb, MaxGainDb);
        _targetFactor = (float)Math.Pow(10, _preampDb / 20.0); // dB to a plain multiplier
    }

    // A band can't sit near or above half the song's sample rate (e.g. 16 kHz on a 22 kHz file).
    private bool BandIsUsable(int band) => Frequencies[band] < _sampleRate * 0.45f;

    // ---- Everything below talks to BASS ----

    private void AddEffects()
    {
        // Higher priority runs first: preamp (2), then EQ (1). The output stage runs last (0).
        _preampDsp = Bass.ChannelSetDSP(_channel, _preampProcedure, IntPtr.Zero, 2);
        _eqEffect = Bass.ChannelSetFX(_channel, EffectType.PeakEQ, 1);

        for (int band = 0; band < Frequencies.Length; band++)
            ApplyBand(band);
    }

    private void RemoveEffects()
    {
        if (_eqEffect != 0) Bass.ChannelRemoveFX(_channel, _eqEffect);
        if (_preampDsp != 0) Bass.ChannelRemoveDSP(_channel, _preampDsp);
        _eqEffect = 0;
        _preampDsp = 0;
    }

    private void ApplyBand(int band)
    {
        if (_eqEffect == 0 || !BandIsUsable(band)) return;

        var parameters = new PeakEQParameters
        {
            lBand = band,                 // which of the 10 bands
            fCenter = Frequencies[band],  // its frequency
            fGain = _gainsDb[band],       // boost or cut in dB
            fBandwidth = BandwidthOctaves,// how wide the band is
            fQ = 0f,                      // 0 = use the bandwidth above
            lChannel = FXChannelFlags.All // both left and right
        };
        Bass.FXSetParameters(_eqEffect, parameters);
    }

    // Runs on BASS's audio thread many times a second. "buffer" holds "length" bytes of
    // sound as 32-bit floating-point samples (the song was opened with BassFlags.Float).
    // Left and right samples alternate: L R L R … (one L+R pair is called a "frame").
    private void ApplyPreampToBlock(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        float start = _currentFactor;
        float end = _targetFactor;
        if (start == 1f && end == 1f) return; // 0 dB: nothing to do
        if (length <= 0) return;

        int count = length / sizeof(float);
        int frames = count / _channelCount;
        if (frames == 0) return;
        if (_scratch.Length < count) _scratch = new float[count];

        Marshal.Copy(buffer, _scratch, 0, count);   // copy the sound out

        // Glide from the old level to the new one across this block, so changes don't click.
        float step = (end - start) / frames;
        float factor = start;
        int i = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            factor += step;
            for (int c = 0; c < _channelCount; c++, i++)
                _scratch[i] *= factor;
        }

        Marshal.Copy(_scratch, 0, buffer, count);   // put it back
        _currentFactor = end;
    }
}
