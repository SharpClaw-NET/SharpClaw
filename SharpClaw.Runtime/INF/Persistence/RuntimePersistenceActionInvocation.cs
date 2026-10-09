using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;


public sealed record RuntimePersistenceActionInvocation(
    SharpClawActionKey ActionKey,
    int AddedCount,
    int ModifiedCount,
    int DeletedCount);
