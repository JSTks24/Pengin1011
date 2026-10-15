using Microsoft.EntityFrameworkCore;
using Pengin1011.Core.Logging;

namespace Pengin1011.Core;

internal sealed class DatabaseService {
	internal static TimeSpan PrepareBudget = TimeSpan.FromSeconds(30);
	private readonly object _gate = new();
	private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
	private readonly Dictionary<string, DatabaseEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
	private CancellationTokenSource _lifetime = new();
	private DataDirectoryOwnership? _ownership;

	internal sealed class DatabaseEntry {
		public required string Id { get; init; }
		public required Type ContextType { get; init; }
		public DatabaseState State { get; set; }
		public Task<DatabasePrepareResult>? PrepareTask { get; set; }
		public string? Failure { get; set; }
	}

	public string DataDirectory { get; private set; } = DataDirectoryResolver.Default;
	public bool DataDirectoryConfigured { get; private set; }
	internal SemaphoreSlim MaintenanceGate => _maintenanceGate;

	internal void AcquireOwnership() {
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

	public DatabaseState StateOf<TContext>() where TContext : DbContext, new() {
		return DatabaseIdentity.TryDerive(typeof(TContext), out var id, out _) ? StateOf(id) : DatabaseState.Failed;
	}

	internal DatabaseState StateOf(string id) {
		lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.State : DatabaseState.Unprepared;
	}

	public void EnsureOwnership(string id, Type contextType) {
		lock (_gate) GetEntry(id, contextType);
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

	internal string PathOf(string id) => Path.Combine(DataDirectory, DatabaseIdentity.FileName(id));

	public string ConnectionStringFor(Type contextType, bool creating) {
		if (!DatabaseIdentity.TryDerive(contextType, out var id, out var failure)) {
			throw new ArgumentException(Loc.Get(failure ?? "DatabaseNameInvalid"), nameof(contextType));
		}
		EnsureOwnership(id, contextType);
		return creating ? SqliteConnectionPolicy.ForCreatingDatabase(PathOf(id)) : SqliteConnectionPolicy.ForExistingDatabase(PathOf(id));
	}

	public async Task<DatabasePrepareResult> PrepareAsync<TContext>(CancellationToken ct) where TContext : DbContext, new() {
		if (!DatabaseIdentity.TryDerive(typeof(TContext), out var id, out var failure)) {
			return new DatabasePrepareResult(false, DatabaseState.Failed, Loc.Get(failure ?? "DatabaseNameInvalid"));
		}
		Task<DatabasePrepareResult> task;
		lock (_gate) {
			var entry = GetEntry(id, typeof(TContext));
			if (entry.PrepareTask is { IsCompleted: false }) {
				task = entry.PrepareTask;
			} else {
				try {
					if (MaintenanceJournal.ListUnfinishedRestores(DataDirectory, id).Count > 0 || MaintenanceJournal.ReadMigrationIntent(DataDirectory, id) != null) {
						entry.State = DatabaseState.MaintenanceRequired;
						entry.Failure = Loc.Format("DatabaseMaintenanceRequired", id);
						return new DatabasePrepareResult(false, entry.State, entry.Failure);
					}
				} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
					entry.State = DatabaseState.MaintenanceRequired;
					entry.Failure = e.Message;
					return new DatabasePrepareResult(false, entry.State, entry.Failure);
				}
				if (entry.State == DatabaseState.Prepared) {
					if (File.Exists(PathOf(id))) return new DatabasePrepareResult(true, entry.State, null);
					entry.State = DatabaseState.Failed;
					entry.Failure = Loc.Format("DatabasePreparedFileMissing", id);
				}
				if (entry.State == DatabaseState.Failed) return new DatabasePrepareResult(false, entry.State, entry.Failure);
				if (_lifetime.IsCancellationRequested) return new DatabasePrepareResult(false, DatabaseState.Failed, Loc.Get("DatabaseWorkPending"));
				entry.State = DatabaseState.Preparing;
				entry.PrepareTask = Task.Run(() => PrepareCoreAsync(entry));
				task = entry.PrepareTask;
			}
		}
		return await task.WaitAsync(ct).ConfigureAwait(false);
	}

	private async Task<DatabasePrepareResult> PrepareCoreAsync(DatabaseEntry entry) {
		DatabasePrepareResult result;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		budget.CancelAfter(PrepareBudget);
		try {
			AcquireOwnership();
			result = await DatabasePrepareEngine.RunAsync(this, entry.Id, entry.ContextType, budget.Token).ConfigureAwait(false);
		} catch (Exception e) {
			Logger.Error(typeof(DatabaseService), e, Loc.Format("DatabasePrepareFailed", entry.Id));
			result = new DatabasePrepareResult(false, DatabaseState.Failed, e.Message);
		}
		lock (_gate) {
			entry.State = result.State;
			entry.Failure = result.Failure;
		}
		return result;
	}

	private DatabaseEntry GetEntry(string id, Type contextType) {
		if (_entries.TryGetValue(id, out var entry)) {
			if (entry.ContextType != contextType) {
				var message = Loc.Format("DbFileConflict", DatabaseIdentity.FileName(id), entry.ContextType.Name, contextType.Name);
				Logger.Error(typeof(DatabaseService), message);
				throw new InvalidOperationException(message);
			}
			return entry;
		}
		entry = new DatabaseEntry { Id = id, ContextType = contextType, State = DatabaseState.Unprepared };
		_entries.Add(id, entry);
		return entry;
	}

	internal void ResetForTest(string? directory = null) {
		StopPreparingAsync().GetAwaiter().GetResult();
		ReleaseOwnership();
		lock (_gate) {
			_entries.Clear();
			_lifetime.Dispose();
			_lifetime = new CancellationTokenSource();
			DataDirectory = directory ?? DataDirectoryResolver.Default;
			DataDirectoryConfigured = directory != null;
		}
	}

	internal void ResetPrepareState() {
		lock (_gate) _entries.Clear();
	}
}
