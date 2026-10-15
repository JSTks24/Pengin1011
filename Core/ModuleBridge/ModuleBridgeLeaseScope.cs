using System.Runtime.CompilerServices;

namespace Pengin1011.Core;

public static class ModuleBridgeLeaseScope {
	public static async Task RunTask<TDelegate>(ModuleBridgeDispatcher.BridgeCall<TDelegate> call, Func<Task> body) where TDelegate : Delegate {
		using (call.Lease) {
			await body().ConfigureAwait(false);
		}
	}

	public static async Task<TResult> RunTaskOf<TDelegate, TResult>(ModuleBridgeDispatcher.BridgeCall<TDelegate> call, Func<Task<TResult>> body) where TDelegate : Delegate {
		using (call.Lease) {
			return await body().ConfigureAwait(false);
		}
	}

	public static async ValueTask RunValueTask<TDelegate>(ModuleBridgeDispatcher.BridgeCall<TDelegate> call, Func<ValueTask> body) where TDelegate : Delegate {
		using (call.Lease) {
			await body().ConfigureAwait(false);
		}
	}

	public static async ValueTask<TResult> RunValueTaskOf<TDelegate, TResult>(ModuleBridgeDispatcher.BridgeCall<TDelegate> call, Func<ValueTask<TResult>> body) where TDelegate : Delegate {
		using (call.Lease) {
			return await body().ConfigureAwait(false);
		}
	}

	public static async IAsyncEnumerable<TItem> RunAsyncEnumerable<TDelegate, TItem>(Func<ModuleBridgeDispatcher.BridgeCall<TDelegate>> admit, Func<TDelegate, IAsyncEnumerable<TItem>> body, [EnumeratorCancellation] CancellationToken ct = default) where TDelegate : Delegate {
		var call = admit();
		using (call.Lease) {
			await foreach (var item in body(call.Handler).WithCancellation(ct).ConfigureAwait(false)) {
				yield return item;
			}
		}
	}

	public static IEnumerable<TItem> RunSyncEnumerable<TDelegate, TItem>(Func<ModuleBridgeDispatcher.BridgeCall<TDelegate>> admit, Func<TDelegate, IEnumerable<TItem>> body) where TDelegate : Delegate {
		var call = admit();
		using (call.Lease) {
			foreach (var item in body(call.Handler)) {
				yield return item;
			}
		}
	}
}
