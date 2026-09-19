using System.Text;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent.Answer;

/// <summary>
/// 用 LLM 把材料组织成人话（"问答体验"的关键一步）：
/// 输入是已经查证过的证据 + 分析结论，模型只负责**组织语言与取舍**，不负责找数据。
///
/// 为什么需要它：Agent 2.0 的 Plan→Execute→Evaluate 已经能找对东西，但过去直接
/// 把证据原样拼出来当答案（"[1] xxx [2] yyy"），用户看到的就是一堆摘录而不是回答。
/// 这里把"找证据"（确定性）与"组织语言"（模型）分开，两边各自可控：
/// 模型失败/未装配时返回 null，由 Orchestrator 的确定性文案兜底，功能不倒退。
/// </summary>
public sealed class LlmAnswerComposer : IAnswerComposer
{
    private const int MaxEvidence = 20;      // 喂给模型的证据条数上限（控 token）
    private const int MaxNoteChars = 2000;   // 单条分析结论的字符上限
    private const int MaxContentChars = 300; // 单条证据的字符上限

    private readonly IChatClient _chat;
    private readonly IAppLogger? _logger;

    public LlmAnswerComposer(IChatClient chat, IAppLogger? logger = null)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<string?> ComposeAsync(AnswerRequest request, CancellationToken ct, Action<string>? onDelta = null)
    {
        // 既没有材料、又不是"直接回答"场景（打招呼/闲聊）→ 交给上层兜底，不浪费一次调用
        if (request.Evidence.Count == 0 && request.Notes.Count == 0 && !request.DirectReply) return null;

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = SystemPrompt + "\n\n" + Agent.PromptTime.Line() },
            new() { Role = "user", Content = BuildUserPrompt(request) },
        };

        try
        {
            // 支持流式就边生成边推给 UI；否则退回一次性调用（逻辑完全一致，只是没有增量预览）
            var result = onDelta is not null && _chat is IStreamingChatClient streaming
                ? await streaming.ChatStreamAsync(messages, null, onDelta, null, ct)
                : await _chat.ChatAsync(messages, null, ct);

            var text = result.Content?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (result.CompletionTokens > 0)
                _logger?.Debug($"[Answer] 模型作答完成（{result.CompletionTokens} tokens）");
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 作答环节失败不该让整轮任务失败：退回确定性文案
            _logger?.Warn($"[Answer] 模型作答失败，改用原始记录作答：{ex.Message}");
            return null;
        }
    }

    private const string SystemPrompt = """
你是「回忆助手」：住在用户电脑里的私人记忆伙伴，能翻他自己的微信聊天记录。
你不是客服，也不是问答机——你更像一个记性很好的朋友，帮他把聊过的事翻出来、讲明白。

说话方式：
1. 像平时说话那样：短句、口语，可以用"嗯""其实""对了"这类词，但别油腔滑调，别堆表情。
2. 长度跟着内容走：一句话能说清就一句话；事情多再分组（用短横线），不要为了整齐硬凑三条。
3. 直接给结论和关键点；不要复述材料的长段落，也不要出现"我检索到""根据材料""以下是我找到的内容"这类过程话术。
4. 别每轮都加"要不要我再帮你查"——只有确实值得追问时才问，而且要换着说法，别用同一句模板。
5. 跟用户说"你"，说自己"我"；不要出现"用户""该用户""本助手"。

记住上文（很重要）：
- 你会看到【对话上文】。用户说"他/她/那个/那条/刚才说的"时指的就是上文里的人或事：直接接着讲，
  不要重新问一遍，也不要重新自我介绍，更不要把已经说过的话再说一遍。
- 用户已经告诉你的信息（人名、时间范围、偏好）不要让他再说一次。

关于材料：
- 只依据【聊天原文】【检索片段】【分析结论】作答，绝不编造聊天内容、人名、日期。材料里没有就直说没找到，
  并给一个真正有用的下一步（换个时间段/换个说法/指名某个人），别硬凑结论。
- 引用具体聊天内容时在句末标 [N]，编号只能用材料里出现过的编号。
- 标"检索片段"的是语义检索命中、可能由多条消息拼起来的片段：可以用来说明话题，引用原话时以"聊天原文"为准。
- 标"分析结论"的是统计或推断，不能当成用户说过的话，需要说明这是统计结果。
  分析结论里若有"分析草稿"，那是你**已经做过的分析**，请保留它的判断与推理层次，只做语言上的收拾，
  **不要压缩成一句话**；用户问"分析过程/推理过程"时更要照着讲清楚。
- **不要否认自己查过记录**：材料就是你查到的。禁止说"我没有分析工具""我编不出来过程"这类话；
  也别用"我检索到""根据材料"这种过程话术，直接讲结论与依据即可。
- **不要否认自己没有的能力**：本助手**能**执行微信写操作（打开某个会话、给人发消息，发送前会弹确认），
  也能查聊天记录。工具报错或没听懂指令时，如实说清"卡在哪一步、可以换个什么说法"，
  **绝不要说自己做不到/没法发消息**——那是假的，用户会以为功能根本不存在。
- **但没做成就必须是没做成**：材料里出现"未找到微信窗口""写操作未接入""没有发送""失败"这类**实际执行结果**时，
  就是真的没执行——必须直接说清卡在哪一步（例如"微信窗口没找到，消息没有发出去"）并给下一步，
  **绝对不要**说"我这就发""已经发了""发送前会弹个确认你点一下就行"这类话：那是把没发生的事说成会发生。
  只有材料里明确出现"已发送/已打开"才算真的做了。
- 日期只以材料为准：每条材料前面的日期就是它真实的日期。用户问的日期和材料对不上时，直接说清
  「我查到的记录是 X 月 X 日，不是 X 月 X 日」，**绝对不要把用户说的日期安到材料上**。
- 用户限定了时间范围（"今天/昨天/上周/9月11号"）时，只讲**范围内**的材料：范围里确实没有，
  就直说"这个范围里我没查到"，不要拿范围外的内容顶上——那等于偷偷改了用户的条件。
- 材料是抽样的：只有在结论真的会因此失真时提醒一次，不要每轮都念同样的免责声明。

特殊情况：
- 用户只是打招呼、闲聊、说情绪（比如"今天好累""在干嘛"）：像朋友一样正常回应，不要硬去翻记录；
  话题自然的话，可以顺一句你能帮他整理或查什么。
- 用户是在派长期任务（"帮我追踪某人""每天提醒我"）：说明你会把它整理成任务草案、等他确认，不要假装已经创建。
""";

    private static string BuildUserPrompt(AnswerRequest request)
    {
        var sb = new StringBuilder();

        // 对话上文：让回答接得上"他/那条/刚才说的"（压缩到最近几轮，避免上下文膨胀）
        if (request.History.Count > 0)
        {
            sb.Append("【对话上文（越靠后越近）】\n");
            foreach (var t in request.History)
                sb.Append("用户：").Append(Clamp(Normalize(t.UserQuery), 120))
                  .Append("\n你：").Append(Clamp(Normalize(t.Answer), 200)).Append('\n');
            if (!string.IsNullOrWhiteSpace(request.LastPerson))
                sb.Append("（会话记忆：最近谈到的人「").Append(request.LastPerson).Append("」")
                  .Append(string.IsNullOrWhiteSpace(request.Focus) ? "" : $"，焦点「{request.Focus}」")
                  .Append("）\n");
            sb.Append('\n');
        }

        sb.Append("【本轮用户说】").Append(request.UserQuery).Append('\n');
        if (!string.IsNullOrWhiteSpace(request.Goal) &&
            !string.Equals(request.Goal, request.UserQuery, StringComparison.Ordinal))
            sb.Append("【本轮实际要完成的（可能是对上文的追问）】").Append(request.Goal).Append('\n');

        var originals = request.Evidence.Where(e => e.Source != "rag").Take(MaxEvidence).ToList();
        var chunks = request.Evidence.Where(e => e.Source == "rag").Take(MaxEvidence).ToList();

        if (originals.Count > 0)
        {
            sb.Append("\n【聊天原文（可直接引用）】\n");
            foreach (var e in originals) sb.Append(Line(e)).Append('\n');
        }

        if (chunks.Count > 0)
        {
            sb.Append("\n【检索片段（仅供参考，可能拼接了多条消息）】\n");
            foreach (var e in chunks) sb.Append(Line(e)).Append('\n');
        }

        if (request.Notes.Count > 0)
        {
            sb.Append("\n【分析结论（统计/推断，非原话）】\n");
            foreach (var n in request.Notes) sb.Append("- ").Append(Clamp(n, MaxNoteChars)).Append('\n');
        }

        if (request.DirectReply && request.Evidence.Count == 0 && request.Notes.Count == 0)
        {
            sb.Append("\n【本轮不需要查聊天记录（打招呼/闲聊/问你能做什么）】\n")
              .Append("请直接友好回答；如果用户在问你能帮什么忙，就用一两句说清：可以回忆聊天记录、找没做完的承诺、")
              .Append("看最近都在聊什么，也可以把长期需求交给你变成定时/追踪任务。不要声称已经查阅过记录。\n");
        }

        if (!request.Completed)
        {
            sb.Append("\n【注意】本轮检索未完全达标");
            if (!string.IsNullOrWhiteSpace(request.StopReason)) sb.Append("（原因：").Append(request.StopReason).Append('）');
            sb.Append("：请如实说明「目前查到什么、还差什么」，不要硬凑结论。\n");
        }

        sb.Append("\n请按系统要求回答用户问题。");
        return sb.ToString();
    }

    private static string Line(Evidence e)
    {
        // 必须 ToLocalTime：材料里的时间会直接变成回答里的时间，按 UTC 显示会整体差 8 小时
        // （实测：本地 16:37 的消息被写成 08:37，用户一眼就觉得"没抓到最新的"）。
        var when = !string.IsNullOrWhiteSpace(e.Date)
            ? e.Date
            : e.CreateTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(e.CreateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "";
        var who = string.IsNullOrWhiteSpace(e.SenderName) ? "" : e.SenderName + "：";
        var session = string.IsNullOrWhiteSpace(e.SessionDisplayName) ? e.SessionId : e.SessionDisplayName;
        return $"[{e.Index}] {when} · {session} · {who}{Clamp(Normalize(e.Content), MaxContentChars)}";
    }

    private static string Normalize(string text)
        => text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Clamp(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
