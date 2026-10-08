namespace MusicAura.Core.Dsp;

/// <summary>Iterative in-place radix-2 complex FFT on split real/imaginary arrays.</summary>
public sealed class Fft
{
    private static readonly Dictionary<int, Fft> Cache = new();

    private readonly int[] _bitReverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    private Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two.", nameof(size));
        }

        Size = size;
        var bits = 0;
        while ((1 << bits) < size)
        {
            bits++;
        }

        _bitReverse = new int[size];
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _bitReverse[i] = r;
        }

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            var angle = -2 * Math.PI * i / size;
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }
    }

    public int Size { get; }

    public static Fft ForSize(int size)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(size, out var fft))
            {
                fft = new Fft(size);
                Cache[size] = fft;
            }

            return fft;
        }
    }

    public static int NextPowerOfTwo(int n)
    {
        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    public void Forward(double[] re, double[] im) => Transform(re, im, inverse: false);

    /// <summary>Inverse transform including the 1/N scaling.</summary>
    public void Inverse(double[] re, double[] im)
    {
        Transform(re, im, inverse: true);
        var scale = 1.0 / Size;
        for (var i = 0; i < Size; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private void Transform(double[] re, double[] im, bool inverse)
    {
        var n = Size;
        if (re.Length < n || im.Length < n)
        {
            throw new ArgumentException("Arrays shorter than FFT size.");
        }

        for (var i = 0; i < n; i++)
        {
            var j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        var sign = inverse ? -1.0 : 1.0;
        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var stride = n / len;
            for (var start = 0; start < n; start += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * stride];
                    var wi = sign * _sin[k * stride];
                    var a = start + k;
                    var b = a + half;
                    var xr = re[b] * wr - im[b] * wi;
                    var xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }
}
