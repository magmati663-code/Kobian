using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;

namespace Kobian.UI.Controls;

/// <summary>
/// ProtectionBadge (R2.6.3): stan Chronione/Uwaga/Zagrożone/Wstrzymane zawsze jako ikona + tekst + kolor (WCAG 1.4.1).
/// Pseudoklasy :protected/:warning/:atrisk/:paused wybierają tokeny z Theme/Tokens.axaml; klasa „large” = wersja dla nagłówka pulpitu.
/// AutomationProperties.Name = pełny tekst stanu (czytnik ekranu nie dostaje samej ikony).
/// </summary>
public class ProtectionBadge : TemplatedControl
{
    public static readonly StyledProperty<ProtectionState> StateProperty =
        AvaloniaProperty.Register<ProtectionBadge, ProtectionState>(nameof(State));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ProtectionBadge, string?>(nameof(Text));

    static ProtectionBadge()
    {
        StateProperty.Changed.AddClassHandler<ProtectionBadge>((b, _) => b.Update());
        TextProperty.Changed.AddClassHandler<ProtectionBadge>((b, _) => b.Update());
    }

    public ProtectionBadge() => Update();

    public ProtectionState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Tekst stanu; null = domyślna nazwa stanu z zasobów („Chronione”…).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Tekst faktycznie wyświetlany (szablon wiąże się do tej wartości).</summary>
    public string DisplayText => Text ?? Strings.Get("State." + State);

    public static readonly DirectProperty<ProtectionBadge, string> DisplayTextProperty =
        AvaloniaProperty.RegisterDirect<ProtectionBadge, string>(nameof(DisplayText), b => b.DisplayText);

    private string _shown = "";

    private void Update()
    {
        PseudoClasses.Set(":protected", State == ProtectionState.Protected);
        PseudoClasses.Set(":warning", State == ProtectionState.Warning);
        PseudoClasses.Set(":atrisk", State == ProtectionState.AtRisk);
        PseudoClasses.Set(":paused", State == ProtectionState.Paused);
        var old = _shown;
        _shown = DisplayText;
        RaisePropertyChanged(DisplayTextProperty, old, _shown);
        AutomationProperties.SetName(this, _shown);
    }
}
