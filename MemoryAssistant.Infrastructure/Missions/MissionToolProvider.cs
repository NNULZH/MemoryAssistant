using System.Text;
using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Missions;

namespace MemoryAssistant.Infrastructure.Missions;

/// <summary>
/// 任务自主管理工具（第三阶段 §19~§21）：Agent 可以创建/查询/修改/启停/立即执行/删除自己的长期任务。
///
/// 安全设计（§25 风险分级）：
///   · 读（list/get）→ 直接执行；
///   · 创建 / 修改 → 中风险，先把"将要做成什么"摆出来要人点确认（<see cref="IActionConfirmation"/>）；
///   · 删除 → 高风险，必须确认；
///   · 启用 / 停用 / 立即执行 → 低风险且可逆，直接执行（不弹窗，避免演示时被弹窗卡住）。
/// 定位目标一律**按名字**（"把秋招监控暂停"），不要求用户提供 Guid。
/// </summary>
public sealed class MissionToolProvider
{
    private readonly MissionStore _store;
    private readonly MissionScheduler? _scheduler;
    private readonly IActionConfirmation _confirm;
    private readonly Action _persist;
    private readonly Action<string>? _audit;
    private readonly IMissionRunStore? _runs;

    public MissionToolProvider(
        MissionStore store,
        MissionScheduler? scheduler,
        IActionConfirmation confirm,
        Action persist,
        Action<string>? audit = null,
        IMissionRunStore? runs = null)
    {
        _store = store;
        _scheduler = scheduler;
        _confirm = confirm;
        _persist = persist;
        _audit = audit;
        _runs = runs;
    }

    public void RegisterAll(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition
        {
            Name = "list_missions",
            Description = "列出我的全部长期任务：名称、触发方式、状态、已执行次数、上次执行、创建方式（Agent 创建 / 手工创建）与目标。"
                        + "用户提到'我的任务''刚才建的那个任务'时先用它。",
            Category = ToolCategory.Context,
            Parameters = [],
            ReadOnly = true,
            ExecuteAsync = (_, _) => TaskOk(RenderList()),
        });

        registry.Register(new ToolDefinition
        {
            Name = "get_mission",
            Description = "按名称查看某个长期任务的详情（触发方式、状态、目标、执行次数、最近结果）。",
            Category = ToolCategory.Context,
            Parameters = [P("name", ToolParamType.String, "任务名称（或名称里的关键词）", required: true)],
            ReadOnly = true,
            ExecuteAsync = (args, _) => Resolve(args, out var m, out var err) ? TaskOk(RenderDetail(m!)) : TaskFail(err),
        });

        registry.Register(new ToolDefinition
        {
            Name = "create_mission",
            Description = "创建一个长期任务（会先弹确认框让用户过目，确认后才真正创建并纳入调度）。"
                        + "用于'帮我每5分钟检查一次新的秋招消息'这类需求。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters =
            [
                P("title", ToolParamType.String, "任务名称（简短，如：秋招信息监控）", required: true),
                P("goal", ToolParamType.String, "任务目标（要做什么、产出什么）", required: true),
                P("trigger", ToolParamType.String, "触发方式：interval（定时）/ watch（盯着某个会话）/ manual（手动）", def: "interval",
                    values: ["interval", "watch", "manual"]),
                P("interval", ToolParamType.String, "巡检间隔：分钟数（如 5）或秒级（如 30秒）", def: "30"),
                P("target", ToolParamType.String, "追踪对象（trigger=watch 或自动回复时必填：人名/群名/主题）", def: ""),
                P("auto_reply", ToolParamType.Boolean, "是否自动回复对方新消息（写操作，默认 false）", def: false),
            ],
            ExecuteAsync = CreateAsync,
        });

