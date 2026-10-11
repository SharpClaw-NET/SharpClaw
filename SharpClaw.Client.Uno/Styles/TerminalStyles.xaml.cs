namespace SharpClaw.Styles;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public TerminalStyles XAML type and its framework constructor/binding contracts. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed partial class TerminalStyles : Microsoft.UI.Xaml.ResourceDictionary
{
    public TerminalStyles()
    {
        InitializeComponent();
    }
}
