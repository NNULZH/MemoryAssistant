using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemoryAssistant.App.Converters;

/// <summary>
/// 消息角色 → 正文文字颜色（user=主色底上的白字，assistant=主文本色，meta=次级色）。
/// 颜色**从应用资源里取**（DesignTokens.xaml），不在这里写死——否则改了色板，
/// 气泡文字还停在旧配色上（改色板时踩过）。
/// </summary>
public class BubbleTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var role = value as string ?? "";
        return role switch
        {
            "user" => Brush("OnAccentBrush", Colors.White),
            "assistant" => Brush("TextPrimaryBrush", Color.FromRgb(0xE9, 0xF1, 0xEC)),
            _ => Brush("TextSecondaryBrush", Color.FromRgb(0xA6, 0xB8, 0xAE)),
        };
    }

    internal static Brush Brush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 反转。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;
}

/// <summary>bool 反转（False → Visible）：用于"还没有内容"这类反向显隐。</summary>
public class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null/空字符串/空集合 → Collapsed，否则 Visible。</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            null => Visibility.Collapsed,
            string s when string.IsNullOrEmpty(s) => Visibility.Collapsed,
            System.Collections.ICollection c when c.Count == 0 => Visibility.Collapsed,
            _ => Visibility.Visible,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 本地图片路径 → BitmapImage（文件不存在时返回 null，避免一堆绑定报错）。
/// 用 OnLoad 一次性读入，立刻释放文件句柄：媒体目录里的图片会被重新导出覆盖。
/// </summary>
public class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// （集合, 是否加载中）→ 空状态可见性：**集合为空且不在加载中**才显示。
/// 只看"集合为空"会在加载期间闪一下"还没有内容"，那是在骗用户。
/// </summary>
public class EmptyStateVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var count = values.Length > 0
            ? values[0] switch
            {
                null => 0,
                System.Collections.ICollection c => c.Count,
                _ => -1,          // 不是集合（绑定还没起来）→ 不显示空状态
            }
            : -1;
        var busy = values.Length > 1 && values[1] is bool b && b;
        return count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>条数 → 条形宽度（话题每日分布用，上限 240px）。</summary>
public class CountToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n ? Math.Min(240, 4 + n * 6) : (object)0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Trace 级别 → 前景色（Error=红 / Warning=橙 / Info=三级灰）。颜色取自令牌，改色板自动跟随。</summary>
public class TraceLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Core.Workflow.TraceLevel.Error => BubbleTextBrushConverter.Brush("ErrorBrush", Color.FromRgb(0xFF, 0x7B, 0x72)),
            Core.Workflow.TraceLevel.Warning => BubbleTextBrushConverter.Brush("WarningBrush", Color.FromRgb(0xFF, 0xC6, 0x6D)),
            _ => BubbleTextBrushConverter.Brush("TextTertiaryBrush", Color.FromRgb(0x84, 0x96, 0x8B)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>执行步骤阶段 → 主色（理解/规划/执行/工具/检索/作答/完成）。取自令牌，改色板自动跟随。</summary>
public class StepKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Display.ExecutionStepKind.Understanding => BubbleTextBrushConverter.Brush("StepUnderstandingBrush", Color.FromRgb(0x25, 0x63, 0xEB)),
            Display.ExecutionStepKind.Planning => BubbleTextBrushConverter.Brush("StepPlanningBrush", Color.FromRgb(0x0E, 0x74, 0x90)),
            Display.ExecutionStepKind.Activity => BubbleTextBrushConverter.Brush("StepActivityBrush", Color.FromRgb(0xA9, 0x65, 0x00)),
            Display.ExecutionStepKind.Tool => BubbleTextBrushConverter.Brush("StepToolBrush", Color.FromRgb(0x7C, 0x3A, 0xED)),
            Display.ExecutionStepKind.Retrieval => BubbleTextBrushConverter.Brush("StepRetrievalBrush", Color.FromRgb(0x15, 0x78, 0x47)),
            Display.ExecutionStepKind.Answer => BubbleTextBrushConverter.Brush("StepAnswerBrush", Color.FromRgb(0x14, 0x18, 0x1C)),
            _ => BubbleTextBrushConverter.Brush("StepCompleteBrush", Color.FromRgb(0x15, 0x78, 0x47)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>执行步骤状态 → 颜色（pending=灰 / running=蓝 / ok=品牌绿 / failed=红）。</summary>
public class StepStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "ok" => BubbleTextBrushConverter.Brush("SuccessBrush", Color.FromRgb(0x15, 0x78, 0x47)),
            "failed" => BubbleTextBrushConverter.Brush("DangerBrush", Color.FromRgb(0xC0, 0x39, 0x2B)),
            "running" => BubbleTextBrushConverter.Brush("InfoBrush", Color.FromRgb(0x25, 0x63, 0xEB)),
            "warning" => BubbleTextBrushConverter.Brush("WarningBrush", Color.FromRgb(0xA9, 0x65, 0x00)),
            _ => BubbleTextBrushConverter.Brush("TextMutedBrush", Color.FromRgb(0x76, 0x7F, 0x8C)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>工具调用状态 → 颜色（running=蓝 / ok=品牌绿 / failed=红）。取自令牌，改色板自动跟随。</summary>
public class ToolStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "ok" => BubbleTextBrushConverter.Brush("SuccessBrush", Color.FromRgb(0x15, 0x78, 0x47)),
            "failed" => BubbleTextBrushConverter.Brush("DangerBrush", Color.FromRgb(0xC0, 0x39, 0x2B)),
            _ => BubbleTextBrushConverter.Brush("InfoBrush", Color.FromRgb(0x25, 0x63, 0xEB)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
