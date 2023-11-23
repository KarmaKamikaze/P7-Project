using ChatRPG.API;
using ChatRPG.Data.Models;
using ChatRPG.Services.Events;
using OpenAI_API.Chat;

namespace ChatRPG.Services;

public class GameInputHandler
{
    private readonly ILogger<GameInputHandler> _logger;
    private readonly IOpenAiLlmClient _llmClient;
    private readonly GameStateManager _gameStateManager;
    private readonly bool _streamChatCompletions;
    private readonly Dictionary<SystemPromptType, string> _systemPrompts = new();

    public GameInputHandler(ILogger<GameInputHandler> logger, IOpenAiLlmClient llmClient, GameStateManager gameStateManager, IConfiguration configuration)
    {
        _logger = logger;
        _llmClient = llmClient;
        _gameStateManager = gameStateManager;
        _streamChatCompletions = configuration.GetValue("StreamChatCompletions", true);
        if (configuration.GetValue("UseMocks", false))
        {
            _streamChatCompletions = false;
        }
        IConfigurationSection sysPromptSec = configuration.GetRequiredSection("SystemPrompts");
        _systemPrompts.Add(SystemPromptType.Default, sysPromptSec.GetValue("Default", "")!);
        _systemPrompts.Add(SystemPromptType.CombatHitHit, sysPromptSec.GetValue("CombatHitHit", "")!);
        _systemPrompts.Add(SystemPromptType.CombatHitMiss, sysPromptSec.GetValue("CombatHitMiss", "")!);
        _systemPrompts.Add(SystemPromptType.CombatMissHit, sysPromptSec.GetValue("CombatMissHit", "")!);
        _systemPrompts.Add(SystemPromptType.CombatMissMiss, sysPromptSec.GetValue("CombatMissMiss", "")!);
    }

    public event EventHandler<ChatCompletionReceivedEventArgs>? ChatCompletionReceived;
    public event EventHandler<ChatCompletionChunkReceivedEventArgs>? ChatCompletionChunkReceived;

    private void OnChatCompletionReceived(OpenAiGptMessage message)
    {
        ChatCompletionReceived?.Invoke(this, new ChatCompletionReceivedEventArgs(message));
    }

    private void OnChatCompletionChunkReceived(bool isStreamingDone, string? chunk = null)
    {
        ChatCompletionChunkReceivedEventArgs args = (chunk is null)
            ? new ChatCompletionChunkReceivedEventArgs(isStreamingDone)
            : new ChatCompletionChunkReceivedEventArgs(isStreamingDone, chunk);
        ChatCompletionChunkReceived?.Invoke(this, args);
    }

    private async Task HandlePlayerDeath(Character player, IList<OpenAiGptMessage> conversation)
    {
        OpenAiGptMessage message = new(ChatMessageRole.System, "The player has died and the campaign is over.");
        conversation.Add(message);
        await GetResponseAndUpdateState(player.Campaign, conversation, _systemPrompts[SystemPromptType.Default]);
    }

    public async Task HandleUserPrompt(Campaign campaign, IList<OpenAiGptMessage> conversation)
    {
        string systemPrompt = GetRelevantSystemPrompt(campaign, conversation);
        await GetResponseAndUpdateState(campaign, conversation, systemPrompt);
        _logger.LogInformation("Finished processing prompt.");
    }

