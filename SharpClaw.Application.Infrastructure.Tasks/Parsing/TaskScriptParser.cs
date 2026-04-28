using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpClaw.Application.Infrastructure.Tasks.Models;
using SharpClaw.Application.Infrastructure.Tasks.Registry;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Infrastructure.Tasks.Parsing;

/// <summary>
/// Parses task script .cs files into <see cref="TaskScriptDefinition"/>.
/// Uses Roslyn to parse the C# syntax tree, then extracts the task
/// metadata, parameters, data types, and entry-point body steps.
/// <para>
/// <b>Allowed subset</b>:
/// <list type="bullet">
///   <item>One public class with <c>[Task("name")]</c> attribute</item>
///   <item>Public properties = task parameters</item>
///   <item>Nested public classes = data types</item>
///   <item>One <c>public async Task RunAsync(CancellationToken ct)</c> entry point</item>
///   <item>Restricted statement set in body (no arbitrary C# allowed)</item>
/// </list>
/// </para>
/// </summary>
public sealed class TaskScriptParser
{
    // ── Module extension registry ─────────────────────────────────

    private static readonly Dictionary<string, (string StepKey, string ModuleId)> _moduleStepKeys = [];
    private static readonly Dictionary<string, (string TriggerKey, string ModuleId)> _moduleEventTriggers = [];
    private static readonly HashSet<string> _moduleSingleArgMethods = [];
    private static readonly Lock _registryLock = new();

    /// <summary>
    /// Register a module's parser extension. Safe to call multiple times
    /// (duplicate method names for the same module are ignored).
    /// Call from <c>ISharpClawModule.ConfigureServices</c>.
    /// </summary>
    public static void RegisterModule(ITaskParserModuleExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        lock (_registryLock)
        {
            foreach (var (method, entry) in extension.StepKeyMappings)
            {
                _moduleStepKeys.TryAdd(method, entry);

                // Register a descriptor in the unified step registry so that
                // TryParseContextApiCall resolves module steps through the same
                // path as core steps.
                TaskStepRegistry.Default.Register(new TaskStepDescriptor
                {
                    MethodName           = method,
                    StepKey              = entry.StepKey,
                    OwnerId              = entry.ModuleId,
                    FirstArgIsExpression = extension.SingleArgExpressionMethods.Contains(method),
                });
            }
            foreach (var (method, entry) in extension.EventTriggerMappings)
                _moduleEventTriggers.TryAdd(method, entry);
            foreach (var method in extension.SingleArgExpressionMethods)
                _moduleSingleArgMethods.Add(method);
        }
    }

