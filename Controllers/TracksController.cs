using GpsTcpProxy.Models;
using GpsTcpProxy.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Authorize]
[Route("api/tracks")]
public sealed class TracksController : ControllerBase
{
    private readonly TrackQueryService _trackQueryService;

    public TracksController(TrackQueryService trackQueryService)
    {
        _trackQueryService = trackQueryService;
    }

    [HttpGet("{deviceId}")]
    public ActionResult<TrackResponse> GetTrack(
        string deviceId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] bool raw = false)
    {
        if (!DeviceRegistry.Exists(deviceId))
            return NotFound(new { message = "Устройство не найдено" });

        try
        {
            var track = _trackQueryService.GetTrack(deviceId, from, to, raw);
            if (track == null)
                return NotFound(new { message = "Устройство не найдено" });

            return Ok(track);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
