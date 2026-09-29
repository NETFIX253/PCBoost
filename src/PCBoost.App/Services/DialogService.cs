using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.App.Helpers;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.App.Services;

/// <summary>Dialogues Fluent (ContentDialog), un seul à la fois. Messages en langage humain (§79).</summary>
public sealed class DialogService : IDialogService
{
    private readonly ILocalizer _localizer;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Func<XamlRoot?>? _xamlRoot;

    public DialogService(ILocalizer localizer, ISettingsService settings)
    {
        _localizer = localizer;
        _settings = settings;
    }

    public void Attach(Func<XamlRoot?> xamlRoot) => _xamlRoot = xamlRoot;

    public async Task<DialogResultKind> ConfirmAsync(ConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new StackPanel { Spacing = 12, MaxWidth = 560 };
        body.Children.Add(Paragraph(_localizer.Format(request.Message)));
        if (request.Details is { Count: > 0 } details)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var detail in details)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top });
                var text = Paragraph(_localizer.Format(detail));
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }

        var dialog = new ContentDialog
        {
            Title = _localizer.Format(request.Title),
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = _localizer.Format(request.PrimaryButton),
            CloseButtonText = request.CloseButton is null ? _localizer.Get("Common_Action_Cancel") : _localizer.Format(request.CloseButton),
            DefaultButton = request.IsDestructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (request.SecondaryButton is not null) dialog.SecondaryButtonText = _localizer.Format(request.SecondaryButton);
        if (request.IsDestructive && Application.Current.Resources.TryGetValue("DangerButtonStyle", out var danger) && danger is Style style)
            dialog.PrimaryButtonStyle = style;
        else if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            dialog.PrimaryButtonStyle = accentStyle;

        var result = await ShowAsync(dialog).ConfigureAwait(true);
        return result switch
        {
            ContentDialogResult.Primary => DialogResultKind.Primary,
            ContentDialogResult.Secondary => DialogResultKind.Secondary,
            _ => DialogResultKind.Cancel,
        };
    }

    public async Task ShowErrorAsync(OperationResult result, TextRef? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var body = new StackPanel { Spacing = 10, MaxWidth = 560 };
        if (context is not null) body.Children.Add(Paragraph(_localizer.Format(context)));
        var message = result.Message is not null ? _localizer.Format(result.Message) : _localizer.Get($"Error_{result.Error}");
        body.Children.Add(Paragraph(message));
        if (_settings.Current.ExpertMode && !string.IsNullOrWhiteSpace(result.TechnicalDetail))
        {
            body.Children.Add(new TextBlock
            {
                Text = _localizer.Format("App_Dialog_TechnicalDetail", result.TechnicalDetail),
                Style = (Style)Application.Current.Resources["DialogDetailTextStyle"],
            });
        }

        var dialog = new ContentDialog
        {
            Title = _localizer.Get("Common_Label_ErrorTitle"),
            Content = body,
            CloseButtonText = _localizer.Get("App_Dialog_Ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog).ConfigureAwait(true);
    }

    public async Task ShowMessageAsync(TextRef title, TextRef message)
    {
        var dialog = new ContentDialog
        {
            Title = _localizer.Format(title),
            Content = Paragraph(_localizer.Format(message)),
            CloseButtonText = _localizer.Get("App_Dialog_Ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog).ConfigureAwait(true);
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var root = _xamlRoot?.Invoke();
        if (root is null) return ContentDialogResult.None;
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            dialog.XamlRoot = root;
            if (root.Content is FrameworkElement fe) dialog.RequestedTheme = fe.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };
}