    /// <summary>
    /// Parse a task script .cs file into its structured definition.
    /// </summary>
    public static TaskScriptParseResult Parse(string sourceText)
    {
        var diagnostics = new List<TaskDiagnostic>();

        // Parse with Roslyn
        var tree = CSharpSyntaxTree.ParseText(sourceText, new CSharpParseOptions(LanguageVersion.CSharp14));
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Collect Roslyn syntax errors
        foreach (var diag in tree.GetDiagnostics())
        {
            if (diag.Severity == DiagnosticSeverity.Error)
            {
                var lineSpan = diag.Location.GetLineSpan();
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    diag.Id,
                    diag.GetMessage(),
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character));
            }
        }

        if (diagnostics.Any(d => d.Severity == TaskDiagnosticSeverity.Error))
        {
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Find the task class
        var taskClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                                 HasTaskAttribute(c));

        if (taskClass is null)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK001",
                "No public class with [Task(\"name\")] attribute found."));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Extract task name from attribute
        var taskName = ExtractTaskName(taskClass);
        if (string.IsNullOrWhiteSpace(taskName))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK002",
                "Task attribute must specify a non-empty name: [Task(\"YourTaskName\")].",
                GetLine(taskClass)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Extract optional description
        var description = ExtractDescription(taskClass);

        // Extract parameters (public properties on the task class)
        var parameters = ExtractParameters(taskClass, diagnostics);

        // Extract data types (nested public classes)
        var dataTypes = ExtractDataTypes(taskClass, diagnostics);

        // Find output type (data type marked with [Output] attribute)
        var outputType = dataTypes.FirstOrDefault(dt => dt.IsOutputType);

        // Extract [AgentOutput("format")] from the task class
        var agentOutputFormat = ExtractAgentOutputFormat(taskClass);

        // Extract [ToolCall("name")] methods
        var toolCallHooks = ExtractToolCallHooks(taskClass, diagnostics);

        // Extract environment requirements ([RequiresProvider], [ModelId], etc.)
        var requirements = ExtractRequirements(taskClass, parameters, diagnostics);

        // Extract self-registration trigger bindings ([Schedule], [OnEvent], etc.)
        var triggerDefinitions = ExtractTriggerDefinitions(taskClass, diagnostics);

        // Find entry point: public async Task RunAsync(CancellationToken ct)
        var entryPoint = taskClass.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "RunAsync" &&
                                 m.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                                 m.Modifiers.Any(SyntaxKind.AsyncKeyword));

        if (entryPoint is null)
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK003",
                "Task class must have: public async Task RunAsync(CancellationToken ct)",
                GetLine(taskClass)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Validate entry-point signature
        if (entryPoint.ParameterList.Parameters.Count != 1 ||
            entryPoint.ParameterList.Parameters[0].Type?.ToString() != "CancellationToken")
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK004",
                "RunAsync must have exactly one parameter: CancellationToken ct",
                GetLine(entryPoint)));
            return new TaskScriptParseResult(null, diagnostics);
        }

        // Parse body statements
        var steps = new List<TaskStepDefinition>();
        if (entryPoint.Body is not null)
        {
            foreach (var statement in entryPoint.Body.Statements)
            {
                var step = ParseStatement(statement, diagnostics);
                if (step is not null)
                {
                    steps.Add(step);
                }
            }
        }

        var definition = new TaskScriptDefinition
        {
            Name = taskName,
            Description = description,
            SourceText = sourceText,
            ClassName = taskClass.Identifier.Text,
            EntryPointMethod = entryPoint.Identifier.Text,
            Parameters = parameters,
            DataTypes = dataTypes,
            OutputType = outputType,
            Steps = steps,
            ToolCallHooks = toolCallHooks,
            AgentOutputFormat = agentOutputFormat,
            Requirements = requirements,
            TriggerDefinitions = triggerDefinitions,
        };

        return new TaskScriptParseResult(definition, diagnostics);
    }

    // ── Attribute helpers ─────────────────────────────────────────

    private static bool HasTaskAttribute(ClassDeclarationSyntax classSyntax)
    {
        return classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString() is "Task" or "TaskAttribute");
    }

    private static string? ExtractTaskName(ClassDeclarationSyntax classSyntax)
    {
        var taskAttr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Task" or "TaskAttribute");

        if (taskAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var arg = taskAttr.ArgumentList.Arguments[0];
            if (arg.Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is string name)
            {
                return name;
            }
        }
        return null;
    }

    private static string? ExtractDescription(ClassDeclarationSyntax classSyntax)
    {
        var descAttr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Description" or "DescriptionAttribute");

        if (descAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var arg = descAttr.ArgumentList.Arguments[0];
            if (arg.Expression is LiteralExpressionSyntax literal &&
                literal.Token.Value is string desc)
            {
                return desc;
            }
        }
        return null;
    }

    private static string? ExtractAgentOutputFormat(ClassDeclarationSyntax classSyntax)
    {
        var attr = classSyntax.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "AgentOutput" or "AgentOutputAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0 &&
            attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
            literal.Token.Value is string format)
        {
            return format;
        }
        return null;
    }

    // ── Requirement extraction ────────────────────────────────────

    /// <summary>
    /// Walk the class-level attributes and collect all requirement declarations:
    /// [RequiresProvider], [RequiresModule], [RecommendsModule], [RequiresPlatform],
    /// [RequiresModel], [RequiresModelCapability], [RequiresPermission].
    /// Property-level annotations ([ModelId], [RequiresCapability]) are appended
    /// after the class-level pass (this method is called with the pre-extracted
    /// parameters so their names are already available).
    /// </summary>
    private static IReadOnlyList<TaskRequirementDefinition> ExtractRequirements(
        ClassDeclarationSyntax classSyntax,
        IReadOnlyList<TaskParameterDefinition> parameters,
        List<TaskDiagnostic> diagnostics)
    {
        var requirements = new List<TaskRequirementDefinition>();

        // ── class-level attributes ────────────────────────────────
        foreach (var attr in classSyntax.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attrName = attr.Name.ToString();
            var line = GetLine(attr);

            switch (attrName)
            {
                case "RequiresProvider" or "RequiresProviderAttribute":
                {
                    var value = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RequiresProvider,
                        Severity = TaskDiagnosticSeverity.Error,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }

                case "RequiresModelCapability" or "RequiresModelCapabilityAttribute":
                {
                    var cap = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind            = TaskRequirementKind.RequiresModelCapability,
                        Severity        = TaskDiagnosticSeverity.Error,
                        CapabilityValue = cap,
                        Line            = line,
                    });
                    break;
                }

                case "RequiresModel" or "RequiresModelAttribute":
                {
                    var value = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RequiresModel,
                        Severity = TaskDiagnosticSeverity.Error,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }

                case "RequiresModule" or "RequiresModuleAttribute":
                {
                    var value = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RequiresModule,
                        Severity = TaskDiagnosticSeverity.Error,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }

                case "RecommendsModule" or "RecommendsModuleAttribute":
                {
                    var value = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RecommendsModule,
                        Severity = TaskDiagnosticSeverity.Warning,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }

                case "RequiresPlatform" or "RequiresPlatformAttribute":
                {
                    // Argument may be:
                    //   a string literal:       [RequiresPlatform("Windows")]
                    //   a member-access:        [RequiresPlatform(TaskPlatform.Windows)]
                    //   a bitwise-or expr:      [RequiresPlatform(TaskPlatform.Windows | TaskPlatform.Linux)]
                    // For string literals use the parsed token value (unquoted).
                    // For enum / bitwise expressions strip the "TaskPlatform." prefix.
                    string? value;
                    var firstArg = attr.ArgumentList?.Arguments.FirstOrDefault();
                    if (firstArg?.Expression is LiteralExpressionSyntax platformLit &&
                        platformLit.Token.Value is string literalValue)
                    {
                        value = literalValue;
                    }
                    else
                    {
                        var rawArg = firstArg?.Expression.ToString();
                        value = rawArg?
                            .Replace("TaskPlatform.", string.Empty)
                            .Replace(" ", string.Empty);
                    }
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RequiresPlatform,
                        Severity = TaskDiagnosticSeverity.Error,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }

                case "RequiresPermission" or "RequiresPermissionAttribute":
                {
                    var value = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind     = TaskRequirementKind.RequiresPermission,
                        Severity = TaskDiagnosticSeverity.Error,
                        Value    = value,
                        Line     = line,
                    });
                    break;
                }
            }
        }

        // ── property-level annotations ────────────────────────────
        foreach (var property in classSyntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var propName = property.Identifier.Text;
            var propLine = GetLine(property);

            foreach (var attr in property.AttributeLists.SelectMany(al => al.Attributes))
            {
                var attrName = attr.Name.ToString();

                if (attrName is "ModelId" or "ModelIdAttribute")
                {
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind          = TaskRequirementKind.ModelIdParameter,
                        Severity      = TaskDiagnosticSeverity.Error,
                        ParameterName = propName,
                        Line          = propLine,
                    });
                }
                else if (attrName is "RequiresCapability" or "RequiresCapabilityAttribute")
                {
                    var cap = ExtractFirstStringArg(attr);
                    requirements.Add(new TaskRequirementDefinition
                    {
                        Kind            = TaskRequirementKind.RequiresCapabilityParameter,
                        Severity        = TaskDiagnosticSeverity.Error,
                        CapabilityValue = cap,
                        ParameterName   = propName,
                        Line            = propLine,
                    });
                }
            }
        }

        return requirements;
    }

    // ── Trigger definitions ───────────────────────────────────────

    private static IReadOnlyList<TaskTriggerDefinition> ExtractTriggerDefinitions(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var triggers = new List<TaskTriggerDefinition>();
        TriggerConcurrency? concurrencyOverride = null;
        int concurrencyCount = 0;
        bool hasWebhook = false;

        // First pass: detect [OnWebhook] presence for TASK428 check
        foreach (var attr in classSyntax.AttributeLists.SelectMany(al => al.Attributes))
        {
            if (attr.Name.ToString() is "OnWebhook" or "OnWebhookAttribute")
                hasWebhook = true;
        }

        // Second pass: collect [WebhookSecret] without [OnWebhook] warning
        foreach (var attr in classSyntax.AttributeLists.SelectMany(al => al.Attributes))
        {
            if (attr.Name.ToString() is "WebhookSecret" or "WebhookSecretAttribute" && !hasWebhook)
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Warning,
                    "TASK428",
                    "[WebhookSecret] is present but no [OnWebhook] attribute was found on this class.",
                    GetLine(attr)));
            }
        }

        foreach (var attr in classSyntax.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attrName = attr.Name.ToString();
            var line = GetLine(attr);

            switch (attrName)
            {
                case "Schedule" or "ScheduleAttribute":
                {
                    var cron = ExtractFirstStringArg(attr);
                    var tz   = GetNamedStringArg(attr, "Timezone");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey     = WellKnownTriggerKeys.Cron,
                        CronExpression = cron,
                        CronTimezone   = tz,
                        Line           = line,
                    });
                    break;
                }

                case "OnEvent" or "OnEventAttribute":
                {
                    var eventType = ExtractFirstStringArg(attr);
                    var filter    = GetNamedStringArg(attr, "Filter");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = WellKnownTriggerKeys.Event,
                        EventType   = eventType,
                        EventFilter = filter,
                        Line        = line,
                    });
                    break;
                }

                case "OnFileChanged" or "OnFileChangedAttribute":
                {
                    var path    = ExtractFirstStringArg(attr);
                    var pattern = GetNamedStringArg(attr, "Pattern");
                    var events  = GetNamedEnumArg<FileWatchEvent>(attr, "Events") ?? FileWatchEvent.Any;
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = WellKnownTriggerKeys.FileChanged,
                        WatchPath   = path,
                        FilePattern = pattern,
                        FileEvents  = events,
                        Line        = line,
                    });
                    break;
                }

                case "OnProcessStarted" or "OnProcessStartedAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "ProcessStarted",
                        ProcessName = ExtractFirstStringArg(attr),
                        Line        = line,
                    });
                    break;
                }

                case "OnProcessStopped" or "OnProcessStoppedAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "ProcessStopped",
                        ProcessName = ExtractFirstStringArg(attr),
                        Line        = line,
                    });
                    break;
                }

                case "OnWebhook" or "OnWebhookAttribute":
                {
                    var route   = ExtractFirstStringArg(attr);
                    var secret  = GetNamedStringArg(attr, "Secret");
                    var sigHdr  = GetNamedStringArg(attr, "SignatureHeader");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey             = WellKnownTriggerKeys.Webhook,
                        WebhookRoute           = route,
                        WebhookSecretEnvVar    = secret,
                        WebhookSignatureHeader = sigHdr,
                        Line                   = line,
                    });
                    break;
                }

                case "OnHostReachable" or "OnHostReachableAttribute":
                {
                    var host = ExtractFirstStringArg(attr);
                    var port = GetNamedIntArg(attr, "Port");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey = WellKnownTriggerKeys.HostReachable,
                        HostName = host,
                        HostPort = port,
                        Line     = line,
                    });
                    break;
                }

                case "OnHostUnreachable" or "OnHostUnreachableAttribute":
                {
                    var host = ExtractFirstStringArg(attr);
                    var port = GetNamedIntArg(attr, "Port");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey = WellKnownTriggerKeys.HostUnreachable,
                        HostName = host,
                        HostPort = port,
                        Line     = line,
                    });
                    break;
                }

                case "OnTaskCompleted" or "OnTaskCompletedAttribute":
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey     = WellKnownTriggerKeys.TaskCompleted,
                        SourceTaskName = ExtractFirstStringArg(attr),
                        Line           = line,
                    });
                    break;

                case "OnTaskFailed" or "OnTaskFailedAttribute":
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey     = WellKnownTriggerKeys.TaskFailed,
                        SourceTaskName = ExtractFirstStringArg(attr),
                        Line           = line,
                    });
                    break;

                case "OnWindowFocused" or "OnWindowFocusedAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "WindowFocused",
                        ProcessName = ExtractFirstStringArg(attr),
                        Line        = line,
                    });
                    break;
                }

                case "OnWindowBlurred" or "OnWindowBlurredAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "WindowBlurred",
                        ProcessName = ExtractFirstStringArg(attr),
                        Line        = line,
                    });
                    break;
                }

                case "OnHotkey" or "OnHotkeyAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    var combo = ExtractFirstStringArg(attr);
                    if (!IsHotkeyComboValid(combo))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK429",
                            $"[OnHotkey] key combination \"{combo}\" could not be parsed. " +
                            "Expected format: \"Modifier+Key\" (e.g. \"Ctrl+Shift+F10\").",
                            line));
                    }
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "Hotkey",
                        HotkeyCombo = combo,
                        Line        = line,
                    });
                    break;
                }

                case "OnSystemIdle" or "OnSystemIdleAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey  = "SystemIdle",
                        IdleMinutes = GetNamedIntArg(attr, "Minutes") ?? ExtractFirstIntArg(attr),
                        Line        = line,
                    });
                    break;
                }

                case "OnSystemActive" or "OnSystemActiveAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey = "SystemActive",
                        Line       = line,
                    });
                    break;
                }

                case "OnScreenLocked" or "OnScreenLockedAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey = "ScreenLocked",
                        Line       = line,
                    });
                    break;
                }

                case "OnScreenUnlocked" or "OnScreenUnlockedAttribute":
                {
                    EmitPlatformWarningIfNeeded(attrName, line, diagnostics);
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey = "ScreenUnlocked",
                        Line       = line,
                    });
                    break;
                }

                case "OnNetworkChanged" or "OnNetworkChangedAttribute":
                {
                    var ssid  = GetNamedStringArg(attr, "Ssid");
                    var state = GetNamedEnumArg<NetworkState>(attr, "State") ?? NetworkState.Any;
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey   = WellKnownTriggerKeys.NetworkChanged,
                        NetworkSsid  = ssid,
                        NetworkState = state,
                        Line         = line,
                    });
                    break;
                }

                case "OnDeviceConnected" or "OnDeviceConnectedAttribute":
                {
                    var devClass   = GetNamedStringArg(attr, "Class");
                    var devPattern = GetNamedStringArg(attr, "Pattern");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey        = "DeviceConnected",
                        DeviceClass       = devClass,
                        DeviceNamePattern = devPattern,
                        Line              = line,
                    });
                    break;
                }

                case "OnDeviceDisconnected" or "OnDeviceDisconnectedAttribute":
                {
                    var devClass   = GetNamedStringArg(attr, "Class");
                    var devPattern = GetNamedStringArg(attr, "Pattern");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey        = "DeviceDisconnected",
                        DeviceClass       = devClass,
                        DeviceNamePattern = devPattern,
                        Line              = line,
                    });
                    break;
                }

                case "OnQueryReturnsRows" or "OnQueryReturnsRowsAttribute":
                {
                    var query    = ExtractFirstStringArg(attr);
                    var interval = GetNamedIntArg(attr, "PollInterval");
                    if (!IsSelectCountQuery(query))
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Warning,
                            "TASK431",
                            "[OnQueryReturnsRows] query should be a SELECT COUNT(*) expression " +
                            "to avoid unintended side-effects.",
                            line));
                    }
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey            = "QueryReturnsRows",
                        SqlQuery              = query,
                        QueryPollIntervalSecs = interval,
                        Line                  = line,
                    });
                    break;
                }

                case "OnMetricThreshold" or "OnMetricThresholdAttribute":
                {
                    var source    = ExtractFirstStringArg(attr);
                    var threshold = GetNamedDoubleArg(attr, "Threshold");
                    var direction = GetNamedEnumArg<ThresholdDirection>(attr, "Direction") ?? ThresholdDirection.Either;
                    var interval  = GetNamedIntArg(attr, "PollInterval");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey             = WellKnownTriggerKeys.MetricThreshold,
                        MetricSource           = source,
                        MetricThreshold        = threshold,
                        MetricDirection        = direction,
                        MetricPollIntervalSecs = interval,
                        Line                   = line,
                    });
                    break;
                }

                case "OnStartup" or "OnStartupAttribute":
                    triggers.Add(new TaskTriggerDefinition { TriggerKey = WellKnownTriggerKeys.Startup, Line = line });
                    break;

                case "OnShutdown" or "OnShutdownAttribute":
                    triggers.Add(new TaskTriggerDefinition { TriggerKey = WellKnownTriggerKeys.Shutdown, Line = line });
                    break;

                case "OsShortcut" or "OsShortcutAttribute":
                {
                    var label    = ExtractFirstStringArg(attr);
                    var icon     = GetNamedStringArg(attr, "Icon");
                    var category = GetNamedStringArg(attr, "Category");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey       = "OsShortcut",
                        ShortcutLabel    = label,
                        ShortcutIcon     = icon,
                        ShortcutCategory = category,
                        Line             = line,
                    });
                    break;
                }

                case "OnTrigger" or "OnTriggerAttribute":
                {
                    var sourceName = ExtractFirstStringArg(attr);
                    var filter     = GetNamedStringArg(attr, "Filter");
                    triggers.Add(new TaskTriggerDefinition
                    {
                        TriggerKey         = sourceName,
                        CustomSourceFilter = filter,
                        Line               = line,
                    });
                    break;
                }

                case "ConcurrencyPolicy" or "ConcurrencyPolicyAttribute":
                {
                    concurrencyCount++;
                    if (concurrencyCount > 1)
                    {
                        diagnostics.Add(new TaskDiagnostic(
                            TaskDiagnosticSeverity.Error,
                            "TASK421",
                            "More than one [ConcurrencyPolicy] attribute found on the same class.",
                            line));
                    }
                    else
                    {
                        concurrencyOverride = GetNamedEnumArg<TriggerConcurrency>(attr, "Policy")
                                           ?? ExtractFirstEnumArg<TriggerConcurrency>(attr);
                    }
                    break;
                }
            }
        }

        // Apply concurrency override to all collected triggers
        if (concurrencyOverride.HasValue)
        {
            for (var i = 0; i < triggers.Count; i++)
                triggers[i] = triggers[i] with { Concurrency = concurrencyOverride.Value };
        }

        return triggers;
    }

    // ── Platform-compatibility helper ─────────────────────────────

    private static readonly HashSet<string> PlatformIncompatibleOnMacOs =
    [
        "OnProcessStarted", "OnProcessStartedAttribute",
        "OnProcessStopped",  "OnProcessStoppedAttribute",
        "OnWindowFocused",   "OnWindowFocusedAttribute",
        "OnWindowBlurred",   "OnWindowBlurredAttribute",
        "OnHotkey",          "OnHotkeyAttribute",
        "OnSystemIdle",      "OnSystemIdleAttribute",
        "OnSystemActive",    "OnSystemActiveAttribute",
        "OnScreenLocked",    "OnScreenLockedAttribute",
        "OnScreenUnlocked",  "OnScreenUnlockedAttribute",
    ];

    private static void EmitPlatformWarningIfNeeded(
        string attrName,
        int line,
        List<TaskDiagnostic> diagnostics)
    {
        if (PlatformIncompatibleOnMacOs.Contains(attrName))
        {
            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Warning,
                "TASK420",
                $"[{attrName.Replace("Attribute", "")}] is not supported on macOS and will be ignored at runtime on that platform.",
                line));
        }
    }

    // ── Hotkey validation ─────────────────────────────────────────

    private static readonly HashSet<string> KnownModifiers =
        ["Ctrl", "Alt", "Shift", "Win", "Meta", "Control", "Windows"];

    private static readonly HashSet<string> KnownKeys =
        ["F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
         "A","B","C","D","E","F","G","H","I","J","K","L","M",
         "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
         "0","1","2","3","4","5","6","7","8","9",
         "Space","Enter","Tab","Escape","Backspace","Delete","Insert",
         "Home","End","PageUp","PageDown","Up","Down","Left","Right",
         "NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
         "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
         "Multiply","Add","Subtract","Divide","Decimal",
         "OemSemicolon","OemPlus","OemComma","OemMinus","OemPeriod",
         "OemOpenBrackets","OemCloseBrackets","OemPipe","OemQuotes","OemBackslash",
         "PrintScreen","Pause","ScrollLock","CapsLock","NumLock"];

    private static bool IsHotkeyComboValid(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo))
            return false;

        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        // Last part must be a key; all preceding parts must be modifiers
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!KnownModifiers.Contains(parts[i]))
                return false;
        }

        return KnownKeys.Contains(parts[^1]);
    }

    // ── Query validation ──────────────────────────────────────────

    private static bool IsSelectCountQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = query.Replace('\n', ' ').Replace('\r', ' ');
        var upper = normalized.Trim().ToUpperInvariant();
        return upper.StartsWith("SELECT COUNT(", StringComparison.Ordinal);
    }

    // ── Named-argument helpers ────────────────────────────────────

    private static string? GetNamedStringArg(AttributeSyntax attr, string name)
    {
        var arg = attr.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
        if (arg?.Expression is LiteralExpressionSyntax lit && lit.Token.Value is string s)
            return s;
        return null;
    }

    private static int? GetNamedIntArg(AttributeSyntax attr, string name)
    {
        var arg = attr.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
        if (arg?.Expression is LiteralExpressionSyntax lit && lit.Token.Value is int i)
            return i;
        return null;
    }

    private static double? GetNamedDoubleArg(AttributeSyntax attr, string name)
    {
        var arg = attr.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
        if (arg?.Expression is LiteralExpressionSyntax lit)
        {
            if (lit.Token.Value is double d) return d;
            if (lit.Token.Value is float  f) return f;
            if (lit.Token.Value is int    i) return i;
        }
        return null;
    }

    private static T? GetNamedEnumArg<T>(AttributeSyntax attr, string name) where T : struct, Enum
    {
        var arg = attr.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
        if (arg is null)
            return null;
        return ParseEnumExpression<T>(arg.Expression);
    }

    private static T? ExtractFirstEnumArg<T>(AttributeSyntax attr) where T : struct, Enum
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (arg is null)
            return null;
        return ParseEnumExpression<T>(arg.Expression);
    }

    private static int? ExtractFirstIntArg(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (arg?.Expression is LiteralExpressionSyntax lit && lit.Token.Value is int i)
            return i;
        return null;
    }

    private static T? ParseEnumExpression<T>(ExpressionSyntax expr) where T : struct, Enum
    {
        // Handle string literal: [ConcurrencyPolicy("Queue")]
        if (expr is LiteralExpressionSyntax lit && lit.Token.Value is string s)
        {
            if (Enum.TryParse<T>(s, ignoreCase: true, out var v)) return v;
            return null;
        }

        // Handle member access: TriggerConcurrency.Queue or just Queue
        var text = expr.ToString();
        var memberName = text.Contains('.') ? text[(text.LastIndexOf('.') + 1)..] : text;

        // Handle bitwise OR for [Flags] enums: FileWatchEvent.Created | FileWatchEvent.Changed
        if (text.Contains('|'))
        {
            var parts = text.Split('|', StringSplitOptions.TrimEntries);
            var result = 0;
            foreach (var part in parts)
            {
                var pName = part.Contains('.') ? part[(part.LastIndexOf('.') + 1)..] : part;
                if (Enum.TryParse<T>(pName, ignoreCase: true, out var pv))
                    result |= Convert.ToInt32(pv);
            }
            return (T)(object)result;
        }

        if (Enum.TryParse<T>(memberName, ignoreCase: true, out var parsed))
            return parsed;

        return null;
    }

    /// <summary>Extracts the first string literal argument from an attribute, or null.</summary>
    private static string? ExtractFirstStringArg(AttributeSyntax attr)
    {
        if (attr.ArgumentList?.Arguments.Count > 0 &&
            attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
            lit.Token.Value is string s)
        {
            return s;
        }
        return null;
    }

    private static IReadOnlyList<TaskToolCallHook> ExtractToolCallHooks(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var hooks = new List<TaskToolCallHook>();

        foreach (var method in classSyntax.Members.OfType<MethodDeclarationSyntax>())
        {
            var toolCallAttr = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a => a.Name.ToString() is "ToolCall" or "ToolCallAttribute");

            if (toolCallAttr is null)
                continue;

            // Extract tool name from attribute argument
            string? toolName = null;
            if (toolCallAttr.ArgumentList?.Arguments.Count > 0 &&
                toolCallAttr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax nameLit &&
                nameLit.Token.Value is string name)
            {
                toolName = name;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                diagnostics.Add(new TaskDiagnostic(
                    TaskDiagnosticSeverity.Error,
                    "TASK020",
                    $"[ToolCall] on method '{method.Identifier.Text}' must specify a non-empty name.",
                    GetLine(method)));
                continue;
            }

            // Extract optional [Description] on the method
            var hookDescription = method.AttributeLists
                .SelectMany(al => al.Attributes)
                .Where(a => a.Name.ToString() is "Description" or "DescriptionAttribute")
                .Select(a => a.ArgumentList?.Arguments.Count > 0 &&
                    a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
                    lit.Token.Value is string desc ? desc : null)
                .FirstOrDefault(d => d is not null);

            // Extract parameters (skip CancellationToken)
            var parameters = new List<TaskToolCallParameter>();
            foreach (var param in method.ParameterList.Parameters)
            {
                var paramType = param.Type?.ToString() ?? "string";
                if (paramType == "CancellationToken")
                    continue;

                var paramName = param.Identifier.Text;

                // Check for [Description] on the parameter
                var paramDesc = param.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Where(a => a.Name.ToString() is "Description" or "DescriptionAttribute")
                    .Select(a => a.ArgumentList?.Arguments.Count > 0 &&
                        a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
                        lit.Token.Value is string d ? d : null)
                    .FirstOrDefault(d => d is not null);

                parameters.Add(new TaskToolCallParameter(paramName, paramType, paramDesc));
            }

            // Parse method body
            var body = new List<TaskStepDefinition>();
            if (method.Body is not null)
            {
                foreach (var statement in method.Body.Statements)
                {
                    var step = ParseStatement(statement, diagnostics);
                    if (step is not null)
                        body.Add(step);
                }
            }

            // Determine return variable: if the last statement is a return
            // with an expression, use that variable name.
            var returnVariable = "$return";
            if (method.Body?.Statements.LastOrDefault() is ReturnStatementSyntax returnStmt
                && returnStmt.Expression is not null)
            {
                returnVariable = returnStmt.Expression.ToString();
            }

            hooks.Add(new TaskToolCallHook
            {
                Name = toolName,
                Description = hookDescription,
                Parameters = parameters,
                Body = body,
                ReturnVariable = returnVariable
            });
        }

        return hooks;
    }

    // ── Parameter extraction ──────────────────────────────────────

    private static IReadOnlyList<TaskParameterDefinition> ExtractParameters(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var parameters = new List<TaskParameterDefinition>();

        foreach (var property in classSyntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var name = property.Identifier.Text;
            var typeName = property.Type.ToString();

            // Check for [Description] and [DefaultValue]
            var description = ExtractPropertyDescription(property);
            var defaultValue = ExtractPropertyDefaultValue(property);

            // Required by default unless [DefaultValue] or initializer is present
            var isRequired = defaultValue is null && property.Initializer is null;

            parameters.Add(new TaskParameterDefinition(
                name,
                typeName,
                description,
                defaultValue,
                isRequired));
        }

        return parameters;
    }

    private static string? ExtractPropertyDescription(PropertyDeclarationSyntax property)
    {
        var attr = property.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "Description" or "DescriptionAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0 &&
            attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit &&
            lit.Token.Value is string desc)
        {
            return desc;
        }
        return null;
    }

    private static string? ExtractPropertyDefaultValue(PropertyDeclarationSyntax property)
    {
        var attr = property.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "DefaultValue" or "DefaultValueAttribute");

        if (attr?.ArgumentList?.Arguments.Count > 0)
        {
            return attr.ArgumentList.Arguments[0].Expression.ToString();
        }

        // Also check for initializer
        if (property.Initializer is not null)
        {
            return property.Initializer.Value.ToString();
        }

        return null;
    }

    // ── Data type extraction ──────────────────────────────────────

    private static IReadOnlyList<TaskDataTypeDefinition> ExtractDataTypes(
        ClassDeclarationSyntax classSyntax,
        List<TaskDiagnostic> diagnostics)
    {
        var dataTypes = new List<TaskDataTypeDefinition>();

        foreach (var nestedClass in classSyntax.Members.OfType<ClassDeclarationSyntax>())
        {
            if (!nestedClass.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            var name = nestedClass.Identifier.Text;
            var properties = new List<TaskPropertyDefinition>();

            foreach (var prop in nestedClass.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!prop.Modifiers.Any(SyntaxKind.PublicKeyword))
                    continue;

                var propName = prop.Identifier.Text;
                var typeName = prop.Type.ToString();
                var defaultValue = prop.Initializer?.Value.ToString();

                // Check if collection type (List<T>, IEnumerable<T>, etc.)
                var isCollection = IsCollectionType(typeName, out var elementType);

                properties.Add(new TaskPropertyDefinition(
                    propName,
                    typeName,
                    defaultValue,
                    isCollection,
                    elementType));
            }

            // Check for [Output] attribute
            var isOutput = nestedClass.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => a.Name.ToString() is "Output" or "OutputAttribute");

            dataTypes.Add(new TaskDataTypeDefinition(name, properties, isOutput));
        }

        return dataTypes;
    }

    private static bool IsCollectionType(string typeName, out string? elementType)
    {
        if (typeName.StartsWith("List<") ||
            typeName.StartsWith("IList<") ||
            typeName.StartsWith("IEnumerable<") ||
            typeName.StartsWith("ICollection<"))
        {
            var start = typeName.IndexOf('<') + 1;
            var end = typeName.LastIndexOf('>');
            if (end > start)
            {
                elementType = typeName.Substring(start, end - start);
                return true;
            }
        }
        elementType = null;
        return false;
    }

    // ── Statement parsing ────────────────────────────────────────

    private static TaskStepDefinition? ParseStatement(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        return statement switch
        {
            LocalDeclarationStatementSyntax local => ParseLocalDeclaration(local, diagnostics),
            ExpressionStatementSyntax expr        => ParseExpressionStatement(expr, diagnostics),
            IfStatementSyntax ifStmt              => ParseIfStatement(ifStmt, diagnostics),
            WhileStatementSyntax whileStmt        => ParseWhileStatement(whileStmt, diagnostics),
            ForEachStatementSyntax forEachStmt    => ParseForEachStatement(forEachStmt, diagnostics),
            ReturnStatementSyntax ret             => ParseReturnStatement(ret),
            _                                     => UnrecognizedStatement(statement, diagnostics)
        };
    }

    // ── Local declaration ─────────────────────────────────────────

    private static TaskStepDefinition? ParseLocalDeclaration(
        LocalDeclarationStatementSyntax local,
        List<TaskDiagnostic> diagnostics)
    {
        var declarator = local.Declaration.Variables.FirstOrDefault();
        if (declarator is null)
            return null;

        var variableName = declarator.Identifier.Text;
        var typeName = local.Declaration.Type.IsVar
            ? null
            : local.Declaration.Type.ToString();
        var line = GetLine(local);
        var column = GetColumn(local);

        // No initializer – bare declaration
        if (declarator.Initializer is null)
        {
            return new TaskStepDefinition
            {
                StepKey  = WellKnownTaskStepKeys.DeclareVariable,
                Line     = line,
                Column   = column,
                VariableName = variableName,
                TypeName = typeName
            };
        }

        var initializer = declarator.Initializer.Value;

        // Unwrap await: var x = await SomeCall(...)
        if (UnwrapAwaitInvocation(initializer) is { } awaitedInvocation)
        {
            var apiStep = TryParseContextApiCall(awaitedInvocation, line, column);
            if (apiStep is not null)
                return apiStep with { ResultVariable = variableName };
        }

        // Non-awaited context API call: var x = FindModel(...), var x = Chat(...)
        if (initializer is InvocationExpressionSyntax directInvocation)
        {
            var apiStep = TryParseContextApiCall(directInvocation, line, column);
            if (apiStep is not null)
                return apiStep with { ResultVariable = variableName };
        }

        // Plain declaration: var x = new Foo(), var x = expr, var x = a ?? await b()
        return new TaskStepDefinition
        {
            StepKey      = WellKnownTaskStepKeys.DeclareVariable,
            Line         = line,
            Column       = column,
            VariableName = variableName,
            TypeName     = typeName,
            Expression   = initializer.ToString()
        };
    }

    // ── Expression statement ──────────────────────────────────────

    private static TaskStepDefinition? ParseExpressionStatement(
        ExpressionStatementSyntax exprStmt,
        List<TaskDiagnostic> diagnostics)
    {
        var expression = exprStmt.Expression;
        var line = GetLine(exprStmt);
        var column = GetColumn(exprStmt);

        // await ContextApiCall(...)
        if (expression is AwaitExpressionSyntax awaitExpr)
        {
            if (awaitExpr.Expression is InvocationExpressionSyntax awaitedInvocation)
            {
                var apiStep = TryParseContextApiCall(awaitedInvocation, line, column);
                if (apiStep is not null)
                    return apiStep;
            }

            diagnostics.Add(new TaskDiagnostic(
                TaskDiagnosticSeverity.Error,
                "TASK010",
                $"Unrecognized await expression: {awaitExpr.Expression}",
                line, column));
            return null;
        }

        // Non-await invocation: event handlers (OnModuleEvent, OnTimer) and Log
        if (expression is InvocationExpressionSyntax invocation)
        {
            var eventStep = TryParseEventHandler(invocation, line, column, diagnostics);
            if (eventStep is not null)
                return eventStep;

            var methodName = GetMethodName(invocation);
            if (methodName == "Log")
            {
                return new TaskStepDefinition
                {
                    StepKey    = WellKnownTaskStepKeys.Log,
                    Line       = line,
                    Column     = column,
                    Expression = ExtractFirstArgText(invocation),
                    Arguments  = ExtractArgumentTexts(invocation)
                };
            }

            // Non-awaited context API call: ChatToThread(...), CreateAgent(...), etc.
            var apiStep = TryParseContextApiCall(invocation, line, column);
            if (apiStep is not null)
                return apiStep;
        }

        // Assignment: x = ..., x.Prop += ..., etc.
        if (expression is AssignmentExpressionSyntax assignment)
        {
            return new TaskStepDefinition
            {
                StepKey      = WellKnownTaskStepKeys.Assign,
                Line         = line,
                Column       = column,
                VariableName = assignment.Left.ToString(),
                Expression   = assignment.Right.ToString()
            };
        }

        // Fallback: arbitrary expression (e.g. list.AddRange(...))
        return new TaskStepDefinition
        {
            StepKey    = WellKnownTaskStepKeys.Evaluate,
            Line       = line,
            Column     = column,
            Expression = expression.ToString()
        };
    }

    // ── Control flow ──────────────────────────────────────────────

    private static TaskStepDefinition ParseIfStatement(
        IfStatementSyntax ifStmt,
        List<TaskDiagnostic> diagnostics)
    {
        var thenBody = ParseBlock(ifStmt.Statement, diagnostics);
        var elseBody = ifStmt.Else is not null
            ? ParseBlock(ifStmt.Else.Statement, diagnostics)
            : null;

        return new TaskStepDefinition
        {
            StepKey    = WellKnownTaskStepKeys.Conditional,
            Line       = GetLine(ifStmt),
            Column     = GetColumn(ifStmt),
            Expression = ifStmt.Condition.ToString(),
            Body       = thenBody,
            ElseBody   = elseBody
        };
    }

    private static TaskStepDefinition ParseWhileStatement(
        WhileStatementSyntax whileStmt,
        List<TaskDiagnostic> diagnostics)
    {
        return new TaskStepDefinition
        {
            StepKey    = WellKnownTaskStepKeys.Loop,
            Line       = GetLine(whileStmt),
            Column     = GetColumn(whileStmt),
            LoopKind   = TaskLoopKind.While,
            Expression = whileStmt.Condition.ToString(),
            Body       = ParseBlock(whileStmt.Statement, diagnostics)
        };
    }

    private static TaskStepDefinition ParseForEachStatement(
        ForEachStatementSyntax forEachStmt,
        List<TaskDiagnostic> diagnostics)
    {
        return new TaskStepDefinition
        {
            StepKey      = WellKnownTaskStepKeys.Loop,
            Line         = GetLine(forEachStmt),
            Column       = GetColumn(forEachStmt),
            LoopKind     = TaskLoopKind.ForEach,
            VariableName = forEachStmt.Identifier.Text,
            TypeName     = forEachStmt.Type.IsVar ? null : forEachStmt.Type.ToString(),
            Expression   = forEachStmt.Expression.ToString(),
            Body         = ParseBlock(forEachStmt.Statement, diagnostics)
        };
    }

    private static TaskStepDefinition ParseReturnStatement(ReturnStatementSyntax ret)
    {
        return new TaskStepDefinition
        {
            StepKey = WellKnownTaskStepKeys.Return,
            Line    = GetLine(ret),
            Column  = GetColumn(ret)
        };
    }

    private static TaskStepDefinition? UnrecognizedStatement(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        diagnostics.Add(new TaskDiagnostic(
            TaskDiagnosticSeverity.Error,
            "TASK011",
            $"Unsupported statement: {statement.Kind()}",
            GetLine(statement),
            GetColumn(statement)));
        return null;
    }

    // ── Context API call recognition ──────────────────────────────

    private static TaskStepDefinition? TryParseContextApiCall(
        InvocationExpressionSyntax invocation,
        int line,
        int column)
    {
        var methodName = GetMethodName(invocation);
        if (methodName is null)
            return null;

        // Task.Delay(...)  — member-access form; must be matched before registry lookup
        // so that bare "Delay" calls to a context method are not confused with Task.Delay.
        if (methodName == "Delay" && IsTaskMemberAccess(invocation))
        {
            return new TaskStepDefinition
            {
                StepKey    = WellKnownTaskStepKeys.Delay,
                Line       = line,
                Column     = column,
                Expression = ExtractFirstArgText(invocation),
                Arguments  = ExtractArgumentTexts(invocation)
            };
        }

        var descriptor = TaskStepRegistry.Default.FindByMethod(methodName);
        if (descriptor is null)
            return null;

        return BuildStepFromDescriptor(descriptor, invocation, line, column, methodName);
    }

    private static TaskStepDefinition BuildStepFromDescriptor(
        TaskStepDescriptor descriptor,
        InvocationExpressionSyntax invocation,
        int line,
        int column,
        string methodName)
    {
        var args = invocation.ArgumentList.Arguments;

        string? expression = null;
        if (descriptor.ExpressionArgIndex > 0 && args.Count > descriptor.ExpressionArgIndex)
            expression = args[descriptor.ExpressionArgIndex].Expression.ToString();
        else if (descriptor.FirstArgIsExpression || descriptor.HttpMethod is not null)
            expression = ExtractFirstArgText(invocation);

        var step = new TaskStepDefinition
        {
            StepKey    = descriptor.StepKey,
            Line       = line,
            Column     = column,
            HttpMethod = descriptor.HttpMethod,
            Expression = expression,
            Arguments  = ExtractArgumentTexts(invocation)
        };

        if (descriptor.CapturesGenericType)
        {
            var typeArg = GetGenericTypeArgument(invocation);
            if (typeArg is not null)
                step = step with { TypeName = typeArg };
        }

        return step;
    }

    // ── Event handler parsing ─────────────────────────────────────

    private static TaskStepDefinition? TryParseEventHandler(
        InvocationExpressionSyntax invocation,
        int line,
        int column,
        List<TaskDiagnostic> diagnostics)
    {
        var methodName = GetMethodName(invocation);
        var triggerKind = ResolveEventTrigger(methodName);
        if (triggerKind is null)
            return null;

        // Non-lambda arguments (e.g. the job variable reference)
        var nonLambdaArgs = invocation.ArgumentList.Arguments
            .Where(a => a.Expression is not
                (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
            .Select(a => a.Expression.ToString())
            .ToList();

        // Find the lambda argument (parenthesized or simple form)
        string? handlerParam = null;
        IReadOnlyList<TaskStepDefinition>? body = null;

        var parenLambda = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<ParenthesizedLambdaExpressionSyntax>()
            .FirstOrDefault();

        if (parenLambda is not null)
        {
            handlerParam = parenLambda.ParameterList.Parameters
                .FirstOrDefault()?.Identifier.Text;
            body = ParseLambdaBody(parenLambda.Body, diagnostics);
        }
        else
        {
            var simpleLambda = invocation.ArgumentList.Arguments
                .Select(a => a.Expression)
                .OfType<SimpleLambdaExpressionSyntax>()
                .FirstOrDefault();

            if (simpleLambda is not null)
            {
                handlerParam = simpleLambda.Parameter.Identifier.Text;
                body = ParseLambdaBody(simpleLambda.Body, diagnostics);
            }
        }

        return new TaskStepDefinition
        {
            StepKey          = WellKnownTaskStepKeys.EventHandler,
            Line             = line,
            Column           = column,
            TriggerKind      = triggerKind.Value,
            ModuleTriggerKey = ResolveModuleTriggerKey(methodName),
            HandlerParameter = handlerParam,
            Arguments        = nonLambdaArgs,
            Body             = body ?? []
        };
    }

    private static IReadOnlyList<TaskStepDefinition> ParseLambdaBody(
        CSharpSyntaxNode body,
        List<TaskDiagnostic> diagnostics)
    {
        if (body is BlockSyntax block)
            return ParseStatements(block.Statements, diagnostics);

        // Expression-bodied lambda → single Evaluate step
        return
        [
            new TaskStepDefinition
            {
                StepKey    = WellKnownTaskStepKeys.Evaluate,
                Line       = GetLine(body),
                Column     = GetColumn(body),
                Expression = body.ToString()
            }
        ];
    }

    // ── Block / statement-list helpers ─────────────────────────────

    private static IReadOnlyList<TaskStepDefinition> ParseBlock(
        StatementSyntax statement,
        List<TaskDiagnostic> diagnostics)
    {
        if (statement is BlockSyntax block)
            return ParseStatements(block.Statements, diagnostics);

        var step = ParseStatement(statement, diagnostics);
        return step is not null ? [step] : [];
    }

    private static IReadOnlyList<TaskStepDefinition> ParseStatements(
        SyntaxList<StatementSyntax> statements,
        List<TaskDiagnostic> diagnostics)
    {
        var steps = new List<TaskStepDefinition>();
        foreach (var stmt in statements)
        {
            var step = ParseStatement(stmt, diagnostics);
            if (step is not null)
                steps.Add(step);
        }
        return steps;
    }

    // ── Syntax extraction helpers ─────────────────────────────────

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax id          => id.Identifier.Text,
            GenericNameSyntax generic         => generic.Identifier.Text,
            MemberAccessExpressionSyntax mem => mem.Name switch
            {
                IdentifierNameSyntax id  => id.Identifier.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                _                        => null
            },
            _ => null
        };
    }

    private static string? GetGenericTypeArgument(InvocationExpressionSyntax invocation)
    {
        var genericName = invocation.Expression switch
        {
            GenericNameSyntax g              => g,
            MemberAccessExpressionSyntax m   => m.Name as GenericNameSyntax,
            _                                => null
        };

        return genericName?.TypeArgumentList.Arguments.Count > 0
            ? genericName.TypeArgumentList.Arguments[0].ToString()
            : null;
    }

    private static bool IsTaskMemberAccess(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax member &&
               member.Expression.ToString() == "Task";
    }

    private static InvocationExpressionSyntax? UnwrapAwaitInvocation(ExpressionSyntax expression)
    {
        if (expression is AwaitExpressionSyntax awaitExpr &&
            awaitExpr.Expression is InvocationExpressionSyntax invocation)
        {
            return invocation;
        }
        return null;
    }

    private static IReadOnlyList<string> ExtractArgumentTexts(InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments
            .Where(a => a.Expression is not
                (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
            .Select(a => ExtractExpressionText(a.Expression))
            .ToList();
    }

    private static string? ExtractFirstArgText(InvocationExpressionSyntax invocation)
    {
        var expression = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return expression is null ? null : ExtractExpressionText(expression);
    }

    private static string ExtractExpressionText(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal && literal.Token.Value is string value
            ? value
            : expression.ToString();

    // ── Lookup tables ─────────────────────────────────────────────


    private static TaskTriggerKind? ResolveEventTrigger(string? methodName)
    {
        if (methodName is null) return null;
        if (methodName == "OnTimer") return TaskTriggerKind.Timer;
        return _moduleEventTriggers.ContainsKey(methodName) ? TaskTriggerKind.ModuleEvent : null;
    }

    internal static string? ResolveModuleTriggerKey(string? methodName)
        => methodName is not null && _moduleEventTriggers.TryGetValue(methodName, out var entry)
            ? entry.TriggerKey : null;

    // ── Position helpers ──────────────────────────────────────────

    private static int GetLine(SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return lineSpan.StartLinePosition.Line + 1;
    }

    private static int GetColumn(SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return lineSpan.StartLinePosition.Character;
    }
}
