using System.Net;
using System.Net.Http.Json;
using System.Text;
using BankRecon.IntegrationTests.Infrastructure;
using BankRecon.Synthetic;
using Npgsql;

namespace BankRecon.IntegrationTests;

public sealed class StatementImportTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(db.AppConnectionString);
        _client = _factory.CreateApiClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Same_file_twice_is_one_import_and_the_second_is_reported_as_duplicate()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "mt940-gvc-subfields.sta"));

        var first = await _client.UploadAsync(file);
        var second = await _client.UploadAsync(file);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var a = (await first.Content.ReadFromJsonAsync<TestApi.ImportDto>())!;
        var b = (await second.Content.ReadFromJsonAsync<TestApi.ImportDto>())!;
        b.Duplicate.Should().BeTrue();
        b.FileId.Should().Be(a.FileId);
        b.Statements.Single().BankLines.Should().Be(2);
    }

    [Fact]
    public async Task The_same_statement_in_different_bytes_is_refused_as_already_imported()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mt940-turkish-free-text.sta"));
        (await _client.UploadAsync(Encoding.UTF8.GetBytes(text))).StatusCode.Should().Be(HttpStatusCode.Created);

        var crlf = await _client.UploadAsync(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));

        crlf.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await crlf.Content.ReadFromJsonAsync<TestApi.ProblemDto>())!.Code.Should().Be("statement.duplicate");
    }

    [Fact]
    public async Task Camt_batch_details_become_separate_bank_lines()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", "camt053-v08-batch.xml"));
        var import = (await (await _client.UploadAsync(file)).Content.ReadFromJsonAsync<TestApi.ImportDto>())!;

        import.Format.Should().Be("camt.053");
        import.Statements.Single().Entries.Should().Be(3);
        import.Statements.Single().BankLines.Should().Be(4, "the itemised batch booking contributes one line per detail");
    }

    [Fact]
    public async Task A_statement_that_does_not_add_up_is_rejected_and_nothing_is_stored()
    {
        var scenario = TestApi.NewScenario(30);
        var broken = scenario.Statement with { Closing = scenario.Statement.Closing with { Amount = scenario.Statement.Closing.Amount + 0.01m } };

        var response = await _client.UploadAsync(TestApi.File(scenario, broken));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<TestApi.ProblemDto>())!.Code.Should().Be("statement.unbalanced");
        await using var connection = new NpgsqlConnection(db.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM recon.bank_line WHERE account = $1", connection);
        cmd.Parameters.Add(new() { Value = scenario.Statement.Header.Account });
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_file_is_rejected_with_the_offending_line()
    {
        var response = await _client.UploadAsync(Encoding.UTF8.GetBytes(":20:X\r\n:25:ACC\r\n:60F:C261103TRY1,00\r\n:61:BROKEN\r\n:62F:C261103TRY1,00\r\n"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("statement.malformed").And.Contain("\"line\":4");
    }

    [Fact]
    public async Task Large_camt_statement_streams_into_the_database()
    {
        var scenario = TestApi.NewScenario(3000, NarrativeStyle.Camt);
        var response = await _client.UploadAsync(TestApi.File(scenario));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var import = (await response.Content.ReadFromJsonAsync<TestApi.ImportDto>())!;
        import.Statements.Single().Entries.Should().Be(scenario.Statement.Entries.Count);
        import.Statements.Single().BankLines.Should().Be(scenario.BankLines().Count);
    }

    [Fact]
    public async Task Requests_without_the_api_key_are_refused()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.PostAsync("/api/v1/statements", new ByteArrayContent([1]))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK, "probes stay open");
    }

    [Fact]
    public async Task The_application_role_cannot_delete_or_rewrite_imported_facts()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "DELETE FROM recon.bank_line",
                     "UPDATE recon.statement_file SET format = 'x'",
                     "UPDATE recon.statement SET opening_balance = 0",
                     "DELETE FROM recon.match",
                     "SELECT * FROM recon_admin.schema_migrations",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            var act = () => cmd.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }
}
