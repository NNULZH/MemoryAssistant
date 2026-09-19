using System.Text;
using System.Text.RegularExpressions;
using MemoryAssistant.Core.Agent.Skills;

namespace MemoryAssistant.Core.Agent.Conversation;

/// <summary>
/// 会话视图的一行（供"仿微信"的聊天记录界面直接绑定）。
/// 文字/表情/图片/语音各走各的展示分支，没有任何"看不懂的占位"被藏起来：
/// 图片没解出来就如实显示原因，表情包解不出图时显示表情库里的中文描述。
/// </summary>
public sealed record ConversationLine
{
    public long LocalId { get; init; }
    public string Time { get; init; } = "";
    public string Sender { get; init; } = "";
    public bool IsSelf { get; init; }
    /// <summary>text | image | voice | video | sticker | app | system | other</summary>
    public string Kind { get; init; } = "text";
    public string Text { get; init; } = "";
    public string? ImagePath { get; init; }
    public string? AudioPath { get; init; }
    /// <summary>次要说明（未解密原因 / "表情包"这类暂不能图形化的类型）。</summary>
    public string Note { get; init; } = "";
    public bool IsTarget { get; init; }
    /// <summary>这一行之前是否要插一条时间分隔（间隔超过 5 分钟）。</summary>
    public string TimeSeparator { get; init; } = "";
    /// <summary>群聊里才显示发送者名字（一对一里显示会很啰嗦）。</summary>
    public bool ShowSender { get; init; }

    public bool HasText => Text.Length > 0;
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);
    public bool HasAudio => !string.IsNullOrEmpty(AudioPath);
    public bool HasNote => Note.Length > 0;
    public bool HasSeparator => TimeSeparator.Length > 0;
}

