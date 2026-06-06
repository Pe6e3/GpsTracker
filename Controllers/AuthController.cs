using GpsTcpProxy.Models;
using GpsTcpProxy.Services;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
    {
        var response = _authService.Login(request);
        if (response == null)
            return Unauthorized(new { message = "Неверный логин или пароль" });

        return Ok(response);
    }
}
