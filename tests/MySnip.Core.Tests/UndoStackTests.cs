using MySnip.Core.Commands;

namespace MySnip.Core.Tests;

public sealed class UndoStackTests
{
    [Fact]
    public void ExecuteUndoRedo_TracksState()
    {
        var value = 0;
        var stack = new UndoStack();
        stack.Execute(new DelegateCommand(() => value++, () => value--));
        Assert.Equal(1, value);
        Assert.True(stack.CanUndo);
        stack.Undo();
        Assert.Equal(0, value);
        Assert.True(stack.CanRedo);
        stack.Redo();
        Assert.Equal(1, value);
    }

    [Fact]
    public void NewCommand_ClearsRedoHistory()
    {
        var value = 0;
        var stack = new UndoStack();
        stack.Execute(new DelegateCommand(() => value++, () => value--));
        stack.Undo();
        stack.Execute(new DelegateCommand(() => value += 2, () => value -= 2));
        Assert.False(stack.CanRedo);
        Assert.Equal(2, value);
    }
}
