using System.Net;
using System.Text;
using System.Text.Json;
using AIAsk.Plugin.Chat;
using Xunit;

namespace AIAsk.Plugin.Tests.Chat;

public sealed class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task StreamAsync_SendsExpectedPayloadAndReturnsContentDeltas()
    {
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret", request.Headers.Authorization?.Parameter);
            Assert.Equal("keep", request.Headers.GetValues("X-Skill-1").Single());
            Assert.False(request.Headers.Contains("X-Skill-2"));

            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.Equal("fast", document.RootElement.GetProperty("model").GetString());
            Assert.Equal(100_000, document.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(0.7, document.RootElement.GetProperty("temperature").GetDouble());
            Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("hello", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

            return SseResponse(
                "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
                "data: not-json\n\n" +
                "data: {\"choices\":[]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
                "data: [DONE]\n\n");
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);
        var request = Request(extraHeaders: new Dictionary<string, string>
        {
            ["X-Skill-1"] = "keep",
            ["X-Skill-2"] = "中文"
        });

        var chunks = await CollectAsync(client.StreamAsync(request));

        Assert.Equal(["Hello", " world"], chunks);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ChatFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, ChatFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, ChatFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ChatFailureKind.Server)]
    public async Task StreamAsync_MapsHttpFailuresToSafeExceptions(HttpStatusCode status, ChatFailureKind expectedKind)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("secret response body https://private.example")
        }));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);

        var exception = await Assert.ThrowsAsync<ChatClientException>(
            async () => await CollectAsync(client.StreamAsync(Request())));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamAsync_MapsConnectionFailuresToSafeException()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("https://secret.example"));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);

        var exception = await Assert.ThrowsAsync<ChatClientException>(
            async () => await CollectAsync(client.StreamAsync(Request())));

        Assert.Equal(ChatFailureKind.Connection, exception.Kind);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamAsync_PropagatesCallerCancellation()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SseResponse(string.Empty);
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CollectAsync(client.StreamAsync(Request(), cancellation.Token)));
    }

    [Fact]
    public async Task StreamAsync_RejectsInvalidHeaderNamesAndKeepsValidAsciiHeaders()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("ok", request.Headers.GetValues("X-Skill-1").Single());
            Assert.False(request.Headers.Contains("Bad Header"));
            Assert.False(request.Headers.Contains("X-Newline"));
            return Task.FromResult(SseResponse("data: [DONE]\n\n"));
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);
        var request = Request(extraHeaders: new Dictionary<string, string>
        {
            ["X-Skill-1"] = "ok",
            ["Bad Header"] = "bad",
            ["X-Newline\r\nInjected"] = "bad"
        });

        var chunks = await CollectAsync(client.StreamAsync(request));

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task StreamAsync_MapsConfiguredTimeoutToSafeException()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SseResponse(string.Empty);
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatClient(httpClient);
        var request = Request(timeout: TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<ChatClientException>(
            async () => await CollectAsync(client.StreamAsync(request)));

        Assert.Equal(ChatFailureKind.Timeout, exception.Kind);
    }

    [Fact]
    public void TryReadContentDelta_IgnoresMalformedOrNonContentEvents()
    {
        Assert.False(OpenAiCompatibleChatClient.TryReadContentDelta("{", out _));
        Assert.False(OpenAiCompatibleChatClient.TryReadContentDelta("{\"choices\":[]}", out _));
        Assert.False(OpenAiCompatibleChatClient.TryReadContentDelta("{\"choices\":[{\"delta\":{}}]}", out _));
        Assert.True(OpenAiCompatibleChatClient.TryReadContentDelta("{\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}", out var content));
        Assert.Equal("ok", content);
    }

    private static ChatRequest Request(
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        TimeSpan? timeout = null) => new(
            "https://example.test/v1/",
            "secret",
            "fast",
            [new ChatMessage("user", "hello")],
            Timeout: timeout,
            ExtraHeaders: extraHeaders);

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
    };

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
