using System;

namespace MediaSessionWSProvider;

public class MetadataCache
{
    private readonly object _lockObj = new();
    private FullMediaState? _last;

    public FullMediaState? Last
    {
        get { lock (_lockObj) { return _last; } }
    }

    public void Update(FullMediaState state)
    {
        lock (_lockObj)
        {
            _last = state;
        }
    }
}
