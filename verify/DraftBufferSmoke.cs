using System;
using FmPlayerTalk;

public static class DraftBufferSmoke
{
    public static string Run()
    {
        CheckEmptyAndComposition();
        CheckTextElementEditing();
        CheckSelection();
        CheckMultiline();
        CheckLengthLimit();
        return "Draft buffer checks passed (Chinese IME, text elements, selection, multiline, and UTF-16 length limit).";
    }

    private static void CheckEmptyAndComposition()
    {
        var draft = new DraftBuffer();
        draft.Backspace();
        draft.Delete();
        draft.MoveLeft();
        draft.MoveRight();
        Equal("", draft.Text, "editing an empty draft");
        Equal("│", draft.Display(""), "empty caret");

        Equal("ni'hao│", draft.Display("ni'hao"), "pending Chinese IME text");
        Equal("", draft.Text, "pending composition has not been committed");
        Require(draft.Insert("你好"), "commit Chinese composition");
        Equal("你好│", draft.Display(""), "committed text and caret");
        draft.MoveLeft();
        Equal("你shi│好", draft.Display("shi"), "composition inserts at caret");
        Equal("你好", draft.Text, "mid-draft composition is only a preview");
        draft.Delete();
        Equal("你│", draft.Display(""), "delete one committed Chinese character");
        draft.Backspace();
        Equal("", draft.Text, "backspace the remaining Chinese character");
    }

    private static void CheckTextElementEditing()
    {
        var draft = new DraftBuffer();
        const string stadium = "🏟️";
        Require(draft.Insert("球" + stadium + "员"), "insert Chinese with emoji");
        draft.MoveLeft();
        Equal("球" + stadium + "│员", draft.Display(""), "left from the end");
        draft.MoveLeft();
        Equal("球│" + stadium + "员", draft.Display(""), "left across a surrogate pair and variation selector");
        draft.MoveRight();
        draft.Backspace();
        Equal("球│员", draft.Display(""), "backspace the complete emoji text element");
        Require(draft.Insert("🙂"), "insert a surrogate pair at the caret");
        draft.MoveLeft();
        draft.Delete();
        Equal("球│员", draft.Display(""), "delete the complete surrogate pair");

        draft.Home();
        Require(draft.Insert("e\u0301"), "insert a combining mark with a base character");
        draft.Backspace();
        Equal("│球员", draft.Display(""), "backspace a base and its combining mark");
    }

    private static void CheckSelection()
    {
        var draft = new DraftBuffer();
        Require(draft.Insert("原草稿"), "set selection test text");
        draft.SelectAll();
        Require(!draft.Insert(""), "empty insertion does not replace the selection");
        Require(draft.Insert("新消息\r\n第二句"), "replace the selected Chinese text");
        Equal("新消息\n第二句│", draft.Display(""), "replacement with normalized line break");
        draft.SelectAll();
        draft.MoveLeft();
        Equal("│新消息\n第二句", draft.Display(""), "left collapses selection to the start");
        draft.SelectAll();
        draft.MoveRight();
        Equal("新消息\n第二句│", draft.Display(""), "right collapses selection to the end");
        draft.SelectAll();
        draft.Delete();
        Equal("", draft.Text, "delete clears the selection");
        Require(draft.Insert("再见"), "set a new draft after deleting");
        draft.SelectAll();
        draft.Backspace();
        Equal("│", draft.Display(""), "backspace clears the selection and resets the caret");
    }

    private static void CheckMultiline()
    {
        var draft = new DraftBuffer();
        Require(draft.Insert("第一行\r\n第二行\r第三行\n"), "insert pasted line breaks");
        Equal("第一行\n第二行\n第三行\n", draft.Text, "normalize line endings without flattening");
        draft.Backspace();
        draft.MoveLeft();
        Require(draft.Insert("\n"), "insert a line break at the caret");
        Equal("第一行\n第二行\n第三\n行", draft.Text, "preserve inserted line break and suffix");
        const string composition = "hang";
        var display = draft.Display(composition);
        var caret = draft.DisplayCaretIndex(composition);
        Require(display[caret] == '│' && display[..caret].EndsWith(composition, StringComparison.Ordinal),
            "caret is after the composition on the current line");
        draft.Backspace();
        Equal("第一行\n第二行\n第三行", draft.Text, "backspace joins two lines again");
    }

    private static void CheckLengthLimit()
    {
        var draft = new DraftBuffer();
        var almostFull = new string('中', 999);
        Require(draft.Insert(almostFull), "insert 999 UTF-16 units");
        Require(!draft.Insert("🙂"), "reject a surrogate pair when only one unit remains");
        Equal(almostFull, draft.Text, "no lone surrogate was retained");
        draft.Backspace();
        Require(draft.Insert("文🙂"), "keep a complete prefix when the input exceeds the limit");
        Equal(almostFull[..998] + "文", draft.Text, "trim without splitting the trailing surrogate pair");
        Require(draft.Insert("足"), "the incomplete emoji left one free unit");
        Require(!draft.Insert("满"), "full draft rejects insertions");
        draft.SelectAll();
        var atLimit = new string('球', 998) + "🙂";
        Require(draft.Insert(atLimit), "replacement can fill the limit with a complete surrogate pair");
        Equal(atLimit, draft.Text, "replacement uses the capacity of the cleared selection");
        draft.Backspace();
        Equal(new string('球', 998), draft.Text, "deleting a pair frees two UTF-16 units");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Draft buffer check failed: " + scenario);
    }

    private static void Equal(string expected, string actual, string scenario) =>
        Require(string.Equals(expected, actual, StringComparison.Ordinal), scenario);
}
