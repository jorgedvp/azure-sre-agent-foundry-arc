using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcFoundryApi.Tools;

/// <summary>
/// Tool implementations that let the Foundry agent read the SAME Azure Arc inventory
/// used by the Azure SRE Agent demo (rg-sre-customer-demo), without touching it.
///
/// IMPORTANT - Safety boundary for this proof of concept:
/// Azure RBAC on Arc (Azure Connected Machine Resource Administrator) only gates WHETHER
/// a command can be invoked on a machine - not WHAT the command does once it runs (this was
/// confirmed empirically while building the original SRE Agent demo). Because this POC's
/// managed identity necessarily has that same broad RBAC (there is no narrower "diagnostics-only"
/// Arc role), the safety boundary here is enforced at the APPLICATION layer instead: every
/// run-command script is checked against a deny-list of state-changing patterns before
/// execution. This mirrors the same lesson documented in docs/ARCHITECTURE.md of the main
/// Arc demo - RBAC controls WHO can act, the application/process controls WHAT is allowed.
/// A production build would add a human-approval step here, exactly like Azure SRE Agent does.
/// </summary>
public class ArcTools(ArmClient arm, IConfiguration config)
{
    private readonly string _subscriptionId = config["Azure:SubscriptionId"]!;
    private readonly string _resourceGroup = config["Azure:CustomerDemoResourceGroup"] ?? "rg-sre-customer-demo";

    private static readonly string[] DenyPatterns =
    [
        "rm -rf", "del /", "remove-item", "stop-service", "start-service", "restart-service",
        "kill ", "kill(", "drop ", "delete ", "insert ", "update ", "grant ", "revoke ",
        "shutdown", "reboot", "set-", "new-", "stop-webapppool", "start-webapppool",
        "systemctl stop", "systemctl start", "systemctl restart", "systemctl disable",
        "mssql-conf", "alter ", "truncate ", "az vm deallocate", "az vm delete"
    ];

    public static bool IsSafeDiagnosticScript(string script, out string? reason)
    {
        var lowered = script.ToLowerInvariant();
        foreach (var pattern in DenyPatterns)
        {
            if (lowered.Contains(pattern))
            {
                reason = $"Blocked by demo safety guard: script contains a state-changing pattern ('{pattern.Trim()}'). This proof of concept only allows read-only diagnostic commands.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    public async Task<object> ListArcMachinesAsync()
    {
        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroup}/providers/Microsoft.HybridCompute/machines?api-version=2024-07-10";
        var doc = await arm.GetAsync(url);
        var machines = new List<object>();
        foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var props = m.GetProperty("properties");
            machines.Add(new
            {
                name = m.GetProperty("name").GetString(),
                status = props.TryGetProperty("status", out var s) ? s.GetString() : "unknown",
                osType = props.TryGetProperty("osName", out var os) ? os.GetString() : "unknown",
                osSku = props.TryGetProperty("osSku", out var sku) ? sku.GetString() : "unknown",
                agentVersion = props.TryGetProperty("agentVersion", out var av) ? av.GetString() : "unknown",
                lastStatusChange = props.TryGetProperty("lastStatusChange", out var lsc) ? lsc.GetString() : null
            });
        }
        return new { machines, source = "Azure Arc (Microsoft.HybridCompute) - same inventory used by Azure SRE Agent demo" };
    }

    public async Task<object> GetMachineInventoryAsync(string machineName)
    {
        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroup}/providers/Microsoft.HybridCompute/machines/{machineName}?api-version=2024-07-10";
        var doc = await arm.GetAsync(url);
        var props = doc.RootElement.GetProperty("properties");
        return new
        {
            name = doc.RootElement.GetProperty("name").GetString(),
            status = props.TryGetProperty("status", out var s) ? s.GetString() : "unknown",
            osName = props.TryGetProperty("osName", out var os) ? os.GetString() : null,
            osSku = props.TryGetProperty("osSku", out var sku) ? sku.GetString() : null,
            osVersion = props.TryGetProperty("osVersion", out var ov) ? ov.GetString() : null,
            agentVersion = props.TryGetProperty("agentVersion", out var av) ? av.GetString() : null,
            mssqlDiscovered = props.TryGetProperty("mssqlDiscovered", out var mssql) && (mssql.ValueKind == JsonValueKind.True || (mssql.ValueKind == JsonValueKind.String && string.Equals(mssql.GetString(), "true", StringComparison.OrdinalIgnoreCase))),
            vmId = props.TryGetProperty("vmId", out var vmid) ? vmid.GetString() : null,
            cloudMetadataProvider = props.TryGetProperty("cloudMetadata", out var cm) && cm.TryGetProperty("provider", out var p) ? p.GetString() : "unknown (on-premises / outside Azure)"
        };
    }

