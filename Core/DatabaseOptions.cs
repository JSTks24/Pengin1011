using System.Text;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;

namespace Pengin1011.Core;

public enum DatabaseProvider {
	SQLite,
	MySQL,
	PostgreSQL,
}

public static class DatabaseOptions {
	private static readonly string[] _validationErrors = [
		"ExternalDatabaseIdentifierInvalid", "DatabaseIdentifierTooLong", "ExternalDatabaseSplitUnsupported",
		"ExternalDatabaseTptUnsupported", "ExternalDatabaseTpcUnsupported",
	];

	internal static bool TryValidate(DatabaseConfig config, out string? failure) {
		failure = null;
		if (config.MySQL != null && config.PostgreSQL != null) {
			failure = Loc.Get("DatabaseProvidersConflict");
			return false;
		}
		var external = config.MySQL ?? config.PostgreSQL;
		if (external == null) return true;
		var name = config.MySQL != null ? "MySQL" : "PostgreSQL";
		foreach (var (field, value) in new[] { ("Host", external.Host), ("Database", external.Database), ("Username", external.Username) }) {
			if (string.IsNullOrWhiteSpace(value)) {
				failure = Loc.Format("DatabaseConfigInvalid", name, field);
				return false;
			}
		}
		if (external.Port is < 1 or > 65535) {
			failure = Loc.Format("DatabaseConfigInvalid", name, "Port");
			return false;
		}
		return true;
	}

	internal static DatabaseProvider ProviderOf(DatabaseConfig config) {
		return config.MySQL != null ? DatabaseProvider.MySQL : config.PostgreSQL != null ? DatabaseProvider.PostgreSQL : DatabaseProvider.SQLite;
	}

	internal static string ConnectionString(DatabaseConfig config) {
		if (config.MySQL is { } mysql) {
			return new MySqlConnectionStringBuilder {
				Server = mysql.Host.Trim(), Port = (uint)(mysql.Port ?? 3306), Database = mysql.Database.Trim(),
				UserID = mysql.Username.Trim(), Password = mysql.Password ?? "", ConnectionTimeout = 30, DefaultCommandTimeout = 30,
			}.ConnectionString;
		}
		var postgres = config.PostgreSQL!;
		return new NpgsqlConnectionStringBuilder {
			Host = postgres.Host.Trim(), Port = postgres.Port ?? 5432, Database = postgres.Database.Trim(),
			Username = postgres.Username.Trim(), Password = postgres.Password ?? "", Timeout = 30, CommandTimeout = 30,
		}.ConnectionString;
	}

