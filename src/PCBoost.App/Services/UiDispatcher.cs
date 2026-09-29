using Microsoft.UI.Dispatching;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.App.Services;

/// <summary>Marshaling vers le thread UI via le DispatcherQueue de la fenêtre principale.</summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public UiDispatcher(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess) action();
        else _queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
    }

    public Task InvokeAsync(Func<Task> action)
    {
        if (_queue.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!queued) tcs.TrySetCanceled();
        return tcs.Task;
    }
}
