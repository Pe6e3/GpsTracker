using GpsTcpProxy.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DeviceDto>> GetAll() =>
        Ok(DeviceRegistry.GetAll());
}
