using System.Text.RegularExpressions;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 内容噪音过滤（V3.3）：把"加好友/验证/表情/链接/撤回"等无信息量内容挡在证据之外，
/// 避免回忆结果被"你已添加…现在可以开始聊天了"这类系统消息灌满。
/// </summary>
public static class ContentNoiseFilter
{
    private static readonly string[] Patterns =
    [
        "你已添加了", "现在可以开始聊天了", "我通过了你的朋友验证请求", "以上是打招呼的内容",
        "撤回了一条消息", "重新加你为好友", "添加你为好友", "邀请你加入了群聊", "你邀请",
        "拍了拍", "红包", "转账", "邀请你语音通话", "邀请你视频通话", "已取消的语音通话",
        "该内容不支持查看", "消息已发出，但被对方拒收",
    ];

    private static readonly Regex PureMedia = new(@"^(\[[^\]]{1,8}\])+$", RegexOptions.Compiled);
    private static readonly Regex LinkOnly = new(@"^\[链接\]$|^https?://\S+$", RegexOptions.Compiled);

    public static bool IsNoise(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        var t = content.Trim();

        if (t.Length <= 2) return true;                              // 单字/符号
        if (PureMedia.IsMatch(t)) return true;                        // 只有表情
        if (LinkOnly.IsMatch(t)) return true;                         // 只有链接
        if (Patterns.Any(p => t.Contains(p, StringComparison.Ordinal))) return true;

        // 纯时间戳/系统提示样式（如 "[19:07] " 后为空）
        if (t.Length <= 10 && t.All(c => !char.IsLetterOrDigit(c) || c == '[' || c == ']' || c == ':' || c == ' ')) return true;
        return false;
    }
}
