using System.Text;

namespace VideoConverter.Core;

/// <summary>Reads streaming metadata without allocating an unbounded line before checking its length.</summary>
/// <remarks>The caller owns the underlying reader. Total output is unlimited so long movies can stream.</remarks>
internal sealed class BoundedLineReader(TextReader reader, int maximumCharacters = MediaResourceLimits.LineCharacters) : TextReader
{
    private readonly char[] buffer = new char[4096];
    private int position, length;
    private bool skipLineFeed;

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        StringBuilder? line = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position == length)
            {
                length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                position = 0;
                if (length == 0)
                {
                    return line?.ToString();
                }
            }
            if (skipLineFeed)
            {
                skipLineFeed = false;
                if (buffer[position] == '\n')
                {
                    position++;
                    continue;
                }
            }
            int start = position;
            while (position < length && buffer[position] is not ('\r' or '\n'))
            {
                position++;
            }
            int count = position - start;
            if (count > maximumCharacters - (line?.Length ?? 0))
            {
                throw new InvalidDataException($"外部工具的单行输出超过 {maximumCharacters} 个字符，已停止读取。");
            }
            if (position < length)
            {
                skipLineFeed = buffer[position++] == '\r';
                // Most frame records fit in the current block. Allocate only
                // the returned string rather than a builder for every frame.
                return line is null ? new string(buffer, start, count) : line.Append(buffer, start, count).ToString();
            }
            (line ??= new StringBuilder()).Append(buffer, start, count);
        }
    }
}
