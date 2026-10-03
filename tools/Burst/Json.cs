using System.Text.Json;

namespace Burst;

public static class Json
{
    // Matches the API's JsonNamingPolicy.SnakeCaseLower (AGENTS.md "JSON naming"), so request/response
    // DTOs here round-trip against it without per-property JsonPropertyName attributes.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
