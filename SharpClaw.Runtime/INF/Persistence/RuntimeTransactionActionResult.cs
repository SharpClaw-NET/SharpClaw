using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.INF.Persistence;


public readonly record struct RuntimeTransactionActionResult(
    [property: System.Text.Json.Serialization.JsonIgnore] IDbContextTransaction? Transaction)
{
    public bool BeganTransaction { get; init; }

    public static RuntimeTransactionActionResult Completed => new(null);
}
