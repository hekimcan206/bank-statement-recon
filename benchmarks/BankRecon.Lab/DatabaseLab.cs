using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BankRecon.Api.Features;
using BankRecon.Api.Infrastructure;
using BankRecon.Api.Persistence;
using BankRecon.Api.Reconciliation;
using BankRecon.Api.WorkQueue;
using BankRecon.Core.Matching;
using BankRecon.Core.Statements.Mt940;
using BankRecon.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BankRecon.Lab;

/// <summary>
/// PostgreSQL-backed measurements with the production services called in-process (no HTTP): statement import
/// throughput, reconciliation run duration, and work-queue drain rate by number of workers, including a run where
/// workers abandon claims and the leases have to expire.
/// </summary>
internal static class DatabaseLab
{
    public static async Task RunAsync(string outputDirectory)
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("recon")
            .WithEnvironment("MIGRATOR_PASSWORD", "migrator-lab")
            .WithEnvironment("APP_PASSWORD", "app-lab")
            .WithCommand("-c", "max_connections=200")
            .WithResourceMapping(File.ReadAllBytes(FindInit()), "/docker-entrypoint-initdb.d/01-roles.sh")
            .Build();
        await container.StartAsync();

        string Conn(string user, string password) => new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        { Username = user, Password = password, MaxPoolSize = 60, Timeout = 60 }.ConnectionString;

        await using (var migrator = PersistenceExtensions.CreateDataSource(Conn("recon_migrator", "migrator-lab")))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        await using var dataSource = PersistenceExtensions.CreateDataSource(Conn("recon_app", "app-lab"));
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using var metrics = new ReconMetrics(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var importer = new StatementImporter(dataSource, metrics, NullLogger<StatementImporter>.Instance);
        var ledger = new LedgerItemStore(dataSource);
        var reconciliation = new ReconciliationService(dataSource, new MatchingOptions(), metrics, NullLogger<ReconciliationService>.Instance);
        var queue = new WorkQueueService(dataSource, new WorkQueueOptions(), metrics);

        var md = new StringBuilder();

        // ---- import + reconciliation run --------------------------------------------------------------------
        md.AppendLine("Import and reconciliation (one account per run, MT940 Turkish free text):");
        md.AppendLine();
        md.AppendLine("| Ledger items | Bank lines | Ledger insert (ms) | Statement import (ms) | Import lines/s | Reconciliation run (ms) | Matches | Queued |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        var importRows = new List<object>();
        var seed = 9000;
        // Sizes stop at 10,000: the run matches inside its transaction, and beyond about 30 s of matching the
        // connection's idle_in_transaction_session_timeout ends it (observed with 40,000 items; see benchmarks.md).
        foreach (var size in new[] { 2_000, 5_000, 10_000 })
        {
            var samples = new List<(double Ledger, double Import, int Lines, double Run, int Matches, int Queued)>();
            for (var r = 0; r < 3; r++)
            {
                var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = ++seed, LedgerItems = size, Days = Math.Max(20, size / 250) });
                var account = scenario.Statement.Header.Account;
                var items = scenario.Ledger.Select(l => new LedgerItemInput(l.Id, account, l.Currency, l.Amount, l.ExpectedDate, l.Reference, l.CounterpartyName)).ToList();
                var sw = Stopwatch.StartNew();
                foreach (var chunk in items.Chunk(LedgerItemStore.MaxBatch))
                {
                    await ledger.InsertAsync(chunk, CancellationToken.None);
                }

                var ledgerMs = sw.Elapsed.TotalMilliseconds;
                var bytes = Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([scenario.Statement], new Mt940WriteOptions { XCharsetOnly = false }));
                sw.Restart();
                using (var body = new MemoryStream(bytes))
                {
                    await importer.ImportAsync(body, "lab.sta", CancellationToken.None);
                }

                var importMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var run = await reconciliation.RunAsync(account, CancellationToken.None);
                samples.Add((ledgerMs, importMs, run.BankLines, sw.Elapsed.TotalMilliseconds, run.Matches, run.Queued));
            }

