using System.Runtime.InteropServices;
using ManagedBass;

namespace NostalgiaRack;

/// <summary>
/// The compressor: turns the loud parts down a little so quiet and loud passages sit closer
/// together. Tuned for gentle levelling (late-night or background listening), not for effect.
///
/// How it works, step by step, for every sample:
/// 1. Measure how loud the music is right now (both sides together, so the stereo image
///    never wobbles), smoothed over about 10 ms, in dB.
/// 2. Above the THRESHOLD, every extra dB only comes out as 1/RATIO dB. Around the threshold
///    the change is eased in over 6 dB (a "soft knee"), so you can't hear it switch on.
/// 3. The amount of turning-down glides: it moves in over the ATTACK time and lets go over
///    the RELEASE time. Slow times = smooth and natural.
/// 4. GAIN (makeup) adds volume back, so the music isn't quieter overall. Any peak that
///    would then go over the ceiling is caught by the soft clipper in OutputStage.cs.
///
/// It sits on the mixer after the EQ and before the volume knob, so the volume knob doesn't
/// change how hard it works.
/// </summary>
public sealed class Compressor
{
    private const float KneeDb = 6f;
    private const double DetectorMs = 10;

    private int _channel;
    private int _dsp;
    private int _channelCount = 2;
    private double _sampleRate = 48000;

    // Settings (written by the screen, read by the audio thread).
    private volatile float _thresholdDb = -18f;
    private volatile float _ratio = 2.5f;
    // Attack and release are fixed at gentle settings (the knobs were removed to keep it simple).
    public const float AttackMs = 30f;
    public const float ReleaseMs = 400f;
    private volatile float _attackMs = AttackMs;
    private volatile float _releaseMs = ReleaseMs;
    private volatile float _makeupDb = 4f;
    private volatile bool _enabled;

    // Audio-thread state.
    private double _detector;          // smoothed loudness (power)
    private double _reductionDb;       // how much we're turning down right now
    private volatile float _meterDb;   // the latest reduction, for the GR meter

    private readonly DSPProcedure _procedure;
    private float[] _scratch = Array.Empty<float>();

    public Compressor()
    {
        _procedure = Process;
    }

    /// <summary>On or off. Off = the music passes through untouched.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) _meterDb = 0; }
    }

    /// <summary>How many dB the compressor is turning down right now (for the GR meter).</summary>
    public float GainReductionDb => _enabled ? _meterDb : 0f;

    /// <summary>True when the makeup gain can push peaks up (so the soft clipper should watch).</summary>
    public bool IsBoosting => _enabled && _makeupDb > 0.05f;

    // ---- The knobs. Each knob runs from 0 to 1; these turn that into real values. ----

    /// <summary>THRESHOLD: −40 dB (knob fully left) … 0 dB (fully right).</summary>
    public static float ThresholdFromKnob(double k) => (float)(-40 + 40 * k);

    /// <summary>RATIO: 1:1 (no effect) … 10:1, spread so the gentle settings get most of the turn.</summary>
    public static float RatioFromKnob(double k) => (float)Math.Pow(10, k);

    /// <summary>GAIN (makeup): 0 dB … +18 dB.</summary>
    public static float GainFromKnob(double k) => (float)(18 * k);

    public void Set(float thresholdDb, float ratio, float attackMs, float releaseMs, float makeupDb)
    {
        _thresholdDb = thresholdDb;
        _ratio = Math.Max(1f, ratio);
        _attackMs = Math.Max(0.5f, attackMs);
        _releaseMs = Math.Max(5f, releaseMs);
        _makeupDb = makeupDb;
    }

    /// <summary>Connects to the mixer (or to nothing, with 0).</summary>
    public void Attach(int channel)
    {
        if (_channel != 0 && _dsp != 0) Bass.ChannelRemoveDSP(_channel, _dsp);
        _channel = channel;
        _dsp = 0;
        _detector = 0;
        _reductionDb = 0;
        _meterDb = 0;
        if (_channel == 0) return;

        var info = Bass.ChannelGetInfo(_channel);
        _channelCount = Math.Max(1, info.Channels);
        _sampleRate = info.Frequency > 0 ? info.Frequency : 48000;

        // Priority 1: after the preamp (3) and the EQ (2), before the output stage (0).
        _dsp = Bass.ChannelSetDSP(_channel, _procedure, IntPtr.Zero, 1);
    }

    // The gain computer: how many dB to turn down for a given input level (with a soft knee).
    private static double ReductionFor(double levelDb, double threshold, double ratio)
    {
        double over = levelDb - threshold;
        double slope = 1 - 1 / ratio;
        if (over <= -KneeDb / 2) return 0;
        if (over >= KneeDb / 2) return over * slope;
        double x = over + KneeDb / 2;
        return slope * x * x / (2 * KneeDb);
    }

    // Runs on the audio thread for every small block of sound (32-bit float samples).
    private void Process(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (!_enabled || length <= 0) return;

        int count = length / sizeof(float);
        int frames = count / _channelCount;
        if (frames == 0) return;
        if (_scratch.Length < count) _scratch = new float[count];
        Marshal.Copy(buffer, _scratch, 0, count);

        double threshold = _thresholdDb, ratio = _ratio, makeup = _makeupDb;
        double detectCoef = Math.Exp(-1000.0 / (DetectorMs * _sampleRate));
        double attackCoef = Math.Exp(-1000.0 / (_attackMs * _sampleRate));
        double releaseCoef = Math.Exp(-1000.0 / (_releaseMs * _sampleRate));
        double detector = _detector, reduction = _reductionDb, peakReduction = 0;

        int i = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            // 1. Loudness of this moment: the louder side, as power, smoothed.
            float loudest = 0;
            for (int c = 0; c < _channelCount; c++)
                loudest = Math.Max(loudest, Math.Abs(_scratch[i + c]));
            detector = detectCoef * detector + (1 - detectCoef) * loudest * loudest;
            double levelDb = 10 * Math.Log10(detector + 1e-12);

            // 2 + 3. How much to turn down, gliding in (attack) and out (release).
            double target = ReductionFor(levelDb, threshold, ratio);
            double coef = target > reduction ? attackCoef : releaseCoef;
            reduction = coef * reduction + (1 - coef) * target;
            if (reduction > peakReduction) peakReduction = reduction;

            // 4. Apply the turning-down plus the makeup gain.
            float gain = (float)Math.Pow(10, (makeup - reduction) / 20);
            for (int c = 0; c < _channelCount; c++, i++)
                _scratch[i] *= gain;
        }

        Marshal.Copy(_scratch, 0, buffer, count);
        _detector = detector;
        _reductionDb = reduction;
        _meterDb = (float)peakReduction;
    }
}
