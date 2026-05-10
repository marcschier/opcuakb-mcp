using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using OpcUaKb.Agent;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert Agent — ASP.NET Core hosting for a Microsoft 365 Agents
// SDK custom engine agent. Replaces the OpcUaKb.Chat console chatbot.
// Hosted on Azure Container Apps; reuses KbService from OpcUaKb.Core.
// ═══════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

// ───────────────────────────────────────────────────────────────────────
// Wire env-var bot credentials into IConfiguration so MSAL + token
// validation can resolve them. The appsettings.json template uses
// $BOT_ID / $BOT_PASSWORD placeholders that are substituted at deploy
// time; here we additionally support standard MicrosoftAppId env vars.
// When BOT_ID is empty (local dev / Teams App Test Tool), the SDK runs
// the channel adapter in anonymous mode.
// ───────────────────────────────────────────────────────────────────────
var botId = Environment.GetEnvironmentVariable("BOT_ID")
         ?? Environment.GetEnvironmentVariable("MicrosoftAppId")
         ?? "";
var botPassword = Environment.GetEnvironmentVariable("BOT_PASSWORD")
               ?? Environment.GetEnvironmentVariable("MicrosoftAppPassword")
               ?? "";

builder.Configuration["TokenValidation:Audiences:0"] = botId;
builder.Configuration["Connections:ServiceConnection:Settings:ClientId"] = botId;
builder.Configuration["Connections:ServiceConnection:Settings:ClientSecret"] = botPassword;

// ───────────────────────────────────────────────────────────────────────
// Core ASP.NET Core services
// ───────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddRouting();

// ───────────────────────────────────────────────────────────────────────
// Microsoft 365 Agents SDK wiring
// ───────────────────────────────────────────────────────────────────────
// MSAL authentication for outbound channel-service calls (uses the
// Connections:ServiceConnection section in appsettings.json).
builder.Services.AddDefaultMsalAuth(builder.Configuration);

// AgentApplicationOptions, CloudAdapter, channel services, etc.
builder.AddAgentApplicationOptions();
builder.AddAgent<OpcUaAgent>();

// In-memory turn state store. Production agents that need to survive
// restarts should switch to a persisted IStorage implementation.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ───────────────────────────────────────────────────────────────────────
// OPC UA Knowledge Base service (reads SEARCH_*, AOAI_*, KB_NAME,
// GPT_DEPLOYMENT env vars internally).
// ───────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<KbService>();

var app = builder.Build();

app.UseHeaderPropagation();
app.UseRouting();

// Health endpoint — Azure Container Apps + load balancer probe target.
app.MapGet("/", () => Results.Ok("OPC UA Expert Agent is running"));

// Bot Framework messaging endpoint — entry point for all Bot Framework
// activities (Teams, Web Chat, Direct Line, Test Tool).
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
});

// ───────────────────────────────────────────────────────────────────────
// Bind port: PORT env var (Container Apps / Cloud Run convention) or
// 3978 default (Bot Framework convention).
// ───────────────────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    app.Urls.Clear();
    app.Urls.Add($"http://0.0.0.0:{port}");
}

app.Run();
