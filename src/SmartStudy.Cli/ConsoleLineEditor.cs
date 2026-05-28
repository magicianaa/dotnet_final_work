using System.Globalization;
using System.Text;

namespace SmartStudy.Cli;

public enum LineEditKey { Character, Left, Right, Home, End, Backspace, Delete }

public sealed class EditableLineBuffer
{
    private readonly List<char> _buffer = new();
    public int Cursor { get; private set; }
    public string Text => new(_buffer.ToArray());

    public void Apply(LineEditKey key, char ch = '\0')
    {
        switch (key)
        {
            case LineEditKey.Character:
                _buffer.Insert(Cursor, ch);
                Cursor++;
                break;
            case LineEditKey.Left:
                if (Cursor > 0) Cursor--;
                break;
            case LineEditKey.Right:
                if (Cursor < _buffer.Count) Cursor++;
                break;
            case LineEditKey.Home:
                Cursor = 0;
                break;
            case LineEditKey.End:
                Cursor = _buffer.Count;
                break;
            case LineEditKey.Backspace:
                if (Cursor > 0)
                {
                    _buffer.RemoveAt(Cursor - 1);
                    Cursor--;
                }
                break;
            case LineEditKey.Delete:
                if (Cursor < _buffer.Count) _buffer.RemoveAt(Cursor);
                break;
        }
    }
}

public static class ConsoleLineEditor
{
    public static string ReadLine(string prompt)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(prompt);
        Console.ResetColor();

        var line = new EditableLineBuffer();
        var inputStartLeft = Console.CursorLeft;
        var inputStartTop = Console.CursorTop;
        var lastRenderCells = 0;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return line.Text;
            }

            ApplyKey(line, key);
            Redraw(line, inputStartLeft, inputStartTop, ref lastRenderCells);
        }
    }

    public static int MeasureCellWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += GetRuneCellWidth(rune);
        return width;
    }

    private static void ApplyKey(EditableLineBuffer line, ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                line.Apply(LineEditKey.Left);
                break;
            case ConsoleKey.RightArrow:
                line.Apply(LineEditKey.Right);
                break;
            case ConsoleKey.Home:
                line.Apply(LineEditKey.Home);
                break;
            case ConsoleKey.End:
                line.Apply(LineEditKey.End);
                break;
            case ConsoleKey.Backspace:
                line.Apply(LineEditKey.Backspace);
                break;
            case ConsoleKey.Delete:
                line.Apply(LineEditKey.Delete);
                break;
            default:
                if (!char.IsControl(key.KeyChar)) line.Apply(LineEditKey.Character, key.KeyChar);
                break;
        }
    }

    private static void Redraw(EditableLineBuffer line, int inputStartLeft, int inputStartTop, ref int lastRenderCells)
    {
        SetCursorByCellOffset(inputStartLeft, inputStartTop, 0);
        Console.Write(line.Text);
        var currentCells = MeasureCellWidth(line.Text);
        var extra = Math.Max(0, lastRenderCells - currentCells);
        if (extra > 0) Console.Write(new string(' ', extra));
        lastRenderCells = currentCells;
        SetCursorByCellOffset(inputStartLeft, inputStartTop, MeasureCellWidth(line.Text[..line.Cursor]));
    }

    private static void SetCursorByCellOffset(int inputStartLeft, int inputStartTop, int cellOffset)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var absolute = inputStartTop * width + inputStartLeft + cellOffset;
        var top = absolute / width;
        var left = absolute % width;

        if (top >= Console.BufferHeight)
            top = Console.BufferHeight - 1;

        Console.SetCursorPosition(left, top);
    }

    private static int GetRuneCellWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Control or UnicodeCategory.Format)
            return 0;

        var value = rune.Value;
        return IsWide(value) ? 2 : 1;
    }

    private static bool IsWide(int value)
    {
        return value is >= 0x1100 and <= 0x115F
            or >= 0x2329 and <= 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;
    }
}
