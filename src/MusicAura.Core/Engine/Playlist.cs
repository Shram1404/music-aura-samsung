using MusicAura.Core.Audio;

namespace MusicAura.Core.Engine;

public sealed class Playlist
{
    private readonly object _lock = new();
    private readonly List<TrackInfo> _items = [];

    public event Action? Changed;

    public bool RepeatAll { get; set; }

    public IReadOnlyList<TrackInfo> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public void Add(IEnumerable<TrackInfo> tracks)
    {
        lock (_lock)
        {
            foreach (var t in tracks)
            {
                if (!_items.Any(i => i.Id == t.Id))
                {
                    _items.Add(t);
                }
            }
        }

        Changed?.Invoke();
    }

    public void Remove(TrackInfo track)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.Id == track.Id);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }

        Changed?.Invoke();
    }

    public int IndexOf(TrackInfo? track)
    {
        if (track is null)
        {
            return -1;
        }

        lock (_lock)
        {
            return _items.FindIndex(i => i.Id == track.Id);
        }
    }

    public TrackInfo? At(int index)
    {
        lock (_lock)
        {
            return index >= 0 && index < _items.Count ? _items[index] : null;
        }
    }

    public TrackInfo? After(TrackInfo? current)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return null;
            }

            var index = current is null ? -1 : _items.FindIndex(i => i.Id == current.Id);
            if (index + 1 < _items.Count)
            {
                return _items[index + 1];
            }

            return RepeatAll ? _items[0] : null;
        }
    }

    public TrackInfo? Before(TrackInfo? current)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return null;
            }

            var index = current is null ? 0 : _items.FindIndex(i => i.Id == current.Id);
            if (index > 0)
            {
                return _items[index - 1];
            }

            return RepeatAll ? _items[^1] : _items[0];
        }
    }
}
