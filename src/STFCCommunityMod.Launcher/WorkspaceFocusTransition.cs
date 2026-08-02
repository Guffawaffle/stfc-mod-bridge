namespace STFCCommunityMod.Launcher;

internal sealed class WorkspaceFocusTransition
{
    private Action? restoreFocus;

    public void Enter(Action focusEntry, Action focusReturn)
    {
        ArgumentNullException.ThrowIfNull(focusEntry);
        ArgumentNullException.ThrowIfNull(focusReturn);
        restoreFocus = focusReturn;
        focusEntry();
    }

    public void Exit()
    {
        var focus = restoreFocus;
        restoreFocus = null;
        focus?.Invoke();
    }
}
