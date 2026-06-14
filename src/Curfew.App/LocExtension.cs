using Curfew.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace Curfew.App;

/// <summary>XAML markup extension that resolves localized string by key, e.g. <c>Text="{local:Loc Key=setup.title}"</c>. resolved once on element load, enough because active language fixed for dialog lifetime</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>catalog key to look up (see <see cref="Loc"/>)</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.T(Key);
}
