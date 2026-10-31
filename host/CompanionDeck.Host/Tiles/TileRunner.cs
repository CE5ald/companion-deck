namespace CompanionDeck.Host.Tiles;

/// <summary>
/// Runs a tile's steps in order, off the command thread, so a tile with a ten second wait
/// does not hold up the next tap. Pressing a tile that is already running stops it.
/// </summary>
public sealed class TileRunner
{
    public const int MaxConcurrent = 4;

    private readonly Func<TileStep, CancellationToken, Task<string?>> _execute;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly object _gate = new();

    public event Action? Changed;

    public (string tile, string message, long atMs)? LastError { get; private set; }

    /// <param name="execute">Performs one step and returns an error message, or null on success.</param>
    public TileRunner(Func<TileStep, CancellationToken, Task<string?>> execute) => _execute = execute;

    public List<string> Running()
    {
        lock (_gate) return _running.Keys.ToList();
    }

    /// <summary>Starts the tile, or stops it if it is already running. False if too many are in flight.</summary>
    public bool Toggle(string key, string name, IReadOnlyList<TileStep> steps)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running.TryGetValue(key, out var existing))
            {
                existing.Cancel();
                return true;
            }
            if (_running.Count >= MaxConcurrent) return false;
            cts = new CancellationTokenSource();
            _running[key] = cts;
            LastError = null;
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(key, name, steps, cts));
        return true;
    }

    public void CancelAll()
    {
        lock (_gate)
            foreach (var cts in _running.Values) cts.Cancel();
    }

    private async Task RunAsync(string key, string name, IReadOnlyList<TileStep> steps, CancellationTokenSource cts)
    {
        string? failure = null;
        try
        {
            foreach (var step in steps)
            {
                cts.Token.ThrowIfCancellationRequested();
                failure = await _execute(step, cts.Token).ConfigureAwait(false);
                if (failure is not null) break;
                // A short gap even without a wait step: the shell drops a chord that follows
                // another too closely, which showed up as every second step being ignored.
                if (step.Do != "wait") await Task.Delay(60, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            lock (_gate)
            {
                _running.Remove(key);
                if (failure is not null) LastError = (name, failure, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            cts.Dispose();
            if (failure is not null) Log.Warn($"tile '{name}' stopped: {failure}");
            Changed?.Invoke();
        }
    }
}
