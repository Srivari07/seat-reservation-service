using System.Globalization;

namespace Burst;

// Minimal Prometheus text-exposition parser: only the handful of series this tool reconciles
// against matter (AppMetrics.cs), and their label values (show_id GUIDs, fixed reason/status
// words) never contain a comma or quote, so a simple split is enough - no general library needed.
public sealed class MetricsSnapshot
{
    private readonly Dictionary<string, double> samples;

    private MetricsSnapshot(Dictionary<string, double> samples) => this.samples = samples;

    public static MetricsSnapshot Parse(string text)
    {
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var spaceIndex = line.LastIndexOf(' ');
            if (spaceIndex < 0 ||
                !double.TryParse(line[(spaceIndex + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var metricPart = line[..spaceIndex];
            var braceIndex = metricPart.IndexOf('{');

            string name;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (braceIndex < 0)
            {
                name = metricPart;
            }
            else
            {
                name = metricPart[..braceIndex];
                var closeIndex = metricPart.LastIndexOf('}');
                var labelPart = closeIndex > braceIndex ? metricPart[(braceIndex + 1)..closeIndex] : string.Empty;
                foreach (var pair in labelPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }

                    var key = pair[..eq];
                    var val = pair[(eq + 1)..].Trim('"');
                    labels[key] = val;
                }
            }

            samples[BuildKey(name, labels)] = value;
        }

        return new MetricsSnapshot(samples);
    }

    public double Counter(string name, string showId, string? reason = null) => Get(name, showId, reason, null);

    public double Gauge(string name, string showId, string? status = null) => Get(name, showId, null, status);

    private double Get(string name, string showId, string? reason, string? status)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["show_id"] = showId };
        if (reason is not null)
        {
            labels["reason"] = reason;
        }

        if (status is not null)
        {
            labels["status"] = status;
        }

        return samples.GetValueOrDefault(BuildKey(name, labels));
    }

    private static string BuildKey(string name, Dictionary<string, string> labels)
    {
        var sorted = labels.OrderBy(kv => kv.Key, StringComparer.Ordinal);
        return $"{name}{{{string.Join(",", sorted.Select(kv => $"{kv.Key}=\"{kv.Value}\""))}}}";
    }
}
