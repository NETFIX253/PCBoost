namespace PCBoost.Optimization.Common;

/// <summary>
/// Consignation write-ahead de plusieurs modifications exécutées ensemble par une seule opération (ex. une seule
/// autorisation administrateur pour plusieurs pilotes) :
/// <list type="number">
/// <item>chaque modification passe par <c>IChangeRecorder.ApplyAsync</c>, dont l'action d'application appelle
/// <see cref="ArriveAsync"/> : la modification est alors consignée (Pending) et attend le résultat groupé ;</item>
/// <item><see cref="Ready"/> se termine quand chaque modification est soit consignée, soit refusée (validation de sûreté,
/// journal indisponible : son action n'est jamais appelée) — seules les modifications consignées sont exécutées ;</item>
/// <item><see cref="Complete"/> transmet le résultat de chacune ; une modification sans résultat reçoit la valeur par défaut.</item>
/// </list>
/// </summary>
internal sealed class WriteAheadBatch<TResult>
{
    private enum State { Waiting, Arrived, Refused }

    private readonly object _gate = new();
    private readonly List<string> _order;
    private readonly Dictionary<string, State> _states;
    private readonly TaskCompletionSource<IReadOnlyList<string>> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, TResult>> _results = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _settled;

    public WriteAheadBatch(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _order = ids.ToList();
        _states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _order)
        {
            if (!_states.TryAdd(id, State.Waiting)) throw new ArgumentException("Identifiant en double", nameof(ids));
        }
        if (_order.Count == 0) _ready.SetResult([]);
    }

    /// <summary>Identifiants consignés (dans l'ordre d'origine), une fois chaque modification consignée ou refusée.</summary>
    public Task<IReadOnlyList<string>> Ready => _ready.Task;

    /// <summary>Appelé par l'action d'application : la modification est consignée ; attend son résultat.</summary>
    public async Task<TResult?> ArriveAsync(string id, CancellationToken cancellationToken)
    {
        Settle(id, State.Arrived);
        var results = await _results.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return results.TryGetValue(id, out var result) ? result : default;
    }

    /// <summary>À appeler quand l'appel ApplyAsync de <paramref name="id"/> est terminé (quelle qu'en soit l'issue).</summary>
    public void Observe(string id, Task applyTask)
    {
        ArgumentNullException.ThrowIfNull(applyTask);
        _ = applyTask.ContinueWith(_ => Settle(id, State.Refused), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Complete(IReadOnlyDictionary<string, TResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results.TrySetResult(new Dictionary<string, TResult>(results, StringComparer.OrdinalIgnoreCase));
    }

    private void Settle(string id, State state)
    {
        List<string>? ready = null;
        lock (_gate)
        {
            // Une modification déjà consignée n'est jamais requalifiée « refusée » à la fin de son ApplyAsync.
            if (!_states.TryGetValue(id, out var current) || current != State.Waiting) return;
            _states[id] = state;
            _settled++;
            if (_settled == _order.Count) ready = _order.Where(i => _states[i] == State.Arrived).ToList();
        }
        if (ready is not null) _ready.TrySetResult(ready);
    }
}
