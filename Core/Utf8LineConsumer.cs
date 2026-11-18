using System.Text;

namespace VideoConverter.Core;

/// <summary>Decodes streamed process bytes with a bounded partial line and no total-output limit.</summary>
internal sealed class Utf8LineConsumer(Action<string> receive)
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new();
    private bool skipLineFeed;

    internal void Feed(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        Span<char> characters = stackalloc char[2048];
        bool completed;
        do
        {
            decoder.Convert(bytes, characters, flush, out int used, out int written, out completed);
            bytes = bytes[used..];
            foreach (char character in characters[..written])
            {
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }
                if (character is '\r' or '\n')
                {
                    skipLineFeed = character == '\r';
                    receive(line.ToString());
                    line.Clear();
                }
                else
                {
                    if (line.Length == MediaResourceLimits.LineCharacters)
                    {
                        throw new InvalidDataException("外部工具的单行输出超过 65536 个字符，已停止读取。");
                    }

                    line.Append(character);
                }
            }
        } while (!completed);
        if (flush && line.Length > 0)
        {
            receive(line.ToString());
            line.Clear();
        }
    }
}
