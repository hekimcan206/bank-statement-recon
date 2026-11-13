using System.Text;
using BankRecon.Core.Statements.Camt;
using BankRecon.Core.Statements.Mt940;

namespace BankRecon.Core.Statements;

public enum StatementFormat
{
    Mt940,
    Camt053,
}

/// <summary>Opens an uploaded statement file whatever its format and encoding.</summary>
public static class StatementFile
{
    private const int SniffLength = 64 * 1024;

    static StatementFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Detects the format from the first non-blank character: '&lt;' is XML (camt.053), ':' or '{' is MT940.
    /// </summary>
    public static StatementFormat DetectFormat(ReadOnlySpan<byte> head)
    {
        var start = head.StartsWith("﻿"u8) ? 3 : 0;
        for (var i = start; i < head.Length; i++)
        {
            switch (head[i])
            {
                case (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n':
                    continue;
                case (byte)'<':
                    return StatementFormat.Camt053;
                case (byte)':' or (byte)'{':
                    return StatementFormat.Mt940;
                default:
                    throw new StatementFormatException("Unrecognised statement format (expected MT940 or camt.053 XML)");
            }
        }

        throw new StatementFormatException("Empty statement file");
    }

    /// <summary>
    /// MT940 has no encoding declaration. SWIFT itself only allows the X character set, but banks that export files
    /// for corporate customers often send Turkish letters, either as UTF-8 or as Windows-1254. Valid UTF-8 in the
    /// first 64 KiB is taken as UTF-8; anything else is decoded as Windows-1254.
    /// </summary>
    public static Encoding DetectMt940Encoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("﻿"u8))
        {
            return new UTF8Encoding(false);
        }

        // The sniffed window may cut a multi-byte character in half; ignore an incomplete trailing sequence.
        var end = head.Length;
        for (var back = 1; back <= 3 && back <= head.Length; back++)
        {
            var b = head[head.Length - back];
            if ((b & 0xC0) == 0xC0)
            {
                end = head.Length - back;
                break;
            }

            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return System.Text.Unicode.Utf8.IsValid(head[..end]) ? new UTF8Encoding(false) : Encoding.GetEncoding(1254);
    }

    /// <summary>Streams the statements of a seekable file. The stream is left open.</summary>
    public static IEnumerable<StatementPart> Read(Stream stream, out StatementFormat format, Mt940ReadOptions? mt940 = null, Camt053ReadOptions? camt = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable so its format can be detected.", nameof(stream));
        }

        var start = stream.Position;
        var buffer = new byte[SniffLength];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += n;
        }

        stream.Position = start;
        var head = buffer.AsSpan(0, read);
        format = DetectFormat(head);
        if (format == StatementFormat.Camt053)
        {
            return Camt053Reader.Read(stream, camt);
        }

        var encoding = DetectMt940Encoding(head);
        return Mt940Reader.Read(new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true), mt940);
    }
}