/// <summary>
/// 把 <see cref="RenderedMessage"/> 组装成可直接渲染的会话视图（纯逻辑，便于单测）。
/// 职责：类型分派、微信表情名→Unicode、时间分隔、目标消息定位。
/// </summary>
public static class ConversationView
{
    private static readonly TimeSpan SeparatorGap = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<ConversationLine> Build(
        IReadOnlyList<RenderedMessage> messages,
        long targetTime = 0,
        string targetContent = "",
        bool forceShowSender = false)
    {
        var lines = new List<ConversationLine>();
        if (messages.Count == 0) return lines;

        var ordered = messages.OrderBy(m => m.CreateTime).ThenBy(m => m.LocalId).ToList();
        var senders = ordered.Where(m => !m.IsSelf).Select(m => m.SenderName).Distinct().Count();
        var showSender = forceShowSender || senders > 1;      // 群聊才显示发言人

        DateTimeOffset? previous = null;
        foreach (var m in ordered)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(m.CreateTime).ToLocalTime();
            var separator = "---- " + at.ToString("yyyy-MM-dd HH:mm") + " ----";
            var gap = previous is null || at - previous.Value >= SeparatorGap;

            var note = "";
            string? imagePath = null;
            string? audioPath = null;
            var text = "";

            switch (m.Kind)
            {
                case "image":
                    if (!string.IsNullOrWhiteSpace(m.MediaPath)) imagePath = m.MediaPath;
                    else note = $"[图片未解密{(string.IsNullOrWhiteSpace(m.MediaError) ? "" : "：" + m.MediaError)}]";
                    break;
                case "sticker":
                    // 表情包能解密出图就画图；否则显示表情库里的描述（如「好震惊」），再否则如实标注。
                    if (!string.IsNullOrWhiteSpace(m.MediaPath)) imagePath = m.MediaPath;
                    else note = string.IsNullOrWhiteSpace(m.Text) ? "[表情包]" : $"[表情包：{m.Text}]";
                    break;
                case "voice":
                    if (!string.IsNullOrWhiteSpace(m.MediaPath)) audioPath = m.MediaPath;
                    else note = "[语音]";
                    break;
                case "video":
                    note = "[视频]";
                    break;
                case "app":
                    text = string.IsNullOrWhiteSpace(m.Text) ? "" : DecorateEmoji(m.Text);
                    note = "［链接/文件/应用消息］";
                    break;
                case "system":
                case "other":
                    text = DecorateEmoji(m.Text);
                    break;
                default:
                    text = DecorateEmoji(m.Text);
                    break;
            }

            lines.Add(new ConversationLine
            {
                LocalId = m.LocalId,
                Time = at.ToString("HH:mm"),
                Sender = m.IsSelf ? "我" : (string.IsNullOrWhiteSpace(m.SenderName) ? "对方" : m.SenderName),
                IsSelf = m.IsSelf,
                Kind = m.Kind,
                Text = text,
                ImagePath = imagePath,
                AudioPath = audioPath,
                Note = note,
                IsTarget = IsTarget(m, targetTime, targetContent),
                TimeSeparator = gap ? separator : "",
                ShowSender = showSender && !m.IsSelf,
            });
            previous = at;
        }
        return lines;
    }

    /// <summary>定位：优先按时间（同一分钟内），再按内容前缀，保证"点哪条就落到哪条"。</summary>
    private static bool IsTarget(RenderedMessage m, long targetTime, string targetContent)
    {
        if (targetTime > 0 && Math.Abs(m.CreateTime - targetTime) <= 60) return true;
        var needle = Trim(targetContent, 12);
        return needle.Length > 3 && m.Text.Contains(needle, StringComparison.Ordinal);
    }

    private static string Trim(string s, int max)
    {
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>
    /// 微信表情名 → Unicode 表情（库里存的是 "[破涕为笑]" 这种文字标记，直接显示会很怪）。
    /// 只做常见集合，未收录的保持原样，不猜。
    /// </summary>
    public static string DecorateEmoji(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('[')) return text;
        return Regex.Replace(text, @"\[([^\[\]\s]{1,6})\]", m =>
            EmojiMap.TryGetValue(m.Groups[1].Value, out var emoji) ? emoji : m.Value);
    }

    private static readonly Dictionary<string, string> EmojiMap = new(StringComparer.Ordinal)
    {
        ["微笑"] = "🙂", ["撇嘴"] = "😒", ["色"] = "😍", ["发呆"] = "😳", ["得意"] = "😎",
        ["流泪"] = "😢", ["害羞"] = "😊", ["闭嘴"] = "🤐", ["睡"] = "😴", ["大哭"] = "😭",
        ["尴尬"] = "😅", ["发怒"] = "😡", ["调皮"] = "😜", ["呲牙"] = "😁", ["惊讶"] = "😲",
        ["难过"] = "🙁", ["酷"] = "😎", ["冷汗"] = "😰", ["抓狂"] = "😫", ["吐"] = "🤮",
        ["偷笑"] = "🤭", ["可爱"] = "🥰", ["白眼"] = "🙄", ["傲慢"] = "😤", ["饥饿"] = "😋",
        ["困"] = "😪", ["惊恐"] = "😱", ["流汗"] = "😅", ["憨笑"] = "😄", ["大兵"] = "😎",
        ["奋斗"] = "💪", ["疑问"] = "❓", ["嘘"] = "🤫", ["晕"] = "😵", ["折磨"] = "😖",
        ["衰"] = "😞", ["骷髅"] = "💀", ["敲打"] = "🔨", ["再见"] = "👋", ["擦汗"] = "😓",
        ["抠鼻"] = "🤏", ["鼓掌"] = "👏", ["糗大了"] = "😅", ["坏笑"] = "😏", ["左哼哼"] = "😤",
        ["右哼哼"] = "😤", ["哈欠"] = "🥱", ["鄙视"] = "😒", ["委屈"] = "🥺", ["快哭了"] = "😖",
        ["阴险"] = "😈", ["亲亲"] = "😘", ["吓"] = "😨", ["可怜"] = "🥺", ["菜刀"] = "🔪",
        ["西瓜"] = "🍉", ["啤酒"] = "🍺", ["篮球"] = "🏀", ["乒乓"] = "🏓", ["咖啡"] = "☕",
        ["饭"] = "🍚", ["猪头"] = "🐷", ["玫瑰"] = "🌹", ["凋谢"] = "🥀", ["示爱"] = "😘",
        ["爱心"] = "❤️", ["心碎"] = "💔", ["蛋糕"] = "🎂", ["闪电"] = "⚡", ["炸弹"] = "💣",
        ["刀"] = "🗡", ["足球"] = "⚽", ["瓢虫"] = "🐞", ["便便"] = "💩", ["月亮"] = "🌙",
        ["太阳"] = "☀️", ["礼物"] = "🎁", ["拥抱"] = "🤗", ["强"] = "👍", ["弱"] = "👎",
        ["握手"] = "🤝", ["胜利"] = "✌️", ["抱拳"] = "🙏", ["勾引"] = "🫰", ["拳头"] = "👊",
        ["差劲"] = "👎", ["爱你"] = "🤟", ["NO"] = "🙅", ["ok"] = "👌", ["OK"] = "👌",
        ["合十"] = "🙏", ["嘿哈"] = "😆", ["捂脸"] = "🤦", ["奸笑"] = "😏", ["机智"] = "🤓",
        ["皱眉"] = "😟", ["耶"] = "✌️", ["红包"] = "🧧", ["發"] = "🧧", ["福"] = "🧧",
        ["破涕为笑"] = "😂", ["恐惧"] = "😱", ["发抖"] = "😰", ["叹气"] = "😮‍💨",
        ["无语"] = "😑", ["666"] = "👍", ["让我看看"] = "👀", ["旺柴"] = "🐶",
        ["天啊"] = "😱", ["惊叹"] = "😲", ["社会社会"] = "😏", ["打脸"] = "🫲",
        ["吃瓜"] = "🍉", ["加油"] = "💪", ["翻白眼"] = "🙄", ["涩"] = "😳",
    };
}
