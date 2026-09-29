using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.AI;

public enum AIRole {
	User,
	Assistant,
}

public sealed record AIMessage(AIRole Role, string Text);
public sealed record AIImage(byte[] Data, string MimeType);
public sealed record AIUsage(int InputTokens, int OutputTokens);
public sealed record AIResult(string Text, AIUsage Usage);
public sealed record AIStreamDelta(string Text, AIUsage? Usage = null);

public sealed class AIEndpoint {
	public string ApiKey { get; init; } = "";
	public Uri? BaseUrl { get; init; }
	public string? Project { get; init; }
	public string? Location { get; init; }
}

public sealed class AIRequest {
	public string System { get; init; } = "";
	public required IReadOnlyList<AIMessage> Messages { get; init; }
	public IReadOnlyList<AIImage> Images { get; init; } = [];
	public string? Model { get; init; }
	public AIEndpoint? Endpoint { get; init; }
	public float? Temperature { get; init; }
	public float? TopP { get; init; }
	public int? MaxOutputTokens { get; init; }
}

public interface IAIBackend {
	Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct);
	IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, CancellationToken ct);
}

public static class AIClient {
	public const int CallTimeoutMs = 120_000;
	public const int RetryCount = 2;

	private static readonly int[] RetryDelaysMs = [1_000, 2_000];

	private static IAIBackend? _openAI;
	private static IAIBackend? _gemini;
	private static AIProvider _defaultStack = AIProvider.OpenAI;
	private static SemaphoreSlim _gate = new(1, 1);
	private static ConcurrentDictionary<string, Lazy<IAIBackend>> _overrideBackends = new();
	private static Func<AIProvider, AIEndpoint, IAIBackend> _overrideFactory = DefaultCreateOverrideBackend;

	private static IAIBackend DefaultCreateOverrideBackend(AIProvider stack, AIEndpoint endpoint) {
		return stack == AIProvider.OpenAI ? new OpenAIBackend(endpoint) : new GeminiBackend(endpoint);
	}

	public static void Init(OpenAIConfig? openAI, GeminiConfig? gemini, AIProvider provider, int maxParallel) {
		IAIBackend? newOpenAI = null;
		IAIBackend? newGemini = null;
		try {
			newOpenAI = openAI == null || openAI.IsEmpty ? null : new OpenAIBackend(openAI);
			newGemini = gemini == null || gemini.IsEmpty ? null : new GeminiBackend(gemini);
		} catch (Exception) {
			(newOpenAI as IDisposable)?.Dispose();
			(newGemini as IDisposable)?.Dispose();
			throw;
		}
		Init(newOpenAI, newGemini, provider, maxParallel);
	}

	public static void Init(IAIBackend? openAI, IAIBackend? gemini, AIProvider provider, int maxParallel, Func<AIProvider, AIEndpoint, IAIBackend>? overrideFactory = null) {
		_openAI = openAI;
		_gemini = gemini;
		_defaultStack = provider == AIProvider.Gemini ? AIProvider.Gemini : AIProvider.OpenAI;
		_gate = new SemaphoreSlim(Math.Max(1, maxParallel), Math.Max(1, maxParallel));
		_overrideFactory = overrideFactory ?? DefaultCreateOverrideBackend;
		ReleaseOverrides();
	}

	public static void Shutdown() {
		var openAI = _openAI;
		var gemini = _gemini;
		_openAI = null;
		_gemini = null;
		if (openAI is IDisposable d1) d1.Dispose();
		if (gemini is IDisposable d2) d2.Dispose();
		ReleaseOverrides();
	}

	private static void ReleaseOverrides() {
		var old = Interlocked.Exchange(ref _overrideBackends, new ConcurrentDictionary<string, Lazy<IAIBackend>>());
		foreach (var lazy in old.Values) {
			if (lazy.IsValueCreated && lazy.Value is IDisposable disposable) disposable.Dispose();
		}
	}

