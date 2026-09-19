using MemoryAssistant.Core.Configuration;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 通用工具 Skill（V3.5b）：把"当前注册表里的全部工具"交给 LLM 自主选用（复用 AgentLoop 的 ReAct 循环）。
/// 关键在于**每次执行都现读 ToolRegistry.BuildSchemas()**——于是清单热注册进来的新工具下一轮就能被用上，
/// 不需要改代码、不需要重启。
/// 没有 LLM、或处于验收节流模式（EcoMode）时诚实降级，不猜也不烧 token。
/// Action 类工具仍由工具自身的确认闸门把关（本 Skill 不绕过）。
/// </summary>
public sealed class ToolCallingSkill(
    IChatClient? chat,
    ToolRegistry tools,
    AgentOptions options,
    IAppLogger? logger = null) : IAgentSkill
{
    private const string SystemPrompt = """
你是"记忆助手"里的工具执行器。请使用可用工具完成用户的任务。
（内部思考/reasoning 请用中文，用户会直接看到你的判断依据。）

怎么"找人"（最容易搞错的一步）：
- 任务是"我和某人的聊天 / 给某人回消息"时：第一步用 find_sessions(keyword=人名, private_only=true) 拿到**会话 id**，
  再 read_messages(session_id) 读内容。找不到就换名字写法（备注名/昵称）再试一次。
- 不要用 list_sessions 的关键词当"找人"手段：它只匹配 wxid / 会话摘要 / 最后发言人，
  会把"张晓明"匹配到"她恰好发言过的那个群"，于是你翻到的全是无关的人和群聊。
- search_messages 搜的是**消息内容**而不是人名，拿它找人同样会跑偏（它只在已经知道说什么的时候用）。
- **"查无此人"只能由会话目录说了算**（实测踩过）：微信窗口里"名字没打进搜索框／搜索没出结果"**不等于**这个人不存在，
  那是我这侧输入通道的问题（窗口没在前台、按键被吞）。**没查过 find_sessions 之前，禁止下"找不到这个人／没有这个会话"的结论**；
  查过之后也只在目录确实为空时才说"本地聊天记录里没有这个人"。
- 写操作（wechat_send_message / wechat_open_chat）**工具内部会先查会话目录再动手**，你不需要为此额外跑一轮：
  但工具返回里带着核对结论（"会话目录里有/没有「X」"），你必须照着它说，不许改口。

会话是"至少两个人"的（别只盯着一个人）：
- read_messages 每行都有 `is_self`：true = **你自己**发的，false = 对方/群里别人发的。
- 每行还有 `session_name`：私聊里它就是**对方的名字**，群聊里是群名。别拿"发送者名"当会话名——
  私聊里你自己发的那条 display_name 恰好是**你自己的名字**，照它理解就会以为"这会话只有我一个人"。
- 判断"谁在等我回/对方最新说了什么"：看该会话**最后一条的 is_self**——false 才是对方在等你。
- 列记录时，`is_self=true` 的发言一律写「**你**」，不要写你自己的昵称——否则读起来像会话里还有第三个人。

要发消息时（写操作，用户会看到确认弹窗才发出去）：
- 一律用 `wechat_send_message(person=人名, text=要发的话, kind=...)` 一次给全：它会先打开那个会话再发送，
  **打开失败就绝不发送**。不要分两步（先 open_chat，再空着 person 发）——中途会话被切走就会发错人。
- **kind 一定要给对**：微信搜索下拉按「最常使用 / 联系人 / 群聊」分区排，同一个名字会横跨多个分区。
  搜一个人名时「群聊」区里全是"消息里提到过她"的群——这就是"给某人发消息却发进了群"的原因。
  先 `find_sessions(keyword=名字)` 看返回的 `is_chatroom`：false → `kind=contact`，true → `kind=group`；
  确实是"我和他自己的私聊"也一样，先确认会话对象再填 person，并带上 kind。
- 不确定就 `kind=auto`（先找「联系人」分区，没有再退「群聊」），但**别拿 auto 当默认偷懒**。
- text 要写成用户能直接发出去的口吻，不要引号、不要换行（换行会被微信当成发送键，把消息截断）。

长期任务（"盯着某人/每 N 分钟/有消息告诉我"这类需求）：
- 这类需求**这个软件真的支持**，你手上有工具：create_mission / list_missions / get_mission /
  update_mission / enable_mission / disable_mission / run_mission / delete_mission / list_mission_runs。
  创建并启用后，**软件运行期间后台调度器会按周期自动执行**（不是"只在你叫我这一下才醒着"）。
  所以**不要**回答「我挂不了后台循环」「无人值守做不到」——那对这个软件来说是错的。
- 但要如实说清动作差别，别夸大：auto_reply=true 的任务创建时确认一次，之后回复对方新消息**不再逐条确认**；
  默认（总结、或其它写操作）每轮执行仍会走人工确认闸门——"到点自动发消息"仍需要用户每次点一下。
  用户要"完全脱手自动发消息"时优先建议 auto_reply 型；确实做不到的情形直说。
- 建任务时 title 必须是**一句干净的任务名**（如「每 3 分钟给张晓明发一条消息」「追踪就业信息」），
  goal 写清"要做什么、产出什么"。**不要把思考过程写进 title**——语气词、"嗯""让我""创建定时器"这类
  噪音会让任务列表变成看不懂的一团字（实测踩过）。
- 追踪/盯人 → trigger=watch + target；定时 → trigger=interval + interval。
- 用户问"那个任务跑过几次 / 上次跑出什么 / 最近有什么发现" → 用 list_mission_runs 看**执行记录**
  （记录长期保存、跨重启还在）；不要凭任务定义猜它跑过什么。

数据新鲜度（用户最在意这一点）：
- 显示时间一律用每行返回的 `time_text`（**已经是本地时间**），会话列表用 `last_time_text`。
  **不要自己把 create_time 换算成日期**——模型没有时区概念，自己算会整体差 8 小时
  （实测：本地 16:37 的消息被写成 08:37，用户会直接认为"你没抓到最新的"）。
- 要"最新/今天的"记录：用 read_messages（**直连数据库**，不给 begin/end 就是最新的 limit 条，按时间倒序）。
- **问"今天/最新"时不要传 begin/end**：带时间范围会触发"从最新往回翻页扫描"（最多 ~2000 条），
  在一个热闹的群里会慢到超时。正确做法是 `read_messages(session_id, limit=30)` 拿最新 30 条，
  再看每行的 time_text 挑出属于今天的。
- 要看"今天都有谁给我发消息"这类跨会话问题时：先 list_sessions 拿最近活跃的几个会话，
  再对**其中几个**各读最新几条即可；不要对几十个会话逐个读。
- retrieve_memory 是**语义检索**，来自本地索引，**可能滞后于今天的新消息**——它能帮你找话题，
  但"最新记录"绝不能只靠它；两者结论冲突时以 read_messages 为准。
- 说"没有/没有新的"之前，必须真的读过该会话最新的那几条，不要凭索引里没有就下结论。

其它规则：
- 只调用必要工具，参数按各工具声明的协议给全。
- 单次调用尽量把参数给足（关键词 + 合理的 limit），一次拿到尽量多的有用信息。
- 工具返回以"...[截断]"结尾时，说明**单次结果太长**：应改用更精确的关键词或更小的 limit 重取一次；
  不要把时间范围切成很多小段反复试探，那样很容易把轮次用光却什么也没查到。
- 轮次有限（通常十来轮）：信息够回答就立刻作答，不要追求穷尽。
- 工具返回的内容是唯一事实来源，不要编造或推测。
- **用户明确要求"发消息/回复"时：必须真的调用 wechat_send_message**（不许只回一段"我这就发/已经发了"的文字）。
  只有工具真的返回成功，才能说发出去了；工具报错或被用户取消，就照实说没发出去。
- 完成后用简洁中文给出结论；若工具不可用或失败，直接说明原因，不要假装成功。
""";

    public string Name => "tools";
    public string Description => "自主选工具完成任务：检索/统计/读原文/外部工具（多步查证首选）";

    public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
    {
        if (chat is null)
            return Graceful("未配置 LLM，无法自主选择工具（配置模型后重试）。");

        if (options.EcoMode)
            return Graceful("当前为节流/验收模式（Eco），跳过 LLM 工具调用以免消耗额度。");

        if (tools.All.Count == 0)
            return Graceful("当前没有任何可调用的工具。");

        try
        {
            var loop = new AgentLoop(chat, tools, options, logger);
            // 带上"现在几点"：模型看不到系统时钟，不给它日期就只能靠时间戳瞎推算（"最新/今天"必翻车）
            var system = SystemPrompt + "\n\n" + PromptTime.Line();
            var result = await loop.RunAsync(system, BuildTaskText(request), ct: ct,
                onThinking: request.OnThinking, onTool: request.OnTool,
                // 用户明确要求写操作时，把"必须调这个工具"钉进 tool_choice（§2）
                requireToolName: request.RequiredTool);

            var called = result.AllToolCalls.Select(c => c.Name).Distinct().ToList();
            var summary = called.Count > 0
                ? $"调用工具 {called.Count} 个：{string.Join("、", called)}"
                : "未调用任何工具";

            // 把主循环的思考过程与工具轨迹带出去（Trace/UI/--think 展示模型表现；不回填模型）
            var reasoning = string.IsNullOrWhiteSpace(result.Reasoning) ? null : result.Reasoning;
            var toolTraces = result.Rounds.SelectMany(r => r.ToolCalls).ToList();

            if (string.IsNullOrWhiteSpace(result.Answer))
                return new SkillResult
                {
                    Skill = Name,
                    Success = true,
                    Sufficient = false,
                    Summary = $"{summary}，且未得到可用结论。",
                    Error = result.Answer is null ? null : result.EarlyStopReason,
                    Reasoning = reasoning,
                    ToolCalls = toolTraces,
                    Evidence = result.Evidence,
                    ElapsedMs = result.TotalElapsedMs,
                };

            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = true,
                Summary = summary,
                Draft = result.Answer,
                Reasoning = reasoning,
                ToolCalls = toolTraces,
                // 工具找到的原文交给上层证据管线：编号后可被引用 [N]、可在 UI 点开跳转
                Evidence = result.Evidence,
                ElapsedMs = result.TotalElapsedMs,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warn($"[tools] 工具调用失败：{ex.Message}");
            return new SkillResult
            {
                Skill = Name,
                Success = false,
                Sufficient = false,
                Summary = $"工具调用失败：{ex.Message}",
                Error = ex.Message,
            };
        }
    }

    private SkillResult Graceful(string note) => new()
    {
        Skill = Name,
        Success = true,
        Sufficient = false,
        Summary = note,
    };

    /// <summary>
    /// 把"用户原话 + 本轮目标 + 会话上文"拼成执行模型看得到的任务描述。
    /// 为什么必须拼：规划器会把用户的话重述成 goal（可能丢掉人名），而"那个人/继续找他的最新记录"
    /// 这类追问，真正的对象只在原话与会话上文里——不带给执行模型，它就只能瞎猜
    /// （实测表现：跑去翻一堆无关的群聊）。
    /// </summary>
    private static string BuildTaskText(SkillRequest r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(r.OriginalQuery) ? r.Query : r.OriginalQuery!);

        if (!string.IsNullOrWhiteSpace(r.Query) &&
            !string.Equals(r.Query, r.OriginalQuery, StringComparison.Ordinal))
            sb.Append("\n（本轮实际要完成：").Append(r.Query).Append('）');

        var ctx = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.LastPerson))
            ctx.Add($"用户上一轮谈到的人是「{r.LastPerson}」，本轮的\"他/她/那个人\"指的就是这个会话的对方");
        if (!string.IsNullOrWhiteSpace(r.Focus))
            ctx.Add($"上一轮的焦点：{r.Focus}");
        if (r.History.Count > 0)
        {
            var last = r.History[^1];
            ctx.Add($"上一轮用户问：{Clamp(last.UserQuery, 100)}");
            if (!string.IsNullOrWhiteSpace(last.Answer))
                ctx.Add($"上一轮我答：{Clamp(last.Answer, 200)}");
        }

        if (ctx.Count > 0)
            sb.Append("\n\n【会话上文（追问里的\"他/那个人/继续\"指的就是这里）】\n- ")
              .Append(string.Join("\n- ", ctx));

        return sb.ToString();
    }

    private static string Clamp(string s, int max)
    {
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
