using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace STFCCommunityMod.Launcher.Behaviors;

public static class TextInputCommitBehavior
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter",
        typeof(bool),
        typeof(TextInputCommitBehavior),
        new PropertyMetadata(false, CommitOnEnterChanged));

    public static bool GetCommitOnEnter(DependencyObject element) =>
        (bool)element.GetValue(CommitOnEnterProperty);

    public static void SetCommitOnEnter(DependencyObject element, bool value) =>
        element.SetValue(CommitOnEnterProperty, value);

    private static void CommitOnEnterChanged(
        DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBox textBox)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            textBox.PreviewKeyDown += OnPreviewKeyDown;
        }
        else
        {
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || sender is not TextBox textBox)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        FindAncestor<ListBox>(textBox)?.Focus();
        args.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
