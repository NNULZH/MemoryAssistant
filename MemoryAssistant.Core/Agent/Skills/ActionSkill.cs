using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 微信写操作 Skill（V3.4，Action 类）：把"打开X的聊天""给X发消息：Y"变成真实动作。
/// 安全边界：整条动作（打开 + 发送）在执行前必须经 <see cref="IActionConfirmation"/> 人工确认；
/// 被拒绝或未接入闸门时一律不动手。默认闸门为 DenyAll，装错也不会误发消息。
/// 只支持规则可解析的显式祈使句，解析不出就诚实说不会（不猜）。
/// </summary>
public sealed class ActionSkill : IAgentSkill
{
    private readonly IWeChatActionBridge? _bridge;
    private readonly IActionConfirmation _confirmation;
    private readonly Action<string>? _audit;
    private readonly IActionGrantStore? _grants;
    private readonly WeChatTargetResolver? _targets;
    private readonly SendAllowList? _allowList;

    public ActionSkill(
        IWeChatActionBridge? bridge,
        IActionConfirmation? confirmation = null,
        Action<string>? audit = null,
        IActionGrantStore? grants = null,
        WeChatTargetResolver? targets = null,
        SendAllowList? sendAllowList = null)
    {
        _bridge = bridge;
        _confirmation = confirmation ?? new DenyAllActionConfirmation();
        _audit = audit;
        _grants = grants;
        _targets = targets;
        _allowList = sendAllowList;
    }

    public string Name => "action";
    public string Description => "执行微信写操作（打开会话/发送消息，需人工确认）";

