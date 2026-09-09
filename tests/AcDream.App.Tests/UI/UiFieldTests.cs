using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiFieldTests
{
    [Fact]
    public void InsertChar_AdvancesCaret()
    {
        var input = new UiField();
        input.InsertChar('h'); input.InsertChar('i');
        Assert.Equal("hi", input.Text);
        Assert.Equal(2, input.CaretPos);
    }

    [Fact]
    public void Backspace_DeletesBeforeCaret()
    {
        var input = new UiField();
        foreach (var c in "abc") input.InsertChar(c);
        input.MoveCaret(-1);
        input.Backspace();
        Assert.Equal("ac", input.Text);
        Assert.Equal(1, input.CaretPos);
    }

    [Fact]
    public void Submit_FiresCallback_ClearsText_PushesHistory()
    {
        string? sent = null;
        var input = new UiField { OnSubmit = t => sent = t };
        foreach (var c in "hello") input.InsertChar(c);
        input.Submit();
        Assert.Equal("hello", sent);
        Assert.Equal("", input.Text);
        Assert.Equal(0, input.CaretPos);
    }

    [Fact]
    public void EmptySubmit_DoesNotFire()
    {
        int n = 0;
        var input = new UiField { OnSubmit = _ => n++ };
        input.Submit();
        Assert.Equal(0, n);
    }

    [Fact]
    public void History_UpDownBrowsesPreviousSubmissions()
    {
        var input = new UiField { OnSubmit = _ => {} };
        foreach (var c in "first") input.InsertChar(c); input.Submit();
        foreach (var c in "second") input.InsertChar(c); input.Submit();
        input.HistoryPrev();
        Assert.Equal("second", input.Text);
        input.HistoryPrev();
        Assert.Equal("first", input.Text);
        input.HistoryNext();
        Assert.Equal("second", input.Text);
        input.HistoryNext();
        Assert.Equal("", input.Text);
    }

    [Fact]
    public void History_CapsAt100()
    {
        var input = new UiField { OnSubmit = _ => {} };
        for (int i = 0; i < 150; i++) { input.InsertChar('x'); input.Submit(); }
        Assert.True(input.HistoryCount <= 100);
    }

    [Fact]
    public void MultilineClick_AfterTextShrankSinceLastWrap_DoesNotThrowAndPlacesCaretInNewText()
    {
        var input = new UiField
        {
            OneLine = false,
            Selectable = true,
            Width = 120,
            Height = 80,
        };
        input.SetText(
            "a long inscription that wraps across multiple lines when it "
            + "is measured with the fallback eight pixel glyph width");
        input.EnsureWrappedLinesCurrent();

        input.SetText("hi");

        var exception = Record.Exception(() => input.OnEvent(
            new UiEvent(0u, input, UiEventType.MouseDown, Data1: 90, Data2: 60)));

        Assert.Null(exception);
        Assert.InRange(input.CaretPos, 0, input.Text.Length);
    }

    [Fact]
    public void MultilineClick_AfterBackspacesSinceLastWrap_DoesNotThrow()
    {
        var input = new UiField
        {
            OneLine = false,
            Selectable = true,
            Width = 96,
            Height = 60,
        };
        input.SetText("wrapped inscription text under edit right now");
        input.EnsureWrappedLinesCurrent();
        for (int i = 0; i < 30; i++)
            input.Backspace();

        var exception = Record.Exception(() => input.OnEvent(
            new UiEvent(0u, input, UiEventType.MouseDown, Data1: 80, Data2: 40)));

        Assert.Null(exception);
        Assert.InRange(input.CaretPos, 0, input.Text.Length);
    }

    [Fact]
    public void CharacterFilter_rejectsDisallowedInput()
    {
        var input = new UiField { CharacterFilter = static c => char.IsAsciiDigit(c) };

        input.InsertChar('4');
        input.InsertChar('x');
        input.InsertChar('2');

        Assert.Equal("42", input.Text);
    }

    [Fact]
    public void SelectAllOnFocus_survivesInitiatingMouseDown_andTypingReplacesValue()
    {
        var input = new UiField { SelectAllOnFocus = true, Selectable = true };
        input.SetText("17");

        input.OnEvent(new UiEvent(0u, input, UiEventType.FocusGained));
        input.OnEvent(new UiEvent(0u, input, UiEventType.MouseDown, Data1: 2));
        input.InsertChar('5');

        Assert.Equal("5", input.Text);
    }

    [Fact]
    public void ReadOnlyField_RejectsMutationsButReportsClick()
    {
        int clicks = 0;
        var input = new UiField
        {
            Editable = false,
            OnReadOnlyClick = () => clicks++,
        };
        input.SetText("fixed");

        input.InsertChar('!');
        input.Backspace();
        input.OnEvent(new UiEvent(0u, input, UiEventType.Click));

        Assert.Equal("fixed", input.Text);
        Assert.Equal(1, clicks);
        Assert.False(input.AcceptsFocus);
        Assert.False(input.IsEditControl);
    }

    [Fact]
    public void MultiLineField_EnterAddsNewlineInsteadOfSubmitting()
    {
        int submissions = 0;
        var input = new UiField
        {
            OneLine = false,
            OnSubmit = _ => submissions++,
        };
        input.InsertChar('a');

        input.OnEvent(new UiEvent(
            0u,
            input,
            UiEventType.KeyDown,
            Data0: (int)Silk.NET.Input.Key.Enter));
        input.OnEvent(new UiEvent(
            0u,
            input,
            UiEventType.Char,
            Data0: '\r'));
        input.InsertChar('b');

        Assert.Equal("a\nb", input.Text);
        Assert.Equal(0, submissions);
    }

    // ── CT-B2: typed-abbreviation expansion ─────────────────────────────

    [Fact]
    public void TypingASpaceOffersTheTextToTheReplacer()
    {
        var input = new UiField();
        input.TextReplacer = text => text == "/r " ? "@tell Dww, " : null;

        foreach (char c in "/r ")
            input.InsertChar(c);

        Assert.Equal("@tell Dww, ", input.Text);
        Assert.Equal("@tell Dww, ".Length, input.CaretPos);
    }

    [Fact]
    public void ANonSpaceCharacterNeverTriggersTheReplacer()
    {
        int calls = 0;
        var input = new UiField();
        input.TextReplacer = _ => { calls++; return null; };

        foreach (char c in "/reply")
            input.InsertChar(c);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void EditingInTheMiddleOfALineIsNotRewritten()
    {
        var input = new UiField();
        input.SetText("/r hello");
        input.MoveCaret(-5);
        input.TextReplacer = _ => "@tell Dww, ";

        input.InsertChar(' ');

        Assert.Equal("/r  hello", input.Text);
    }

    [Fact]
    public void AReplacerReturningNullLeavesTheTextExactlyAsTyped()
    {
        var input = new UiField();
        input.TextReplacer = _ => null;

        foreach (char c in "hi ")
            input.InsertChar(c);

        Assert.Equal("hi ", input.Text);
    }


    [Fact]
    public void EscapeIsHandledAndKeepsWhatWasTyped()
    {
        var input = new UiField();
        input.SetText("half written");

        bool handled = input.OnEvent(new UiEvent(
            0, input, UiEventType.KeyDown, Data0: (int)Silk.NET.Input.Key.Escape));

        Assert.True(handled);
        Assert.Equal("half written", input.Text);
    }

    [Fact]
    public void EscapeIsIgnoredWhenTheFieldIsNotEditable()
    {
        // A read-only field returns early before the key switch, so Escape
        // must not acquire behaviour there.
        var input = new UiField { Editable = false };

        Assert.True(input.OnEvent(new UiEvent(
            0, input, UiEventType.KeyDown, Data0: (int)Silk.NET.Input.Key.Escape)));
    }
}
