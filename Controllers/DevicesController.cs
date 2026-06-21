using System.Security.Claims;
using GpsTcpProxy.Models;
using GpsTcpProxy.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly ServiceStatusService _serviceStatusService;

    public DevicesController(ServiceStatusService serviceStatusService)
    {
        _serviceStatusService = serviceStatusService;
    }

    [HttpGet]
    public ActionResult<DevicesResponse> GetAll()
    {
        var username = User.FindFirstValue(ClaimTypes.Name);
        return Ok(_serviceStatusService.GetDevicesStatus(username));
    }
}
