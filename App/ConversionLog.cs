using System.Text;

namespace VideoConverter;

/// <summary>Retains recent GUI diagnostics without repeatedly rebuilding the entire displayed string.</summary>
internal sealed class ConversionLog
{
    private const int Limit = 40000;
    private const int Retained = 30000;
    private readonly StringBuilder text = new();
    public bool Changed
    {
        get; private set;
    }
    public void Clear()
    {
        text.Clear();
        Changed = true;
    }
    public void Append(string value)
    {
        if (value.Length > Retained)
        {
            value = value[SafeStart(value, value.Length - Retained)..];
        }

        if (text.Length + value.Length > Limit)
        {
            var remove = Math.Max(0, text.Length + value.Length - Retained);
            if (remove < text.Length && char.IsLowSurrogate(text[remove]))
            {
                remove++;
            }

            text.Remove(0, remove);
        }
        text.Append(value);
        Changed = true;
    }
    public string Snapshot()
    {
        Changed = false;
        return text.ToString();
    }
    private static int SafeStart(string value, int start) => char.IsLowSurrogate(value[start]) ? start + 1 : start;
}
