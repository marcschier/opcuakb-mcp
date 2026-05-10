using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace OpcUaKb.Agent;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert Agent — Microsoft 365 Agents SDK custom engine agent.
// Routes incoming Bot Framework activities to the OPC UA Knowledge Base
// retrieve + GPT-4o synthesis pipeline (KbService).
// ═══════════════════════════════════════════════════════════════════════

public sealed class OpcUaAgent : AgentApplication
{
    const string WelcomeText =
        "👋 Welcome to **OPC UA Expert**! I'm your assistant for OPC UA specifications, " +
        "NodeSets, and companion specs. Ask me anything about Part 3, Part 9 alarms, " +
        "Pumps, Machinery, DI, or compliance. Type `/help` for examples.";

    const string HelpText =
        "**OPC UA Expert — example questions**\n\n" +
        "• What is the difference between an ObjectType and a VariableType?\n" +
        "• Summarize Part 9 alarm states and their transitions\n" +
        "• What members does the PumpType in the Pumps companion spec define?\n" +
        "• Which Part 3 service sets are required for compliance level Standard?\n" +
        "• Show the supertype chain for AnalogUnitType\n" +
        "• How does the Machinery companion spec model identification?\n\n" +
        "Just type your question and I'll search the OPC UA reference specs for you.";

    readonly KbService _kbService;
    readonly ILogger<OpcUaAgent> _logger;

    public OpcUaAgent(AgentApplicationOptions options, KbService kbService, ILogger<OpcUaAgent> logger)
        : base(options)
    {
        _kbService = kbService;
        _logger = logger;

        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, OnMembersAddedAsync);
        OnMessage("/help", OnHelpAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync, rank: RouteRank.Last);
    }

    async Task OnMembersAddedAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (var member in turnContext.Activity.MembersAdded ?? [])
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(WelcomeText), cancellationToken);
            }
        }
    }

    async Task OnHelpAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await turnContext.SendActivityAsync(MessageFactory.Text(HelpText), cancellationToken);
    }

    async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var query = turnContext.Activity.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (!_kbService.Available)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(
                "⚠️ The knowledge base is not configured. Set `SEARCH_ENDPOINT`, `SEARCH_API_KEY`, " +
                "and `AOAI_ENDPOINT` environment variables on the agent host."),
                cancellationToken);
            return;
        }

        // Surface "thinking" feedback while the KB retrieve + GPT-4o synthesis runs.
        await turnContext.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, cancellationToken);

        try
        {
            _logger.LogInformation("[AGENT] Query=\"{Query}\"", query);
            var answer = await _kbService.AskAsync(query);
            await turnContext.SendActivityAsync(MessageFactory.Text(answer), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AGENT] Error answering query");
            await turnContext.SendActivityAsync(MessageFactory.Text(
                $"❌ Sorry, I hit an error while answering: {ex.Message}"),
                cancellationToken);
        }
    }
}
