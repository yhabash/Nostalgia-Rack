using System.Windows;

namespace NostalgiaRack;

/// <summary>
/// An extra on/off setting the skin's buttons can read. "Lit" means the backlit stencil
/// symbol glows pale yellow (for example Play while music plays, or Shuffle while it's on).
/// Usage in XAML: local:SkinProps.Lit="True"; in code: SkinProps.SetLit(button, true).
/// </summary>
public static class SkinProps
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(SkinProps), new FrameworkPropertyMetadata(false));

    public static bool GetLit(DependencyObject element) => (bool)element.GetValue(LitProperty);
    public static void SetLit(DependencyObject element, bool value) => element.SetValue(LitProperty, value);
}
