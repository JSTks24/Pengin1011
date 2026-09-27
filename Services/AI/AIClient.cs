using System.Runtime.CompilerServices;
using QingQiu1011.Core;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Services.AI;

public enum AIRole {
	User,
	Assistant,
}

public sealed record AIMessage(AIRole Role, string Text);
public sealed record AIImage(byte[] Data, string MimeType);
public sealed record AIUsage(int InputTokens, int OutputTokens);
public sealed record AIResult(string Text, AIUsage Usage);
public sealed record AIStreamDelta(string Text, AIUsage? Usage = null);

public sealed class AIRequest {
	public string System { get; init; } = "";
	public required IReadOnlyList<AIMessage> Messages { get; init; }
	public IReadOnlyList<AIImage> Images { get; init; } = [];
	public string? Model { get; init; }
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

	public static void Init(IAIBackend? openAI, IAIBackend? gemini, AIProvider provider, int maxParallel) {
		_openAI = openAI;
		_gemini = gemini;
		_defaultStack = provider == AIProvider.Gemini ? AIProvider.Gemini : AIProvider.OpenAI;
		_gate = new SemaphoreSlim(Math.Max(1, maxParallel), Math.Max(1, maxParallel));
	}

	public static void Shutdown() {
		var openAI = _openAI;
		var gemini = _gemini;
		_openAI = null;
		_gemini = null;
		if (openAI is IDisposable d1) d1.Dispose();
		if (gemini is IDisposable d2) d2.Dispose();
	}

	public static Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct = default) {
		return CompleteAsync(_defaultStack, request, ct);
	}

	public static async Task<AIResult?> CompleteAsync(AIProvider stack, AIRequest request, CancellationToken ct = default) {
		var backend = Resolve(stack);
		if (backend == null) return null;
		await _gate.WaitAsync(ct);
		try {
			for (var attempt = 0; ; attempt++) {
				try {
					return await AttemptAsync(backend, request, ct);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					var transient = OpenAIBackend.IsTransient(e) || GeminiBackend.IsTransient(e);
					if (transient) {
						Logger.Error(typeof(AIClient), e, $"AI 非流式调用临时失败（第 {attempt + 1} 次尝试）");
					} else {
						Logger.Error(typeof(AIClient), e, "AI 非流式调用失败");
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

	public static async IAsyncEnumerable<AIStreamDelta> StreamAsync(AIProvider stack, AIRequest request, [EnumeratorCancellation] CancellationToken ct = default) {
		var backend = Resolve(stack);
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

	private static IAIBackend? Resolve(AIProvider stack) {
		return stack == AIProvider.OpenAI ? _openAI : _gemini;
	}

	private static async Task<AIResult?> AttemptAsync(IAIBackend backend, AIRequest request, CancellationToken ct) {
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(CallTimeoutMs);
		return await backend.CompleteAsync(request, timeout.Token);
	}
}
