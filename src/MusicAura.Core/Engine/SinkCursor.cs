using MusicAura.Core.Audio;
using MusicAura.Core.Sync;

namespace MusicAura.Core.Engine;

/// <summary>
/// One output's read position on the shared timeline. Each render call is told which
/// timeline position the output <i>should</i> be at; small differences are removed by
/// reading slightly faster or slower (sinc interpolation), large ones by a crossfaded jump.
/// </summary>
internal sealed class SinkCursor
{
    private const int Channels = 2;

    private readonly TimelineBuffer _timeline;
    private readonly SincKernel _kernel;
    private readonly int _sampleRate;
    private readonly float[] _coefficients;
    private readonly int _crossfadeFrames;
    private float[] _raw = new float[4096];
    private float[] _fade = new float[1024];
    private double _pos;
    private bool _locked;

    public SinkCursor(
        TimelineBuffer timeline,
        SincKernel kernel,
        int sampleRate,
        RateController controller,
        GateList? sharedGates = null,
        OverlayList? sharedOverlays = null)
    {
        _timeline = timeline;
        Gates = sharedGates ?? new GateList();
        Overlays = sharedOverlays ?? new OverlayList();
        _kernel = kernel;
        _sampleRate = sampleRate;
        Controller = controller;
        _coefficients = new float[kernel.Taps];
        _crossfadeFrames = Math.Max(16, sampleRate / 200); // 5 ms
    }

    public RateController Controller { get; }

    /// <summary>Mute ranges of this output (shared by all cursors of one output).</summary>
    public GateList Gates { get; }

    /// <summary>Calibration probes for this output (shared by all cursors of one output).</summary>
    public OverlayList Overlays { get; }

    /// <summary>Timeline position of the next frame this output will render.</summary>
    public double Position => Volatile.Read(ref _pos);

    public bool Locked => _locked;

    public double LastErrorSeconds { get; private set; }

    public double LastStep { get; private set; } = 1;

    public int Resyncs { get; private set; }

    /// <summary>Forces the next render to jump straight to its target.</summary>
    public void Unlock() => _locked = false;

    /// <summary>
    /// Renders <c>destination.Length / 2</c> stereo frames. <paramref name="desiredPosition"/> is the
    /// timeline position the first rendered frame should have.
    /// </summary>
    public void Render(Span<float> destination, double desiredPosition, double dtSeconds)
    {
        var frames = destination.Length / Channels;
        var error = (_pos - desiredPosition) / _sampleRate;
        LastErrorSeconds = error;

        double step;
        if (!_locked || Math.Abs(error) > Controller.HardResyncSeconds)
        {
            var crossfade = _locked;
            var fadeFrames = Math.Min(_crossfadeFrames, frames);
            if (crossfade)
            {
                EnsureCapacity(ref _fade, fadeFrames * Channels);
                RenderAt(_pos, 1.0, _fade.AsSpan(0, fadeFrames * Channels));
                Resyncs++;
            }

            Controller.Reset();
            _locked = true;
            step = 1.0;
            Volatile.Write(ref _pos, Math.Round(desiredPosition));
            RenderAt(_pos, step, destination);

            if (crossfade)
            {
                for (var i = 0; i < fadeFrames; i++)
                {
                    var w = (i + 1) / (float)(fadeFrames + 1);
                    destination[i * 2] = destination[i * 2] * w + _fade[i * 2] * (1 - w);
                    destination[i * 2 + 1] = destination[i * 2 + 1] * w + _fade[i * 2 + 1] * (1 - w);
                }
            }
        }
        else
        {
            step = Controller.Update(error, dtSeconds);
            RenderAt(_pos, step, destination);
        }

        LastStep = step;
        Volatile.Write(ref _pos, _pos + step * frames);
    }

    private void RenderAt(double pos, double step, Span<float> destination)
    {
        var frames = destination.Length / Channels;
        if (frames == 0)
        {
            return;
        }

        var half = _kernel.HalfTaps;
        var first = (long)Math.Floor(pos) - (half - 1);
        var last = (long)Math.Floor(pos + step * (frames - 1)) + half;
        var rawFrames = (int)(last - first + 1);
        EnsureCapacity(ref _raw, rawFrames * Channels);
        var raw = _raw.AsSpan(0, rawFrames * Channels);
        _timeline.Read(first, raw);
        Gates.Apply(first, raw);
        Overlays.Apply(first, raw);

        for (var n = 0; n < frames; n++)
        {
            var p = pos + step * n;
            var i = (long)Math.Floor(p);
            var frac = p - i;
            if (frac < 1e-9)
            {
                var idx = (int)(i - first) * Channels;
                destination[n * 2] = raw[idx];
                destination[n * 2 + 1] = raw[idx + 1];
                continue;
            }

            _kernel.GetCoefficients(frac, _coefficients);
            var start = (int)(i - (half - 1) - first) * Channels;
            SincKernel.InterpolateStereo(raw.Slice(start, _kernel.Taps * Channels), _coefficients, out var l, out var r);
            destination[n * 2] = l;
            destination[n * 2 + 1] = r;
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new float[Math.Max(length, buffer.Length * 2)];
        }
    }
}
