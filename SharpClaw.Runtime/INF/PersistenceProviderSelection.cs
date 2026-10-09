using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Registrations;

namespace SharpClaw.Runtime.INF;


public sealed record PersistenceProviderSelection(ISharpClawPersistenceProvider Provider);
