using System.Text.Json;

namespace ArcFoundryApi.Tools;

/// <summary>
/// Reusable helper for calling Azure Resource Manager REST endpoints with a bearer token.
/// Mirrors exactly the az rest pattern validated manually throughout the Arc demo build.
/// </summary>
public class ArmClient(HttpClient httpClient, Azure.Core.TokenCredential credential)
{
    private static readonly string[] ArmScope = ["https://management.azure.com/.default"];

    /// <summary>Exposed so other tool classes (e.g. Log Analytics, which uses a different token scope) can reuse the same credential.</summary>
    public Azure.Core.TokenCredential Credential => credential;
    public HttpClient Http => httpClient;

    public async Task<JsonDocument> GetAsync(string url)
    {
        var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(ArmScope), default);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token.Token);
        var resp = await httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARM GET {url} failed: {resp.StatusCode} - {body}");
        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> PutAsync(string url, object body)
    {
        var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(ArmScope), default);
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new("Bearer", token.Token);
        var resp = await httpClient.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARM PUT {url} failed: {resp.StatusCode} - {respBody}");
        return JsonDocument.Parse(respBody);
    }

    public async Task DeleteAsync(string url)
    {
        var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(ArmScope), default);
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new("Bearer", token.Token);
        await httpClient.SendAsync(req);
    }
}