    private string GetRelevantSystemPrompt(Campaign campaign, IList<OpenAiGptMessage> conversation)
    {
        SystemPromptType type = SystemPromptType.Default;
        if (_gameStateManager.CombatMode)
        {
            OpenAiGptMessage lastPlayerMsg = conversation.Last(m => m.Role.Equals(ChatMessageRole.User));
            string playerMsg = lastPlayerMsg.Content.ToLower();
            Character? opponent = campaign.Characters.LastOrDefault();
            if (opponent == null)
            {
                _logger.LogError("Could not find an opponent from Message with content: \"{Content}\"", lastPlayerMsg.Content);
                // TODO: manually set CombatMode = false?
                return _systemPrompts[SystemPromptType.Default];
            }
            type = DetermineCombatOutcome();
            (int playerDmg, int opponentDmg) = ComputeCombatDamage(type);
            string messageContent = "";
            if (playerDmg != 0)
            {
                messageContent += $"The player hits with their attack, dealing {playerDmg} damage.";
                _logger.LogInformation("Combat: {Name} hits {Name} for {x} damage. Health: {CurrentHealth}/{MaxHealth}", campaign.Player.Name, opponent.Name, playerDmg, opponent.CurrentHealth, opponent.MaxHealth);
                if (opponent.AdjustHealth(-playerDmg))
                {
                    messageContent +=
                        $" With no health points remaining, {opponent.Name} dies and can no longer participate in the narrative.";
                }
            }
            else
            {
                messageContent += "The player misses with their attack, dealing no damage.";
            }

            if (opponentDmg != 0)
            {
                messageContent += $"The opponent will hit with their next attack, dealing {opponentDmg} damage.";
                _logger.LogInformation("Combat: {Name} hits {Name} for {x} damage. Health: {CurrentHealth}/{MaxHealth}", opponent.Name, campaign.Player.Name, opponentDmg, campaign.Player.CurrentHealth, campaign.Player.MaxHealth);
                if (campaign.Player.AdjustHealth(-opponentDmg))
                {
                    Task.Run(() => HandlePlayerDeath(campaign.Player, conversation));
                }
            }
            else
            {
                messageContent += "The opponent will miss their next attack, dealing no damage.";
            }

            OpenAiGptMessage message = new (ChatMessageRole.System, messageContent);
            conversation.Add(message);
        }
        return _systemPrompts[type];
    }

    private static SystemPromptType DetermineCombatOutcome()
    {
        Random rand = new Random();
        double playerRoll = rand.NextDouble();
        double opponentRoll = rand.NextDouble();

        if (playerRoll >= 0.4)
        {
            return opponentRoll >= 0.6 ? SystemPromptType.CombatHitHit : SystemPromptType.CombatHitMiss;
        }

        return opponentRoll >= 0.6 ? SystemPromptType.CombatMissHit : SystemPromptType.CombatMissMiss;
    }

    private static (int, int) ComputeCombatDamage(SystemPromptType combatOutcome)
    {
        Random rand = new Random();
        int playerDmg = 0;
        int opponentDmg = 0;

        switch (combatOutcome)
        {
            case SystemPromptType.CombatHitHit:
                playerDmg = rand.Next(5, 20);
                opponentDmg = rand.Next(3, 15);
                break;
            case SystemPromptType.CombatHitMiss:
                playerDmg = rand.Next(5, 20);
                break;
            case SystemPromptType.CombatMissHit:
                opponentDmg = rand.Next(3, 15);
                break;
            case SystemPromptType.CombatMissMiss:
                break;
        }
        return (playerDmg, opponentDmg);
    }

    private async Task GetResponseAndUpdateState(Campaign campaign, IList<OpenAiGptMessage> conversation, string systemPrompt)
    {
        if (conversation.Any(m => m.Role.Equals(ChatMessageRole.User)))
        {
            _gameStateManager.UpdateStateFromMessage(campaign, conversation.Last(m => m.Role.Equals(ChatMessageRole.User)));
        }
        if (_streamChatCompletions)
        {
            OpenAiGptMessage message = new(ChatMessageRole.Assistant, "");
            OnChatCompletionReceived(message);

            await foreach (string chunk in _llmClient.GetStreamedChatCompletion(conversation, systemPrompt))
            {
                OnChatCompletionChunkReceived(isStreamingDone: false, chunk);
            }
            OnChatCompletionChunkReceived(isStreamingDone: true);
            _gameStateManager.UpdateStateFromMessage(campaign, message);
            await _gameStateManager.SaveCurrentState(campaign);
        }
        else
        {
            string response = await _llmClient.GetChatCompletion(conversation, systemPrompt);
            OpenAiGptMessage message = new(ChatMessageRole.Assistant, response);
            OnChatCompletionReceived(message);
            _gameStateManager.UpdateStateFromMessage(campaign, message);
            await _gameStateManager.SaveCurrentState(campaign);
        }
    }
}
