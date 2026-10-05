namespace Renamer.App;

// Post at most ten UI updates per second so many small files cannot flood the UI queue.
internal sealed class ThrottledProgress<T> : IProgress<T>
{
    readonly IProgress<T> progress;
    readonly object gate = new();
    long last = Environment.TickCount64 - 100;
    public ThrottledProgress(Action<T> action) { progress = new Progress<T>(action); }
    public void Report(T value)
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (now - last < 100) return;
            last = now; progress.Report(value);
        }
    }
}
