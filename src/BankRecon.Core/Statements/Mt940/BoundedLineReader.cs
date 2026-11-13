namespace BankRecon.Core.Statements.Mt940;

/// <summary>
/// Line reader with a hard line-length limit. <see cref="TextReader.ReadLine"/> would happily buffer a 2 GB file
/// that contains no newline; a statement parser facing untrusted uploads must not.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxLineLength)
{
    private readonly char[] _buffer = new char[64 * 1024];
    private readonly char[] _line = new char[maxLineLength];
    private int _position;
    private int _length;

    public long LineNumber { get; private set; }

    public string? ReadLine()
    {
        var count = 0;
        var sawAny = false;
        while (true)
        {
            if (_position == _length)
            {
                _length = reader.Read(_buffer, 0, _buffer.Length);
                _position = 0;
                if (_length == 0)
                {
                    if (!sawAny)
                    {
                        return null;
                    }

                    LineNumber++;
                    return new string(_line, 0, count);
                }
            }

            sawAny = true;
            var span = _buffer.AsSpan(_position, _length - _position);
            var newline = span.IndexOf('\n');
            var take = newline < 0 ? span.Length : newline;
            if (count + take > _line.Length)
            {
                throw new StatementFormatException($"Line longer than {_line.Length} characters", LineNumber + 1);
            }

            span[..take].CopyTo(_line.AsSpan(count));
            count += take;
            if (newline >= 0)
            {
                _position += newline + 1;
                LineNumber++;
                if (count > 0 && _line[count - 1] == '\r')
                {
                    count--;
                }

                return new string(_line, 0, count);
            }

            _position = _length;
        }
    }
}
