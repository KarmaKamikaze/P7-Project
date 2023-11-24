using ChatRPG.API;
using ChatRPG.API.Response;
using ChatRPG.Data.Models;
using ChatRPG.Services.Events;
using OpenAI_API.Chat;
using Environment = ChatRPG.Data.Models.Environment;

namespace ChatRPG.Services;

public class GameInputHandler
{
    private readonly ILogger<GameInputHandler> _logger;
    private readonly IOpenAiLlmClient _llmClient;
    private readonly GameStateManager _gameStateManager;
    private readonly bool _streamChatCompletions;
    private readonly Dictionary<SystemPromptType, string> _systemPrompts = new();
    private const int PlayerDmgMin = 10;
    private const int PlayerDmgMax = 25;

    private static readonly Dictionary<CharacterType, (int, int)> CharacterTypeDamageDict = new()
    {
        { CharacterType.Humanoid, (10, 20) },
        { CharacterType.SmallCreature, (5, 10) },
        { CharacterType.LargeCreature, (15, 25) },
        { CharacterType.Monster, (20, 30) }
    };

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
        _systemPrompts.Add(SystemPromptType.CombatOpponentDescription, sysPromptSec.GetValue("CombatOpponentDescription", "")!);
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
        string systemPrompt = await GetRelevantSystemPrompt(campaign, conversation);
        await GetResponseAndUpdateState(campaign, conversation, systemPrompt);
        _logger.LogInformation("Finished processing prompt.");
    }

    private async Task<string> GetRelevantSystemPrompt(Campaign campaign, IList<OpenAiGptMessage> conversation)
    {
        SystemPromptType combatOutcome = SystemPromptType.Default;
        if (campaign.CombatMode)
        {
            string opponentDescriptionString = await _llmClient.GetChatCompletion(conversation, _systemPrompts[SystemPromptType.CombatOpponentDescription]);
            _logger.LogInformation("Opponent description response: {opponentDescriptionString}", opponentDescriptionString);
            OpenAiGptMessage opponentDescriptionMessage = new(ChatMessageRole.Assistant, opponentDescriptionString);
            LlmResponse? opponentDescriptionResponse = opponentDescriptionMessage.TryParseFromJson();
            LlmResponseCharacter? resChar = opponentDescriptionResponse?.Characters?.FirstOrDefault();
            if (resChar != null)
            {
                Environment environment = campaign.Environments.Last();
                Character character = new(campaign, environment, GameStateManager.ParseToEnum(resChar.Type!, CharacterType.Humanoid),
                    resChar.Name!, resChar.Description!, false);
                campaign.InsertOrUpdateCharacter(character);
            }
            string? opponentName = opponentDescriptionResponse?.Opponent?.ToLower();
            Character? opponent = campaign.Characters.LastOrDefault(c => !c.IsPlayer && c.Name.ToLower().Equals(opponentName));
            if (opponent == null)
            {
                _logger.LogError("Opponent is unknown! Leaving combat mode...");
                campaign.CombatMode = false;
                return _systemPrompts[SystemPromptType.Default];
            }
            combatOutcome = DetermineCombatOutcome();
            (int playerDmg, int opponentDmg) = ComputeCombatDamage(combatOutcome, opponent.Type);
            string messageContent = "";
            if (playerDmg != 0)
            {
                messageContent += $"The player hits with their attack, dealing {playerDmg} damage.";
                if (opponent.AdjustHealth(-playerDmg))
                {
                    messageContent +=
                        $" With no health points remaining, {opponent.Name} dies and can no longer participate in the narrative.";
                }
                _logger.LogInformation("Combat: {Name} hits {Name} for {x} damage. Health: {CurrentHealth}/{MaxHealth}", campaign.Player.Name, opponent.Name, playerDmg, opponent.CurrentHealth, opponent.MaxHealth);
            }
            else
            {
                messageContent += "The player misses with their attack, dealing no damage.";
            }

            if (opponentDmg != 0)
            {
                messageContent += $"The opponent will hit with their next attack, dealing {opponentDmg} damage.";
                if (campaign.Player.AdjustHealth(-opponentDmg))
                {
                    await HandlePlayerDeath(campaign.Player, conversation);
                }
                _logger.LogInformation("Combat: {Name} hits {Name} for {x} damage. Health: {CurrentHealth}/{MaxHealth}", opponent.Name, campaign.Player.Name, opponentDmg, campaign.Player.CurrentHealth, campaign.Player.MaxHealth);
            }
            else
            {
                messageContent += "The opponent will miss their next attack, dealing no damage.";
            }

            OpenAiGptMessage message = new(ChatMessageRole.System, messageContent);
            conversation.Add(message);
        }
        return _systemPrompts[combatOutcome];
    }

    private static SystemPromptType DetermineCombatOutcome()
    {
        Random rand = new Random();
        double playerRoll = rand.NextDouble();
        double opponentRoll = rand.NextDouble();

        if (playerRoll >= 0.3)
        {
            return opponentRoll >= 0.6 ? SystemPromptType.CombatHitHit : SystemPromptType.CombatHitMiss;
        }

        return opponentRoll >= 0.5 ? SystemPromptType.CombatMissHit : SystemPromptType.CombatMissMiss;
    }

    private static (int, int) ComputeCombatDamage(SystemPromptType combatOutcome, CharacterType opponentType)
    {
        Random rand = new Random();
        int playerDmg = 0;
        int opponentDmg = 0;
        (int opponentMin, int opponentMax) = CharacterTypeDamageDict[opponentType];
        switch (combatOutcome)
        {
            case SystemPromptType.CombatHitHit:
                playerDmg = rand.Next(PlayerDmgMin, PlayerDmgMax);
                opponentDmg = rand.Next(opponentMin, opponentMax);
                break;
            case SystemPromptType.CombatHitMiss:
                playerDmg = rand.Next(PlayerDmgMin, PlayerDmgMax);
                break;
            case SystemPromptType.CombatMissHit:
                opponentDmg = rand.Next(opponentMin, opponentMax);
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
