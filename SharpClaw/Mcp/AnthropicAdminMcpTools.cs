using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Mcp;

public sealed class AnthropicAdminMcpTools(
    IOptions<AnthropicAdminMcpOptions> options,
    ILogger<AnthropicAdminMcpTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool]
    [Description("Get daily Anthropic token usage for the past N days using the Anthropic Admin API.")]
    public async Task<string> AnthropicUsageByDay(
        [Description("Days to look back (1-31).")]
        int days = 0,
        [Description("Optional workspace ID filter.")]
        string? workspaceId = null,
        CancellationToken ct = default)
    {
        var (start, end, effectiveDays) = GetDateRange(ResolveLookbackDays(days));
        var query = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["starting_at"] = [ToRfc3339(start)],
            ["ending_at"] = [ToRfc3339(end)],
            ["bucket_width"] = ["1d"],
        };

        if (!string.IsNullOrWhiteSpace(workspaceId))
            query["workspace_ids[]"] = [workspaceId];

        using var payload = await GetAnthropicAsync("/v1/organizations/usage_report/messages", query, ct);
        var rows = ExtractRows(payload.RootElement);

        long uncached = 0;
        long cachedRead = 0;
        long output = 0;
        var buckets = new List<object>();

        foreach (var (row, bucketStart, bucketEnd) in rows)
        {
            var rowUncached = GetInt64(row, "uncached_input_tokens");
            var rowCachedRead = GetInt64(row, "cache_read_input_tokens");
            var rowOutput = GetInt64(row, "output_tokens");

            uncached += rowUncached;
            cachedRead += rowCachedRead;
            output += rowOutput;

            buckets.Add(new
            {
                bucket_start = bucketStart,
                bucket_end = bucketEnd,
                uncached_input_tokens = rowUncached,
                cache_read_input_tokens = rowCachedRead,
                output_tokens = rowOutput,
                model = GetString(row, "model"),
                workspace_id = GetString(row, "workspace_id"),
            });
        }

        var response = new
        {
            source = "anthropic_admin_usage_api",
            range = new { start = ToRfc3339(start), end = ToRfc3339(end), days = effectiveDays },
            totals = new
            {
                uncached_input_tokens = uncached,
                cache_read_input_tokens = cachedRead,
                output_tokens = output,
            },
            buckets,
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    [McpServerTool]
    [Description("Get daily Anthropic spend for the past N days using the Anthropic Admin API.")]
    public async Task<string> AnthropicSpendByDay(
        [Description("Days to look back (1-31).")]
        int days = 0,
        [Description("Optional workspace ID filter.")]
        string? workspaceId = null,
        CancellationToken ct = default)
    {
        var (start, end, effectiveDays) = GetDateRange(ResolveLookbackDays(days));
        var query = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["starting_at"] = [ToRfc3339(start)],
            ["ending_at"] = [ToRfc3339(end)],
            ["bucket_width"] = ["1d"],
            ["group_by[]"] = ["workspace_id", "description"],
        };

        using var payload = await GetAnthropicAsync("/v1/organizations/cost_report", query, ct);
        var rows = ExtractRows(payload.RootElement)
            .Where(item => string.IsNullOrWhiteSpace(workspaceId) || string.Equals(GetString(item.Row, "workspace_id"), workspaceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        decimal totalRawAmount = 0;
        var buckets = new List<object>();

        foreach (var (row, bucketStart, bucketEnd) in rows)
        {
            var rawAmount = GetDecimal(row, "amount");
            totalRawAmount += rawAmount;

            buckets.Add(new
            {
                bucket_start = bucketStart,
                bucket_end = bucketEnd,
                amount_raw = rawAmount,
                amount_usd_estimate = rawAmount / 100m,
                cost_type = GetString(row, "cost_type"),
                token_type = GetString(row, "token_type"),
                model = GetString(row, "model"),
                workspace_id = GetString(row, "workspace_id"),
                description = GetString(row, "description"),
            });
        }

        var response = new
        {
            source = "anthropic_admin_cost_api",
            range = new { start = ToRfc3339(start), end = ToRfc3339(end), days = effectiveDays },
            total_amount_raw = totalRawAmount,
            total_usd_estimate = totalRawAmount / 100m,
            amount_note = "USD estimate is derived by dividing Anthropic cost amount values by 100.",
            buckets,
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    [McpServerTool]
    [Description("Estimate remaining funds as configured budget minus Anthropic spend over the selected range.")]
    public async Task<string> AnthropicRemainingFunds(
        [Description("Days to look back for spend calculation (1-31).")]
        int days = 0,
        [Description("Optional workspace ID filter.")]
        string? workspaceId = null,
        [Description("Optional budget override in USD.")]
        decimal? monthlyBudgetUsd = null,
        CancellationToken ct = default)
    {
        var spendJson = await AnthropicSpendByDay(ResolveLookbackDays(days), workspaceId, ct);
        using var spendDoc = JsonDocument.Parse(spendJson);

        var totalUsd = spendDoc.RootElement.GetProperty("total_usd_estimate").GetDecimal();
        var budget = monthlyBudgetUsd ?? options.Value.MonthlyBudgetUsd;

        if (budget <= 0)
        {
            var noBudgetResponse = new
            {
                source = "anthropic_admin_cost_api",
                spent_usd_estimate = totalUsd,
                remaining_funds_usd_estimate = (decimal?)null,
                note = "No budget configured. Set AnthropicAdminMcp:MonthlyBudgetUsd or pass monthlyBudgetUsd.",
                dashboard_fallback_enabled = options.Value.EnableDashboardFallback,
                dashboard_fallback_status = "not_implemented",
            };
            return JsonSerializer.Serialize(noBudgetResponse, JsonOptions);
        }

        var response = new
        {
            source = "anthropic_admin_cost_api",
            budget_usd = budget,
            spent_usd_estimate = totalUsd,
            remaining_funds_usd_estimate = budget - totalUsd,
            remaining_funds_note = "Anthropic does not provide a direct remaining balance endpoint; this value is budget minus spend estimate.",
            dashboard_fallback_enabled = options.Value.EnableDashboardFallback,
            dashboard_fallback_status = "not_implemented",
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private async Task<JsonDocument> GetAnthropicAsync(string path, IReadOnlyDictionary<string, List<string>> query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            throw new InvalidOperationException("AnthropicAdminMcp:ApiKey is required.");

        var baseUrl = string.IsNullOrWhiteSpace(options.Value.ApiBaseUrl)
            ? "https://api.anthropic.com"
            : options.Value.ApiBaseUrl.TrimEnd('/');

        var uri = BuildUri(baseUrl + path, query);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-api-key", options.Value.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Anthropic Admin API call failed: {StatusCode} {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Anthropic Admin API error {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    private static Uri BuildUri(string baseUrl, IReadOnlyDictionary<string, List<string>> query)
    {
        var encoded = new List<string>();
        foreach (var (key, values) in query)
        {
            foreach (var value in values)
                encoded.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        var queryString = string.Join("&", encoded);
        return new Uri($"{baseUrl}?{queryString}");
    }

    private static (DateTimeOffset Start, DateTimeOffset End, int Days) GetDateRange(int days)
    {
        var effectiveDays = Math.Clamp(days <= 0 ? 7 : days, 1, 31);
        var now = DateTimeOffset.UtcNow;
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        var start = end.AddDays(-(effectiveDays));
        return (start, end, effectiveDays);
    }

    private int ResolveLookbackDays(int requestedDays)
    {
        if (requestedDays > 0)
            return requestedDays;

        var configured = options.Value.DefaultLookbackDays;
        return configured > 0 ? configured : 7;
    }

    private static string ToRfc3339(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static IReadOnlyList<(JsonElement Row, string? BucketStart, string? BucketEnd)> ExtractRows(JsonElement root)
    {
        // The API returns: { data: [{ starting_at, ending_at, results: [...] }] }
        // We need to flatten all results arrays, preserving bucket timestamp context
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var allRows = new List<(JsonElement, string?, string?)>();
            foreach (var bucket in data.EnumerateArray())
            {
                var bucketStart = GetString(bucket, "starting_at");
                var bucketEnd = GetString(bucket, "ending_at");
                
                if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in results.EnumerateArray())
                    {
                        allRows.Add((row, bucketStart, bucketEnd));
                    }
                }
            }
            return allRows;
        }
        
        return [];
    }

    private static string? GetString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static long GetInt64(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static decimal GetDecimal(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)) return 0m;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    }
}