    public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
    {
        // 用**用户原话**解析，不要用规划器重述过的 goal：
        // 实测规划器把"给文件传输助手发消息：X"重述成"打开与微信「文件传输助手」的会话并发送…"，
        // 从这句里正则抠目标会连"与/微信/「」"一起抠进来，搜索必然失败（当时真的拿这个假名字去搜了）。
        // 带上"上一轮谈到的人"：追问型指令（"再发送一句X"）只有靠它才补得出收件人——
        // 否则这条最常见的追问会在第一步就判成"未能识别"，一个动作都不做（实测踩过）。
        var rawQuery = request.OriginalQuery ?? request.Query;
        var parsed = ActionIntentParser.TryParse(rawQuery, request.LastPerson);
        if (parsed is null)
        {
            // 说清"什么都没做" + 怎么改：不能让作答器把它讲成"发过了/没等到回执"
            var note = ActionIntentParser.IsBareSendFollowUp(rawQuery)
                ? "这条指令是要**发送消息**，但没有说清**发给谁**，所以我没有执行任何动作"
                  + "（也不会发到当前打开的会话——那是最容易发错人的情况）。"
                  + "请说成「给某人发消息：内容」，例如「给张晓明发消息：晚上见」。"
                : "未能识别为写操作指令。支持的说法：「打开张三的聊天」「给张三发消息：晚上见」「回复：好的」。";
            _audit?.Invoke($"[Action] 未执行任何动作（指令没解析成可执行的写操作）：{rawQuery}");
            return Graceful(note);
        }

        // 进度事件（给 UI 看）与 ToolCallTrace（给 Runtime 判据）成对产生：
        // 以前只推进度事件，于是"UI 上看着调了工具"但 Runtime 的 trace 里一条工具调用都没有——
        // 这正是"模型自认为行动了、其实没有真实 tool 调用"的根源（第三阶段补充 §3/§4）。
        var traces = new List<ToolCallTrace>();
        var onTool = request.OnTool;
        void EmitStart(string name, string args) => onTool?.Invoke(AgentProgress.ToolStart(name, args));
        void EmitEnd(string name, string args, bool ok, string summary, double ms)
        {
            onTool?.Invoke(AgentProgress.ToolEnd(name, ok, summary));
            traces.Add(new ToolCallTrace
            {
                Name = name,
                ArgumentsJson = args,
                Success = ok,
                Output = ok ? summary : "",
                Error = ok ? null : summary,
                LatencyMs = ms,
                Round = 1,
            });
        }

        // ===== 动手前的两道硬规矩（都排在"问用户"之前）=====
        // 顺序说明：下面的"对象核对"不是环境检查，而是**决定"到底发给谁"**的前提——
        // 白名单要拿核对后的真名来判，所以必须先核对、后问用户（弹窗里显示的也才是真名）。
        var target = new ResolvedTarget { Query = parsed.Target, Checked = false, Note = "" };
        if (parsed.Target.Length > 0 && _targets is not null)
        {
            var lookupSw = System.Diagnostics.Stopwatch.StartNew();
            EmitStart("find_sessions", $"keyword={parsed.Target}");
            target = await _targets.ResolveAsync(parsed.Target, ct);
            lookupSw.Stop();
            EmitEnd("find_sessions", $"keyword={parsed.Target}", target.Checked,
                target.Checked ? target.Note : "会话目录不可用，未核对到对象（不影响继续）",
                lookupSw.Elapsed.TotalMilliseconds);
        }

        // 目录里有就用目录里的名字（用户口语的名字未必和微信里的一致），并按它判分区；
        // 目录里没有/没核对成 → 不据此断定"不存在"，照原样交给 Auto 继续试。
        var openName = target.Found ? target.SearchName : parsed.Target;
        var openKind = target.Found
            ? (target.IsGroup ? ChatTargetKind.Group : ChatTargetKind.Contact)
            : ChatTargetKind.Auto;

        // 发送对象白名单：名单非空时，名单外的对象**连确认弹窗都不弹，直接拒绝**。
        // 与"确认"的分工：确认是用户当场拍板，白名单是事前定好的死规矩——
        // 这一条不依赖用户每次手点，也不依赖模型自觉，是"绝不许发错人"的最后一道闸。
        //
        // 判定用**核对后的真名**（目录核对到了就只认真名）：于是"点名给「周斌」发"却命中
        // "周斌的工作群"会被拦住。只有目录里根本查不到时，才回退用用户说的名字——
        // 那已经是我们唯一能核实的依据了（没聊过的联系人查不到会话，但确实可以发）。
        string[] allowCandidates = target.Found ? [openName, target.Name] : [openName];
        if (parsed.Payload.Length > 0 && _allowList is { Enabled: true }
            && !_allowList.Allows(allowCandidates))
        {
            var refusal = _allowList.RejectionFor(openName.Length > 0 ? openName : parsed.Target);
            EmitStart("send_message", $"to={openName}");
            EmitEnd("send_message", $"to={openName}", false, refusal, 0);
            _audit?.Invoke($"[Action] 白名单拒绝发送：{refusal}");
            return Failed(refusal, traces);
        }

        // 一条指令只确认一次：把"打开 + 发送"作为整体给用户看
        var description = BuildDescription(parsed, openName);
        var proposal = new ActionProposal
        {
            Action = parsed.Action,
            Target = openName,
            Payload = parsed.Payload,
            Description = description,
        };

        // 写操作也要出现在「工具调用」面板里（用户看得到"到底要做什么"），
        // 而且**确认这一步不能被跳过**：用户说"给某人发消息"时，Agent 必须先把决定权交出去，
        // 不能因为"微信没开着"之类的环境检查就悄悄作罢（那样用户只会看到一句空承诺）。
        // （对象核对与白名单是上面两道"决定发给谁"的规矩，不是环境检查——它们过了才轮到问用户。）
        var confirmSw = System.Diagnostics.Stopwatch.StartNew();
        EmitStart("request_user_confirmation", $"question={description}");

        bool approved;
        if (_grants?.IsGranted(parsed.Action, openName) == true)
        {
            // 用户此前点过「以后都允许」：如实写"按记住的授权直接执行"，
            // 别谎称"用户刚刚确认了"——台账里说的话必须和真实发生的事一致。
            confirmSw.Stop();
            approved = true;
            EmitEnd("request_user_confirmation", description, true,
                $"已按记住的授权直接执行（对象「{parsed.Target}」，本次未再询问）", 0);
            _audit?.Invoke($"[Action] 命中记住的授权，直接执行：{description}");
        }
        else
        {
            try
            {
                approved = await _confirmation.ConfirmAsync(proposal, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                confirmSw.Stop();
                EmitEnd("request_user_confirmation", description, false, ex.Message, confirmSw.Elapsed.TotalMilliseconds);
                return Graceful($"确认环节出错，已放弃执行：{ex.Message}", traces);
            }

            confirmSw.Stop();
            var choice = approved ? "确认执行" : "取消（未做任何事）";
            EmitEnd("request_user_confirmation", description, true, $"用户选择：{choice}", confirmSw.Elapsed.TotalMilliseconds);
        }

        if (!approved)
        {
            _audit?.Invoke($"[Action] 用户取消：{description}");
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = true,
                Summary = "用户取消了该操作，未执行任何写动作。",
                Draft = $"已取消：{description}（未发送任何消息）",
                ToolCalls = traces,
            };
        }

        // 用户已经点头，这时才检查环境：检查不通过就如实说"做不了"，别让回答变成一句空承诺
        if (_bridge is null)
        {
            EmitEnd("wechat_status", "检查微信窗口", false, "写操作桥未接入", 0);
            return Graceful("微信写操作未接入（当前环境未提供动作桥），消息**没有发送**。", traces);
        }

        var statusSw = System.Diagnostics.Stopwatch.StartNew();
        EmitStart("wechat_status", "检查微信窗口");
        if (!_bridge.IsAvailable)
        {
            statusSw.Stop();
            EmitEnd("wechat_status", "检查微信窗口", false, "未找到微信窗口", statusSw.Elapsed.TotalMilliseconds);
            return Graceful("未找到微信窗口，消息**没有发送**。请先登录并打开微信"
                            + "（窗口最小化到托盘时先点开），然后再说一次。", traces);
        }
        statusSw.Stop();
        EmitEnd("wechat_status", "检查微信窗口", true, "微信窗口已就绪", statusSw.Elapsed.TotalMilliseconds);

        var steps = new List<string>();

        // 1) 打开会话（若指定了目标）
        if (parsed.Target.Length > 0)
        {
            var openSw = System.Diagnostics.Stopwatch.StartNew();
            EmitStart("open_chat", $"person={openName}, kind={openKind}");
            var opened = await _bridge.OpenChatAsync(openName, openKind, ct);
            openSw.Stop();
            EmitEnd("open_chat", $"person={openName}, kind={openKind}", opened.Success,
                opened.Success ? $"已打开「{openName}」的会话" : (opened.Error ?? "打开失败"),
                openSw.Elapsed.TotalMilliseconds);
            if (!opened.Success)
            {
                _audit?.Invoke($"[Action] 打开会话失败：{openName} | {opened.Error}");
                // 失败归因必须带上核对结论：把"窗口/输入通道故障"和"没有这个人"分清，
                // 上层（含模型）照这句说，就不会再把窗口问题说成"查无此人"。
                var failureNote = WeChatTargetResolver.FailureNote(target);
                return Failed($"打开与「{openName}」的会话失败：{opened.Error}"
                              + (failureNote.Length > 0 ? "\n" + failureNote : ""), traces);
            }
            steps.Add(!string.Equals(openName, parsed.Target, StringComparison.Ordinal)
                ? $"已打开「{openName}」（会话目录里「{parsed.Target}」对应的名字）"
                : $"已打开「{parsed.Target}」的会话");
        }

        // 2) 发送消息（若有内容）
        if (parsed.Payload.Length > 0)
        {
            var to = parsed.Target.Length > 0 ? openName : "当前会话";
            var sendSw = System.Diagnostics.Stopwatch.StartNew();
            EmitStart("send_message", $"to={to}, text={parsed.Payload}");
            var sent = await _bridge.SendMessageAsync(parsed.Payload, ct);
            sendSw.Stop();
            EmitEnd("send_message", $"to={to}, text={parsed.Payload}", sent.Success,
                sent.Success ? sent.Detail : (sent.Error ?? "发送失败"), sendSw.Elapsed.TotalMilliseconds);
            if (!sent.Success)
            {
                _audit?.Invoke($"[Action] 发送失败：{to} | {sent.Error}");
                return Failed($"发送失败：{sent.Error}", traces);
            }
            steps.Add(parsed.Target.Length > 0
                ? $"已向「{to}」发送：{parsed.Payload}"
                : $"已向当前会话发送：{parsed.Payload}");
        }

        var summary = string.Join("；", steps) + "。";
        _audit?.Invoke($"[Action] 已执行：{description}");
        return new SkillResult
        {
            Skill = Name,
            Success = true,
            Sufficient = true,
            Summary = summary,
            Draft = summary,
            ToolCalls = traces,
        };
    }

    private static string BuildDescription(ParsedAction parsed, string targetName)
    {
        var parts = new List<string>();
        if (parsed.Target.Length > 0) parts.Add($"打开与「{targetName}」的微信会话");
        if (parsed.Payload.Length > 0)
            parts.Add(parsed.Target.Length > 0
                ? $"并发送消息：{parsed.Payload}"
                : $"向当前微信会话发送消息：{parsed.Payload}");
        return string.Join("，", parts);
    }

    private SkillResult Graceful(string note, IReadOnlyList<ToolCallTrace>? traces = null) => new()
    {
        Skill = Name,
        Success = true,
        Sufficient = false,
        Summary = note,
        ToolCalls = traces ?? [],
    };

    private SkillResult Failed(string error, IReadOnlyList<ToolCallTrace>? traces = null) => new()
    {
        Skill = Name,
        Success = false,
        Sufficient = false,
        Summary = error,
        Error = error,
        ToolCalls = traces ?? [],
    };
}
