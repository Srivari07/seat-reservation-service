namespace Burst;

public sealed class BurstUsageException(string message) : Exception(message);

public sealed record BurstOptions(
    Uri BaseUrl,
    int Seats,
    int HotSeats,
    int Storm,
    int Users,
    int Concurrency,
    string AdminSecret)
{
    public static BurstOptions Parse(string[] args)
    {
        string? baseUrl = null;
        var seats = 1000;
        var hotSeats = 5;
        var storm = 500;
        var users = 5000;
        var concurrency = 1000;
        var adminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET");

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                baseUrl ??= arg;
                continue;
            }

            var parts = arg[2..].Split('=', 2);
            var name = parts[0];
            // Accepts both --name=value and --name value (the next arg), so a plain space-separated
            // invocation (as README examples typically read) works the same as an "=" one.
            var value = parts.Length > 1 ? parts[1] : (i + 1 < args.Length ? args[++i] : null);

            switch (name)
            {
                case "seats": seats = ParseInt(name, value); break;
                case "hot-seats": hotSeats = ParseInt(name, value); break;
                case "storm": storm = ParseInt(name, value); break;
                case "users": users = ParseInt(name, value); break;
                case "concurrency": concurrency = ParseInt(name, value); break;
                case "admin-secret":
                    adminSecret = value ?? throw new BurstUsageException($"--{name} needs a value");
                    break;
                default:
                    throw new BurstUsageException($"Unknown option --{name}");
            }
        }

        if (baseUrl is null)
        {
            throw new BurstUsageException(
                "Usage: burst <BASE_URL> [--seats=N] [--hot-seats=N] [--storm=N] [--users=N] [--concurrency=N] [--admin-secret=SECRET]");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new BurstUsageException($"BASE_URL '{baseUrl}' is not a valid absolute URI");
        }

        if (hotSeats <= 0 || storm <= 0 || users <= 0 || concurrency <= 0 || seats <= 0)
        {
            throw new BurstUsageException("--seats, --hot-seats, --storm, --users and --concurrency must all be positive");
        }

        if (string.IsNullOrWhiteSpace(adminSecret))
        {
            throw new BurstUsageException("Admin secret is required: pass --admin-secret=... or set the ADMIN_SECRET env var");
        }

        if (seats > 10_000)
        {
            throw new BurstUsageException("--seats must be at most 10,000 (the API's own /shows cap)");
        }

        // Scenarios D/E/F/G each draw a handful of untouched seats from a "reserved tail" that B/C
        // never touch (see SeatPlan); --seats must be large enough to fit hot seats plus that tail.
        var reservedTailSize = Math.Max(20, hotSeats + 40);
        var minSeats = hotSeats + reservedTailSize + 10;
        if (seats < minSeats)
        {
            throw new BurstUsageException(
                $"--seats must be at least {minSeats} to fit {hotSeats} hot seats, a reserved tail of {reservedTailSize}, and a general stampede pool");
        }

        return new BurstOptions(uri, seats, hotSeats, storm, users, concurrency, adminSecret);
    }

    private static int ParseInt(string name, string? value)
    {
        if (value is null || !int.TryParse(value, out var parsed))
        {
            throw new BurstUsageException($"--{name} needs an integer value");
        }

        return parsed;
    }
}
