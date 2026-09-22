namespace MediaTransfer.Core;

/// <summary>
/// Fans one scan out to several sinks, so the same walk can print to the
/// console and write to a database without knowing that either exists.
///
/// The interesting part is what happens when one of them fails. A scan is
/// 14,000 device round trips over hardware that is already known to wedge; if a
/// full disk or a locked database file could abort it, the storage layer would
/// have made the scanner LESS reliable than it was without storage. So a
/// throwing sink is dropped, loudly, and the walk carries on.
///
/// Dropped rather than retried on every call, because a broken sink usually
/// stays broken: re-entering it 14,000 more times turns one failure into a
/// 14,000-line error log and a scan slowed to a crawl. Loudly rather than
/// silently, because a database that quietly stopped receiving files halfway
/// through is indistinguishable from a phone that only had that many - and
/// storing a partial scan as though it were complete is the exact failure this
/// project exists to prevent. A dropped SQLite sink also leaves its scan row at
/// status='running', so the gap is recorded in the database itself and not only
/// on a screen nobody kept.
/// </summary>
public sealed class CompositeScanSink : IScanSink, IDisposable
{
    // Two lists on purpose. `live` shrinks as sinks fault out; `all` keeps
    // every sink ever handed in, because a faulted sink still owns whatever it
    // had open and Dispose has to reach it.
    readonly IScanSink[] all;
    readonly List<IScanSink> live;
    readonly List<SinkFault> faults = new();

    public CompositeScanSink(params IScanSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        all = sinks;
        live = new List<IScanSink>(sinks);
    }

    /// <summary>A sink that threw and was dropped, with the call that broke it.</summary>
    public record SinkFault(string SinkType, string Call, string Message);

    public IReadOnlyList<SinkFault> Faults => faults;

    /// <summary>Sinks still receiving events. Shrinks as sinks fault out.</summary>
    public int LiveCount => live.Count;

    void Dispatch(string call, Action<IScanSink> send)
    {
        // Indexed and backwards so a sink can be removed mid-iteration without
        // skipping its neighbour, which a foreach over a copy would hide and a
        // forward loop would get wrong.
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var sink = live[i];
            try
            {
                send(sink);
            }
            catch (Exception ex)
            {
                live.RemoveAt(i);
                var fault = new SinkFault(sink.GetType().Name, call, ex.Message);
                faults.Add(fault);
                Console.Error.WriteLine(
                    $"[SINK FAILED] {fault.SinkType} threw during {call} and will receive nothing further " +
                    $"from this scan: {ex.Message}");
            }
        }
    }

    public void OnScanStarted(DeviceIdentity device, bool cameraMode) =>
        Dispatch(nameof(OnScanStarted), s => s.OnScanStarted(device, cameraMode));

    public void OnFile(DeviceObject obj, string path, FileKind kind, bool recovered) =>
        Dispatch(nameof(OnFile), s => s.OnFile(obj, path, kind, recovered));

    public void OnFolder(DeviceObject obj, string path, bool recovered) =>
        Dispatch(nameof(OnFolder), s => s.OnFolder(obj, path, recovered));

    public void OnFolderSkipped(string path, string reason) =>
        Dispatch(nameof(OnFolderSkipped), s => s.OnFolderSkipped(path, reason));

    public void OnError(ScanError error) =>
        Dispatch(nameof(OnError), s => s.OnError(error));

    public void OnRetryStarted(int objectCount) =>
        Dispatch(nameof(OnRetryStarted), s => s.OnRetryStarted(objectCount));

    public void OnRetryFinished(RetryOutcome outcome) =>
        Dispatch(nameof(OnRetryFinished), s => s.OnRetryFinished(outcome));

    public void OnScanFinished(ScanOutcome outcome)
    {
        Dispatch(nameof(OnScanFinished), s => s.OnScanFinished(outcome));

        // After the summary, so it is the last thing on screen rather than a
        // line scrolled past 14,000 files ago.
        foreach (var fault in faults)
        {
            Console.Error.WriteLine(
                $"[SINK FAILED] {fault.SinkType} did not record this scan (failed during {fault.Call}: {fault.Message}).");
        }
    }

    /// <summary>
    /// Disposes every sink that has one, including the ones already dropped -
    /// a faulted sink may still be holding a file handle or an open
    /// transaction, and leaving those behind is how the next run finds a
    /// locked database.
    /// </summary>
    public void Dispose()
    {
        foreach (var sink in all)
        {
            if (sink is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Console.Error.WriteLine($"[SINK FAILED] {sink.GetType().Name}.Dispose: {ex.Message}"); }
            }
        }
        live.Clear();
    }
}
