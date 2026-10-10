using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIAsk.Plugin.Chat;

/// <summary>
/// Streams text deltas from an OpenAI-compatible /chat/completions endpoint.
/// The caller owns the injected HttpClient; this class does not dispose it.
/// </summary>
public sealed class OpenAiCompatibleChatClient
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        using var message = CreateRequestMessage(request);
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ChatClientException(ChatFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            throw new ChatClientException(ChatFailureKind.Connection);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw ToChatException(response.StatusCode);
            }

            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null || !line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var data = line[5..].Trim();
                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {
                        yield break;
                    }

                    if (TryReadContentDelta(data, out var content))
                    {
                        yield return content;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new ChatClientException(ChatFailureKind.Timeout);
            }
            catch (HttpRequestException)
            {
                throw new ChatClientException(ChatFailureKind.Connection);
            }
            catch (IOException)
            {
                throw new ChatClientException(ChatFailureKind.Connection);
            }
        }
    }

    internal static bool TryReadContentDelta(string eventData, out string content)
    {
        content = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(eventData);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)
                || delta.ValueKind != JsonValueKind.Object
                || !delta.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            content = contentElement.GetString() ?? string.Empty;
            return content.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CreateRequestMessage(ChatRequest request)
    {
        var endpoint = new Uri(new Uri(request.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "chat/completions");
        var payload = JsonSerializer.Serialize(new ChatCompletionPayload(
            request.Model,
            request.Messages.Select(message => new ChatCompletionMessage(message.Role, message.Content)).ToArray(),
            request.MaxTokens));

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (request.ExtraHeaders is not null)
        {
            foreach (var (name, value) in request.ExtraHeaders)
            {
                if (IsAscii(value))
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return message;
    }

    private static void ValidateRequest(ChatRequest request)
    {
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Base URL must be an absolute URI.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new ArgumentException("API key is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("Model is required.", nameof(request));
        }

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(request));
        }
    }

    private static bool IsAscii(string value) => value.All(character => character <= 0x7F);

    private static ChatClientException ToChatException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ChatClientException(ChatFailureKind.Authentication, (int)statusCode),
        HttpStatusCode.TooManyRequests =>
            new ChatClientException(ChatFailureKind.RateLimited, (int)statusCode),
        _ when (int)statusCode >= 500 =>
            new ChatClientException(ChatFailureKind.Server, (int)statusCode),
        _ => new ChatClientException(ChatFailureKind.Protocol, (int)statusCode)
    };

    private sealed record ChatCompletionPayload(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatCompletionMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] double Temperature = 0.7,
        [property: JsonPropertyName("stream")] bool Stream = true);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
