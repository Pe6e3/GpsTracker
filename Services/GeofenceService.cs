using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class GeofenceService
{
    private readonly GeofenceStore _geofenceStore;
    private readonly TelegramService _telegramService;

    public GeofenceService(GeofenceStore geofenceStore, TelegramService telegramService)
    {
        _geofenceStore = geofenceStore;
        _telegramService = telegramService;
    }

    public IReadOnlyList<GeofenceDto> GetForUser(string ownerUser) =>
        _geofenceStore.GetByOwner(ownerUser).Select(MapDto).ToArray();

    public GeofenceDto? GetById(long id, string ownerUser)
    {
        var geofence = _geofenceStore.GetById(id, ownerUser);
        return geofence == null ? null : MapDto(geofence);
    }

    public GeofenceDto Create(string ownerUser, GeofenceCreateRequest request)
    {
        ValidateRequest(request.Name, request.Points);
        var points = MapPoints(request.Points);
        var geofence = _geofenceStore.Create(ownerUser, request.Name, points);
        return MapDto(geofence);
    }

    public GeofenceDto? Update(long id, string ownerUser, GeofenceUpdateRequest request)
    {
        ValidateRequest(request.Name, request.Points);
        var points = MapPoints(request.Points);
        var geofence = _geofenceStore.Update(id, ownerUser, request.Name, points);
        return geofence == null ? null : MapDto(geofence);
    }

    public bool Delete(long id, string ownerUser) =>
        _geofenceStore.Delete(id, ownerUser);

    public GeofencePointResult ProcessNewPoint(string deviceId, double latitude, double longitude)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        var previousNames = _geofenceStore.GetLastPointGeofenceNames(normalizedId);
        var matched = MatchGeofences(latitude, longitude);
        var currentNames = matched.Select(g => g.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        NotifyTransitions(normalizedId, previousNames, currentNames);

        return new GeofencePointResult
        {
            GeofenceNamesJson = GeofenceStore.SerializeNames(currentNames)
        };
    }

    private IReadOnlyList<Geofence> MatchGeofences(double latitude, double longitude) =>
        _geofenceStore.GetAllGeofences()
            .Where(g => GeofencePolygon.Contains(g.Points, latitude, longitude))
            .ToArray();

    private void NotifyTransitions(string deviceId, IReadOnlyList<string> previousNames, IReadOnlyList<string> currentNames)
    {
        var previous = new HashSet<string>(previousNames, StringComparer.OrdinalIgnoreCase);
        var current = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);

        if (previous.SetEquals(current))
            return;

        var deviceName = DeviceRegistry.GetDisplayName(deviceId);
        var label = deviceName == deviceId || deviceName == "?" ? deviceId : $"{deviceName} ({deviceId})";

        var chatId = UserRegistry.GetTelegramChatIdForDevice(deviceId);

        foreach (var name in previous.Except(current, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            TrafficLogger.LogInfo($"[GEOFENCE] {label}: вышел из «{name}»");
            _telegramService.NotifyGeofenceTransition(deviceId, deviceName, name, entered: false, chatId);
        }

        foreach (var name in current.Except(previous, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            TrafficLogger.LogInfo($"[GEOFENCE] {label}: вошёл в «{name}»");
            _telegramService.NotifyGeofenceTransition(deviceId, deviceName, name, entered: true, chatId);
        }
    }

    private static void ValidateRequest(string name, IReadOnlyList<GeofencePointDto> points)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Название геозоны обязательно");

        if (!GeofencePolygon.IsValidPointCount(points.Count))
            throw new ArgumentException($"Геозона должна содержать от {GeofencePolygon.MinPoints} до {GeofencePolygon.MaxPoints} точек");
    }

    private static IReadOnlyList<GeofencePoint> MapPoints(IReadOnlyList<GeofencePointDto> points) =>
        points.Select(p => new GeofencePoint { Lat = p.Lat, Lon = p.Lon }).ToArray();

    private static GeofenceDto MapDto(Geofence geofence) =>
        new()
        {
            Id = geofence.Id,
            Name = geofence.Name,
            Points = geofence.Points
                .Select(p => new GeofencePointDto { Lat = p.Lat, Lon = p.Lon })
                .ToArray(),
            CreatedAtUtc = AppTime.AsUtc(geofence.CreatedAtUtc).ToString("O", CultureInfo.InvariantCulture),
            UpdatedAtUtc = AppTime.AsUtc(geofence.UpdatedAtUtc).ToString("O", CultureInfo.InvariantCulture)
        };
}

public sealed class GeofencePointResult
{
    public required string GeofenceNamesJson { get; init; }
}
