using System;
using System.Threading;
using System.Threading.Tasks;

namespace CherryDesk.Performance;

internal interface IPerformanceReader : IDisposable
{
    PerformanceSnapshot Read();
}

internal sealed class PerformanceMonitor : IDisposable
{
    private readonly Func<IPerformanceReader> readerFactory;
    private readonly TimeSpan interval;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool disposed;
    private int running;
    private long generation;
    private long readCount;
    private int activeReaders;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public long Generation => Interlocked.Read(ref generation);
    public long ReadCount => Interlocked.Read(ref readCount);
    public int ActiveReaders => Volatile.Read(ref activeReaders);
    public event Action<long, PerformanceSnapshot>? Updated;
    public event Action<Exception>? Failed;

    public PerformanceMonitor(Func<IPerformanceReader>? readerFactory = null, TimeSpan? interval = null)
    {
        this.readerFactory = readerFactory ?? (() => new SystemPerformanceReader());
        this.interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled && worker is { IsCompleted: false } && IsRunning) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (!enabled) return;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var run = Interlocked.Increment(ref generation);
            Volatile.Write(ref running, 1);
            worker = Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    // No reader, GPU query or sampling timer exists before explicit enable.
                    using var reader = readerFactory();
                    Interlocked.Increment(ref activeReaders);
                    using var timer = new PeriodicTimer(interval);
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        var sample = reader.Read();
                        Interlocked.Increment(ref readCount);
                        token.ThrowIfCancellationRequested();
                        Updated?.Invoke(run, sample);
                    } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { App.Log(ex); Failed?.Invoke(ex); }
                finally { Volatile.Write(ref activeReaders, 0); Volatile.Write(ref running, 0); }
            });
        }
        finally { transitions.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref running, 0);
        cancellation?.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
        worker = null;
        cancellation?.Dispose();
        cancellation = null;
        // Awaiting completion guarantees disposal of the native query and timer.
    }

    public void Dispose()
    {
        transitions.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            StopCoreAsync().GetAwaiter().GetResult();
        }
        finally { transitions.Release(); }
    }
}
