using System.Diagnostics;
using System.Security.Cryptography;
using BankRecon.Api.Infrastructure;
using BankRecon.Core.Matching;
using BankRecon.Core.Statements;
using Npgsql;
using NpgsqlTypes;

namespace BankRecon.Api.Features;

public sealed record ImportedStatement(long Id, string StatementRef, string Account, string Currency, int Entries, int BankLines, decimal Opening, decimal Closing);

public sealed record ImportResult(long FileId, string Format, bool Duplicate, IReadOnlyList<ImportedStatement> Statements);

/// <summary>The file was understood but must not be booked (it does not add up, or it was imported before in another file).</summary>
public sealed class ImportRejectedException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Imports a statement file in one transaction. The file is buffered to a temporary file (bounded by the request
/// size limit) so its SHA-256 is known before anything is written: the same bytes twice are a duplicate, not a
/// second booking. Entries stream from the parser straight into a binary COPY, so memory does not grow with the
/// number of entries. A statement whose balances do not add up rolls the whole file back.
/// </summary>
internal sealed partial class StatementImporter(NpgsqlDataSource dataSource, ReconMetrics metrics, ILogger<StatementImporter> logger)
{
    public async Task<ImportResult> ImportAsync(Stream body, string? fileName, CancellationToken cancellationToken)
    {
        await using var buffer = new FileStream(
            Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            hash.AppendData(chunk, 0, read);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (buffer.Length == 0)
        {
            throw new StatementFormatException("Empty statement file");
        }

        var sha256 = hash.GetHashAndReset();
        buffer.Position = 0;
        var head = new byte[Math.Min(64 * 1024, (int)Math.Min(int.MaxValue, buffer.Length))];
        await buffer.ReadExactlyAsync(head, cancellationToken);
        buffer.Position = 0;
        var format = StatementFile.DetectFormat(head) == StatementFormat.Mt940 ? "MT940" : "camt.053";

        var existing = await FindByHashAsync(sha256, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        long fileId;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO recon.statement_file (sha256, format, file_name, size_bytes) VALUES ($1, $4, $2, $3) " +
            "ON CONFLICT (sha256) DO NOTHING RETURNING id", connection, tx))
        {
            insert.Parameters.Add(new() { Value = sha256 });
            insert.Parameters.Add(new() { Value = (object?)Truncate(fileName, 200) ?? DBNull.Value });
            insert.Parameters.Add(new() { Value = buffer.Length });
            insert.Parameters.Add(new() { Value = format });
            var id = await insert.ExecuteScalarAsync(cancellationToken);
            if (id is null)
            {
                // A concurrent upload of the same bytes won the race.
                await tx.RollbackAsync(cancellationToken);
                return await FindByHashAsync(sha256, cancellationToken) ?? throw new InvalidOperationException("Duplicate file vanished.");
            }

            fileId = (long)id;
        }

        var statements = new List<ImportedStatement>();
        long statementId = 0;
        var bankLines = 0;
        NpgsqlBinaryImporter? copy = null;
        try
        {
            foreach (var part in StatementFile.Read(buffer, out _))
            {
                switch (part)
                {
                    case StatementStarted started:
                        statementId = await InsertStatementAsync(connection, tx, fileId, started.Header, cancellationToken);
                        bankLines = 0;
                        copy = await connection.BeginBinaryImportAsync(
                            "COPY recon.bank_line (statement_id, entry_index, detail_index, account, currency, amount, value_date, booking_date, " +
                            "is_reversal, transaction_type, customer_reference, bank_reference, end_to_end_id, remittance, narrative, " +
                            "counterparty_name, counterparty_iban) FROM STDIN (FORMAT BINARY)", cancellationToken);
                        break;
                    case EntryRead entry:
                        bankLines += await WriteEntryAsync(copy!, statementId, entry, cancellationToken);
                        break;
                    case StatementEnded ended:
                        await copy!.CompleteAsync(cancellationToken);
                        await copy.DisposeAsync();
                        copy = null;
                        if (!ended.IsBalanced)
                        {
                            throw new ImportRejectedException(
                                $"Statement '{ended.Header.StatementId}' does not add up: opening {ended.Header.Opening.SignedAmount} plus movements " +
                                $"is {ended.ComputedClosing}, but the closing balance is {ended.Closing.SignedAmount}.",
                                StatusCodes.Status422UnprocessableEntity);
                        }

                        await CloseStatementAsync(connection, tx, statementId, ended, cancellationToken);
                        statements.Add(new ImportedStatement(
                            statementId, ended.Header.StatementId, ended.Header.Account, ended.Header.Currency, ended.EntryCount, bankLines,
                            ended.Header.Opening.SignedAmount, ended.Closing.SignedAmount));
                        break;
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "statement_identity")
        {
            throw new ImportRejectedException(
                "A statement in this file (same account, statement reference and sequence number) was already imported from another file.",
                StatusCodes.Status409Conflict);
        }
        finally
        {
            if (copy is not null)
            {
                // Cancels the COPY; the transaction is rolled back when it is disposed without commit.
                await copy.DisposeAsync();
            }
        }

        await tx.CommitAsync(cancellationToken);
        var total = statements.Sum(s => s.BankLines);
        metrics.EntriesImported(total);
        LogImported(fileId, format, statements.Count, total, stopwatch.ElapsedMilliseconds);
        return new ImportResult(fileId, format, false, statements);
    }

    private static async Task<long> InsertStatementAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long fileId, StatementHeader header, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO recon.statement (file_id, statement_ref, sequence_number, account, currency, opening_balance, opening_date) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id", connection, tx);
        cmd.Parameters.Add(new() { Value = fileId });
        cmd.Parameters.Add(new() { Value = header.StatementId });
        cmd.Parameters.Add(new() { Value = (object?)header.SequenceNumber ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = AccountKey(header) });
        cmd.Parameters.Add(new() { Value = header.Currency });
        cmd.Parameters.Add(new() { Value = header.Opening.SignedAmount });
        cmd.Parameters.Add(new() { Value = header.Opening.Date });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task CloseStatementAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long statementId, StatementEnded ended, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE recon.statement SET closing_balance = $1, closing_date = $2, entry_count = $3 WHERE id = $4", connection, tx);
        cmd.Parameters.Add(new() { Value = ended.Closing.SignedAmount });
        cmd.Parameters.Add(new() { Value = ended.Closing.Date });
        cmd.Parameters.Add(new() { Value = ended.EntryCount });
        cmd.Parameters.Add(new() { Value = statementId });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> WriteEntryAsync(NpgsqlBinaryImporter copy, long statementId, EntryRead read, CancellationToken cancellationToken)
    {
        var header = read.Header;
        var lines = BankLineFactory.From("x", read.Entry, header.Currency).ToList();
        for (var k = 0; k < lines.Count; k++)
        {
            var line = lines[k];
            await copy.StartRowAsync(cancellationToken);
            await copy.WriteAsync(statementId, NpgsqlDbType.Bigint, cancellationToken);
            await copy.WriteAsync(read.Index, NpgsqlDbType.Integer, cancellationToken);
            await copy.WriteAsync(lines.Count == 1 ? -1 : k, NpgsqlDbType.Integer, cancellationToken);
            await copy.WriteAsync(AccountKey(header), NpgsqlDbType.Text, cancellationToken);
            await copy.WriteAsync(header.Currency, NpgsqlDbType.Char, cancellationToken);
            await copy.WriteAsync(line.Amount, NpgsqlDbType.Numeric, cancellationToken);
            await copy.WriteAsync(read.Entry.ValueDate, NpgsqlDbType.Date, cancellationToken);
            await WriteNullableAsync(copy, read.Entry.BookingDate, NpgsqlDbType.Date, cancellationToken);
            await copy.WriteAsync(read.Entry.IsReversal, NpgsqlDbType.Boolean, cancellationToken);
            await copy.WriteAsync(read.Entry.TransactionType, NpgsqlDbType.Text, cancellationToken);
            await WriteTextAsync(copy, line.CustomerReference, cancellationToken);
            await WriteTextAsync(copy, read.Entry.BankReference, cancellationToken);
            await WriteTextAsync(copy, line.EndToEndId, cancellationToken);
            await WriteTextAsync(copy, line.Remittance, cancellationToken);
            await WriteTextAsync(copy, Combine(line.Narrative, read.Entry.SupplementaryDetails), cancellationToken);
            await WriteTextAsync(copy, line.CounterpartyName, cancellationToken);
            await WriteTextAsync(copy, lines.Count == 1 ? read.Entry.CounterpartyIban : read.Entry.Details[k].CounterpartyIban, cancellationToken);
        }

        return lines.Count;
    }

    /// <summary>Statements are filed under the IBAN when there is one, so MT940 and camt.053 of one account meet.</summary>
    internal static string AccountKey(StatementHeader header) => header.Iban ?? header.Account;

    private static string? Combine(string? narrative, string? supplementary) =>
        supplementary is null ? narrative : narrative is null ? supplementary : narrative + " " + supplementary;

    private static async Task WriteTextAsync(NpgsqlBinaryImporter copy, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await copy.WriteNullAsync(cancellationToken);
        }
        else
        {
            await copy.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
        }
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter copy, DateOnly? value, NpgsqlDbType type, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await copy.WriteNullAsync(cancellationToken);
        }
        else
        {
            await copy.WriteAsync(value.Value, type, cancellationToken);
        }
    }

    private async Task<ImportResult?> FindByHashAsync(byte[] sha256, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT f.id, f.format, s.id, s.statement_ref, s.account, s.currency, s.entry_count, s.opening_balance, s.closing_balance, " +
            "(SELECT count(*) FROM recon.bank_line b WHERE b.statement_id = s.id) " +
            "FROM recon.statement_file f LEFT JOIN recon.statement s ON s.file_id = f.id WHERE f.sha256 = $1 ORDER BY s.id");
        cmd.Parameters.Add(new() { Value = sha256 });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        long? fileId = null;
        var format = string.Empty;
        var statements = new List<ImportedStatement>();
        while (await reader.ReadAsync(cancellationToken))
        {
            fileId = reader.GetInt64(0);
            format = reader.GetString(1);
            if (!reader.IsDBNull(2))
            {
                statements.Add(new ImportedStatement(
                    reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                    (int)reader.GetInt64(9), reader.GetDecimal(7), reader.IsDBNull(8) ? 0 : reader.GetDecimal(8)));
            }
        }

        return fileId is null ? null : new ImportResult(fileId.Value, format, true, statements);
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported file {FileId} ({Format}): {Statements} statement(s), {BankLines} bank lines in {ElapsedMs} ms")]
    private partial void LogImported(long fileId, string format, int statements, int bankLines, long elapsedMs);
}
