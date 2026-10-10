using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpClaw.Tests.Architecture;

// Produces controlled Hyperlinq warnings through Roslyn's real analyzer pipeline.
// The compiler warnings in GeneratedCodeSuppressionTests come from C# itself.
internal sealed class ControlledGeneratedWarningAnalyzer : DiagnosticAnalyzer
{
    private readonly DiagnosticDescriptor _warning;

    public ControlledGeneratedWarningAnalyzer(string diagnosticId)
    {
        _warning = new DiagnosticDescriptor(
            diagnosticId,
            "Controlled generated-code warning",
            "Controlled generated-code warning",
            "Test fixture",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_warning];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxTreeAction(treeContext => treeContext.ReportDiagnostic(
            Diagnostic.Create(_warning, Location.Create(treeContext.Tree, new TextSpan(0, 0)))));
    }
}
