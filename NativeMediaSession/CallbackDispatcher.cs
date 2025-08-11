using System.Collections.Concurrent;

namespace NativeMediaSession;

public class CallbackDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;

    public CallbackDispatcher()
    {
        _worker = new Thread(Loop) { IsBackground = true };
        _worker.Start();
    }

    public void Enqueue(Action action)
    {
        if (!_queue.IsAddingCompleted)
            _queue.Add(action);
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // swallow exceptions
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try
        {
            _worker.Join();
        }
        catch { }
    }
}
