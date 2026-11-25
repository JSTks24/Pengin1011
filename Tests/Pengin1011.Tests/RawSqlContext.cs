using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

public sealed class RawSqlContext : DbContext {
	private readonly string _path;

	public RawSqlContext(string path) {
		_path = path;
	}

	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		options.UseSqlite($"Data Source={_path}");
	}
}