            var m = samples.OrderBy(s => s.Import).ElementAt(1);
            var runMedian = samples.Select(s => s.Run).Order().ElementAt(1);
            var ledgerMedian = samples.Select(s => s.Ledger).Order().ElementAt(1);
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {size:N0} | {m.Lines:N0} | {ledgerMedian:0} | {m.Import:0} | {m.Lines / (m.Import / 1000):N0} | {runMedian:0} | {m.Matches:N0} | {m.Queued:N0} |");
            importRows.Add(new { size, m.Lines, ledgerMedian, importMs = m.Import, runMedian, m.Matches, m.Queued });
        }

        // ---- queue drain ----------------------------------------------------------------------------------
        md.AppendLine();
        md.AppendLine("Work-queue drain (claim + write-off per item, in-process service calls, one account with 3,000 queued items):");
        md.AppendLine();
        md.AppendLine("| Workers | Items | Drain time (s) | Items/s | Double claims | Items resolved exactly once |");
        md.AppendLine("|---|---|---|---|---|---|");
        var queueRows = new List<object>();
        foreach (var workers in new[] { 1, 2, 4, 8, 16 })
        {
            var account = await QueueAccountAsync(importer, reconciliation, ++seed, 3_000);
            var (seconds, items, doubles, exactlyOnce) = await DrainAsync(queue, dataSource, account, workers, abandonEvery: 0);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {workers} | {items:N0} | {seconds:0.00} | {items / seconds:N0} | {doubles} | {exactlyOnce} |");
            queueRows.Add(new { workers, items, seconds, doubles, exactlyOnce });
        }

        // Every 10th claim is abandoned (the worker "crashes"): those items come back only after their 5 s lease.
        var crashAccount = await QueueAccountAsync(importer, reconciliation, ++seed, 1_000);
        var crash = await DrainAsync(queue, dataSource, crashAccount, 8, abandonEvery: 10);
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"Crash simulation: 8 workers, every 10th claim abandoned with a 5 s lease: {crash.Items:N0} items resolved in {crash.Seconds:0.0} s, " +
            $"double claims {crash.Doubles}, every item resolved exactly once: {crash.ExactlyOnce}.");

        var text = md.ToString();
        Console.WriteLine(text);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "database.md"), text);
        File.WriteAllText(Path.Combine(outputDirectory, "database.json"), JsonSerializer.Serialize(new { importRows, queueRows, crash }, LabJson.Options));
    }

    private static async Task<string> QueueAccountAsync(StatementImporter importer, ReconciliationService reconciliation, int seed, int items)
    {
        // No ledger: every bank line of the statement lands in the queue.
        var scenario = ScenarioGenerator.Generate(new ScenarioOptions { Seed = seed, LedgerItems = (int)(items / 0.85), Days = 20 });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Mt940Writer.WriteToString([scenario.Statement])));
        await importer.ImportAsync(body, "queue.sta", CancellationToken.None);
        await reconciliation.RunAsync(scenario.Statement.Header.Account, CancellationToken.None);
        return scenario.Statement.Header.Account;
    }

    private static async Task<(double Seconds, int Items, int Doubles, bool ExactlyOnce)> DrainAsync(
        WorkQueueService queue, NpgsqlDataSource dataSource, string account, int workers, int abandonEvery)
    {
        var claims = new ConcurrentDictionary<(long Id, long Token), int>();
        var resolved = 0;
        var claimCounter = 0;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            var worker = string.Create(CultureInfo.InvariantCulture, $"lab-{w}");
            var idle = 0;
            while (idle < 3)
            {
                var item = await queue.ClaimAsync(worker, abandonEvery > 0 ? 5 : 300, account, CancellationToken.None);
                if (item is null)
                {
                    // Abandoned leases may still be running; wait for them before giving up.
                    if (abandonEvery == 0)
                    {
                        break;
                    }

                    idle++;
                    await Task.Delay(TimeSpan.FromSeconds(2.5));
                    continue;
                }

                idle = 0;
                claims.AddOrUpdate((item.Id, item.LeaseToken), 1, (_, n) => n + 1);
                if (abandonEvery > 0 && Interlocked.Increment(ref claimCounter) % abandonEvery == 0)
                {
                    continue; // crash: never resolve, never release
                }

                var result = await queue.ResolveAsync(item.Id, new ResolveRequest(worker, item.LeaseToken, ResolveAction.WriteOff, null, "lab"), CancellationToken.None);
                if (result.Outcome == QueueOutcome.Ok)
                {
                    Interlocked.Increment(ref resolved);
                }
            }
        })));
        stopwatch.Stop();

        await using var cmd = dataSource.CreateCommand(
            "SELECT count(*), count(*) FILTER (WHERE status = 'resolved') FROM recon.work_item WHERE account = $1");
        cmd.Parameters.Add(new() { Value = account });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var total = (int)reader.GetInt64(0);
        var done = (int)reader.GetInt64(1);
        var doubles = claims.Values.Count(n => n > 1);
        return (stopwatch.Elapsed.TotalSeconds, total, doubles, done == total && resolved == total);
    }

    private static string FindInit()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BankStatementRecon.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "deploy", "postgres", "init", "01-roles.sh");
    }
}
