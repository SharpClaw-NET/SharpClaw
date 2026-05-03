namespace SharpClaw.Modules.DatabaseAccess.Triggers;

/// <summary>
/// Trigger and parameter keys owned by the database-access module's
/// query-rows trigger. String values mirror the legacy
/// <c>TaskTriggerDefinition</c> property names verbatim so persisted
/// binding rows and serialized scripts continue to round-trip.
/// </summary>
public static class DatabaseAccessTriggerKeys
{
    public const string QueryReturnsRows = "QueryReturnsRows";

    // Parameter names — must match the legacy TaskTriggerDefinition
    // property names so existing on-disk JSON keeps deserialising.
    public const string SqlQuery              = "SqlQuery";
    public const string QueryPollIntervalSecs = "QueryPollIntervalSecs";
}
