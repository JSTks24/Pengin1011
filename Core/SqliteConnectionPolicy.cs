using Microsoft.Data.Sqlite;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pengin1011.Core;

public static class SqliteConnectionPolicy {
	public const int DefaultCommandTimeoutSeconds = 30;
	internal static readonly DbConnectionInterceptor Interceptor = new ConnectionPolicyInterceptor();

	public static string ForExistingDatabase(string path) {
		return Build(path, SqliteOpenMode.ReadWrite);
	}

	public static string ForCreatingDatabase(string path) {
		return Build(path, SqliteOpenMode.ReadWriteCreate);
	}

	public static string ForReadOnlyExisting(string path) {
		return Build(path, SqliteOpenMode.ReadOnly);
	}

	private static string Build(string path, SqliteOpenMode mode) {
		var builder = new SqliteConnectionStringBuilder {
			DataSource = path,
			Mode = mode,
			Cache = SqliteCacheMode.Default,
			Pooling = false,
			ForeignKeys = true,
			DefaultTimeout = DefaultCommandTimeoutSeconds,
		};
		return builder.ToString();
	}

	public static void ApplyConnectionPragmas(SqliteConnection connection) {
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
		command.ExecuteNonQuery();
	}

	public static SqliteConnection OpenExisting(string path) {
		var connection = new SqliteConnection(ForExistingDatabase(path));
		connection.Open();
		ApplyConnectionPragmas(connection);
		return connection;
	}

	public static SqliteConnection OpenReadOnly(string path) {
		var connection = new SqliteConnection(ForReadOnlyExisting(path));
		connection.Open();
		return connection;
	}

	public static string? ReadJournalMode(SqliteConnection connection) {
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA journal_mode";
		return command.ExecuteScalar() as string;
	}

	public static long ReadSynchronous(SqliteConnection connection) {
		return ReadLong(connection, "PRAGMA synchronous");
	}

	public static long ReadForeignKeys(SqliteConnection connection) {
		return ReadLong(connection, "PRAGMA foreign_keys");
	}

	public static long ReadIntegrityCheck(SqliteConnection connection) {
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA integrity_check";
		var result = command.ExecuteScalar();
		return result is string text && string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase) ? 1L : 0L;
	}

	public static long ReadForeignKeyCheck(SqliteConnection connection) {
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_key_check";
		using var reader = command.ExecuteReader();
		return reader.Read() ? 1L : 0L;
	}

	private static long ReadLong(SqliteConnection connection, string sql) {
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		var value = command.ExecuteScalar();
		return value switch {
			long number => number,
			int number => number,
			string text when long.TryParse(text, out var parsed) => parsed,
			_ => -1L,
		};
	}

	private sealed class ConnectionPolicyInterceptor : DbConnectionInterceptor {
		public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) {
			ApplyConnectionPragmas((SqliteConnection)connection);
		}

		public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) {
			ApplyConnectionPragmas((SqliteConnection)connection);
			return Task.CompletedTask;
		}
	}
}
