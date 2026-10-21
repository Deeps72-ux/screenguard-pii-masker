namespace ScreenGuard.App.Services;

/// <summary>
/// Buffers one capture's console output so concurrent processing workers print
/// whole blocks instead of interleaved lines.
/// </summary>
public sealed class CaptureLog
{
    private static readonly object ConsoleLock = new();
    private readonly List<(ConsoleColor? Color, string Text)> _lines = new();
    private ConsoleColor? _color;

    public void Color(ConsoleColor color) => _color = color;

    public void Reset() => _color = null;

    public void Line(string text = "") => _lines.Add((_color, text));

    public void Flush()
    {
        lock (ConsoleLock)
        {
            foreach (var (color, text) in _lines)
            {
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.WriteLine(text);
                if (color.HasValue) Console.ResetColor();
            }
        }
        _lines.Clear();
    }
}
