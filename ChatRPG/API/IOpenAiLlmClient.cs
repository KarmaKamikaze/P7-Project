namespace ChatRPG.API;

public interface IOpenAiLlmClient
{
    Task<string> GetChatCompletion(List<OpenAiGptInputMessage> inputs);
}

public record ChatCompletionObject(string Id, string Object, int Created, string Model, Choice[] Choices, Usage Usage);

public record Choice(int Index, Message Message, string FinishReason);

public record Message(string Role, string Content);

public record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

public record OpenAiGptInputMessage(string Role, string Content);

public record OpenAiGptInput(string Model, List<OpenAiGptInputMessage> Messages, double Temperature);
