using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GpsTcpProxy.Models;
using Microsoft.IdentityModel.Tokens;

namespace GpsTcpProxy.Services;

public sealed class AuthService
{
    private readonly ProxySettings _settings;

    public AuthService(ProxySettings settings)
    {
        _settings = settings;
    }

    public LoginResponse? Login(LoginRequest request)
    {
        string? username = null;

        if (UserRegistry.TryValidate(request.Username, request.Password, out var normalizedUsername))
            username = normalizedUsername;
        else if (string.Equals(request.Username, _settings.AuthUsername, StringComparison.Ordinal) &&
                 string.Equals(request.Password, _settings.AuthPassword, StringComparison.Ordinal))
            username = request.Username.Trim();

        if (string.IsNullOrEmpty(username))
            return null;

        var expiresAtUtc = DateTime.UtcNow.AddHours(_settings.JwtExpireHours);
        var token = CreateToken(username, expiresAtUtc);

        return new LoginResponse
        {
            Token = token,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    private string CreateToken(string username, DateTime expiresAtUtc)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.JwtIssuer,
            audience: _settings.JwtAudience,
            claims: [new Claim(ClaimTypes.Name, username)],
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
