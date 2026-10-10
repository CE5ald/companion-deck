using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// The one audio format everything in this agent converges on, and the conversions that get
/// arbitrary sources there. Having a single target matters more than which one it is.
/// </summary>
public static class MixFormat
{
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public static int SampleRate => Format.SampleRate;
    public static int Channels => Format.Channels;

    public static ISampleProvider Convert(IWaveProvider src) => Convert(src.ToSampleProvider());

    /// <summary>Brings any source to stereo, 48kHz, float.</summary>
    public static ISampleProvider Convert(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels > 2) sp = new DownmixToStereo(sp);
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
        return sp;
    }

    /// <summary>
    /// Folds a multi-channel source down to stereo by averaging the odd and even channels.
    ///
    /// Rare on a laptop, but a surround-configured HDMI output does present as six channels,
    /// and a mixer would reject it outright - taking down whatever was using it.
    /// </summary>
    public sealed class DownmixToStereo : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _channels;
        private float[] _buf = Array.Empty<float>();

        public DownmixToStereo(ISampleProvider src)
        {
            _src = src;
            _channels = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _channels;
            if (_buf.Length < need) _buf = new float[need];

            int got = _src.Read(_buf, 0, need);
            int gotFrames = got / _channels;

            for (int f = 0; f < gotFrames; f++)
            {
                float l = 0, r = 0;
                int lc = 0, rc = 0;
                for (int c = 0; c < _channels; c++)
                {
                    float s = _buf[f * _channels + c];
                    if ((c & 1) == 0) { l += s; lc++; } else { r += s; rc++; }
                }
                buffer[offset + f * 2] = lc > 0 ? l / lc : 0f;
                buffer[offset + f * 2 + 1] = rc > 0 ? r / rc : 0f;
            }
            return gotFrames * 2;
        }
    }
}
