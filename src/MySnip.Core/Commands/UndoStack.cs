namespace MySnip.Core.Commands;

public interface IUndoableCommand
{
    void Execute();
    void Undo();
}

public sealed class UndoStack
{
    private readonly Stack<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var command)) return;
        command.Undo();
        _redo.Push(command);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command)) return;
        command.Execute();
        _undo.Push(command);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

public sealed class DelegateCommand(Action execute, Action undo) : IUndoableCommand
{
    public void Execute() => execute();
    public void Undo() => undo();
}
