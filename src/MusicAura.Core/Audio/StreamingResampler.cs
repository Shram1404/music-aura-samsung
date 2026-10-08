namespace MusicAura.Core.Audio;

/// <summary>
/// Push/pull stereo sample-rate converter with a fixed ratio (e.g. a 48 kHz file into a
/// 44.1 kHz session). Equal rates are passed through untouched.
/// </summary>
public sealed class StreamingResampler
{
    private const int Channels = 2;

    private readonly SincKernel? _kernel;
    private readonly double _step;
    private readonly float[] _coefficients;
    private float[] _buffer = new float[8192 * Channels];
    private int _frames;   // frames held in _buffer
    private double _pos;   // position of the next output frame, in input frames relative to _buffer[0]

    public StreamingResampler(int inputRate, int outputRate, int halfTaps = 16)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        _step = inputRate / (double)outputRate;
        if (inputRate != outputRate)
        {
            // Leave a little transition band below Nyquist of the lower rate.
            var cutoff = Math.Min(1.0, outputRate / (double)inputRate) * 0.94;
            _kernel = new SincKernel(halfTaps, cutoff);
        }

        _coefficients = new float[_kernel?.Taps ?? 0];
        Reset();
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public bool IsPassThrough => _kernel is null;

    /// <summary>Input frames buffered but not yet consumed.</summary>
    public int BufferedInputFrames => _frames;

    public void Reset()
    {
        _frames = 0;
        if (_kernel is not null)
        {
            // Prime with silent history so output frame 0 lines up with input frame 0.
            var history = _kernel.HalfTaps - 1;
            EnsureCapacity(history);
            Array.Clear(_buffer, 0, history * Channels);
            _frames = history;
            _pos = history;
        }
        else
        {
            _pos = 0;
        }
    }

    public void Push(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / Channels;
        EnsureCapacity(_frames + frames);
        interleaved[..(frames * Channels)].CopyTo(_buffer.AsSpan(_frames * Channels));
        _frames += frames;
    }

    /// <summary>Produces as many output frames as the buffered input allows. Returns frames written.</summary>
    public int Pull(Span<float> destination)
    {
        var maxOut = destination.Length / Channels;
        if (_kernel is null)
        {
            var n = Math.Min(maxOut, _frames);
            _buffer.AsSpan(0, n * Channels).CopyTo(destination);
            Consume(n);
            return n;
        }

        var produced = 0;
        var half = _kernel.HalfTaps;
        while (produced < maxOut)
        {
            var i = (int)Math.Floor(_pos);
            if (i + half >= _frames)
            {
                break;
            }

            _kernel.GetCoefficients(_pos - i, _coefficients);
            var start = (i - (half - 1)) * Channels;
            SincKernel.InterpolateStereo(_buffer.AsSpan(start, _kernel.Taps * Channels), _coefficients, out var l, out var r);
            destination[produced * Channels] = l;
            destination[produced * Channels + 1] = r;
            produced++;
            _pos += _step;
        }

        // Drop input that no future output frame can reference.
        var keepFrom = (int)Math.Floor(_pos) - (half - 1);
        if (keepFrom > 0)
        {
            var drop = Math.Min(keepFrom, _frames);
            Consume(drop);
            _pos -= drop;
        }

        return produced;
    }

    private void Consume(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var remaining = _frames - frames;
        if (remaining > 0)
        {
            Array.Copy(_buffer, frames * Channels, _buffer, 0, remaining * Channels);
        }

        _frames = remaining;
    }

    private void EnsureCapacity(int frames)
    {
        if (_buffer.Length < frames * Channels)
        {
            Array.Resize(ref _buffer, Math.Max(frames * Channels, _buffer.Length * 2));
        }
    }
}
