namespace PCBoost.Presentation.Common;

/// <summary>Option d'une liste de choix (ComboBox / RadioButtons) : liaison par index (SelectedIndex).</summary>
public sealed record OptionItem(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Ligne « libellé : valeur » prête à afficher (fiche matériel, rapport, détails).</summary>
public sealed record InfoRowViewModel(string Label, string Value, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string AccessibleName => HasDetail ? $"{Label} : {Value}, {Detail}" : $"{Label} : {Value}";
}

/// <summary>Texte simple avec glyphe facultatif (listes de constats, messages, raisons).</summary>
public sealed record TextItemViewModel(string Text, string? IconGlyph = null, string? Detail = null)
{
    public bool HasIcon => !string.IsNullOrEmpty(IconGlyph);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}
