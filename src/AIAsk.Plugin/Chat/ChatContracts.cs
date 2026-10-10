namespace AIAsk.Plugin.Chat;

/// <summary>
/// A role/content message sent to an OpenAI-compatible chat-completions endpoint.
/// </summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Immutable input for one streaming chat-completions request.
/// </summary>
public sealed record ChatRequest(
    string BaseUrl,
    string ApiKey,
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens = 100_000,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null);

public enum ChatFailureKind
{
    Authentication,
    RateLimited,
    Timeout,
    Connection,
    Server,
    Protocol
}

/// <summary>
/// A safe, user-displayable chat failure. Its Message deliberately excludes the
/// endpoint, response body, and API key.
/// </summary>
public sealed class ChatClientException : Exception
{
    public ChatClientException(ChatFailureKind kind, int? statusCode = null)
        : base(GetSafeMessage(kind))
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public ChatFailureKind Kind { get; }

    public int? StatusCode { get; }

    private static string GetSafeMessage(ChatFailureKind kind) => kind switch
    {
        ChatFailureKind.Authentication => "Authentication failed. Check the API key in plugin settings.",
        ChatFailureKind.RateLimited => "Rate limit reached. Please try again later.",
        ChatFailureKind.Timeout => "The AI request timed out. Try again or increase Timeout.",
        ChatFailureKind.Connection => "Could not connect to the AI endpoint. Check Base URL and network.",
        ChatFailureKind.Protocol => "The AI endpoint returned an invalid response.",
        _ => "The AI request failed. Check endpoint, model, and plugin settings."
    };
}
