// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert — Foundry Hosted Agent (Agent Framework + Toolbox)
//
// Loads a Foundry Toolbox and passes its tools to the agent as SERVER-SIDE
// tools. The Foundry platform handles tool discovery and invocation through
// the Responses API — this process does not connect to the toolbox MCP
// proxy or invoke tools locally.
//
// Required environment variables:
//   FOUNDRY_PROJECT_ENDPOINT       — Foundry project endpoint (auto-injected in hosted containers)
//   AZURE_AI_MODEL_DEPLOYMENT_NAME — Model deployment name (declared in agent.manifest.yaml)
//   TOOLBOX_NAME                   — Name of the Foundry Toolbox to load
// ═══════════════════════════════════════════════════════════════════════
#pragma warning disable OPENAI001 // GetToolboxToolsAsync is experimental
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;

// Load .env file if present (for local development)
Env.TraversePath().Load();

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));

var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");

var toolboxName = Environment.GetEnvironmentVariable("TOOLBOX_NAME")
    ?? throw new InvalidOperationException("TOOLBOX_NAME environment variable is not set.");

const string Instructions =
    "You are OPC UA Expert. Use the available tools to answer questions about " +
    "OPC UA specifications, NodeSets, type hierarchies, compliance, and companion " +
    "specs. Prefer structured tools (search_nodes, get_type_hierarchy, count_nodes, " +
    "list_specs, compare_versions, get_spec_summary, etc.) over free-form retrieval. " +
    "Use search_docs or search_docs_rag for free-form spec text questions. " +
    "Cite specification part numbers and sections. Be technically precise.";

// Fetch the toolbox's tools from Foundry. Omitting the version resolves the toolbox's
// current default version. The returned AITools are passed directly to the agent as
// server-side tools — Foundry will execute them on the agent's behalf.
var projectClient = new AIProjectClient(projectEndpoint, new DefaultAzureCredential());
var tools = await projectClient.GetToolboxToolsAsync(toolboxName);

AIAgent agent = projectClient
    .AsAIAgent(
        model: deployment,
        instructions: Instructions,
        name: "opcua-kb-agent",
        description: "OPC UA Expert — knowledge base agent backed by Azure AI Search and an MCP toolbox.",
        tools: [.. tools]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
