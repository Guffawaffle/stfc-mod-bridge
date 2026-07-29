using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;

namespace STFCCommunityMod.Launcher.Controls;

public sealed class KeybindingCaptureButton : Button
{
    public event EventHandler? CaptureFinished;

    public static readonly DependencyProperty CapturedCommandProperty =
        DependencyProperty.Register(
            nameof(CapturedCommand),
            typeof(ICommand),
            typeof(KeybindingCaptureButton));

    public static readonly DependencyProperty PromptProperty =
        DependencyProperty.Register(
            nameof(Prompt),
            typeof(string),
            typeof(KeybindingCaptureButton),
            new PropertyMetadata("Set", OnPromptChanged));

    private bool isCapturing;
    private object? idleToolTip;

    public KeybindingCaptureButton()
    {
        Unloaded += (_, _) => CancelCapture();
        LostKeyboardFocus += (_, _) =>
        {
            if (isCapturing && !IsKeyboardFocusWithin)
            {
                CancelCapture();
            }
        };
    }

    public ICommand? CapturedCommand
    {
        get => (ICommand?)GetValue(CapturedCommandProperty);
        set => SetValue(CapturedCommandProperty, value);
    }

    public string Prompt
    {
        get => (string)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    protected override void OnClick()
    {
        if (isCapturing)
        {
            CancelCapture();
            return;
        }

        BeginCapture();
    }

    public void StartCapture()
    {
        if (IsEnabled && !isCapturing)
        {
            BeginCapture();
        }
    }

    public void StopCapture() => CancelCapture();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!isCapturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelCapture();
            CaptureFinished?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsModifier(key) || !TryMapKey(key, out var keyName))
        {
            return;
        }

        CompleteCapture(BuildChord(keyName));
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!isCapturing)
        {
            base.OnPreviewMouseDown(e);
            return;
        }

        e.Handled = true;
        var keyName = e.ChangedButton switch
        {
            MouseButton.Left => "MOUSE0",
            MouseButton.Right => "MOUSE1",
            MouseButton.Middle => "MOUSE2",
            MouseButton.XButton1 => "MOUSE3",
            MouseButton.XButton2 => "MOUSE4",
            _ => null,
        };
        if (keyName is not null)
        {
            CompleteCapture(BuildChord(keyName));
        }
    }

    private static void OnPromptChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var button = (KeybindingCaptureButton)dependencyObject;
        if (!button.isCapturing)
        {
            button.Content = eventArgs.NewValue;
        }
    }

    private void BeginCapture()
    {
        isCapturing = true;
        idleToolTip = ToolTip;
        Content = "Press a key…";
        ToolTip = "Press a key or mouse button. Escape cancels.";
        Focus();
        Keyboard.Focus(this);
        Mouse.Capture(this, CaptureMode.Element);
    }

    private void CompleteCapture(string chord)
    {
        var command = CapturedCommand;
        CancelCapture();
        if (command?.CanExecute(chord) == true)
        {
            command.Execute(chord);
        }
        CaptureFinished?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCapture()
    {
        if (!isCapturing)
        {
            Content ??= Prompt;
            return;
        }

        isCapturing = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Content = Prompt;
        ToolTip = idleToolTip;
        idleToolTip = null;
    }

    private static string BuildChord(string keyName)
    {
        var tokens = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            tokens.Add("CTRL");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            tokens.Add("SHIFT");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            tokens.Add("ALT");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            tokens.Add("WIN");
        }

        tokens.Add(keyName);
        return string.Join('-', tokens);
    }

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

    private static bool TryMapKey(Key key, out string name)
    {
        name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString().ToUpperInvariant(),
            >= Key.D0 and <= Key.D9 =>
                ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F12 => key.ToString().ToUpperInvariant(),
            Key.Space => "SPACE",
            Key.Tab => "TAB",
            Key.Enter => "RETURN",
            Key.Back => "BACKSPACE",
            Key.Delete => "DELETE",
            Key.Insert => "INSERT",
            Key.Home => "HOME",
            Key.End => "END",
            Key.PageUp => "PGUP",
            Key.PageDown => "PGDOWN",
            Key.Up => "UP",
            Key.Down => "DOWN",
            Key.Left => "LEFT",
            Key.Right => "RIGHT",
            Key.Pause => "PAUSE",
            Key.PrintScreen => "PRINT",
            Key.CapsLock => "CAPS",
            Key.Scroll => "SCROLL",
            Key.OemMinus => "MINUS",
            Key.OemPlus => "+",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemQuestion => "/",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => string.Empty,
        };
        return name.Length > 0;
    }
}
