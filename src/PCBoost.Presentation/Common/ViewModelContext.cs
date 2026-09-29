using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.Common;

/// <summary>
/// Dépendances communes à tous les ViewModels (localisation, formatage, thread UI, navigation, dialogues, journalisation).
/// Enregistré en Singleton.
/// </summary>
public sealed class ViewModelContext
{
    public ViewModelContext(
        ILocalizer localizer,
        IValueFormatter formatter,
        IUiDispatcher dispatcher,
        INavigationService navigation,
        IDialogService dialogs,
        ILoggerFactory loggerFactory,
        IClock? clock = null)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        Formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        Clock = clock ?? new SystemClock();
    }

    public ILocalizer Localizer { get; }

    public IValueFormatter Formatter { get; }

    public IUiDispatcher Dispatcher { get; }

    public INavigationService Navigation { get; }

    public IDialogService Dialogs { get; }

    public ILoggerFactory LoggerFactory { get; }

    public IClock Clock { get; }
}
