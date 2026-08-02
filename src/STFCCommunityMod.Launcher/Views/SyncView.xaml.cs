using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Views;

public partial class SyncView : UserControl
{
    private SyncWorkspaceViewModel? subscribedViewModel;
    private IInputElement? wizardPreviousFocus;

    public SyncView()
    {
        InitializeComponent();
        DataContextChanged += SyncView_DataContextChanged;
        Unloaded += (_, _) => SubscribeToViewModel(null);
        Loaded += (_, _) =>
        {
            SubscribeToViewModel(ViewModel);
            UpdateTabOverflowControls();
        };
    }

    private SyncWorkspaceViewModel? ViewModel => DataContext as SyncWorkspaceViewModel;

    private void ReplacementToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: SyncTargetCardViewModel target } passwordBox)
        {
            target.SetReplacementToken(passwordBox.Password);
        }
    }

    private void WizardToken_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: SyncWizardFieldViewModel field } passwordBox)
        {
            field.Value = passwordBox.Password;
        }
    }

    private void WizardChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SyncAddChoiceViewModel choice }
            && ViewModel?.AddWizard is { } wizard)
        {
            wizard.SelectedChoice = choice;
        }
    }

    private void GlobalTab_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { Tabs.Count: > 0 } viewModel)
        {
            viewModel.SelectedTab = viewModel.Tabs[0];
            DestinationTabScrollViewer.ScrollToLeftEnd();
            PageScrollViewer.ScrollToTop();
        }
    }

    private void DestinationTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SyncWorkspaceTabViewModel tab } && ViewModel is { } viewModel)
        {
            viewModel.SelectedTab = tab;
            EnsureSelectedTabVisible();
            PageScrollViewer.ScrollToTop();
        }
    }

    private void ScrollTabsLeft_Click(object sender, RoutedEventArgs e) =>
        DestinationTabScrollViewer.ScrollToHorizontalOffset(
            Math.Max(0, DestinationTabScrollViewer.HorizontalOffset - 240));

    private void ScrollTabsRight_Click(object sender, RoutedEventArgs e) =>
        DestinationTabScrollViewer.ScrollToHorizontalOffset(
            Math.Min(DestinationTabScrollViewer.ScrollableWidth, DestinationTabScrollViewer.HorizontalOffset + 240));

    private void DestinationTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DestinationTabScrollViewer.ScrollableWidth <= 0.5)
        {
            return;
        }

        DestinationTabScrollViewer.ScrollToHorizontalOffset(
            Math.Clamp(
                DestinationTabScrollViewer.HorizontalOffset - e.Delta,
                0,
                DestinationTabScrollViewer.ScrollableWidth));
        e.Handled = true;
    }

    private void TabStrip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { Tabs.Count: > 0 } viewModel
            || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
        {
            return;
        }

        var current = Math.Max(0, viewModel.Tabs.IndexOf(viewModel.SelectedTab!));
        var next = e.Key switch
        {
            Key.Home => 0,
            Key.End => viewModel.Tabs.Count - 1,
            Key.Left => Math.Max(0, current - 1),
            _ => Math.Min(viewModel.Tabs.Count - 1, current + 1),
        };
        viewModel.SelectedTab = viewModel.Tabs[next];
        EnsureSelectedTabVisible();
        FocusSelectedTab();
        PageScrollViewer.ScrollToTop();
        e.Handled = true;
    }

    private void FocusSelectedTab()
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (ViewModel?.SelectedTab is not { } selected)
                {
                    return;
                }

                if (selected.IsGlobal)
                {
                    GlobalTabButton.Focus();
                    return;
                }

                var container = DestinationTabs.ItemContainerGenerator.ContainerFromItem(selected) as DependencyObject;
                FindVisualChild<ToggleButton>(container)?.Focus();
            });
    }

    private static T? FindVisualChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); ++index)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void EnsureSelectedTabVisible()
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (ViewModel?.SelectedTab is not { IsGlobal: false } selected)
                {
                    return;
                }

                var container = DestinationTabs.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement;
                container?.BringIntoView();
                UpdateTabOverflowControls();
            });
    }

    private void DestinationTabs_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateTabOverflowControls();

    private void DestinationTabs_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTabOverflowControls();

    private void UpdateTabOverflowControls()
    {
        var hasOverflow = DestinationTabScrollViewer.ScrollableWidth > 0.5;
        ScrollTabsLeftButton.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        ScrollTabsRightButton.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        ScrollTabsLeftButton.IsEnabled = hasOverflow && DestinationTabScrollViewer.HorizontalOffset > 0.5;
        ScrollTabsRightButton.IsEnabled = hasOverflow
            && DestinationTabScrollViewer.HorizontalOffset < DestinationTabScrollViewer.ScrollableWidth - 0.5;
    }

    private void SyncView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SubscribeToViewModel(e.NewValue as SyncWorkspaceViewModel);

    private void SubscribeToViewModel(SyncWorkspaceViewModel? viewModel)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        subscribedViewModel = viewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        EnsureSelectedTabVisible();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SyncWorkspaceViewModel.SelectedTab))
        {
            EnsureSelectedTabVisible();
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SyncWorkspaceViewModel.IsAddWizardOpen))
        {
            _ = Dispatcher.BeginInvoke(UpdateWizardFocus);
        }
    }

    private void UpdateWizardFocus()
    {
        if (ViewModel?.IsAddWizardOpen == true)
        {
            wizardPreviousFocus ??= Keyboard.FocusedElement;
            WizardDialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        if (wizardPreviousFocus is { } previousFocus)
        {
            Keyboard.Focus(previousFocus);
        }

        wizardPreviousFocus = null;
    }

    private void SyncRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.AddWizard is { } wizard)
        {
            wizard.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void InformationButton_StateChanged(object sender, RoutedEventArgs e)
    {
        InformationPopup.IsOpen = InformationButton.IsChecked == true
            || InformationButton.IsKeyboardFocusWithin
            || InformationButton.IsMouseOver;
    }

    private void ProxySegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value, DataContext: SyncTargetCardViewModel target }
            && Enum.TryParse<SyncProxyOverrideChoice>(value, out var choice))
        {
            target.ProxyChoice = choice;
        }
    }

    private void BooleanSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value, DataContext: SyncTargetCardViewModel target }
            && Enum.TryParse<SyncBooleanOverrideChoice>(value, out var choice))
        {
            target.VerifySslChoice = choice;
        }
    }

    private void UnsafeTlsSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value, DataContext: SyncTargetCardViewModel target }
            && Enum.TryParse<SyncBooleanOverrideChoice>(value, out var choice))
        {
            target.UnsafeTlsChoice = choice;
        }
    }

    private void FeedSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value, DataContext: SyncTargetFeedViewModel feed }
            && Enum.TryParse<SyncBooleanOverrideChoice>(value, out var choice))
        {
            feed.Choice = choice;
        }
    }
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = culture;
        if (value is null || parameter is null)
        {
            return false;
        }

        if (value.GetType().IsEnum && parameter is string text)
        {
            return Enum.TryParse(value.GetType(), text, true, out var parsed) && Equals(value, parsed);
        }

        return Equals(value, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
