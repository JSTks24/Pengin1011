using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal sealed class DatabaseService {
	internal static TimeSpan PrepareBudget = TimeSpan.FromSeconds(30);
	private readonly object _gate = new();
	private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
	private readonly Dictionary<string, DatabaseEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<Type, DatabaseEntry> _contexts = [];
	private CancellationTokenSource _lifetime = new();
	private DataDirectoryOwnership? _ownership;

	internal sealed class DatabaseEntry {
		public required string Id { get; init; }
		public required Type ContextType { get; init; }
		public required Assembly Owner { get; init; }
		public required Func<string, DbContext> Create { get; init; }
		public DatabaseState State { get; set; }
		public Task<DatabasePrepareResult>? PrepareTask { get; set; }
		public string? CurrentHistory { get; set; }
		public string? Failure { get; set; }
	}

	public string DataDirectory { get; private set; } = DataDirectoryResolver.Default;
	public bool DataDirectoryConfigured { get; private set; }
	public DatabaseProvider Provider { get; private set; }
	internal string ExternalConnectionString { get; private set; } = "";
	internal SemaphoreSlim MaintenanceGate => _maintenanceGate;

	internal void AcquireOwnership() {
		if (Provider != DatabaseProvider.SQLite) return;
		lock (_gate) {
			if (_ownership != null) return;
			if (!DataDirectoryOwnership.TryAcquire(DataDirectory, out _ownership, out var reason)) {
				throw new IOException(Loc.Format("DataDirectoryNotOwned", DataDirectory, reason));
			}
		}
	}

	internal void ReleaseOwnership() {
		lock (_gate) {
			if (_entries.Values.Any(entry => entry.PrepareTask is { IsCompleted: false })) {
				throw new InvalidOperationException(Loc.Get("DatabaseWorkPending"));
			}
			_ownership?.Dispose();
			_ownership = null;
		}
	}

	internal async Task StopPreparingAsync() {
		Task[] tasks;
		lock (_gate) {
			_lifetime.Cancel();
			tasks = _entries.Values.Select(entry => entry.PrepareTask).OfType<Task>().ToArray();
		}
		await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	internal bool Register(Assembly owner, IReadOnlyList<DatabaseDefinition> definitions) {
		lock (_gate) {
			if (definitions.Count == 0 || definitions.All(definition => _entries.TryGetValue(definition.Id, out var existing) && existing.Owner == owner && existing.ContextType == definition.ContextType)) return false;
			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var contexts = new HashSet<Type>();
			foreach (var definition in definitions) {
				if (!ids.Add(definition.Id) || !contexts.Add(definition.ContextType) || _entries.ContainsKey(definition.Id)
					|| _contexts.ContainsKey(definition.ContextType)) {
					throw new InvalidOperationException(Loc.Format("DatabaseRegistrationConflict", definition.Id));
				}
			}
			foreach (var definition in definitions) {
				_entries.Add(definition.Id, new DatabaseEntry {
					Id = definition.Id, ContextType = definition.ContextType, Owner = owner, Create = definition.Create,
					State = DatabaseState.Unprepared
				});
				_contexts.Add(definition.ContextType, _entries[definition.Id]);
			}
			return true;
		}
	}

	internal void Unregister(Assembly owner) {
		lock (_gate) {
			var entries = _entries.Values.Where(entry => entry.Owner == owner).ToArray();
			if (entries.Any(entry => entry.PrepareTask is { IsCompleted: false })) throw new InvalidOperationException(Loc.Get("DatabaseWorkPending"));
			foreach (var entry in entries) {
				_entries.Remove(entry.Id);
				_contexts.Remove(entry.ContextType);
			}
		}
	}

	internal DatabaseEntry EntryOf(Type contextType) {
		lock (_gate) return _contexts.TryGetValue(contextType, out var entry) ? entry : throw new InvalidOperationException(Loc.Format("DatabaseNotRegistered", contextType.Name));
	}

	internal DatabaseState StateOf<TContext>() where TContext : DbContext {
		lock (_gate) return _contexts.TryGetValue(typeof(TContext), out var entry) ? entry.State : DatabaseState.Unprepared;
	}

	internal DatabaseState StateOf(string id) {
		lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.State : DatabaseState.Unprepared;
	}

	internal DatabaseEntry? FindEntry(string id) {
		lock (_gate) return _entries.GetValueOrDefault(id);
	}

	internal IReadOnlyList<string> Ids() {
		lock (_gate) return _entries.Keys.ToArray();
	}

	internal async Task PrepareModuleAsync(Assembly owner, CancellationToken ct) {
		DatabaseEntry[] entries;
		lock (_gate) entries = _entries.Values.Where(entry => entry.Owner == owner).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
		foreach (var entry in entries) {
			ct.ThrowIfCancellationRequested();
			var result = await PrepareAsync(entry, ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			if (!result.Success) throw new InvalidOperationException(result.Failure);
		}
	}

	public void Configure(string dataDirectory) {
		lock (_gate) {
			if (_ownership != null && !DataDirectoryResolver.IsSameDirectory(DataDirectory, dataDirectory)) {
				throw new InvalidOperationException(Loc.Get("DataDirectoryAlreadyOwned"));
			}
			DataDirectory = Path.GetFullPath(dataDirectory);
			DataDirectoryConfigured = true;
		}
	}

	internal void ConfigureProvider(DatabaseConfig config) {
		if (!DatabaseOptions.TryValidate(config, out var failure)) throw new ArgumentException(failure, nameof(config));
		lock (_gate) {
			if (_entries.Count != 0 || _ownership != null) throw new InvalidOperationException(Loc.Get("DatabaseWorkPending"));
			Provider = DatabaseOptions.ProviderOf(config);
			ExternalConnectionString = Provider == DatabaseProvider.SQLite ? "" : DatabaseOptions.ConnectionString(config);
		}
	}

	internal string PathOf(string id) => Path.Combine(DataDirectory, DatabaseIdentity.FileName(id));

	internal string ConnectionStringFor(DatabaseEntry entry, bool creating) {
		if (Provider != DatabaseProvider.SQLite) return ExternalConnectionString;
		return creating ? SqliteConnectionPolicy.ForCreatingDatabase(PathOf(entry.Id)) : SqliteConnectionPolicy.ForExistingDatabase(PathOf(entry.Id));
	}

	internal Task<DatabasePrepareResult> PrepareAsync<TContext>(CancellationToken ct) where TContext : DbContext {
		return PrepareAsync(EntryOf(typeof(TContext)), ct);
	}

	private Task<DatabasePrepareResult> PrepareAsync(DatabaseEntry entry, CancellationToken ct) {
		Task<DatabasePrepareResult> rejected;
		string failure;
		lock (_gate) {
			if (entry.PrepareTask != null) return entry.PrepareTask;
			if (!_lifetime.IsCancellationRequested) {
				entry.State = DatabaseState.Preparing;
				return entry.PrepareTask = Task.Run(() => PrepareCoreAsync(entry, ct));
			}
			entry.State = DatabaseState.Failed;
			failure = Provider == DatabaseProvider.SQLite ? Loc.Get("DatabaseWorkPending") : DatabaseOptions.Failure(new OperationCanceledException(), cancelled: true);
			entry.Failure = failure;
			rejected = entry.PrepareTask = Task.FromResult(new DatabasePrepareResult(false, entry.State, failure));
		}
		Logger.Error(typeof(DatabaseService), Loc.Format("DatabasePrepareFailed", entry.Id) + " " + failure);
		return rejected;
	}

	private async Task<DatabasePrepareResult> PrepareCoreAsync(DatabaseEntry entry, CancellationToken ct) {
		DatabasePrepareResult result;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
		budget.CancelAfter(PrepareBudget);
		try {
			budget.Token.ThrowIfCancellationRequested();
			if (Provider != DatabaseProvider.SQLite) {
				result = await ExternalDatabasePrepareEngine.RunAsync(this, entry, budget.Token).ConfigureAwait(false);
			} else {
				AcquireOwnership();
				result = DatabaseMaintenance.Exists(DataDirectory, entry.Id)
					? new DatabasePrepareResult(false, DatabaseState.MaintenanceRequired, Loc.Format("DatabaseMaintenanceRequired", entry.Id))
					: await DatabasePrepareEngine.RunAsync(this, entry, budget.Token).ConfigureAwait(false);
			}
		} catch (Exception e) {
			var failure = Provider == DatabaseProvider.SQLite ? e.Message : DatabaseOptions.Failure(e,
				cancelled: ct.IsCancellationRequested || _lifetime.IsCancellationRequested, timedOut: budget.IsCancellationRequested);
			if (Provider == DatabaseProvider.SQLite) Logger.Error(typeof(DatabaseService), e, Loc.Format("DatabasePrepareFailed", entry.Id));
			else Logger.Error(typeof(DatabaseService), Loc.Format("DatabasePrepareFailed", entry.Id) + " " + failure);
			result = new DatabasePrepareResult(false, DatabaseState.Failed, failure);
		}
		lock (_gate) {
			entry.State = result.State;
			entry.Failure = result.Failure;
		}
		return result;
	}

	internal void ResetForTest(string? directory = null) {
		StopPreparingAsync().GetAwaiter().GetResult();
		ReleaseOwnership();
		lock (_gate) {
			_entries.Clear();
			_contexts.Clear();
			_lifetime.Dispose();
			_lifetime = new CancellationTokenSource();
			DataDirectory = directory ?? DataDirectoryResolver.Default;
			DataDirectoryConfigured = directory != null;
			Provider = DatabaseProvider.SQLite;
			ExternalConnectionString = "";
		}
	}
}
