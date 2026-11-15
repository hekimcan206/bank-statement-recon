using BankRecon.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(BankRecon.IntegrationTests.Infrastructure.PostgresFixture))]

namespace BankRecon.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 container for the assembly, initialised with the same role script as docker compose, migrated
/// as <c>recon_migrator</c>; the API under test connects as <c>recon_app</c>. Tests never reset the database: each
/// one works on its own synthetic account, and the queue is always filtered by account.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Test-only credentials for a throwaway container.
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("recon")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithCommand("-c", "max_connections=200")
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    public string SuperuserConnectionString => _container.GetConnectionString();

    public string MigratorConnectionString => WithCredentials("recon_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("recon_app", AppPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var dataSource = PersistenceExtensions.CreateDataSource(MigratorConnectionString);
        await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = username,
            Password = password,
            MaxPoolSize = 40,
            // Generous: this host runs many other containers.
            Timeout = 60,
        }.ConnectionString;
}