        registry.Register(new ToolDefinition
        {
            Name = "update_mission",
            Description = "修改一个长期任务（名称/目标/触发方式/巡检间隔/追踪对象）。改完会弹确认框。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters =
            [
                P("name", ToolParamType.String, "要改的任务名称（或关键词）", required: true),
                P("title", ToolParamType.String, "新名称", def: ""),
                P("goal", ToolParamType.String, "新目标", def: ""),
                P("trigger", ToolParamType.String, "新触发方式：interval / watch / manual", def: ""),
                P("interval", ToolParamType.String, "新巡检间隔：分钟数或 30秒", def: ""),
                P("target", ToolParamType.String, "新追踪对象", def: ""),
            ],
            ExecuteAsync = UpdateAsync,
        });

        registry.Register(new ToolDefinition
        {
            Name = "enable_mission",
            Description = "启用（恢复）一个长期任务：重新纳入调度器按周期巡检。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters = [P("name", ToolParamType.String, "任务名称（或关键词）", required: true)],
            ExecuteAsync = (args, _) =>
            {
                if (!Resolve(args, out var m, out var err)) return TaskFail(err);
                if (m!.Trigger == MissionTriggerKind.Manual)
                    return TaskFail($"「{m.Title}」是手动任务，不参与自动调度；可直接用 run_mission 执行一次。");
                var ok = _scheduler?.StartMission(m.Id, runImmediately: false) ?? false;
                if (ok) _persist();
                return ok
                    ? TaskOk($"已启用「{m.Title}」（{m.TriggerText}），已纳入后台巡检。")
                    : TaskFail("调度器未就绪，启用失败。");
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "disable_mission",
            Description = "停用（暂停）一个长期任务：取消在途执行并移出巡检；任务本身与历史保留。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters = [P("name", ToolParamType.String, "任务名称（或关键词）", required: true)],
            ExecuteAsync = (args, _) =>
            {
                if (!Resolve(args, out var m, out var err)) return TaskFail(err);
                var ok = _scheduler?.StopMission(m!.Id) ?? false;
                if (ok) _persist();
                return ok ? TaskOk($"已暂停「{m!.Title}」。") : TaskFail("调度器未就绪，暂停失败。");
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "run_mission",
            Description = "立即执行一次某个长期任务（不等周期），返回本次执行结论。会真的跑一遍，可能要几十秒。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters = [P("name", ToolParamType.String, "任务名称（或关键词）", required: true)],
            ExecuteAsync = async (args, ct) =>
            {
                if (!Resolve(args, out var m, out var err)) return Fail(err);
                if (_scheduler is null) return Fail("调度器未就绪。");
                var r = await _scheduler.RunOnceAsync(m!.Id);
                _persist();
                if (r is null) return Fail($"「{m.Title}」执行被取消。");
                var note = r.Skipped ? "（本轮无需执行）" : "";
                return new ToolCallResult
                {
                    Success = r.Success,
                    Output = $"「{m.Title}」执行完毕{note}：{r.Summary}（证据 {r.EvidenceCount} 条 / {r.ElapsedMs:0}ms）",
                    Error = r.Success ? null : r.Summary,
                    ElapsedMs = r.ElapsedMs,
                };
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "list_mission_runs",
            Description = "查看长期任务的**执行记录**（长期保存、跨重启）：每条的时间、结论全文、证据条数、耗时、是否跳过/失败。"
                        + "用户问「那个任务跑过几次 / 上次跑出什么 / 最近有什么发现」时用它——"
                        + "不要凭任务定义猜它跑过什么，记录里才是真的结果。",
            Category = ToolCategory.Context,
            Parameters =
            [
                P("name", ToolParamType.String, "任务名称（或关键词）；留空 = 看全部任务的最近记录", def: ""),
                P("limit", ToolParamType.String, "最多返回几条（默认 10）", def: "10"),
            ],
            ReadOnly = true,
            ExecuteAsync = (args, _) =>
            {
                if (_runs is null) return TaskFail("执行账本未接入，读不到任务执行记录。");
                var name = Str(args, "name").Trim();
                Guid? id = null;
                if (name.Length > 0)
                {
                    if (!Resolve(args, out var m, out var err)) return TaskFail(err);
                    id = m!.Id;
                }
                var limit = int.TryParse(Str(args, "limit").Trim(), out var n) && n > 0 ? Math.Min(n, 50) : 10;
                var records = _runs.List(id, limit);
                if (records.Count == 0)
                    return TaskOk(name.Length > 0
                        ? $"「{name}」还没有执行记录（记录会在它跑过之后长期保存）。"
                        : "还没有任何任务执行记录。");

                var sb = new StringBuilder();
                sb.AppendLine($"共 {records.Count} 条执行记录（按时间倒序，最新在上）：");
                foreach (var r in records)
                {
                    sb.AppendLine($"- [{r.AtText}]「{r.MissionTitle}」{r.StatusText}（{r.ElapsedMs:0}ms，证据 {r.EvidenceCount} 条）");
                    // 单条正文给模型看时会截一刀（上下文有限）；任务页里是完整不截断的
                    var body = r.Summary.Replace("\n", " ").Trim();
                    if (body.Length > 800) body = body[..800] + "…";
                    sb.AppendLine("  " + body);
                }
                return TaskOk(sb.ToString().TrimEnd());
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "delete_mission",
            Description = "删除一个长期任务（不可恢复，需人工确认）：先停止再移出任务目录，它的执行记录也一并删除。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters = [P("name", ToolParamType.String, "任务名称（或关键词）", required: true)],
            ExecuteAsync = async (args, ct) =>
            {
                if (!Resolve(args, out var m, out var err)) return Fail(err);
                var yes = await _confirm.ConfirmAsync(new ActionProposal
                {
                    Action = "delete_mission",
                    Target = m!.Title,
                    Description = $"将删除长期任务「{m.Title}」：\n触发方式：{m.TriggerText}\n目标：{m.Goal}\n\n"
                                + "删除后不再巡检，它的执行记录一并移除，且无法恢复。",
                }, ct);
                if (!yes) return Fail($"用户拒绝删除「{m.Title}」。");
                // 走调度器的删除：停巡检 + 取消在途 + 清执行记录（记录属于这个任务，留着是孤儿数据）
                if (_scheduler is not null)
                {
                    _scheduler.RemoveMission(m.Id);
                }
                else
                {
                    _store.Remove(m.Id);
                    _runs?.Clear(m.Id);
                }
                _persist();
                _audit?.Invoke($"[任务] 已删除「{m.Title}」");
                return Ok($"已删除任务「{m.Title}」（及其执行记录）。");
            },
        });
    }

    // ---------------- 创建 / 修改（都要人确认） ----------------

    private async Task<ToolCallResult> CreateAsync(string argsJson, CancellationToken ct)
    {
        var title = Str(argsJson, "title").Trim();
        var goal = Str(argsJson, "goal").Trim();
        if (title.Length == 0 || goal.Length == 0)
            return Fail("create_mission 需要 title 与 goal。");

        var triggerText = Str(argsJson, "trigger").Trim().ToLowerInvariant();
        var trigger = triggerText switch
        {
            "watch" => MissionTriggerKind.Watch,
            "manual" => MissionTriggerKind.Manual,
            _ => MissionTriggerKind.Interval,
        };
        var (minutes, seconds) = ParseInterval(Str(argsJson, "interval"), fallbackMinutes: 30);
        var target = Str(argsJson, "target").Trim();
        var autoReply = Bool(argsJson, "auto_reply");

        if (autoReply && target.Length == 0)
            return Fail("自动回复任务必须给出 target（回给谁）——不猜对象。");
        if (trigger == MissionTriggerKind.Watch && target.Length == 0)
            return Fail("追踪型任务必须给出 target（盯谁/盯什么主题）。");

        var triggerLine = trigger switch
        {
            MissionTriggerKind.Interval => $"定时执行：每 {IntervalText(minutes, seconds)} 一次",
            MissionTriggerKind.Watch => $"追踪「{target}」：每 {IntervalText(minutes, seconds)} 巡检，有新内容才执行",
            _ => "手动执行（不自动跑）",
        };

        var yes = await _confirm.ConfirmAsync(new ActionProposal
        {
            Action = "create_mission",
            Target = title,
            Description = $"将创建一个长期任务：\n名称：{title}\n触发：{triggerLine}\n"
                        + (autoReply ? "动作：自动回复对方的新消息（写操作）\n" : "动作：产出总结汇报\n")
                        + $"\n目标：{goal}",
        }, ct);
        if (!yes) return Fail($"用户取消了创建「{title}」。");

        var mission = _store.Add(new MissionDefinition
        {
            Title = title,
            Goal = goal,
            Trigger = trigger,
            Action = autoReply ? MissionActionKind.AutoReply : MissionActionKind.Summarize,
            IntervalMinutes = minutes,
            IntervalSeconds = seconds,
            Target = trigger == MissionTriggerKind.Watch || autoReply ? target : "",
            RequiresApproval = true,
            Origin = MissionDefinition.OriginAgent,
        });
        if (trigger != MissionTriggerKind.Manual) _scheduler?.StartMission(mission.Id);
        _persist();
        _audit?.Invoke($"[任务] Agent 创建了「{mission.Title}」（{mission.TriggerText}）");
        return Ok($"已创建任务「{mission.Title}」：{mission.TriggerText}。"
                  + (trigger == MissionTriggerKind.Manual ? "可在任务页手动执行。" : "已在后台纳入巡检。"));
    }

    private async Task<ToolCallResult> UpdateAsync(string argsJson, CancellationToken ct)
    {
        if (!Resolve(argsJson, out var m, out var err)) return Fail(err);
        var title = Str(argsJson, "title").Trim();
        var goal = Str(argsJson, "goal").Trim();
        var triggerText = Str(argsJson, "trigger").Trim().ToLowerInvariant();
        var intervalText = Str(argsJson, "interval").Trim();
        var target = Str(argsJson, "target").Trim();

        if (title.Length == 0 && goal.Length == 0 && triggerText.Length == 0 && intervalText.Length == 0 && target.Length == 0)
            return Fail("没有给出任何要修改的字段。");

        var changes = new List<string>();
        if (title.Length > 0 && title != m!.Title) changes.Add($"名称：{m.Title} → {title}");
        if (goal.Length > 0 && goal != m!.Goal) changes.Add($"目标：{m.Goal} → {goal}");
        if (triggerText.Length > 0) changes.Add($"触发方式：{m!.Trigger} → {triggerText}");
        if (intervalText.Length > 0) changes.Add($"巡检间隔：{m!.TriggerText} → {intervalText}");
        if (target.Length > 0 && target != m!.Target) changes.Add($"追踪对象：{Blank(m.Target)} → {target}");
        if (changes.Count == 0) return Ok($"「{m!.Title}」无需改动。");

        var yes = await _confirm.ConfirmAsync(new ActionProposal
        {
            Action = "update_mission",
            Target = m!.Title,
            Description = $"将修改任务「{m.Title}」：\n" + string.Join("\n", changes.Select(c => "· " + c)),
        }, ct);
        if (!yes) return Fail($"用户取消了修改「{m.Title}」。");

        if (title.Length > 0) m.Title = title;
        if (goal.Length > 0) m.Goal = goal;
        if (target.Length > 0) m.Target = target;
        if (intervalText.Length > 0)
        {
            var (mi, se) = ParseInterval(intervalText, fallbackMinutes: Math.Max(1, m.IntervalMinutes));
            m.IntervalMinutes = mi;
            m.IntervalSeconds = se;
        }
        if (triggerText.Length > 0)
            m.Trigger = triggerText switch
            {
                "watch" => MissionTriggerKind.Watch,
                "manual" => MissionTriggerKind.Manual,
                _ => MissionTriggerKind.Interval,
            };

        // 间隔/触发方式变了 → 重新排下一次执行（运行中的任务才会被重排）
        if (m.Status == MissionStatus.Running && m.Trigger != MissionTriggerKind.Manual)
            _scheduler?.StartMission(m.Id, runImmediately: false);
        _persist();
        _audit?.Invoke($"[任务] Agent 修改了「{m.Title}」：{string.Join("；", changes)}");
        return Ok($"已更新「{m.Title}」：\n" + string.Join("\n", changes.Select(c => "· " + c)));
    }

    // ---------------- 工具函数 ----------------

    /// <summary>按名称（或名称片段）定位任务；重复命中时报出候选让人说清楚。</summary>
    private bool Resolve(string argsJson, out MissionDefinition? mission, out string error)
    {
        mission = null;
        error = "";
        var key = Str(argsJson, "name").Trim();
        if (key.Length == 0)
        {
            error = "缺少 name（任务名称）。";
            return false;
        }
        if (Guid.TryParse(key, out var id))
        {
            mission = _store.Find(id);
            if (mission is null) error = "找不到该 id 对应的任务。";
            return mission is not null;
        }

        var exact = _store.Items.FirstOrDefault(m => string.Equals(m.Title, key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) { mission = exact; return true; }

        var hits = _store.Items.Where(m => m.Title.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) { mission = hits[0]; return true; }
        if (hits.Count == 0)
        {
            error = $"没有找到名字里含「{key}」的任务。当前任务：{(_store.Items.Count == 0 ? "（无）" : string.Join("、", _store.Items.Select(m => m.Title)))}";
            return false;
        }
        error = $"「{key}」匹配到多个任务：{string.Join("、", hits.Select(m => m.Title))}。请说得更具体。";
        return false;
    }

    private string RenderList()
    {
        if (_store.Items.Count == 0) return "目前没有任何长期任务。";
        var sb = new StringBuilder($"我有 {_store.Items.Count} 个长期任务：\n");
        foreach (var m in _store.Items)
        {
            var last = m.LastRunAt is null ? "尚未执行" : $"{m.LastRunAt:MM-dd HH:mm}";
            sb.AppendLine($"- {m.Title}｜{m.TriggerText}｜{m.StatusText}｜已执行 {m.RunCount} 次｜上次 {last}｜{m.OriginText}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderDetail(MissionDefinition m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"任务：{m.Title}");
        sb.AppendLine($"状态：{m.StatusText}（{m.TriggerText}）");
        sb.AppendLine($"创建方式：{m.OriginText}");
        sb.AppendLine($"目标：{m.Goal}");
        if (!string.IsNullOrWhiteSpace(m.Target)) sb.AppendLine($"追踪对象：{m.Target}");
        sb.AppendLine($"已执行：{m.RunCount} 次，上次 {m.LastRunAtText}");
        if (!string.IsNullOrWhiteSpace(m.LastResult)) sb.AppendLine($"最近结果：{m.LastResult}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>解析间隔："30" = 30 分钟；"30秒"/"30s" = 秒级。</summary>
    internal static (int Minutes, int Seconds) ParseInterval(string? raw, int fallbackMinutes)
    {
        var t = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d{1,4})\s*(?:秒|s|S)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var sec) && sec > 0) return (0, sec);
        return int.TryParse(t, out var min) && min > 0 ? (min, 0) : (fallbackMinutes, 0);
    }

    private static string IntervalText(int minutes, int seconds)
        => seconds > 0 ? $"{seconds} 秒" : $"{Math.Max(1, minutes)} 分钟";

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "（未设置）" : s;

    private static ToolParameterSpec P(
        string name, string type, string description,
        bool required = false, object? def = null,
        IReadOnlyList<string>? values = null) => new()
    {
        Name = name,
        Type = type,
        Description = description,
        Required = required,
        Default = def,
        Enum = values,
    };

    private static string Str(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var v))
            {
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => v.ToString(),
                };
            }
        }
        catch (JsonException) { /* 参数坏掉按缺省处理 */ }
        return "";
    }

    private static bool Bool(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.String) return v.GetString() is "true" or "1" or "是";
            }
        }
        catch (JsonException) { /* 同上 */ }
        return false;
    }

    private static ToolCallResult Ok(string output) => new() { Success = true, Output = output };
    private static ToolCallResult Fail(string error) => new() { Success = false, Error = error };
    private static Task<ToolCallResult> TaskOk(string output) => Task.FromResult(Ok(output));
    private static Task<ToolCallResult> TaskFail(string error) => Task.FromResult(Fail(error));
}
