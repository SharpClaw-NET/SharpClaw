using Microsoft.EntityFrameworkCore;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;


public sealed record RuntimeRegistrationDbContextRegistration(
    string SourceId,
    Type DbContextType,
    IReadOnlyList<Type> EntityTypes);
