namespace SeatReservation.IntegrationTests;

/// <summary>
/// WebApplicationFactory&lt;Program&gt; intercepts the top-level-statement entry point via shared
/// static state (HostFactoryResolver). Two factories booting at the same moment race each other,
/// so every test class that boots the real host (one per IClassFixture&lt;...ApiFactory&gt;) shares
/// this collection to force sequential execution between classes. Tests that only talk to MySQL
/// directly (SchemaConstraintTests, MigrationRunnerTests) are unaffected and keep running in parallel.
/// </summary>
[CollectionDefinition("ApiHost")]
public sealed class ApiHostCollection;
