// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert — Foundry Hosted Agent (Agent Framework + direct MCP client)
//
// Connects directly to OpcUaKb.McpServer over SSE so each of the 11 MCP
// tools (search_docs, list_specs, get_spec_summary, etc.) is exposed as a
// distinct AIFunction that GPT-4o can call by name. The Agent Framework
// hosting library runs the tool-call loop locally in this container.
//
// We do NOT use the Foundry Toolbox here. GetToolboxToolsAsync wraps the
// whole MCP server into a single opaque "McpTool" with no schema visible
// to the model, which prevents GPT-4o from picking specific tools.
//
// Required environment variables:
//   FOUNDRY_PROJECT_ENDPOINT       — Foundry project endpoint (auto-injected in hosted containers)
//   AZURE_AI_MODEL_DEPLOYMENT_NAME — Model deployment name (declared in agent.manifest.yaml)
//   MCP_SERVER_URL                 — Base URL of the OpcUaKb.McpServer (Container Apps endpoint)
// ═══════════════════════════════════════════════════════════════════════
#pragma warning disable OPENAI001 // some Agent Framework hooks are experimental
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

// Load .env file if present (for local development)
Env.TraversePath().Load();

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));

var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");

var mcpServerUrl = Environment.GetEnvironmentVariable("MCP_SERVER_URL")
    ?? throw new InvalidOperationException("MCP_SERVER_URL environment variable is not set.");

const string Instructions =
    "You are OPC UA Expert. Use the available tools to answer questions about " +
    "OPC UA specifications, NodeSets, type hierarchies, compliance, and companion " +
    "specs. Prefer structured tools (search_nodes, get_type_hierarchy, count_nodes, " +
    "list_specs, compare_versions, get_spec_summary, etc.) over free-form retrieval. " +
    "Use search_docs or search_docs_rag for free-form spec text questions. " +
    "Cite specification part numbers and sections. Be technically precise.";

// Build an MCP client against the OpcUaKb.McpServer HTTP/SSE endpoint. Each tool the
// server advertises becomes its own McpClientTool (which is an AIFunction) so the
// model can pick by name.
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri(mcpServerUrl),
    Name = "opcua-kb-agent",
});
var mcpClient = await McpClient.CreateAsync(transport);
var mcpTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"[STARTUP] Loaded {mcpTools.Count} tool(s) from MCP server '{mcpServerUrl}': " +
    string.Join(", ", mcpTools.Select(t => t.Name)));

var projectClient = new AIProjectClient(projectEndpoint, new DefaultAzureCredential());

AIAgent agent = projectClient
    .AsAIAgent(
        model: deployment,
        instructions: Instructions,
        name: "opcua-kb-agent",
        description: "OPC UA Expert — knowledge base agent backed by Azure AI Search via the OpcUaKb MCP server.",
        tools: [.. mcpTools]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
