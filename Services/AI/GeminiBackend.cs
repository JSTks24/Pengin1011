using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using QingQiu1011.Core;

namespace QingQiu1011.Services.AI;

public sealed class GeminiBackend : IAIBackend, IDisposable {
	private readonly Client _client;
	private readonly string _defaultModel;

	public GeminiBackend(GeminiConfig config) {
		_client = config.ApiKey.Length > 0
			? new Client(enterprise: false, vertexAI: false, apiKey: config.ApiKey)
			: new Client(enterprise: true, project: config.Project, location: config.Location);
		_defaultModel = config.Model;
	}

	public void Dispose() {
		_client.Dispose();
	}

	internal static bool IsTransient(Exception exception) {
		return exception switch {
			Google.GenAI.ServerError => true,
			OperationCanceledException => true,
			HttpRequestException => true,
			IOException => true,
			_ => false,
		};
	}

	public async Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct) {
		var response = await _client.Models.GenerateContentAsync(
			request.Model ?? _defaultModel,
			BuildContents(request),
			BuildConfig(request),
			cancellationToken: ct);
		var usage = response.UsageMetadata;
		return new AIResult(response.Text ?? "", new AIUsage(usage?.PromptTokenCount ?? 0, usage?.CandidatesTokenCount ?? 0));
	}

	public async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
		var responses = _client.Models.GenerateContentStreamAsync(
			request.Model ?? _defaultModel,
			BuildContents(request),
			BuildConfig(request),
			cancellationToken: ct);
		await foreach (var response in responses.WithCancellation(ct)) {
			var usage = response.UsageMetadata;
			yield return new AIStreamDelta(response.Text ?? "", usage == null ? null : new AIUsage(usage.PromptTokenCount ?? 0, usage.CandidatesTokenCount ?? 0));
		}
	}

	private static List<Content> BuildContents(AIRequest request) {
		var contents = new List<Content>();
		foreach (var message in request.Messages) {
			contents.Add(new Content {
				Role = message.Role == AIRole.Assistant ? "model" : "user",
				Parts = [new Part { Text = message.Text }],
			});
		}
		if (request.Images.Count > 0) {
			var imageParts = new List<Part>();
			foreach (var image in request.Images) {
				imageParts.Add(new Part { InlineData = new Blob { Data = image.Data, MimeType = image.MimeType } });
			}
			var last = contents.Count > 0 && contents[^1].Role == "user" ? contents[^1] : null;
			if (last != null) {
				last.Parts = [.. last.Parts ?? [], .. imageParts];
			} else {
				contents.Add(new Content { Role = "user", Parts = imageParts });
			}
		}
		return contents;
	}

	private static GenerateContentConfig BuildConfig(AIRequest request) {
		if (request.System.Length == 0) return new GenerateContentConfig();
		return new GenerateContentConfig {
			SystemInstruction = new Content { Parts = [new Part { Text = request.System }] },
		};
	}
}
