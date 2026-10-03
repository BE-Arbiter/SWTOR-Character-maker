namespace Swtor.Formats;

/// <summary>Error in a game file. Carries the byte offset where the parser failed.</summary>
public sealed class GameFormatException(string message, long offset)
    : FormatException($"{message} (offset 0x{offset:X})")
{
    public long Offset { get; } = offset;
}
