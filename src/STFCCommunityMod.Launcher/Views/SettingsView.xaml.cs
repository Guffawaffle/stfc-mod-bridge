using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? subscribedViewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += SettingsView_DataContextChanged;
        Loaded += (_, _) => SubscribeToViewModel(DataContext as SettingsViewModel);
        Unloaded += (_, _) => SubscribeToViewModel(null);
    }

    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SubscribeToViewModel(e.NewValue as SettingsViewModel);

    private void SubscribeToViewModel(SettingsViewModel? viewModel)
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
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.RecoveryFocusRevision))
        {
            _ = Dispatcher.BeginInvoke(FocusRecoverySetting);
        }
    }

    private void FocusRecoverySetting()
    {
        if (subscribedViewModel?.RecoveryFocusTargetId is not { } targetId)
        {
            return;
        }

        var row = subscribedViewModel.FilteredSettings
            .OfType<SettingsRowViewModel>()
            .FirstOrDefault(item => string.Equals(item.Path, targetId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        SettingsList.ScrollIntoView(row);
        SettingsList.UpdateLayout();
        if (SettingsList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
        {
            container.BringIntoView();
            container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }
}
