namespace SharpClaw.Runtime.BLL.Services.Auth;

public sealed class JwtOptions
{
    public required string Secret { get; set; }
    public string Issuer { get; set; } = "SharpClaw";
    public string Audience { get; set; } = "SharpClaw";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
