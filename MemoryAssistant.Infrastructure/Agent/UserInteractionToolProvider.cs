using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Interaction;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 通用用户交互工具（第三阶段补充要求 §2/§3）：
/// 把"需要用户拍板"做成 Agent **可调用的一等能力**，而不是藏在某个 Action 内部偷偷弹窗。
///
/// 一个工具覆盖所有"要用户决定"的场景：
///   · 高风险动作前的确认（是否给张三发这条消息）
///   · 多个候选里选一个（找到两个同名联系人，选哪个）
///   · 要不要继续深入（有 5 个搜索结果，先看哪个）
///   · 是否执行某个 Workflow
/// 返回给 Agent 的是**用户选择的原文**（UserSelection = "发送"），Agent 据此继续执行。
///
/// 只读工具（ReadOnly = true）：它本身不改任何状态，只是"问一句"。
/// </summary>
public sealed class UserInteractionToolProvider(IUserInteraction ui)
{
    private readonly IUserInteraction _ui = ui;

    /// <summary>工具名（别处按名字引用时用它，避免手写字符串写歪）。</summary>
    public const string ToolName = "request_user_confirmation";

    /// <summary>用户取消时返回给 Agent 的选择文本。</summary>
    public const string CancelledText = "（用户取消，未做任何选择）";

    public void RegisterAll(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition
        {
            Name = ToolName,
            Description =
                "向用户请求一个决定，并等待用户选择后再继续。\n"
                + "**什么时候用**：① 即将执行有副作用且不可逆的动作（发消息/删东西）之前；"
                + "② 候选有多个、你确实判断不出该选哪个（两个同名联系人、多个候选任务）；"
                + "③ 用户明确要求「你来问我 / 让我确认」。\n"
                + "**什么时候不要用**：只读查询、或者你已经能确定答案的普通操作——那些直接做完再说结论，"
                + "反复问会让用户觉得你不敢做事。同一个问题不要问第二遍（用户已经给过的信息要用起来）。\n"
                + "参数：question=要问的问题；options=选项列表（用 | 分隔，如 \"发送|修改后发送|取消\"）；"
                + "default_option=默认选项（必须出现在 options 里，高风险场景请把保守项设为默认）；"
                + "context=给用户看的背景（要发送的内容、候选来源等）。\n"
                + "返回：用户选中的那一项的原文（形如 UserSelection = \"发送\"）；用户取消时返回取消标记。",
            Category = ToolCategory.Context,
            Parameters =
            [
                new ToolParameterSpec
                {
                    Name = "question",
                    Type = ToolParamType.String,
                    Description = "要用户决定的问题，一句话说清楚（如：是否给张三发送这条消息？）",
                    Required = true,
                },
                new ToolParameterSpec
                {
                    Name = "options",
                    Type = ToolParamType.String,
                    Description = "可选项，用 | 分隔（如：发送|修改后发送|取消）；至少 2 项",
                    Required = true,
                },
                new ToolParameterSpec
                {
                    Name = "default_option",
                    Type = ToolParamType.String,
                    Description = "默认选项（回车即选中；必须出现在 options 里）。高风险动作用户最可能的保守选项",
                    Default = "",
                },
                new ToolParameterSpec
                {
                    Name = "context",
                    Type = ToolParamType.String,
                    Description = "给用户看的背景信息（要发送的原文、候选对象的来源、影响范围）",
                    Default = "",
                },
            ],
            ReadOnly = true,
            ExecuteAsync = async (argsJson, ct) =>
            {
                var question = Str(argsJson, "question").Trim();
                var options = ParseOptions(Str(argsJson, "options"));
                if (question.Length == 0 || options.Count < 2)
                    return new ToolCallResult
                    {
                        Success = false,
                        Error = "request_user_confirmation 需要 question 与至少 2 个 options（用 | 分隔）。",
                    };

                var def = Str(argsJson, "default_option").Trim();
                if (def.Length > 0 && !options.Contains(def, StringComparer.Ordinal)) def = "";

                var started = DateTime.UtcNow;
                var choice = await _ui.RequestChoiceAsync(new UserChoiceRequest
                {
                    Question = question,
                    Options = options,
                    DefaultOption = def,
                    Context = Str(argsJson, "context"),
                }, ct);

                var text = choice.Cancelled || choice.Selection.Length == 0
                    ? CancelledText
                    : choice.Selection;

                // 返回格式刻意保持机器可读：Agent 下一步直接按这个值走
                return new ToolCallResult
                {
                    Success = true,
                    Output = $"UserSelection = \"{text}\"",
                    ElapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
                };
            },
        });
    }

    /// <summary>
    /// 解析选项：主力格式是 "A|B|C"；同时容忍 JSON 数组字符串（模型有时会给 ["A","B"]）
    /// 与中文顿号/换行分隔——模型格式不稳，这里不能挑剔，否则一个格式不符就白问一次。
    /// </summary>
    internal static IReadOnlyList<string> ParseOptions(string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return [];

        if (t.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(t);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var s = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
                        if (s.Trim().Length > 0) list.Add(s.Trim());
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch (JsonException) { /* 不是合法 JSON 就走下面的分隔符解析 */ }
        }

        var parts = t.Split(['|', '｜', '、', '\n', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => p.Trim().Trim('"', '\'', '[', ']'))
                     .Where(p => p.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .ToList();
        return parts;
    }

    private static string Str(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => v.ToString(),
                };
        }
        catch (JsonException) { /* 参数坏掉按空处理 */ }
        return "";
    }
}
