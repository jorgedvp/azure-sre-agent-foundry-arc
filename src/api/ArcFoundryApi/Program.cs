using Azure.Identity;
using ArcFoundryApi;
using ArcFoundryApi.Tools;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Prefer the app's user-assigned managed identity in Azure; falls back to az login locally.
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ =>
{
    var managedIdentityClientId = builder.Configuration["Azure:ManagedIdentityClientId"];
    if (!string.IsNullOrEmpty(managedIdentityClientId))
    {
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId
        });
    }
    return new DefaultAzureCredential();
});

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<ArmClient>();
builder.Services.AddSingleton<ArcTools>();
builder.Services.AddSingleton<GitHubTools>();
builder.Services.AddSingleton<ChatOrchestrator>();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

app.MapPost("/api/chat", async (ChatRequest req, ChatOrchestrator orchestrator) =>
{
    var history = (req.History ?? [])
        .Select(m => m.Role == "assistant"
            ? (ChatMessage)new AssistantChatMessage(m.Content)
            : new UserChatMessage(m.Content))
        .ToList();

    var (reply, toolCalls) = await orchestrator.SendAsync(history, req.Message);
    return Results.Ok(new { reply, toolCalls });
});

app.Run();

public record ChatRequest(string Message, List<HistoryMessage>? History);
public record HistoryMessage(string Role, string Content);
