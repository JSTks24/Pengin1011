using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using Pengin1011.Core;

namespace Pengin1011.Services.AI;

public sealed class GeminiBackend : IAIBackend, IDisposable {
	private readonly Client _client;
	private readonly string _defaultModel;

	public GeminiBackend(AIPreset preset) {
		_client = BuildClient(preset, null);
		_defaultModel = preset.Model;
	}

	internal GeminiBackend(AIPreset preset, ClientOptions clientOptions) {
		_client = BuildClient(preset, clientOptions);
		_defaultModel = preset.Model;
	}

	private static Client BuildClient(AIPreset preset, ClientOptions? clientOptions) {
		HttpOptions? http = null;
		if (preset.BaseUrl.Length > 0) http = new HttpOptions { BaseUrl = preset.BaseUrl };
		return preset.Provider == AIPresetProvider.Vertex
			? new Client(enterprise: true, project: preset.Project, location: preset.Location, httpOptions: http, clientOptions: clientOptions)
			: new Client(enterprise: false, vertexAI: false, apiKey: preset.ApiKey, httpOptions: http, clientOptions: clientOptions);
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
		var config = new GenerateContentConfig();
		if (request.System.Length > 0) config.SystemInstruction = new Content { Parts = [new Part { Text = request.System }] };
		if (request.Temperature is float temperature) config.Temperature = temperature;
		if (request.TopP is float topP) config.TopP = topP;
		if (request.MaxOutputTokens is int max) config.MaxOutputTokens = max;
		return config;
	}
}
