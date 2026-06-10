using Curfew.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace Curfew.App;

/// <summary>
/// XAML markup extension that resolves a localized string by key, e.g.
/// <c>Text="{local:Loc Key=setup.title}"</c>. Resolved once when the element is
/// loaded, which is sufficient because the active language is fixed for the
/// lifetime of a dialog.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>Catalog key to look up (see <see cref="Loc"/>).</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.T(Key);
}
