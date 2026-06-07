using System.Security.Claims;
using GpsTcpProxy.Models;
using GpsTcpProxy.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GpsTcpProxy.Controllers;

[ApiController]
[Authorize]
[Route("api/geofences")]
public sealed class GeofencesController : ControllerBase
{
    private readonly GeofenceService _geofenceService;

    public GeofencesController(GeofenceService geofenceService)
    {
        _geofenceService = geofenceService;
    }

    [HttpGet]
    public ActionResult<GeofencesResponse> GetAll()
    {
        var ownerUser = GetOwnerUser();
        if (string.IsNullOrEmpty(ownerUser))
            return Unauthorized();

        return Ok(new GeofencesResponse
        {
            Geofences = _geofenceService.GetForUser(ownerUser)
        });
    }

    [HttpGet("{id:long}")]
    public ActionResult<GeofenceDto> GetById(long id)
    {
        var ownerUser = GetOwnerUser();
        if (string.IsNullOrEmpty(ownerUser))
            return Unauthorized();

        var geofence = _geofenceService.GetById(id, ownerUser);
        return geofence == null ? NotFound() : Ok(geofence);
    }

    [HttpPost]
    public ActionResult<GeofenceDto> Create([FromBody] GeofenceCreateRequest? request)
    {
        var ownerUser = GetOwnerUser();
        if (string.IsNullOrEmpty(ownerUser))
            return Unauthorized();

        if (request == null)
            return BadRequest(new { error = "Request body is required" });

        try
        {
            var geofence = _geofenceService.Create(ownerUser, request);
            return Created($"/api/geofences/{geofence.Id}", geofence);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:long}")]
    public ActionResult<GeofenceDto> Update(long id, [FromBody] GeofenceUpdateRequest? request)
    {
        var ownerUser = GetOwnerUser();
        if (string.IsNullOrEmpty(ownerUser))
            return Unauthorized();

        if (request == null)
            return BadRequest(new { error = "Request body is required" });

        try
        {
            var geofence = _geofenceService.Update(id, ownerUser, request);
            return geofence == null ? NotFound() : Ok(geofence);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:long}")]
    public IActionResult Delete(long id)
    {
        var ownerUser = GetOwnerUser();
        if (string.IsNullOrEmpty(ownerUser))
            return Unauthorized();

        return _geofenceService.Delete(id, ownerUser) ? NoContent() : NotFound();
    }

    private string? GetOwnerUser() =>
        User.FindFirstValue(ClaimTypes.Name);
}
