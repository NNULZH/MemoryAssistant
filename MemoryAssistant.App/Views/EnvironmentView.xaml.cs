using MemoryAssistant.App.ViewModels;

namespace MemoryAssistant.App.Views;

/// <summary>环境页（Agent 环境感知）：进页面刷新一次快照；自检由按钮显式触发（会真的去问各子系统）。</summary>
public partial class EnvironmentView : System.Windows.Controls.UserControl
{
    public EnvironmentView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is EnvironmentViewModel vm) vm.RefreshCommand.Execute(null);
        };
    }
}