	public static DbContextOptions<TContext> Create<TContext>(DatabaseProvider provider, string connectionString, string id) where TContext : DbContext {
		var builder = new DbContextOptionsBuilder<TContext>();
		switch (provider) {
			case DatabaseProvider.SQLite:
				builder.UseSqlite(connectionString).AddInterceptors(SqliteConnectionPolicy.Interceptor);
				break;
			case DatabaseProvider.MySQL:
				builder.UseMySQL(connectionString, options => options.MigrationsHistoryTable(HistoryTable(id)));
				break;
			case DatabaseProvider.PostgreSQL:
				builder.UseNpgsql(connectionString, options => options.MigrationsHistoryTable(HistoryTable(id), "public"));
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(provider));
		}
		return builder.Options;
	}

	public static void ApplyPrefix(ModelBuilder modelBuilder, string id, DatabaseProvider provider) {
		if (provider == DatabaseProvider.SQLite) return;
		var prefix = Prefix(id);
		var entities = modelBuilder.Model.GetEntityTypes().ToArray();
		foreach (var entity in entities) {
			if (entity.GetMappingFragments().Any()) throw new InvalidOperationException(Loc.Get("ExternalDatabaseSplitUnsupported"));
			if (entity.GetMappingStrategy() == "TPC") throw new InvalidOperationException(Loc.Get("ExternalDatabaseTpcUnsupported"));
			var defaultSchema = provider == DatabaseProvider.PostgreSQL ? "public" : null;
			if (entity.GetMappingStrategy() == "TPT" || entity.BaseType is { } parent
				&& (entity.GetTableName() != parent.GetTableName() || (entity.GetSchema() ?? defaultSchema) != (parent.GetSchema() ?? defaultSchema))) {
				throw new InvalidOperationException(Loc.Get("ExternalDatabaseTptUnsupported"));
			}
		}
		if (provider == DatabaseProvider.PostgreSQL) modelBuilder.HasDefaultSchema("public");
		var tables = entities.Select(entity => (Entity: entity, Name: entity.GetTableName())).ToArray();
		var keys = entities.SelectMany(entity => entity.GetDeclaredKeys()).Select(key => (Key: key, Name: key.GetName())).ToArray();
		var indexes = entities.SelectMany(entity => entity.GetDeclaredIndexes()).Select(index => (Index: index, Name: index.GetDatabaseName())).ToArray();
		var foreignKeys = entities.SelectMany(entity => entity.GetDeclaredForeignKeys()).Select(key => (Key: key, Name: key.GetConstraintName())).ToArray();
		var checks = entities.SelectMany(entity => entity.GetDeclaredCheckConstraints()).Select(check => (Check: check, check.Name)).ToArray();
		foreach (var (entity, name) in tables) {
			if (name != null) entity.SetTableName(CheckedName(prefix + name));
		}
		foreach (var (key, name) in keys) {
			if (name != null) key.SetName(CheckedName(prefix + name));
		}
		foreach (var (index, name) in indexes) {
			if (name != null) index.SetDatabaseName(CheckedName(prefix + name));
		}
		foreach (var (key, name) in foreignKeys) {
			if (name != null) key.SetConstraintName(CheckedName(prefix + name));
		}
		foreach (var (check, name) in checks) {
			if (name != null) check.Name = CheckedName(prefix + name);
		}
	}

	internal static void ValidateExternalIdentifier(string id) {
		if (id.Contains('_')) throw new InvalidOperationException(Loc.Get("ExternalDatabaseIdentifierInvalid"));
	}

	internal static string Prefix(string id) {
		ValidateExternalIdentifier(id);
		return "PG1011_" + id + "_";
	}
	internal static string HistoryTable(string id) => CheckedName(Prefix(id) + "__EFMigrationsHistory");

	private static string CheckedName(string name) {
		if (Encoding.UTF8.GetByteCount(name) > 63) throw new InvalidOperationException(Loc.Get("DatabaseIdentifierTooLong"));
		return name;
	}

	internal static string Failure(Exception exception, bool cancelled = false, bool timedOut = false) {
		var errors = new List<Exception>();
		for (var error = exception; error != null; error = error.InnerException) errors.Add(error);
		var mysql = errors.OfType<MySqlException>().FirstOrDefault(error => error.Number != 0) ?? errors.OfType<MySqlException>().FirstOrDefault();
		var postgres = errors.OfType<PostgresException>().FirstOrDefault();
		var code = mysql != null ? "MySQL " + mysql.Number : postgres != null && postgres.SqlState.Length == 5
			&& postgres.SqlState.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z') ? "SQLSTATE " + postgres.SqlState : exception.GetType().Name;
		string reason;
		if (cancelled) reason = Loc.Get("ExternalDatabaseCancelled");
		else if (timedOut || errors.Any(error => error is TimeoutException)) reason = Loc.Get("ExternalDatabaseTimeout");
		else if (errors.Any(error => error is OperationCanceledException)) reason = Loc.Get("ExternalDatabaseCancelled");
		else {
			var local = _validationErrors.Select(key => Loc.Get(key)).FirstOrDefault(message => errors.Any(error => error.Message == message));
			var key = mysql?.Number switch {
				1049 => "ExternalDatabaseMissing",
				1045 => "ExternalDatabaseAuthentication",
				1044 or 1142 or 1143 or 1227 or 1370 => "ExternalDatabasePermission",
				1040 or 1042 or 1152 or 1184 or 1203 or 2002 or 2003 or 2006 or 2013 => "ExternalDatabaseConnection",
				1205 or 3024 => "ExternalDatabaseTimeout",
				1317 => "ExternalDatabaseCancelled",
				_ => postgres?.SqlState switch {
					"3D000" => "ExternalDatabaseMissing",
					"28000" or "28P01" => "ExternalDatabaseAuthentication",
					"42501" => "ExternalDatabasePermission",
					"57014" => "ExternalDatabaseCancelled",
					{ } state when state.StartsWith("08", StringComparison.Ordinal) => "ExternalDatabaseConnection",
					_ => errors.Any(error => error is SocketException) ? "ExternalDatabaseConnection" : "ExternalDatabaseOperation",
				},
			};
			reason = local ?? Loc.Get(key);
		}
		return Loc.Format("ExternalDatabaseFailed", reason, code);
	}
}
