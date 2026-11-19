using System;
using System.Globalization;

namespace FmPlayerTalk;

internal sealed class DraftBuffer
{
    private const int Limit = 1000;
    public string Text { get; private set; } = "";
    private int cursor;
    private bool selected;

    public string Display(string composition) =>
        Text.Insert(cursor, composition + "│");

    public int DisplayCaretIndex(string composition) => cursor + composition.Length;

    public void End()
    {
        cursor = Text.Length;
        selected = false;
    }

    public void Home()
    {
        cursor = 0;
        selected = false;
    }

    public void SelectAll()
    {
        End();
        selected = true;
    }

    public void Clear()
    {
        Text = "";
        Home();
    }

    public bool Insert(string input)
    {
        var available = Limit - (selected ? 0 : Text.Length);
        if (available == 0 || input.Length == 0)
            return false;

        input = input.ReplaceLineEndings("\n");
        if (input.Length > available)
        {
            var length = available;
            if (char.IsHighSurrogate(input[length - 1]))
                length--;
            input = input[..length];
        }
        if (input.Length == 0)
            return false;

        if (selected)
            Clear();
        Text = Text.Insert(cursor, input);
        cursor += input.Length;
        return true;
    }

    public void MoveLeft()
    {
        cursor = selected ? 0 : Previous();
        selected = false;
    }

    public void MoveRight()
    {
        cursor = selected ? Text.Length : Next();
        selected = false;
    }

    public void Backspace()
    {
        if (selected)
        {
            Clear();
            return;
        }
        var start = Previous();
        Text = Text.Remove(start, cursor - start);
        cursor = start;
    }

    public void Delete()
    {
        if (selected)
        {
            Clear();
            return;
        }
        Text = Text.Remove(cursor, Next() - cursor);
    }

    private int Previous()
    {
        var previous = 0;
        foreach (var index in StringInfo.ParseCombiningCharacters(Text))
        {
            if (index >= cursor)
                break;
            previous = index;
        }
        return previous;
    }

    private int Next()
    {
        foreach (var index in StringInfo.ParseCombiningCharacters(Text))
            if (index > cursor)
                return index;
        return Text.Length;
    }
}
