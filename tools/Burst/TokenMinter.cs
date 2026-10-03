namespace Burst;

public static class TokenMinter
{
    public static async Task<string> MintAdminAsync(ApiClient client, string adminSecret, CancellationToken ct)
    {
        var userId = $"burst-admin-{Guid.NewGuid():N}";
        var result = await client.MintTokenAsync(userId, "admin", adminSecret, ct);
        if (!result.Success || result.Value is null)
        {
            throw new BurstFatalException($"Could not mint the admin token: {Describe(result)}");
        }

        return result.Value.AccessToken;
    }

    public static async Task<List<(string UserId, string Token)>> MintUserPoolAsync(ApiClient client, int count, CancellationToken ct)
    {
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var userId = $"burst-u{i}-{Guid.NewGuid():N}";
            var result = await client.MintTokenAsync(userId, "user", null, ct);
            if (!result.Success || result.Value is null)
            {
                throw new BurstFatalException($"Could not mint a user token for {userId}: {Describe(result)}");
            }

            return (UserId: userId, Token: result.Value.AccessToken);
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static string Describe<T>(ApiResult<T> result) =>
        result.TransportFailed
            ? $"transport error: {result.TransportError}"
            : $"{result.StatusCode} {result.Error?.Error} {result.Error?.Message}";
}
