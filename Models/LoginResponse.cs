namespace GpsTcpProxy.Models;

public sealed class LoginResponse
{
    public required string Token { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
}