	public static Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct = default) {
		return CompleteAsync(_defaultStack, request, ct);
	}

	public static async Task<AIResult?> CompleteAsync(AIProvider stack, AIRequest request, CancellationToken ct = default) {
		Validate(stack, request);
		if (request.Endpoint == null && Resolve(stack, request) == null) return null;
		await _gate.WaitAsync(ct);
		try {
			for (var attempt = 0; ; attempt++) {
				try {
					var backend = Resolve(stack, request);
					if (backend == null) return null;
					return await AttemptAsync(backend, request, ct);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					var transient = OpenAIBackend.IsTransient(e) || GeminiBackend.IsTransient(e);
					if (transient) {
						Logger.Error(typeof(AIClient), e, Localizer.Format("AICallTransientFailure", attempt + 1));
					} else {
						Logger.Error(typeof(AIClient), e, Localizer.Get("AICallFailed"));
					}
					if (!transient || attempt >= RetryCount) return null;
					await Task.Delay(RetryDelaysMs[attempt], ct);
				}
			}
		} finally {
			_gate.Release();
		}
	}

	public static IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, CancellationToken ct = default) {
		return StreamAsync(_defaultStack, request, ct);
	}

	public static IAsyncEnumerable<AIStreamDelta> StreamAsync(AIProvider stack, AIRequest request, CancellationToken ct = default) {
		Validate(stack, request);
		return StreamCoreAsync(stack, request, ct);
	}

	private static async IAsyncEnumerable<AIStreamDelta> StreamCoreAsync(AIProvider stack, AIRequest request, [EnumeratorCancellation] CancellationToken ct) {
		var backend = Resolve(stack, request);
		if (backend == null) yield break;
		await _gate.WaitAsync(ct);
		try {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(CallTimeoutMs);
			await foreach (var delta in backend.StreamAsync(request, timeout.Token)) {
				yield return delta;
			}
		} finally {
			_gate.Release();
		}
	}

	private static IAIBackend? Resolve(AIProvider stack, AIRequest request) {
		var endpoint = request.Endpoint;
		if (endpoint == null) return stack == AIProvider.OpenAI ? _openAI : _gemini;
		var key = $"{(stack == AIProvider.OpenAI ? "openai" : "gemini")}|{endpoint.ApiKey}|{endpoint.BaseUrl}|{endpoint.Project}|{endpoint.Location}";
		var lazy = _overrideBackends.GetOrAdd(key, _ => new Lazy<IAIBackend>(() => _overrideFactory(stack, endpoint)));
		try {
			return lazy.Value;
		} catch (Exception) {
			((ICollection<KeyValuePair<string, Lazy<IAIBackend>>>)_overrideBackends).Remove(new(key, lazy));
			throw;
		}
	}

	private static void Validate(AIProvider stack, AIRequest request) {
		if (request.MaxOutputTokens is <= 0) throw new ArgumentException(Localizer.Format("AIMaxOutputTokensInvalid", request.MaxOutputTokens));
		if (request.Temperature is < 0) throw new ArgumentException(Localizer.Format("AITemperatureInvalid", request.Temperature));
		if (request.TopP is < 0) throw new ArgumentException(Localizer.Format("AITopPInvalid", request.TopP));
		var endpoint = request.Endpoint;
		if (endpoint == null) return;
		if (string.IsNullOrEmpty(request.Model)) throw new ArgumentException(Localizer.Get("EndpointModelRequired"));
		if (endpoint.BaseUrl != null && endpoint.BaseUrl.Scheme != "http" && endpoint.BaseUrl.Scheme != "https") throw new ArgumentException(Localizer.Format("EndpointBaseUrlInvalid", endpoint.BaseUrl));
		if (stack == AIProvider.OpenAI) {
			if (endpoint.ApiKey.Length == 0) throw new ArgumentException(Localizer.Get("OpenAIEndpointMissingApiKey"));
			if (endpoint.BaseUrl == null) throw new ArgumentException(Localizer.Get("OpenAIEndpointMissingBaseUrl"));
			if (endpoint.Project != null || endpoint.Location != null) throw new ArgumentException(Localizer.Get("OpenAIEndpointProjectLocationUnsupported"));
		} else {
			var hasKey = endpoint.ApiKey.Length > 0;
			var hasProject = !string.IsNullOrEmpty(endpoint.Project);
			var hasLocation = !string.IsNullOrEmpty(endpoint.Location);
			if (hasKey && (hasProject || hasLocation)) throw new ArgumentException(Localizer.Get("GeminiEndpointAuthExclusive"));
			if (!hasKey && (!hasProject || !hasLocation)) throw new ArgumentException(Localizer.Get("GeminiEndpointAuthIncomplete"));
		}
	}

	private static async Task<AIResult?> AttemptAsync(IAIBackend backend, AIRequest request, CancellationToken ct) {
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(CallTimeoutMs);
		return await backend.CompleteAsync(request, timeout.Token);
	}
}
