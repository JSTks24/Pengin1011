using Pengin1011.Core;

namespace Pengin1011.Tests;

internal static class DatabaseTestSetup {
	internal static void SetDirectory(string? directory) {
		Databases.SetDataDirectoryForTest(directory);
		if (directory == null) return;
		var registrar = new DatabaseRegistrar();
		registrar.Add<FakeDbContext>("Fake", (provider, connection) => new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, connection, "Fake")));
		registrar.Add<OtherDbContext>("Other", (provider, connection) => new OtherDbContext(DatabaseOptions.Create<OtherDbContext>(provider, connection, "Other")));
		registrar.Add<BareDbContext>("Bare", (provider, connection) => new BareDbContext(DatabaseOptions.Create<BareDbContext>(provider, connection, "Bare")));
		registrar.Add<ClashDbContext>("Clash", (provider, connection) => new ClashDbContext(DatabaseOptions.Create<ClashDbContext>(provider, connection, "Clash")));
		registrar.Add<ShutdownDbContext>("Shutdown", (provider, connection) => new ShutdownDbContext(DatabaseOptions.Create<ShutdownDbContext>(provider, connection, "Shutdown")));
		registrar.Add<DriftedDbContext>("Drifted", (provider, connection) => new DriftedDbContext(DatabaseOptions.Create<DriftedDbContext>(provider, connection, "Drifted")));
		Databases.RegisterForTest(registrar);
	}
}
