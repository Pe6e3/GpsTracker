using System.Globalization;
using GpsTcpProxy.Models;
using GpsTcpProxy.Services;
using Microsoft.Data.Sqlite;

namespace GpsTcpProxy;

public sealed class TrackPointStore : IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public TrackPointStore(string databasePath)
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
                CREATE TABLE IF NOT EXISTS track_points (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    latitude REAL,
                    longitude REAL,
                    speed_kmh REAL NOT NULL DEFAULT 0,
                    course INTEGER NOT NULL DEFAULT 0,
                    altitude INTEGER NOT NULL DEFAULT 0,
                    accuracy REAL,
                    source_raw_telemetry_id INTEGER,
                    point_type TEXT NOT NULL,
                    processing_batch_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    original_points_count INTEGER,
                    raw_start_id INTEGER,
                    raw_end_id INTEGER
                );

                CREATE INDEX IF NOT EXISTS ix_track_points_device_timestamp
                    ON track_points(device_id, timestamp_utc);

                CREATE INDEX IF NOT EXISTS ix_track_points_device_raw_end
                    ON track_points(device_id, raw_end_id);

                CREATE INDEX IF NOT EXISTS ix_track_points_device_source_raw
                    ON track_points(device_id, source_raw_telemetry_id);
                """;
            command.ExecuteNonQuery();
        }
    }

    public long? GetLastProcessedRawId(string deviceId)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT MAX(COALESCE(raw_end_id, source_raw_telemetry_id))
                FROM track_points
                WHERE device_id = $device_id;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);

            var value = command.ExecuteScalar();
            if (value == null || value is DBNull)
                return null;

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    public void DeleteFromRawEndId(string deviceId, long minRawEndId)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM track_points
                WHERE device_id = $device_id
                  AND COALESCE(raw_end_id, source_raw_telemetry_id) >= $min_raw_end_id;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$min_raw_end_id", minRawEndId);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ProcessedTrackPoint> GetPrecedingStationaryGroup(
        string deviceId,
        DateTime fromUtc,
        DateTime firstPointUtc)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return Array.Empty<ProcessedTrackPoint>();

        var groupStart = GetPrecedingStationaryStart(normalizedId, firstPointUtc);
        if (groupStart == null)
            return Array.Empty<ProcessedTrackPoint>();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    device_id,
                    timestamp_utc,
                    latitude,
                    longitude,
                    speed_kmh,
                    course,
                    altitude,
                    accuracy,
                    source_raw_telemetry_id,
                    point_type,
                    processing_batch_id,
                    created_at_utc,
                    original_points_count,
                    raw_start_id,
                    raw_end_id
                FROM track_points
                WHERE device_id = $device_id
                  AND timestamp_utc >= $group_start_utc
                  AND timestamp_utc < $first_point_utc
                  AND point_type IN ($stationary_start, $heartbeat, $stationary_end)
                ORDER BY timestamp_utc ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$group_start_utc", FormatUtc(groupStart.TimestampUtc));
            command.Parameters.AddWithValue("$first_point_utc", FormatUtc(firstPointUtc));
            command.Parameters.AddWithValue("$stationary_start", Models.TrackPointType.StationaryStart);
            command.Parameters.AddWithValue("$heartbeat", Models.TrackPointType.Heartbeat);
            command.Parameters.AddWithValue("$stationary_end", Models.TrackPointType.StationaryEnd);

            using var reader = command.ExecuteReader();
            var points = new List<ProcessedTrackPoint>();

            while (reader.Read())
                points.Add(ReadPoint(reader));

            if (points.Count == 0)
                return [groupStart];

            return points;
        }
    }

    public ProcessedTrackPoint? GetPrecedingStationaryStart(string deviceId, DateTime beforeUtc)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    device_id,
                    timestamp_utc,
                    latitude,
                    longitude,
                    speed_kmh,
                    course,
                    altitude,
                    accuracy,
                    source_raw_telemetry_id,
                    point_type,
                    processing_batch_id,
                    created_at_utc,
                    original_points_count,
                    raw_start_id,
                    raw_end_id
                FROM track_points
                WHERE device_id = $device_id
                  AND point_type = $stationary_start
                  AND timestamp_utc < $before_utc
                ORDER BY timestamp_utc DESC, id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$stationary_start", Models.TrackPointType.StationaryStart);
            command.Parameters.AddWithValue("$before_utc", FormatUtc(beforeUtc));

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPoint(reader) : null;
        }
    }

    public IReadOnlyList<ProcessedTrackPoint> GetTrack(
        string deviceId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 10000)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return Array.Empty<ProcessedTrackPoint>();

        if (limit <= 0)
            limit = 10000;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            var filters = new List<string>
            {
                "device_id = $device_id",
                "point_type != $synthetic_type"
            };
            command.Parameters.AddWithValue("$synthetic_type", Models.TrackPointType.Synthetic);
            command.Parameters.AddWithValue("$device_id", normalizedId);
            command.Parameters.AddWithValue("$limit", limit);

            if (fromUtc.HasValue)
            {
                filters.Add("timestamp_utc >= $from_utc");
                command.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc.Value));
            }

            if (toUtc.HasValue)
            {
                filters.Add("timestamp_utc <= $to_utc");
                command.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc.Value));
            }

            command.CommandText = $"""
                SELECT
                    id,
                    device_id,
                    timestamp_utc,
                    latitude,
                    longitude,
                    speed_kmh,
                    course,
                    altitude,
                    accuracy,
                    source_raw_telemetry_id,
                    point_type,
                    processing_batch_id,
                    created_at_utc,
                    original_points_count,
                    raw_start_id,
                    raw_end_id
                FROM track_points
                WHERE {string.Join(" AND ", filters)}
                ORDER BY timestamp_utc ASC, id ASC
                LIMIT $limit;
                """;

            using var reader = command.ExecuteReader();
            var points = new List<ProcessedTrackPoint>();

            while (reader.Read())
                points.Add(ReadPoint(reader));

            return points;
        }
    }

    public void MergeAdjacentStationary(string deviceId, TrackProcessingSettings settings)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return;

        var mergeGapMinutes = Math.Max(settings.StationaryMinDurationMinutes, 15);

        lock (_lock)
        {
            using var connection = OpenConnection();

            while (true)
            {
                using var select = connection.CreateCommand();
                select.CommandText = """
                    SELECT
                        end_point.id,
                        start_point.id,
                        start_point.timestamp_utc
                    FROM track_points end_point
                    INNER JOIN track_points start_point
                        ON start_point.device_id = end_point.device_id
                       AND start_point.point_type = $stationary_start
                       AND start_point.timestamp_utc > end_point.timestamp_utc
                       AND start_point.timestamp_utc = (
                           SELECT MIN(inner_start.timestamp_utc)
                           FROM track_points inner_start
                           WHERE inner_start.device_id = end_point.device_id
                             AND inner_start.point_type = $stationary_start
                             AND inner_start.timestamp_utc > end_point.timestamp_utc
                       )
                    WHERE end_point.device_id = $device_id
                      AND end_point.point_type = $stationary_end
                      AND NOT EXISTS (
                          SELECT 1
                          FROM track_points moving_point
                          WHERE moving_point.device_id = end_point.device_id
                            AND moving_point.point_type = $moving_type
                            AND moving_point.timestamp_utc > end_point.timestamp_utc
                            AND moving_point.timestamp_utc < start_point.timestamp_utc
                      )
                    ORDER BY end_point.timestamp_utc ASC
                    LIMIT 1;
                    """;
                select.Parameters.AddWithValue("$device_id", normalizedId);
                select.Parameters.AddWithValue("$stationary_end", TrackPointType.StationaryEnd);
                select.Parameters.AddWithValue("$stationary_start", TrackPointType.StationaryStart);
                select.Parameters.AddWithValue("$moving_type", TrackPointType.Moving);

                using var reader = select.ExecuteReader();
                if (!reader.Read())
                    return;

                var endId = reader.GetInt64(0);
                var nextStartId = reader.GetInt64(1);
                var nextStartTimestamp = ParseUtc(reader.GetString(2));
                reader.Close();

                var endPoint = LoadPointById(connection, endId);
                if (endPoint == null)
                    return;

                var gapMinutes = (nextStartTimestamp - endPoint.TimestampUtc).TotalMinutes;
                if (gapMinutes > mergeGapMinutes)
                    return;

                var groupStart = LoadGroupStart(connection, normalizedId, endPoint.TimestampUtc);
                var nextEnd = LoadNextGroupEnd(connection, normalizedId, nextStartId, nextStartTimestamp);
                if (groupStart == null || nextEnd == null)
                    return;

                var mergedEnd = new ProcessedTrackPoint
                {
                    DeviceId = normalizedId,
                    TimestampUtc = nextEnd.TimestampUtc,
                    Latitude = groupStart.Latitude,
                    Longitude = groupStart.Longitude,
                    SpeedKmh = nextEnd.SpeedKmh,
                    Course = nextEnd.Course,
                    Altitude = nextEnd.Altitude,
                    Accuracy = nextEnd.Accuracy,
                    SourceRawTelemetryId = nextEnd.SourceRawTelemetryId,
                    PointType = TrackPointType.StationaryEnd,
                    ProcessingBatchId = nextEnd.ProcessingBatchId,
                    CreatedAtUtc = nextEnd.CreatedAtUtc,
                    OriginalPointsCount = (groupStart.OriginalPointsCount ?? 0)
                        + (endPoint.OriginalPointsCount ?? 0)
                        + (nextEnd.OriginalPointsCount ?? 0),
                    RawStartId = groupStart.RawStartId,
                    RawEndId = nextEnd.RawEndId
                };

                var movementFollowsAfter = HasMovingPointAfter(connection, normalizedId, nextEnd.TimestampUtc);
                var regenerated = TrackSegmentProcessor.CreateStationaryPointsFromBounds(
                    groupStart,
                    mergedEnd,
                    settings,
                    movementFollowsAfter: movementFollowsAfter);

                using var transaction = connection.BeginTransaction();

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = """
                        DELETE FROM track_points
                        WHERE device_id = $device_id
                          AND timestamp_utc >= $from_utc
                          AND timestamp_utc <= $to_utc
                          AND point_type IN ($stationary_start, $heartbeat, $stationary_end);
                        """;
                    delete.Parameters.AddWithValue("$device_id", normalizedId);
                    delete.Parameters.AddWithValue("$from_utc", FormatUtc(groupStart.TimestampUtc));
                    delete.Parameters.AddWithValue("$to_utc", FormatUtc(nextEnd.TimestampUtc));
                    delete.Parameters.AddWithValue("$stationary_start", TrackPointType.StationaryStart);
                    delete.Parameters.AddWithValue("$heartbeat", TrackPointType.Heartbeat);
                    delete.Parameters.AddWithValue("$stationary_end", TrackPointType.StationaryEnd);
                    delete.ExecuteNonQuery();
                }

                foreach (var point in regenerated)
                    InsertPoint(connection, transaction, point);

                transaction.Commit();
            }
        }
    }

    private void InsertPoint(SqliteConnection connection, SqliteTransaction transaction, ProcessedTrackPoint point)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO track_points (
                device_id,
                timestamp_utc,
                latitude,
                longitude,
                speed_kmh,
                course,
                altitude,
                accuracy,
                source_raw_telemetry_id,
                point_type,
                processing_batch_id,
                created_at_utc,
                original_points_count,
                raw_start_id,
                raw_end_id
            ) VALUES (
                $device_id,
                $timestamp_utc,
                $latitude,
                $longitude,
                $speed_kmh,
                $course,
                $altitude,
                $accuracy,
                $source_raw_telemetry_id,
                $point_type,
                $processing_batch_id,
                $created_at_utc,
                $original_points_count,
                $raw_start_id,
                $raw_end_id
            );
            """;
        command.Parameters.AddWithValue("$device_id", point.DeviceId);
        command.Parameters.AddWithValue("$timestamp_utc", FormatUtc(point.TimestampUtc));
        command.Parameters.AddWithValue("$latitude", point.Latitude.HasValue ? point.Latitude.Value : DBNull.Value);
        command.Parameters.AddWithValue("$longitude", point.Longitude.HasValue ? point.Longitude.Value : DBNull.Value);
        command.Parameters.AddWithValue("$speed_kmh", point.SpeedKmh);
        command.Parameters.AddWithValue("$course", point.Course);
        command.Parameters.AddWithValue("$altitude", point.Altitude);
        command.Parameters.AddWithValue("$accuracy", point.Accuracy.HasValue ? point.Accuracy.Value : DBNull.Value);
        command.Parameters.AddWithValue("$source_raw_telemetry_id", point.SourceRawTelemetryId.HasValue ? point.SourceRawTelemetryId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$point_type", point.PointType);
        command.Parameters.AddWithValue("$processing_batch_id", point.ProcessingBatchId);
        command.Parameters.AddWithValue("$created_at_utc", FormatUtc(point.CreatedAtUtc));
        command.Parameters.AddWithValue("$original_points_count", point.OriginalPointsCount.HasValue ? point.OriginalPointsCount.Value : DBNull.Value);
        command.Parameters.AddWithValue("$raw_start_id", point.RawStartId.HasValue ? point.RawStartId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$raw_end_id", point.RawEndId.HasValue ? point.RawEndId.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private ProcessedTrackPoint? LoadPointById(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                device_id,
                timestamp_utc,
                latitude,
                longitude,
                speed_kmh,
                course,
                altitude,
                accuracy,
                source_raw_telemetry_id,
                point_type,
                processing_batch_id,
                created_at_utc,
                original_points_count,
                raw_start_id,
                raw_end_id
            FROM track_points
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPoint(reader) : null;
    }

    private static bool HasMovingPointAfter(SqliteConnection connection, string deviceId, DateTime afterUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM track_points
            WHERE device_id = $device_id
              AND point_type = $moving_type
              AND timestamp_utc > $after_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$moving_type", TrackPointType.Moving);
        command.Parameters.AddWithValue("$after_utc", FormatUtc(afterUtc));
        return command.ExecuteScalar() != null;
    }

    private static ProcessedTrackPoint? LoadGroupStart(SqliteConnection connection, string deviceId, DateTime endTimestampUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                device_id,
                timestamp_utc,
                latitude,
                longitude,
                speed_kmh,
                course,
                altitude,
                accuracy,
                source_raw_telemetry_id,
                point_type,
                processing_batch_id,
                created_at_utc,
                original_points_count,
                raw_start_id,
                raw_end_id
            FROM track_points
            WHERE device_id = $device_id
              AND point_type = $stationary_start
              AND timestamp_utc <= $end_timestamp_utc
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$stationary_start", TrackPointType.StationaryStart);
        command.Parameters.AddWithValue("$end_timestamp_utc", FormatUtc(endTimestampUtc));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPoint(reader) : null;
    }

    private static ProcessedTrackPoint? LoadNextGroupEnd(
        SqliteConnection connection,
        string deviceId,
        long startId,
        DateTime startTimestampUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                device_id,
                timestamp_utc,
                latitude,
                longitude,
                speed_kmh,
                course,
                altitude,
                accuracy,
                source_raw_telemetry_id,
                point_type,
                processing_batch_id,
                created_at_utc,
                original_points_count,
                raw_start_id,
                raw_end_id
            FROM track_points
            WHERE device_id = $device_id
              AND point_type = $stationary_end
              AND timestamp_utc >= $start_timestamp_utc
            ORDER BY timestamp_utc ASC, id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$stationary_end", TrackPointType.StationaryEnd);
        command.Parameters.AddWithValue("$start_timestamp_utc", FormatUtc(startTimestampUtc));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPoint(reader) : null;
    }

    public PeriodTrackStats GetPeriodStats(string deviceId, DateTime fromUtc, DateTime toUtc)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return EmptyPeriodStats();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM track_points
                WHERE device_id = $device_id
                  AND point_type != $synthetic_type
                  AND point_type != $heartbeat_type
                  AND timestamp_utc >= $from_utc
                  AND timestamp_utc <= $to_utc;
                """;
            countCommand.Parameters.AddWithValue("$device_id", normalizedId);
            countCommand.Parameters.AddWithValue("$synthetic_type", Models.TrackPointType.Synthetic);
            countCommand.Parameters.AddWithValue("$heartbeat_type", Models.TrackPointType.Heartbeat);
            countCommand.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
            countCommand.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
            var pointsCount = (long)(countCommand.ExecuteScalar() ?? 0L);

            using var heartbeatCommand = connection.CreateCommand();
            heartbeatCommand.CommandText = """
                SELECT COUNT(*)
                FROM track_points
                WHERE device_id = $device_id
                  AND point_type = $heartbeat_type
                  AND timestamp_utc >= $from_utc
                  AND timestamp_utc <= $to_utc;
                """;
            heartbeatCommand.Parameters.AddWithValue("$device_id", normalizedId);
            heartbeatCommand.Parameters.AddWithValue("$heartbeat_type", Models.TrackPointType.Heartbeat);
            heartbeatCommand.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
            heartbeatCommand.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
            var heartbeatPointsCount = (long)(heartbeatCommand.ExecuteScalar() ?? 0L);

            using var leadingEnd = connection.CreateCommand();
            leadingEnd.CommandText = """
                SELECT COUNT(*)
                FROM track_points first_point
                WHERE first_point.device_id = $device_id
                  AND first_point.point_type = $stationary_end
                  AND first_point.timestamp_utc >= $from_utc
                  AND first_point.timestamp_utc <= $to_utc
                  AND NOT EXISTS (
                      SELECT 1
                      FROM track_points start_point
                      WHERE start_point.device_id = first_point.device_id
                        AND start_point.point_type = $stationary_start
                        AND start_point.timestamp_utc >= $from_utc
                        AND start_point.timestamp_utc <= $to_utc
                        AND start_point.timestamp_utc <= first_point.timestamp_utc
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM track_points start_point
                      WHERE start_point.device_id = first_point.device_id
                        AND start_point.point_type = $stationary_start
                        AND start_point.timestamp_utc < $from_utc
                  );
                """;
            leadingEnd.Parameters.AddWithValue("$device_id", normalizedId);
            leadingEnd.Parameters.AddWithValue("$stationary_end", Models.TrackPointType.StationaryEnd);
            leadingEnd.Parameters.AddWithValue("$stationary_start", Models.TrackPointType.StationaryStart);
            leadingEnd.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
            leadingEnd.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));
            pointsCount += (long)(leadingEnd.ExecuteScalar() ?? 0L);

            using var distanceCommand = connection.CreateCommand();
            distanceCommand.CommandText = """
                SELECT latitude, longitude
                FROM track_points
                WHERE device_id = $device_id
                  AND point_type != $synthetic_type
                  AND timestamp_utc >= $from_utc
                  AND timestamp_utc <= $to_utc
                  AND latitude IS NOT NULL
                  AND longitude IS NOT NULL
                ORDER BY timestamp_utc ASC, id ASC;
                """;
            distanceCommand.Parameters.AddWithValue("$device_id", normalizedId);
            distanceCommand.Parameters.AddWithValue("$synthetic_type", Models.TrackPointType.Synthetic);
            distanceCommand.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));
            distanceCommand.Parameters.AddWithValue("$to_utc", FormatUtc(toUtc));

            var coordinates = new List<(double Latitude, double Longitude)>();
            using (var reader = distanceCommand.ExecuteReader())
            {
                while (reader.Read())
                    coordinates.Add((reader.GetDouble(0), reader.GetDouble(1)));
            }

            if (coordinates.Count == 0)
            {
                using var precedingStart = connection.CreateCommand();
                precedingStart.CommandText = """
                    SELECT latitude, longitude
                    FROM track_points
                    WHERE device_id = $device_id
                      AND point_type = $stationary_start
                      AND timestamp_utc < $from_utc
                      AND latitude IS NOT NULL
                      AND longitude IS NOT NULL
                    ORDER BY timestamp_utc DESC, id DESC
                    LIMIT 1;
                    """;
                precedingStart.Parameters.AddWithValue("$device_id", normalizedId);
                precedingStart.Parameters.AddWithValue("$stationary_start", Models.TrackPointType.StationaryStart);
                precedingStart.Parameters.AddWithValue("$from_utc", FormatUtc(fromUtc));

                using var precedingReader = precedingStart.ExecuteReader();
                if (precedingReader.Read())
                    coordinates.Add((precedingReader.GetDouble(0), precedingReader.GetDouble(1)));
            }

            double? previousLat = null;
            double? previousLon = null;
            var totalKm = 0.0;

            foreach (var coordinate in coordinates)
            {
                if (previousLat.HasValue && previousLon.HasValue)
                    totalKm += GeoDistance.HaversineKm(
                        previousLat.Value,
                        previousLon.Value,
                        coordinate.Latitude,
                        coordinate.Longitude);

                previousLat = coordinate.Latitude;
                previousLon = coordinate.Longitude;
            }

            return new PeriodTrackStats
            {
                PointsCount = pointsCount,
                HeartbeatPointsCount = heartbeatPointsCount,
                DistanceKm = totalKm
            };
        }
    }

    public void InsertBatch(IReadOnlyList<ProcessedTrackPoint> points)
    {
        if (points.Count == 0)
            return;

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var point in points)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO track_points (
                        device_id,
                        timestamp_utc,
                        latitude,
                        longitude,
                        speed_kmh,
                        course,
                        altitude,
                        accuracy,
                        source_raw_telemetry_id,
                        point_type,
                        processing_batch_id,
                        created_at_utc,
                        original_points_count,
                        raw_start_id,
                        raw_end_id
                    ) VALUES (
                        $device_id,
                        $timestamp_utc,
                        $latitude,
                        $longitude,
                        $speed_kmh,
                        $course,
                        $altitude,
                        $accuracy,
                        $source_raw_telemetry_id,
                        $point_type,
                        $processing_batch_id,
                        $created_at_utc,
                        $original_points_count,
                        $raw_start_id,
                        $raw_end_id
                    );
                    """;
                command.Parameters.AddWithValue("$device_id", point.DeviceId);
                command.Parameters.AddWithValue("$timestamp_utc", FormatUtc(point.TimestampUtc));
                command.Parameters.AddWithValue("$latitude", point.Latitude.HasValue ? point.Latitude.Value : DBNull.Value);
                command.Parameters.AddWithValue("$longitude", point.Longitude.HasValue ? point.Longitude.Value : DBNull.Value);
                command.Parameters.AddWithValue("$speed_kmh", point.SpeedKmh);
                command.Parameters.AddWithValue("$course", point.Course);
                command.Parameters.AddWithValue("$altitude", point.Altitude);
                command.Parameters.AddWithValue("$accuracy", point.Accuracy.HasValue ? point.Accuracy.Value : DBNull.Value);
                command.Parameters.AddWithValue("$source_raw_telemetry_id", point.SourceRawTelemetryId.HasValue ? point.SourceRawTelemetryId.Value : DBNull.Value);
                command.Parameters.AddWithValue("$point_type", point.PointType);
                command.Parameters.AddWithValue("$processing_batch_id", point.ProcessingBatchId);
                command.Parameters.AddWithValue("$created_at_utc", FormatUtc(point.CreatedAtUtc));
                command.Parameters.AddWithValue("$original_points_count", point.OriginalPointsCount.HasValue ? point.OriginalPointsCount.Value : DBNull.Value);
                command.Parameters.AddWithValue("$raw_start_id", point.RawStartId.HasValue ? point.RawStartId.Value : DBNull.Value);
                command.Parameters.AddWithValue("$raw_end_id", point.RawEndId.HasValue ? point.RawEndId.Value : DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static PeriodTrackStats EmptyPeriodStats() =>
        new()
        {
            PointsCount = 0,
            HeartbeatPointsCount = 0,
            DistanceKm = 0
        };

    public void Dispose()
    {
    }

    private static ProcessedTrackPoint ReadPoint(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            DeviceId = reader.GetString(1),
            TimestampUtc = ParseUtc(reader.GetString(2)),
            Latitude = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            Longitude = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            SpeedKmh = reader.GetDouble(5),
            Course = reader.GetInt32(6),
            Altitude = reader.GetInt32(7),
            Accuracy = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            SourceRawTelemetryId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            PointType = reader.GetString(10),
            ProcessingBatchId = reader.GetString(11),
            CreatedAtUtc = ParseUtc(reader.GetString(12)),
            OriginalPointsCount = reader.IsDBNull(13) ? null : reader.GetInt32(13),
            RawStartId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            RawEndId = reader.IsDBNull(15) ? null : reader.GetInt64(15)
        };

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
}
