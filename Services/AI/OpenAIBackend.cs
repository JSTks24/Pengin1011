using System.ClientModel;
using System.Runtime.CompilerServices;
using OpenAI;
using OpenAI.Chat;
using Pengin1011.Core;

namespace Pengin1011.Services.AI;

public sealed class OpenAIBackend : IAIBackend {
	private readonly ApiKeyCredential _credential;
	private readonly OpenAIClientOptions _options;
	private readonly string _defaultModel;

	public OpenAIBackend(OpenAIConfig config) {
		_credential = new ApiKeyCredential(config.ApiKey);
		_options = new OpenAIClientOptions();
		if (config.BaseUrl != null) _options.Endpoint = config.BaseUrl;
		_options.RetryPolicy = NoRetryPolicy.Instance;
		_defaultModel = config.Model;
	}

	public OpenAIBackend(AIEndpoint endpoint) {
		_credential = new ApiKeyCredential(endpoint.ApiKey);
		_options = new OpenAIClientOptions();
		_options.Endpoint = endpoint.BaseUrl!;
		_options.RetryPolicy = NoRetryPolicy.Instance;
		_defaultModel = "";
	}

	internal OpenAIBackend(AIEndpoint endpoint, System.ClientModel.Primitives.PipelineTransport transport) : this(endpoint) {
		_options.Transport = transport;
	}

	internal static bool IsTransient(Exception exception) {
		return exception switch {
			System.ClientModel.ClientResultException result => result.Status is 408 or 429 or (>= 500 and <= 599),
			OperationCanceledException => true,
			HttpRequestException => true,
			IOException => true,
			_ => false,
		};
	}

	public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
		var client = new ChatClient(request.Model ?? _defaultModel, _credential, _options);
		var completion = await client.CompleteChatAsync(BuildMessages(request), BuildOptions(request), ct);
		var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text ?? "" : "";
		var usage = completion.Value.Usage;
		return new AIResult(text, new AIUsage(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0));
	}

	public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
		var client = new ChatClient(request.Model ?? _defaultModel, _credential, _options);
		var updates = client.CompleteChatStreamingAsync(BuildMessages(request), BuildOptions(request), ct);
		await foreach (var update in updates.WithCancellation(ct)) {
			var text = update.ContentUpdate.Count > 0 ? update.ContentUpdate[0].Text ?? "" : "";
			var usage = update.Usage;
			if (text.Length > 0) {
				yield return new AIStreamDelta(text, usage == null ? null : new AIUsage(usage.InputTokenCount, usage.OutputTokenCount));
			} else if (usage != null) {
				yield return new AIStreamDelta("", new AIUsage(usage.InputTokenCount, usage.OutputTokenCount));
			}
		}
	}

	private static ChatCompletionOptions BuildOptions(AIRequest request) {
		var options = new ChatCompletionOptions();
		if (request.MaxOutputTokens is int max) options.MaxOutputTokenCount = max;
		if (request.Temperature is float temperature) options.Temperature = temperature;
		if (request.TopP is float topP) options.TopP = topP;
		return options;
	}

	private static List<ChatMessage> BuildMessages(AIRequest request) {
		var messages = new List<ChatMessage>();
		if (request.System.Length > 0) messages.Add(ChatMessage.CreateSystemMessage(request.System));
		var images = request.Images;
		var lastUserIndex = -1;
		for (var i = 0; i < request.Messages.Count; i++) {
			if (request.Messages[i].Role == AIRole.User) lastUserIndex = i;
		}
		for (var i = 0; i < request.Messages.Count; i++) {
			var message = request.Messages[i];
			if (i == lastUserIndex && images.Count > 0) {
				var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(message.Text) };
				foreach (var image in images) {
					parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image.Data), image.MimeType));
				}
				messages.Add(ChatMessage.CreateUserMessage(parts));
				continue;
			}
			if (message.Role == AIRole.Assistant) messages.Add(ChatMessage.CreateAssistantMessage(message.Text)); else messages.Add(ChatMessage.CreateUserMessage(message.Text));
		}
		if (images.Count > 0 && lastUserIndex < 0) {
			var parts = images.Select(static image => ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image.Data), image.MimeType)).ToList();
			messages.Add(ChatMessage.CreateUserMessage(parts));
		}
		return messages;
	}
}

internal sealed class NoRetryPolicy : System.ClientModel.Primitives.ClientRetryPolicy {
	public static readonly NoRetryPolicy Instance = new();

	protected override bool ShouldRetry(System.ClientModel.Primitives.PipelineMessage message, Exception? exception) {
		return false;
	}
}
