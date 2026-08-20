using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace STFCCommunityMod.Launcher.Controls;

public sealed class InAppDialog : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(InAppDialog),
        new FrameworkPropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty DialogTitleProperty = DependencyProperty.Register(
        nameof(DialogTitle),
        typeof(string),
        typeof(InAppDialog),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty DialogWidthProperty = DependencyProperty.Register(
        nameof(DialogWidth),
        typeof(double),
        typeof(InAppDialog),
        new FrameworkPropertyMetadata(440d));

    public static readonly DependencyProperty DialogMaxHeightProperty = DependencyProperty.Register(
        nameof(DialogMaxHeight),
        typeof(double),
        typeof(InAppDialog),
        new FrameworkPropertyMetadata(720d));

    private ButtonBase? closeButton;
    private IInputElement? previousFocus;

    public event EventHandler? Closed;

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string DialogTitle
    {
        get => (string)GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public double DialogWidth
    {
        get => (double)GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public double DialogMaxHeight
    {
        get => (double)GetValue(DialogMaxHeightProperty);
        set => SetValue(DialogMaxHeightProperty, value);
    }

    public override void OnApplyTemplate()
    {
        if (closeButton is not null)
        {
            closeButton.Click -= CloseButton_Click;
        }

        base.OnApplyTemplate();
        closeButton = GetTemplateChild("PART_CloseButton") as ButtonBase;
        if (closeButton is not null)
        {
            closeButton.Click += CloseButton_Click;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsOpen && e.Key == Key.Escape)
        {
            IsOpen = false;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private static void OnIsOpenChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var dialog = (InAppDialog)dependencyObject;
        if ((bool)eventArgs.NewValue)
        {
            dialog.previousFocus = Keyboard.FocusedElement;
            _ = dialog.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => dialog.closeButton?.Focus());
            return;
        }

        if (dialog.previousFocus is { } previousFocus)
        {
            _ = dialog.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => Keyboard.Focus(previousFocus));
        }

        dialog.previousFocus = null;
        dialog.Closed?.Invoke(dialog, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        IsOpen = false;
    }
}
