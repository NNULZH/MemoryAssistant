using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MemoryAssistant.App.Views;

/// <summary>
/// 统一的空状态 / 加载中占位（图标 + 主提示 + 一句"怎么继续"）。
/// 用法：<c>&lt;views:EmptyStateView Icon="Search24" Title="还没有结果" Hint="换个关键词试试" /&gt;</c>
/// 可见性由各页自己绑（列表为空 且 不在加载中）。
/// </summary>
public partial class EmptyStateView : UserControl
{
    public EmptyStateView() => InitializeComponent();

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(EmptyStateView),
        new PropertyMetadata(SymbolRegular.SearchInfo24, (d, e) =>
            ((EmptyStateView)d).IconGlyph.Symbol = (SymbolRegular)e.NewValue));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata("", (d, e) => ((EmptyStateView)d).TitleText.Text = (string)e.NewValue));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata("", (d, e) => ((EmptyStateView)d).HintText.Text = (string)e.NewValue));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
