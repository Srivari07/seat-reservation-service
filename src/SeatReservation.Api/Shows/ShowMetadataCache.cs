using Microsoft.Extensions.Caching.Memory;

namespace SeatReservation.Api.Shows;

// price_paise and per_user_limit are immutable after a show is created (D-14), so this
// cache has no expiration or invalidation path - it exists to save a query per reserve
// in Phase 5, not to serve the live seat counts, which must always come from the DB (I3).
public sealed record ShowMetadata(long PricePaise, int PerUserLimit);

public sealed class ShowMetadataCache(IMemoryCache cache)
{
    public void Set(Guid showId, ShowMetadata metadata) => cache.Set(CacheKey(showId), metadata);

    public bool TryGet(Guid showId, out ShowMetadata? metadata) => cache.TryGetValue(CacheKey(showId), out metadata);

    private static string CacheKey(Guid showId) => $"show-metadata:{showId}";
}
