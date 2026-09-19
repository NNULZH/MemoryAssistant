using System.Text.Json;
using MemoryAssistant.Core.Agent;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Integrations;
using MemoryAssistant.Core.Logging;

namespace MemoryAssistant.Infrastructure.Agent;

/// <summary>
/// 把微信写操作暴露为 Agent 可调用的工具（V3.4，Action 类）：
/// - 声明真实参数（ToolParameterSpec）→ ToolRegistry 生成完整 JSON Schema，模型能看到参数协议；
/// - 执行前一律经 <see cref="IActionConfirmation"/> 人工确认，未确认则不动作（默认闸门是拒绝）；
/// - 每次调用留痕（确认内容 + 执行结果）。
/// 与 ActionSkill 共用同一套闸门与桥，两条入口（Skill / Tool）的安全边界一致。
/// </summary>
public sealed class WeChatActionToolProvider(
    IWeChatActionBridge bridge,
    IActionConfirmation confirmation,
    IAppLogger? logger = null,
    WeChatTargetResolver? targets = null,
    SendAllowList? sendAllowList = null)
{
    public void RegisterAll(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition
        {
            Name = "wechat_open_chat",
            Description = "打开与指定人的微信会话（写操作：会先请求用户确认）。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters =
            [
                new ToolParameterSpec
                {
                    Name = "person",
                    Type = ToolParamType.String,
                    Description = "要打开的会话对象：联系人昵称或群名",
                    Required = true,
                },
                KindParameter,
            ],
            ExecuteAsync = async (argsJson, ct) =>
            {
                var person = GetString(argsJson, "person").Trim();
                if (person.Length == 0) return Failure("person 不能为空");

                // 强规则：动手前先查本地会话目录（与 ActionSkill 同一条规则，两条入口口径一致）。
                // 它会告诉我们：这个会话在不在、是私聊还是群聊、微信里的名字怎么写。
                var target = await ResolveAsync(person, ct);
                var openName = target.Found ? target.SearchName : person;
                var kind = target.Found
                    ? (target.IsGroup ? ChatTargetKind.Group : ChatTargetKind.Contact)
                    : GetKind(argsJson);

                var proposal = new ActionProposal
                {
                    Action = "open_chat",
                    Target = openName,
                    Description = $"打开与「{openName}」的微信会话",
                };
                if (!await ConfirmAsync(proposal, ct)) return Cancelled(proposal);

                var r = await bridge.OpenChatAsync(openName, kind, ct);
                return r.Success
                    ? Success($"{r.Detail}（会话对象：{openName}）{TargetNote(target)}")
                    : Failure((r.Error ?? "打开会话失败") + TargetNote(target));
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "wechat_send_message",
            Description = "给某人/某个群发一条微信消息（写操作：会把「打开会话 + 发送」作为一件事请用户确认一次）。"
                + "**必须给 person**（联系人昵称或群名）：不给就会发到「当前碰巧打开的那个会话」，很容易发错人。"
                + "内容不能含换行。",
            Category = ToolCategory.Action,
            ReadOnly = false,
            Parameters =
            [
                new ToolParameterSpec
                {
                    Name = "text",
                    Type = ToolParamType.String,
                    Description = "要发送的消息文本（不含换行符）",
                    Required = true,
                },
                new ToolParameterSpec
                {
                    Name = "person",
                    Type = ToolParamType.String,
                    Description = "收件人：联系人昵称或群名。强烈建议每次都给，避免发错会话",
                    Required = false,
                },
                KindParameter,
            ],
            ExecuteAsync = async (argsJson, ct) =>
            {
                var text = GetString(argsJson, "text");
                var person = GetString(argsJson, "person").Trim();
                if (text.Trim().Length == 0) return Failure("text 不能为空");
                if (text.Contains('\n') || text.Contains('\r'))
                    return Failure("text 不能包含换行符（回车会被微信当作「发送」，会把消息截断发出）");

                // 强规则：动手前先查本地会话目录（与 ActionSkill 同一条规则）。
                // 目录里有：用它给的名字和"私聊/群聊"分区（用户口语的名字未必和微信里一致）；
                // 目录里没有：不据此断定"没有这个人"，照模型给的名字 + kind 继续试。
                var target = await ResolveAsync(person, ct);
                var openName = person.Length > 0 && target.Found ? target.SearchName : person;
                var kind = person.Length > 0 && target.Found
                    ? (target.IsGroup ? ChatTargetKind.Group : ChatTargetKind.Contact)
                    : GetKind(argsJson);

                // 发送对象白名单（第三阶段补充）：名单非空 + 名单外 → 直接拒绝，连确认弹窗都不弹。
                // 判定用**核对后的真名**（核对到了就只认真名），于是"点名给某人发"却命中同名群会被拦住。
                var allowCandidates = target.Found ? new[] { openName, target.Name } : new[] { openName };
                if (sendAllowList is { Enabled: true } && !sendAllowList.Allows(allowCandidates))
                {
                    var refusal = sendAllowList.RejectionFor(openName);
                    logger?.Warn($"[Action][Tool] 白名单拒绝发送：{refusal}");
                    return Failure(refusal);
                }

                var description = person.Length > 0
                    ? $"打开与「{openName}」的微信会话，并发送消息：{text}"
                    : $"向「当前已打开的」微信会话发送消息：{text}";
                var proposal = new ActionProposal
                {
                    Action = "send_message",
                    Target = openName,
                    Payload = text,
                    Description = description,
                };
                if (!await ConfirmAsync(proposal, ct)) return Cancelled(proposal);

                // 打开 + 发送当成一件事：打开失败就**绝不发**——否则会发到当前碰巧打开的那个会话，
                // 那是真实场景里最容易出的事故（"给张三发"结果发到了群里）。
                if (person.Length > 0)
                {
                    var opened = await bridge.OpenChatAsync(openName, kind, ct);
                    if (!opened.Success)
                        return Failure($"打开「{openName}」的会话失败，未发送任何消息：{opened.Error}"
                                       + TargetNote(target, failure: true));
                }

                var r = await bridge.SendMessageAsync(text, ct);
                return r.Success
                    ? Success($"{r.Detail}（{description}）{TargetNote(target)}")
                    : Failure((r.Error ?? "发送失败") + TargetNote(target, failure: true));
            },
        });
    }

    /// <summary>
    /// "对象是联系人还是群聊"——微信搜索结果按「最常使用 / 联系人 / 群聊」分区展示，
    /// 同一个名字会横跨多个分区，填错分区就会点进另一个会话。
    /// **任务里已内置对象核对**（会先查本地会话目录按 is_chatroom 判分区），所以留空通常就对；
    /// 只有在你有更准的信息（例如用户明确说这是群）时才填。
    /// </summary>
    private static readonly ToolParameterSpec KindParameter = new()
    {
        Name = "kind",
        Type = ToolParamType.String,
        Description = "对象类型：contact=联系人（私聊），group=群聊。"
            + "不填=auto：会先查本地会话目录（find_sessions 同源）按 is_chatroom 判分区，"
            + "查不到再退「联系人」分区、没有再退「群聊」。"
            + "微信搜索下拉是按分区排的——搜一个人名时「群聊」区全是\"消息里提到过她\"的群，填错就点错会话。",
        Required = false,
        Enum = ["contact", "group", "auto"],
        Default = "auto",
    };

    /// <summary>kind 参数（大小写不敏感；认不出来就当 auto，不要乱猜）。</summary>
    private static ChatTargetKind GetKind(string argsJson)
        => GetString(argsJson, "kind").Trim().ToLowerInvariant() switch
        {
            "contact" or "private" or "friend" => ChatTargetKind.Contact,
            "group" or "chatroom" or "room" => ChatTargetKind.Group,
            _ => ChatTargetKind.Auto,
        };

    /// <summary>
    /// 对象核对（强规则）：查不到 / 查不动都不阻断操作，只影响
    /// "用哪个名字去搜、该点哪个分区、失败了该怎么归因"。
    /// </summary>
    private async Task<ResolvedTarget> ResolveAsync(string person, CancellationToken ct)
        => string.IsNullOrWhiteSpace(person) || targets is null
            ? new ResolvedTarget { Query = person, Checked = false, Note = "" }
            : await targets.ResolveAsync(person, ct);

    /// <summary>
    /// 把核对结论附在工具结果后面。模型看到的是这段文字，照它说就不会再把
    /// "窗口/输入通道故障"讲成"没有这个人"。
    /// </summary>
    private static string TargetNote(ResolvedTarget t, bool failure = false)
    {
        var note = failure ? WeChatTargetResolver.FailureNote(t) : (t.Checked && t.Found ? t.Note : "");
        return note.Length > 0 ? " " + note : "";
    }

    private async Task<bool> ConfirmAsync(ActionProposal proposal, CancellationToken ct)
    {
        try
        {
            var approved = await confirmation.ConfirmAsync(proposal, ct);
            logger?.Info(approved
                ? $"[Action][Tool] 用户已确认：{proposal.Description}"
                : $"[Action][Tool] 用户取消了：{proposal.Description}");
            return approved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Error($"[Action][Tool] 确认环节出错，放弃执行：{ex.Message}");
            return false;
        }
    }

    private static string GetString(string argsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}") return "";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static ToolCallResult Success(string output) => new() { Success = true, Output = output };

    private static ToolCallResult Failure(string error) => new() { Success = false, Error = error };

    private static ToolCallResult Cancelled(ActionProposal proposal)
        => new() { Success = false, Error = $"用户取消了该操作，未执行：{proposal.Description}" };
}
