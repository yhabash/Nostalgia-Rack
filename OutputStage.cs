using System.Runtime.InteropServices;
using ManagedBass;

namespace AmpPlayer;

/// <summary>
/// The very last step before the sound goes to your speakers:
/// 1. Meter: measures how loud the music is, for the VU needles (before the volume knob,
///    like a real deck, so the needles move the same at any volume).
/// 2. Balance and volume, gliding smoothly so they never click.
/// 3. Soft clipper: catches anything that would go over the maximum level.
///
/// Why a soft clipper?
/// Digital audio has a hard ceiling (1.0). When an EQ boost pushes a peak above it,
/// the top of the wave is chopped off flat, which sounds like crackle or crunch.
/// The soft clipper leaves everything below 90% of the ceiling exactly as it is.
/// Above that, it bends the peak smoothly towards the ceiling instead of chopping it,
/// like an analogue amp being pushed. You keep the full volume.
/// </summary>
public sealed class OutputStage
{
    // Below this level (about −1 dB) the sound passes through untouched.
    private const float Threshold = 0.89f;

    private int _channel;
    private int _dsp;
    private int _channelCount = 2;

    private volatile float _targetVolume = 0.8f;
    private float _currentVolume = 0.8f;
    private volatile float _balance;           // −1 = left only, 0 = centre, +1 = right only
    private float _currentLeft = 1f, _currentRight = 1f;

    // Latest loudness of each side (0 to 1, RMS), read by the screen for the VU needles.
    private volatile float _levelLeft, _levelRight;

    private readonly DSPProcedure _procedure;
    private float[] _scratch = Array.Empty<float>();

    public OutputStage()
    {
        _procedure = Process;
    }

    /// <summary>True when the soft clipper is switched on.</summary>
    public bool SoftClipEnabled { get; set; } = true;

    /// <summary>
    /// True while the EQ boosts something. With a flat EQ the sound can never go over the
    /// ceiling, so the clipper stays completely out of the way.
    /// </summary>
    public bool BoostActive { get; set; }

    /// <summary>Volume from 0 (silent) to 1 (full).</summary>
    public void SetVolume(double volume) => _targetVolume = (float)Math.Clamp(volume, 0, 1);

    /// <summary>Balance from −1 (left) through 0 (centre) to +1 (right).</summary>
    public void SetBalance(double balance) => _balance = (float)Math.Clamp(balance, -1, 1);

    /// <summary>Latest loudness of the left and right side (0 to 1).</summary>
    public (float left, float right) Levels => (_levelLeft, _levelRight);

    /// <summary>Connects to the mixer (or to nothing, with 0).</summary>
    public void Attach(int channel)
    {
        if (_channel != 0 && _dsp != 0) Bass.ChannelRemoveDSP(_channel, _dsp);
        _channel = channel;
        _dsp = 0;
        _currentVolume = _targetVolume;
        _levelLeft = _levelRight = 0;
        if (_channel == 0) return;

        _channelCount = Math.Max(1, Bass.ChannelGetInfo(_channel).Channels);

        // Priority 0 = runs after the preamp and the EQ.
        _dsp = Bass.ChannelSetDSP(_channel, _procedure, IntPtr.Zero, 0);
    }

    // Runs on the audio thread for every small block of sound (32-bit float samples).
    private void Process(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;

        int count = length / sizeof(float);
        int frames = count / _channelCount;
        if (frames == 0) return;
        if (_scratch.Length < count) _scratch = new float[count];

        Marshal.Copy(buffer, _scratch, 0, count);

        // 1. Meter (RMS of each side), before the volume.
        double sumLeft = 0, sumRight = 0;
        for (int i = 0; i < count; i += _channelCount)
        {
            float l = _scratch[i];
            float r = _channelCount > 1 ? _scratch[i + 1] : l;
            sumLeft += l * l;
            sumRight += r * r;
        }
        _levelLeft = (float)Math.Sqrt(sumLeft / frames);
        _levelRight = (float)Math.Sqrt(sumRight / frames);

        // 2. Volume and balance (constant total loudness: the far side fades, the near side stays).
        float balance = _balance;
        float targetLeft = balance > 0 ? 1f - balance : 1f;
        float targetRight = balance < 0 ? 1f + balance : 1f;

        float startVolume = _currentVolume, endVolume = _targetVolume;
        float volumeStep = (endVolume - startVolume) / frames;
        float leftStep = (targetLeft - _currentLeft) / frames;
        float rightStep = (targetRight - _currentRight) / frames;

        float volume = startVolume, left = _currentLeft, right = _currentRight;
        bool clip = SoftClipEnabled && BoostActive;

        int index = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            volume += volumeStep;
            left += leftStep;
            right += rightStep;
            for (int c = 0; c < _channelCount; c++, index++)
            {
                float side = c == 0 ? left : c == 1 ? right : 1f;
                float s = _scratch[index] * volume * side;
                _scratch[index] = clip ? SoftClip(s) : s;
            }
        }

        Marshal.Copy(_scratch, 0, buffer, count);
        _currentVolume = endVolume;
        _currentLeft = targetLeft;
        _currentRight = targetRight;
    }

    // Untouched below the threshold. Above it, a smooth curve (tanh) that bends the peak
    // towards 1.0 but never crosses it. The curve joins the straight part without a kink.
    private static float SoftClip(float s)
    {
        float magnitude = Math.Abs(s);
        if (magnitude <= Threshold) return s;

        float room = 1f - Threshold;
        float bent = Threshold + room * MathF.Tanh((magnitude - Threshold) / room);
        return s < 0 ? -bent : bent;
    }
}
