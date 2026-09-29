using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.Common;

/// <summary>
/// Base des ViewModels de page : état occupé / erreur, cycle de navigation, exécution sûre
/// (aucune exception ne remonte à l'UI), marshaling vers le thread UI.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, INavigationAware
{
    private CancellationTokenSource _pageCts = new();
    private int _busyCount;

    protected ViewModelBase(ViewModelContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Logger = context.LoggerFactory.CreateLogger(GetType());
    }

    protected ViewModelContext Context { get; }

    protected ILocalizer Localizer => Context.Localizer;

    protected IValueFormatter Formatter => Context.Formatter;

    protected INavigationService Navigation => Context.Navigation;

    protected IDialogService Dialogs => Context.Dialogs;

    protected ILogger Logger { get; }

    /// <summary>Jeton annulé quand l'utilisateur quitte la page.</summary>
    protected CancellationToken PageToken => _pageCts.Token;

    /// <summary>La page est actuellement affichée (entre OnNavigatedToAsync et OnNavigatedFrom).</summary>
    public bool IsActive { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; protected set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Message de réussite ou d'information non bloquant (ex. « 3 modifications restaurées. »).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; protected set; }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public async Task OnNavigatedToAsync(object? parameter)
    {
        if (_pageCts.IsCancellationRequested)
        {
            _pageCts.Dispose();
            _pageCts = new CancellationTokenSource();
        }

        IsActive = true;
        await RunSafeAsync(ct => OnActivatedAsync(parameter, ct), trackBusy: false).ConfigureAwait(true);
    }

    public void OnNavigatedFrom()
    {
        IsActive = false;
        try
        {
            OnDeactivated();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogWarning(ex, "Erreur lors de la sortie de la page {Page}", GetType().Name);
        }
        finally
        {
            _pageCts.Cancel();
        }
    }

    /// <summary>Chargement de la page. Le jeton est annulé quand l'utilisateur quitte la page.</summary>
    protected virtual Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Désabonnements et arrêt des rafraîchissements.</summary>
    protected virtual void OnDeactivated()
    {
    }

    [RelayCommand]
    private void DismissError() => ErrorText = null;

    [RelayCommand]
    private void DismissStatus() => StatusMessage = null;

    /// <summary>
    /// Exécute <paramref name="action"/> en capturant toute exception : l'annulation est silencieuse,
    /// les autres erreurs deviennent un message localisé (<c>Error_&lt;kind&gt;</c>). Retourne true en cas de succès.
    /// </summary>
    protected async Task<bool> RunSafeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default, bool trackBusy = true, bool linkToPage = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var linked = linkToPage
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, PageToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (trackBusy) EnterBusy();
        try
        {
            await action(linked.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogError(ex, "Erreur dans {ViewModel}", GetType().Name);
            ErrorText = DescribeError(OperationResult.FromException(ex));
            return false;
        }
        finally
        {
            if (trackBusy) ExitBusy();
        }
    }

    /// <summary>Message humain pour un résultat en échec (message du service ou <c>Error_&lt;kind&gt;</c>).</summary>
    protected string DescribeError(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Message is not null) return Localizer.Format(result.Message);
        var kind = result.Error == OperationErrorKind.None ? OperationErrorKind.Failed : result.Error;
        return Localizer.Get($"Error_{kind}");
    }

    /// <summary>Affiche l'erreur d'un résultat en échec dans <see cref="ErrorText"/>. Retourne true si le résultat est un succès.</summary>
    protected bool CheckResult(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success) return true;
        if (result.Error == OperationErrorKind.Cancelled) return false;
        ErrorText = DescribeError(result);
        if (result.TechnicalDetail is not null)
            Logger.LogWarning("Opération échouée ({Kind}) : {Detail}", result.Error, result.TechnicalDetail);
        return false;
    }

    protected void EnterBusy()
    {
        _busyCount++;
        IsBusy = true;
    }

    protected void ExitBusy()
    {
        _busyCount = Math.Max(0, _busyCount - 1);
        IsBusy = _busyCount > 0;
    }

    /// <summary>Exécute sur le thread UI (immédiatement si déjà dessus).</summary>
    protected void OnUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Context.Dispatcher.HasThreadAccess) action();
        else Context.Dispatcher.Post(action);
    }

    protected string T(string key) => Localizer.Get(key);

    protected string T(string key, params object[] args) => Localizer.Format(key, args);

    protected string T(TextRef? text) => text is null ? string.Empty : Localizer.Format(text);

    protected IProgress<TValue> UiProgress<TValue>(Action<TValue> handler) => new DispatcherProgress<TValue>(Context.Dispatcher, handler);
}
