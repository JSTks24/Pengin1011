using System.Collections.Concurrent;
using System.Reflection;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011.Core;

public sealed class ModuleRegistration : IDisposable {
	private readonly List<Action> _rollbacks = [];
	private int _committed;
	private int _disposed;

	internal ModuleRegistration(string name) {
		Name = name;
	}

	public string Name { get; }

	internal void Track(Action rollback) {
		_rollbacks.Add(rollback);
	}

	public void Commit() {
		Interlocked.Exchange(ref _committed, 1);
	}

	public void Dispose() {
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		if (Volatile.Read(ref _committed) != 0) return;
		Rollback();
	}

	internal void Rollback() {
		for (var i = _rollbacks.Count - 1; i >= 0; i--) {
			try {
				_rollbacks[i]();
			} catch (Exception e) {
				Logger.Error(typeof(ModuleRegistration), e, Loc.Format("ModuleRegistrationRollbackFailed", Name));
			}
		}
		_rollbacks.Clear();
	}
}
