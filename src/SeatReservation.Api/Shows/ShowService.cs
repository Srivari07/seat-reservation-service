using Dapper;
using SeatReservation.Api.Infrastructure.Db;

namespace SeatReservation.Api.Shows;

public sealed class ShowService(DbRunner db, ShowMetadataCache metadataCache)
{
    private const int DefaultPerUserLimit = 4;
    private const int MaxNameLength = 200;
    private const int MaxSeats = 10_000;
    private const int SeatInsertBatchSize = 500;

    public async Task<(bool Success, string? ErrorMessage, ShowResponse? Show)> CreateShowAsync(
        CreateShowRequest request, CancellationToken cancellationToken)
    {
        var error = Validate(request, out var name, out var seats, out var perUserLimit, out var pricePaise);
        if (error is not null)
        {
            return (false, error, null);
        }

        // Generated once, outside the transaction: a retried attempt re-inserts the same id
        // (the failed attempt was rolled back, so it never collides with itself).
        var showId = Guid.CreateVersion7();

        await db.WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO shows (show_id, name, price_paise, per_user_limit, total_seats)
                VALUES (@ShowId, @Name, @PricePaise, @PerUserLimit, @TotalSeats)
                """,
                new { ShowId = showId, Name = name, PricePaise = pricePaise, PerUserLimit = perUserLimit, TotalSeats = seats.Count },
                transaction);

            // Multi-row INSERT in batches of ~500 (02-schema.md), rather than one row per
            // round trip - total_seats can be up to 10,000. Seats are pre-sorted by Validate,
            // so seat_id (auto-increment) order matches (show_id, seat_no) order.
            foreach (var batch in seats.Chunk(SeatInsertBatchSize))
            {
                var parameters = new DynamicParameters();
                parameters.Add("ShowId", showId);
                var valuesSql = string.Join(',', Enumerable.Range(0, batch.Length).Select(i =>
                {
                    parameters.Add($"s{i}", batch[i]);
                    return $"(@ShowId, @s{i})";
                }));

                await connection.ExecuteAsync($"INSERT INTO seats (show_id, seat_no) VALUES {valuesSql}", parameters, transaction);
            }

            return TxResult.Commit(showId);
        }, cancellationToken);

        // Only after the commit, so the cache never holds a show that doesn't exist.
        metadataCache.Set(showId, new ShowMetadata(pricePaise, perUserLimit));

        var seatDtos = seats.Select(seat => new SeatDto(seat, "available")).ToList();

        var response = new ShowResponse(
            showId.ToString(),
            name,
            pricePaise,
            perUserLimit,
            seats.Count,
            new ShowCounts(seats.Count, 0, 0),
            seatDtos);

        return (true, null, response);
    }

    public async Task<ShowResponse?> GetShowAsync(Guid showId, CancellationToken cancellationToken)
    {
        var rowList = await db.ReadAsync(async connection =>
        {
            var rows = await connection.QueryAsync<ShowSeatRow>(
                new CommandDefinition(
                    """
                    SELECT sh.show_id AS ShowId, sh.name AS Name, sh.price_paise AS PricePaise,
                           sh.per_user_limit AS PerUserLimit, sh.total_seats AS TotalSeats,
                           se.seat_no AS SeatNo, se.status AS Status
                    FROM shows sh
                    JOIN seats se ON se.show_id = sh.show_id
                    WHERE sh.show_id = @ShowId
                    ORDER BY se.seat_no
                    """,
                    new { ShowId = showId },
                    cancellationToken: cancellationToken));
            return rows.AsList();
        }, cancellationToken);

        if (rowList.Count == 0)
        {
            return null;
        }

        var available = 0;
        var held = 0;
        var confirmed = 0;
        var seats = new List<SeatDto>(rowList.Count);
        foreach (var row in rowList)
        {
            seats.Add(new SeatDto(row.SeatNo, row.Status));
            switch (row.Status)
            {
                case "available": available++; break;
                case "held": held++; break;
                case "confirmed": confirmed++; break;
            }
        }

        var first = rowList[0];
        return new ShowResponse(
            first.ShowId.ToString(),
            first.Name,
            first.PricePaise,
            first.PerUserLimit,
            first.TotalSeats,
            new ShowCounts(available, held, confirmed),
            seats);
    }

    private static string? Validate(
        CreateShowRequest request, out string name, out List<string> seats, out int perUserLimit, out long pricePaise)
    {
        name = string.Empty;
        seats = [];
        perUserLimit = DefaultPerUserLimit;
        pricePaise = 0;

        var trimmedName = request.Name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            return "name must not be empty.";
        }

        if (trimmedName.Length > MaxNameLength)
        {
            return $"name must be at most {MaxNameLength} characters.";
        }

        if (request.Seats is null || request.Seats.Count == 0)
        {
            return "seats must not be empty.";
        }

        if (request.Seats.Count > MaxSeats)
        {
            return $"seats must not exceed {MaxSeats}.";
        }

        var normalized = new List<string>(request.Seats.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawSeat in request.Seats)
        {
            var seat = SeatNumbers.Normalize(rawSeat);
            if (seat is null)
            {
                return $"seat '{Truncate(rawSeat)}' must match {SeatNumbers.FormatDescription}.";
            }

            if (!seen.Add(seat))
            {
                return $"duplicate seat '{seat}' after normalization.";
            }

            normalized.Add(seat);
        }

        if (request.PricePaise is not { } price || price < 0 || decimal.Truncate(price) != price || price > long.MaxValue)
        {
            return "price_paise must be a non-negative integer.";
        }

        if (request.PerUserLimit is { } limit && limit < 1)
        {
            return "per_user_limit must be at least 1.";
        }

        normalized.Sort(StringComparer.Ordinal);

        name = trimmedName;
        seats = normalized;
        perUserLimit = request.PerUserLimit ?? DefaultPerUserLimit;
        pricePaise = (long)price;
        return null;
    }

    private static string Truncate(string? value, int maxLength = 40)
    {
        value ??= string.Empty;
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");
    }

    private sealed class ShowSeatRow
    {
        public Guid ShowId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long PricePaise { get; set; }
        public int PerUserLimit { get; set; }
        public int TotalSeats { get; set; }
        public string SeatNo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
