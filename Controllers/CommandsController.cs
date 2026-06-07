using GpsTcpProxy.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class CommandsController : ControllerBase
{
    private readonly MqttService _mqttService;

    public CommandsController(MqttService mqttService)
    {
        _mqttService = mqttService;
    }

    [HttpPost("{deviceId}/command")]
    public async Task<IActionResult> SendCommand(string deviceId, [FromBody] DeviceCommandRequest? request, CancellationToken cancellationToken)
    {
        if (!DeviceRegistry.Exists(deviceId))
            return NotFound(new { error = "Device not found" });

        if (DeviceRegistry.GetProtocol(deviceId) != Models.DeviceProtocol.OwnTracks)
            return BadRequest(new { error = "Commands are supported for OwnTracks devices only" });

        try
        {
            if (request?.RequestLocation == true || string.IsNullOrWhiteSpace(request?.Payload))
            {
                await _mqttService.RequestLocationAsync(deviceId, cancellationToken);
                return Ok(new { status = "sent", action = "reportLocation", deviceId });
            }

            await _mqttService.SendCommandAsync(deviceId, request!.Payload!, cancellationToken);
            return Ok(new { status = "sent", deviceId, payload = request.Payload });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class DeviceCommandRequest
{
    public bool RequestLocation { get; set; }
    public string? Payload { get; set; }
}
