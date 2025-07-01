using System;

namespace MediaSessionWSProvider;

public class MetadataCache
{
    private readonly object _lockObj = new();
    private Worker.FullMediaState? _last;

    public Worker.FullMediaState? Last
    {
        get { lock (_lockObj) { return _last; } }
    }

    public void Update(Worker.FullMediaState state)
    {
        lock (_lockObj)
        {
            _last = state;
        }
    }
}
