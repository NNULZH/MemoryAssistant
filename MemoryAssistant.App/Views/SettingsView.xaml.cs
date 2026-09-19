using System.Windows;
using System.Windows.Controls;
using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            ApiKeyBox.Password = vm.ApiKey;
    }

    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // PasswordBox.Password 非依赖属性，需 code-behind 同步回 VM。
        if (DataContext is SettingsViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }
}
