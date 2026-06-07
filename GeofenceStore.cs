using System.Globalization;
using System.Text.Json;
using GpsTcpProxy.Models;
using Microsoft.Data.Sqlite;

namespace GpsTcpProxy;

public sealed class GeofenceStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly object _lock = new();

    public GeofenceStore(string databasePath)
    {
        var databaseFullPath = Path.IsPathRooted(databasePath)
            ? databasePath
            : Path.Combine(AppContext.BaseDirectory, databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public void Initialize()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS geofences (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_user TEXT NOT NULL,
                    name TEXT NOT NULL,
                    points_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_geofences_owner_user
                    ON geofences(owner_user);
                """;
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Geofence> GetAllGeofences()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, owner_user, name, points_json, created_at_utc, updated_at_utc
                FROM geofences
                ORDER BY name COLLATE NOCASE ASC, id ASC;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<Geofence>();

            while (reader.Read())
                items.Add(ReadGeofence(reader));

            return items;
        }
    }

    public IReadOnlyList<Geofence> GetByOwner(string ownerUser)
    {
        if (string.IsNullOrWhiteSpace(ownerUser))
            return Array.Empty<Geofence>();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, owner_user, name, points_json, created_at_utc, updated_at_utc
                FROM geofences
                WHERE owner_user = $owner_user
                ORDER BY name COLLATE NOCASE ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$owner_user", ownerUser.Trim());

            using var reader = command.ExecuteReader();
            var items = new List<Geofence>();

            while (reader.Read())
                items.Add(ReadGeofence(reader));

            return items;
        }
    }

    public Geofence? GetById(long id, string ownerUser)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, owner_user, name, points_json, created_at_utc, updated_at_utc
                FROM geofences
                WHERE id = $id AND owner_user = $owner_user;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner_user", ownerUser.Trim());

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadGeofence(reader) : null;
        }
    }

    public Geofence Create(string ownerUser, string name, IReadOnlyList<GeofencePoint> points)
    {
        var nowUtc = DateTime.UtcNow;
        var normalizedOwner = ownerUser.Trim();
        var normalizedName = name.Trim();
        var pointsJson = SerializePoints(points);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO geofences (owner_user, name, points_json, created_at_utc, updated_at_utc)
                VALUES ($owner_user, $name, $points_json, $created_at_utc, $updated_at_utc);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$owner_user", normalizedOwner);
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$points_json", pointsJson);
            command.Parameters.AddWithValue("$created_at_utc", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$updated_at_utc", FormatUtc(nowUtc));

            var id = (long)(command.ExecuteScalar() ?? 0L);

            return new Geofence
            {
                Id = id,
                OwnerUser = normalizedOwner,
                Name = normalizedName,
                Points = points,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
        }
    }

    public Geofence? Update(long id, string ownerUser, string name, IReadOnlyList<GeofencePoint> points)
    {
        var nowUtc = DateTime.UtcNow;
        var normalizedOwner = ownerUser.Trim();
        var normalizedName = name.Trim();
        var pointsJson = SerializePoints(points);

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE geofences
                SET name = $name,
                    points_json = $points_json,
                    updated_at_utc = $updated_at_utc
                WHERE id = $id AND owner_user = $owner_user;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner_user", normalizedOwner);
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$points_json", pointsJson);
            command.Parameters.AddWithValue("$updated_at_utc", FormatUtc(nowUtc));

            if (command.ExecuteNonQuery() == 0)
                return null;

            return GetById(id, normalizedOwner);
        }
    }

    public bool Delete(long id, string ownerUser)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM geofences
                WHERE id = $id AND owner_user = $owner_user;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner_user", ownerUser.Trim());
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<string> GetLastPointGeofenceNames(string deviceId)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT geofence_names
                FROM telemetry_points
                WHERE device_id = $device_id
                ORDER BY gps_time_utc DESC, id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);

            var value = command.ExecuteScalar();
            if (value == null || value is DBNull)
                return Array.Empty<string>();

            return DeserializeNames(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }
    }

    public static IReadOnlyList<string> DeserializeNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string SerializeNames(IReadOnlyList<string> names) =>
        JsonSerializer.Serialize(names, JsonOptions);

    private static Geofence ReadGeofence(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            OwnerUser = reader.GetString(1),
            Name = reader.GetString(2),
            Points = DeserializePoints(reader.GetString(3)),
            CreatedAtUtc = ParseUtc(reader.GetString(4)),
            UpdatedAtUtc = ParseUtc(reader.GetString(5))
        };

    private static IReadOnlyList<GeofencePoint> DeserializePoints(string json)
    {
        try
        {
            var points = JsonSerializer.Deserialize<List<GeofencePointDto>>(json, JsonOptions) ?? [];
            return points
                .Select(p => new GeofencePoint { Lat = p.Lat, Lon = p.Lon })
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<GeofencePoint>();
        }
    }

    private static string SerializePoints(IReadOnlyList<GeofencePoint> points)
    {
        var dto = points.Select(p => new GeofencePointDto { Lat = p.Lat, Lon = p.Lon }).ToArray();
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string FormatUtc(DateTime utc) =>
        AppTime.AsUtc(utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
    }
}
