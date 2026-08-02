using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace STFCCommunityMod.Launcher.Controls;

public partial class LaunchTargetSplitButton : UserControl
{
    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel), typeof(string), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty PrimaryAutomationNameProperty = DependencyProperty.Register(
        nameof(PrimaryAutomationName), typeof(string), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty PrimaryCommandProperty = DependencyProperty.Register(
        nameof(PrimaryCommand), typeof(ICommand), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty SelectPrimeCommandProperty = DependencyProperty.Register(
        nameof(SelectPrimeCommand), typeof(ICommand), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty SelectScopelyCommandProperty = DependencyProperty.Register(
        nameof(SelectScopelyCommand), typeof(ICommand), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty IsPrimaryEnabledProperty = DependencyProperty.Register(
        nameof(IsPrimaryEnabled), typeof(bool), typeof(LaunchTargetSplitButton), new PropertyMetadata(true));
    public static readonly DependencyProperty IsMenuEnabledProperty = DependencyProperty.Register(
        nameof(IsMenuEnabled), typeof(bool), typeof(LaunchTargetSplitButton), new PropertyMetadata(true));
    public static readonly DependencyProperty IsPrimeSelectedProperty = DependencyProperty.Register(
        nameof(IsPrimeSelected), typeof(bool), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty IsScopelySelectedProperty = DependencyProperty.Register(
        nameof(IsScopelySelected), typeof(bool), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty PrimeChoiceAutomationNameProperty = DependencyProperty.Register(
        nameof(PrimeChoiceAutomationName), typeof(string), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty ScopelyChoiceAutomationNameProperty = DependencyProperty.Register(
        nameof(ScopelyChoiceAutomationName), typeof(string), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty PrimeChoiceStatusProperty = DependencyProperty.Register(
        nameof(PrimeChoiceStatus), typeof(string), typeof(LaunchTargetSplitButton));
    public static readonly DependencyProperty ScopelyChoiceStatusProperty = DependencyProperty.Register(
        nameof(ScopelyChoiceStatus), typeof(string), typeof(LaunchTargetSplitButton));

    public LaunchTargetSplitButton()
    {
        InitializeComponent();
    }

    public string PrimaryLabel { get => (string)GetValue(PrimaryLabelProperty); set => SetValue(PrimaryLabelProperty, value); }
    public string PrimaryAutomationName { get => (string)GetValue(PrimaryAutomationNameProperty); set => SetValue(PrimaryAutomationNameProperty, value); }
    public ICommand PrimaryCommand { get => (ICommand)GetValue(PrimaryCommandProperty); set => SetValue(PrimaryCommandProperty, value); }
    public ICommand SelectPrimeCommand { get => (ICommand)GetValue(SelectPrimeCommandProperty); set => SetValue(SelectPrimeCommandProperty, value); }
    public ICommand SelectScopelyCommand { get => (ICommand)GetValue(SelectScopelyCommandProperty); set => SetValue(SelectScopelyCommandProperty, value); }
    public bool IsPrimaryEnabled { get => (bool)GetValue(IsPrimaryEnabledProperty); set => SetValue(IsPrimaryEnabledProperty, value); }
    public bool IsMenuEnabled { get => (bool)GetValue(IsMenuEnabledProperty); set => SetValue(IsMenuEnabledProperty, value); }
    public bool IsPrimeSelected { get => (bool)GetValue(IsPrimeSelectedProperty); set => SetValue(IsPrimeSelectedProperty, value); }
    public bool IsScopelySelected { get => (bool)GetValue(IsScopelySelectedProperty); set => SetValue(IsScopelySelectedProperty, value); }
    public string PrimeChoiceAutomationName { get => (string)GetValue(PrimeChoiceAutomationNameProperty); set => SetValue(PrimeChoiceAutomationNameProperty, value); }
    public string ScopelyChoiceAutomationName { get => (string)GetValue(ScopelyChoiceAutomationNameProperty); set => SetValue(ScopelyChoiceAutomationNameProperty, value); }
    public string PrimeChoiceStatus { get => (string)GetValue(PrimeChoiceStatusProperty); set => SetValue(PrimeChoiceStatusProperty, value); }
    public string ScopelyChoiceStatus { get => (string)GetValue(ScopelyChoiceStatusProperty); set => SetValue(ScopelyChoiceStatusProperty, value); }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ChoicePopup.IsOpen = true;
        Dispatcher.BeginInvoke(
            () => (IsPrimeSelected ? PrimeChoiceButton : ScopelyChoiceButton).Focus());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WPF XAML callback binding requires an instance method.")]
    private CustomPopupPlacement[] PlaceChoicePopup(
        Size popupSize,
        Size targetSize,
        Point offset) =>
    [
        new(
            new Point(targetSize.Width - popupSize.Width + offset.X, targetSize.Height + 4 + offset.Y),
            PopupPrimaryAxis.Horizontal),
        new(
            new Point(targetSize.Width - popupSize.Width + offset.X, -popupSize.Height - 4 + offset.Y),
            PopupPrimaryAxis.Horizontal),
    ];

    private void ChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ClosePopupAndRestoreFocus();
    }

    private void ChoicePopup_Closed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
    }

    private void ChoicePopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClosePopupAndRestoreFocus();
            return;
        }

        if (e.Key is Key.Down or Key.Up or Key.Home or Key.End)
        {
            e.Handled = true;
            var focusPrime = e.Key switch
            {
                Key.Home => true,
                Key.End => false,
                _ => !PrimeChoiceButton.IsKeyboardFocusWithin,
            };
            (focusPrime ? PrimeChoiceButton : ScopelyChoiceButton).Focus();
        }
    }

    private void ClosePopupAndRestoreFocus()
    {
        ChoicePopup.IsOpen = false;
        MenuButton.Focus();
        Keyboard.Focus(MenuButton);
    }
}