    public async Task<object> RunDiagnosticAsync(string machineName, string script, int timeoutSeconds = 60)
    {
        if (!IsSafeDiagnosticScript(script, out var blockReason))
        {
            return new { blocked = true, reason = blockReason };
        }

        var runCommandName = $"foundry-diag-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var createUrl = $"https://management.azure.com/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroup}/providers/Microsoft.HybridCompute/machines/{machineName}/runCommands/{runCommandName}?api-version=2024-07-10";

        await arm.PutAsync(createUrl, new
        {
            location = "eastus2",
            properties = new
            {
                source = new { script },
                timeoutInSeconds = timeoutSeconds,
                asyncExecution = false
            }
        });

        // Poll for completion (Arc run-command is async server-side even though asyncExecution=false
        // refers to the script itself, not the ARM operation). The resource can briefly 404 right
        // after the PUT (ARM hasn't fully materialized it yet) - this is a known Arc quirk already
        // documented in the main Arc demo's REDEPLOY.md, so we tolerate a few 404s before giving up.
        var getUrl = createUrl;
        string state = "Creating";
        string output = "";
        string error = "";
        var consecutiveNotFound = 0;
        for (var i = 0; i < 25 && (state == "Creating" || state == "Accepted"); i++)
        {
            await Task.Delay(3000);
            try
            {
                var doc = await arm.GetAsync(getUrl);
                consecutiveNotFound = 0;
                var props = doc.RootElement.GetProperty("properties");
                state = props.GetProperty("provisioningState").GetString() ?? "Unknown";
                if (props.TryGetProperty("instanceView", out var iv))
                {
                    if (iv.TryGetProperty("output", out var o)) output = o.GetString() ?? "";
                    if (iv.TryGetProperty("error", out var e)) error = e.GetString() ?? "";
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("NotFound"))
            {
                consecutiveNotFound++;
                if (consecutiveNotFound > 8)
                {
                    return new { machineName, script, state = "TimedOut", output = "", error = "Run command resource never materialized after repeated 404s." };
                }
            }
        }

        // Clean up the run-command resource so we don't leave clutter on the demo VM.
        try { await arm.DeleteAsync(getUrl); } catch { /* best-effort cleanup */ }

        return new { machineName, script, state, output, error };
    }

    public async Task<object> QueryLogAnalyticsAsync(string workspaceId, string kqlQuery)
    {
        var scope = new[] { "https://api.loganalytics.io/.default" };
        var token = await arm.Credential.GetTokenAsync(new Azure.Core.TokenRequestContext(scope), default);
        var url = $"https://api.loganalytics.io/v1/workspaces/{workspaceId}/query";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query = kqlQuery }), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new("Bearer", token.Token);
        var resp = await arm.Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return new { error = $"Log Analytics query failed: {resp.StatusCode} - {body}" };

        var doc = JsonDocument.Parse(body);
        var rows = new List<Dictionary<string, object?>>();
        var table = doc.RootElement.GetProperty("tables")[0];
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
        foreach (var row in table.GetProperty("rows").EnumerateArray())
        {
            var dict = new Dictionary<string, object?>();
            var i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                dict[columns[i]] = cell.ValueKind switch
                {
                    JsonValueKind.String => cell.GetString(),
                    JsonValueKind.Number => cell.GetDouble(),
                    JsonValueKind.True or JsonValueKind.False => cell.GetBoolean(),
                    _ => null
                };
                i++;
            }
            rows.Add(dict);
        }
        return new { rows, rowCount = rows.Count };
    }
}
