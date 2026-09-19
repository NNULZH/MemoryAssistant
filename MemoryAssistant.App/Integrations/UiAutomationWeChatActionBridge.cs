using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using MemoryAssistant.Core.Agent.Action;
using MemoryAssistant.Core.Integrations;

namespace MemoryAssistant.App.Integrations;

/// <summary>
/// 微信写操作桥（V3.4）：打开会话 + 发送消息。
/// 微信 4.x 是 Qt 自绘、不暴露控件树，无法用 UIAutomation 点控件，故写操作走
/// 「还原/激活窗口 → OCR 定位控件 → 模拟鼠标/键盘」，并在动作后用 OCR 回验结果（不臆造成功）。
///
/// 关键经验：
/// - 微信 4.x 的 Ctrl+F 打开的是全局「搜一搜」（公众号/文章/网页），**不是本地会话搜索**；
///   本地搜索必须点会话列表顶部的搜索框（本类用 OCR 在窗口里找"搜索"占位文字来定位）。
/// - **绝不用回车"赌首条结果"**：搜索框刚输入完时高亮行通常是"搜索网络结果"的联网建议，
///   一按回车就掉进"联网搜索结果"页（用户看得见的那种失控）。找不到明确的本地会话行就收手、并退出搜索。
/// - 全局搜索覆盖层不一定吞掉左侧会话列表，所以判据要"否定式 + 正向"两条一起看（见 LooksLikeGlobalSearch）。
/// - 微信窗口的布局会随版本/缩放变化，**不能用硬编码坐标**，一律用 OCR 定位。
/// - 模拟键盘输入（SendInput Unicode）微信是接受的；但用 SendMessage 直接投递 WM_CHAR/WM_PASTE 会卡死微信。
///
/// 本类不做人工确认：确认由 ActionSkill 的 IActionConfirmation 强制，调用方勿绕过。
/// </summary>
public sealed class UiAutomationWeChatActionBridge(Action<string>? log = null) : IWeChatActionBridge
{
    private const int SW_RESTORE = 9;

    /// <summary>
    /// "输入被吞"之后等多久再重投一次。依据：这种坏态**会自己恢复**（两次实测观测都是 2~3 分钟内），
    /// 而所有"动窗口"的自愈手段（最小化→还原 / 置前 / 补点一次 / 移窗口）都验证无效、还有副作用
    /// （见本文件那段"关于注入按键被整个吞掉"的实测记录）。所以这里**只等，不碰窗口状态**。
    /// </summary>
    private const int InputSwallowedRetryDelayMs = 30000;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_A = 0x41;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_DELETE = 0x2E;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private readonly Action<string>? _log = log;

    /// <summary>
    /// 最近一次**通过标题回验**打开的会话名，供发送前复核用（"发错人"的最后一道闸）。
    /// 用完即清（一次性）：这样"不指定对象、直接回复当前会话"那条路不会被陈旧状态误伤。
    /// </summary>
    private string? _openedChatName;

    public bool IsAvailable => LocateHandle(out _) != IntPtr.Zero;

    public Task<ActionResult> OpenChatAsync(
        string person, ChatTargetKind kind = ChatTargetKind.Auto, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var hwnd = LocateHandle(out var error);
            if (hwnd == IntPtr.Zero)
                return new ActionResult { Success = false, Error = error };

            if (!EnsureReadyWithRetry(hwnd, out error))
                return new ActionResult { Success = false, Error = error };
            ct.ThrowIfCancellationRequested();

            var (ok, _, boxes, ocrError) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
            if (!ok)
                return new ActionResult { Success = false, Error = $"无法读取微信窗口：{ocrError}" };
            if (!GetWindowRect(hwnd, out var rect))
                return new ActionResult { Success = false, Error = "无法获取微信窗口位置" };
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;

            // 定位并点击会话列表顶部的本地搜索框（先定位，下面判"搜一搜整页"要靠它）
            var searchBox = FindSearchBox(boxes, w, h, LeftColumnMaxX(hwnd, w));
            if (searchBox is null)
                return new ActionResult
                {
                    Success = false,
                    Error = "未能在微信窗口中找到会话搜索框（请确认微信停留在「聊天」页）",
                };

            int leftMaxX = (int)LeftColumnMaxX(hwnd, w);

            // 微信 4.x 的全局搜索（搜一搜）可能以整页覆盖层占住主窗。**这时一律不代按 Esc**：
            // Esc 在普通聊天页的含义是"把微信收进托盘"——那是把用户的微信藏起来，属于明显的打扰；
            // 而且**按一次就可能顺势掉进"输入通道被吞"的坏态**（见本文件里那段"关于注入按键被整个吞掉"
            // 的实测记录：这个坏态只能检测、不能治疗）。所以这里只如实失败，让用户自己返回。
            //
            // ⚠ **下拉开着时不算**：下拉里本来就有「搜索网络结果」这个**分区标题**，
            // 实测上一次失败留下的下拉开着时这里会假阳性——把一次本来能成的发送判成"停在搜一搜"，
            // 于是"失败一次之后就再也发不出去"（必须手动清掉搜索框才恢复）。
            if (LooksLikeGlobalSearch(boxes) && !DropdownLooksOpen(boxes, searchBox, leftMaxX))
                return new ActionResult
                {
                    Success = false,
                    Error = "微信当前停在「搜一搜」整页上，请手动点一下微信左上角的返回箭头回到聊天页，再让我重试"
                            + "（我故意不代按 Esc：它在普通聊天页会把微信收进托盘，那是明显的打扰）",
                };

            double scale = DpiScale(hwnd);
            int listLeftX = Math.Max(0, searchBox.X - (int)Math.Round(14 * scale));
            int listTopY = searchBox.Y + searchBox.Height + (int)Math.Round(10 * scale);

            // 点搜索框 → 输入名字 → **确认名字真的进了搜索框**，没进就绝不往下走。
            // 为什么必须验：微信窗口没拿到焦点时（SetForegroundWindow 会被系统拒），
            // 模拟按键会打到别的窗口，搜索框还是空的——这时下拉显示的是「最常使用」**默认列表**，
            // 里面照样有头像行、有"包含：xxx"预览，跟真搜索结果长得一样，照它选必然点错会话（实测踩过）。
            if (!await TypeIntoSearchAsync(hwnd, searchBox, person, leftMaxX, ct))
            {
                // 这里**不按 Esc 收尾**：Esc 在聊天页会把微信收进托盘（把用户的微信藏起来，明显的打扰）。
                // 留在搜索态没关系——下一次会先点搜索框再 Ctrl+A 覆盖，不影响。
                return new ActionResult
                {
                    Success = false,
                    Error = $"名字没能输进微信的搜索框，已放弃，未打开任何会话；本次目标「{person}」。"
                            + $"这是微信输入通道卡住的特征（直投和剪贴板都被吞掉，已等 {InputSwallowedRetryDelayMs / 1000} 秒重投过一次）——"
                            + "**下一步：先调用 wechat_wake_window 唤醒窗口（它会在窗口空白处点一下、抢回焦点），然后重试这次操作**；"
                            + "唤醒后仍不行，稍等一两分钟再试，或重启微信",
                };
            }

            // 挑会话行：**首选判据是文字**（见 FindRowsByNameText），"分区标题 + 像素头像"退为兜底。
            //
            // 为什么把文字提到第一位（实测的教训 + 用户口径）：
            // 微信下拉按「最常使用 / 联系人 / 群聊 / 聊天记录」分区排，同一个词横跨多个分区。
            // **一个对象被搜多了就会跑到「最常使用」区**，而原来的分区判据只认「联系人」「群聊」两个标题，
            // 碰到「最常使用」就掉进"无预览=联系人"这条弱判据；那条判据又依赖**彩色度**找头像行，
            // 一旦这个人的头像是浅色的（彩色度不够）就会**整行漏掉命中行**——实测搜「求一下通项公式」时
            // 漏掉了 y≈226 那一行，兜底挑到群聊标题下的空行，点下去毫无反应，连续 8 次全部失败。
            // 而命中行的文字 OCR 是读得到的，且它和群聊命中长得完全不同：
            //   私聊命中 = 名字本身（「张晓明」「求一下通项公式」）；群聊命中 = 群名 + "包含：xxx" 预览。
            var (ddOk, _, dropdown, _) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
            var (pixels, pw, ph) = OcrScreenReader.CaptureRaw(hwnd);

            var spots = new List<(int X, int Y)>();

            // 预览判定带宽：预览紧贴在命中行名字下面，取 ~45 DIP（实测行高 ~110px@1.75x，
            // 名字→预览 ≈ 25px，行距 ≈ 105px，45 DIP 既够覆盖又不会吃掉下一行的预览）
            int previewBandPx = (int)Math.Round(45 * scale);

            // 0) 文字命中（最强）
            if (ddOk)
                foreach (var hit in FindRowsByNameText(dropdown, person, searchBox, leftMaxX))
                {
                    spots.Add((hit.CenterX, hit.CenterY));
                    _log?.Invoke($"[Action] 按名字文字命中候选行 = 「{Normalize(hit.Text)}」({hit.CenterX},{hit.CenterY})");
                }

            // 0.5) 下拉里的**第一行**（用户口径："搜多了它会变成搜索框下面的第一项"）。
            //      为什么必须有这条：命中行的名字是绿色高亮，OCR 可能**完全读不出那一行**
            //      （实测搜「张晓明」时，y=166~374 之间 OCR 一行都没有），文字判据于是废掉；
            //      这时"搜索框下面第一行"就是唯一稳定的锚点。
            //      实测两个像素检测器各有失效场景：块差分（模式色）给 (188,237) 是对的，
            //      彩色度给 (214,207) 偏了 34px（点在标题与头像之间的空档，点了毫无反应）。
            //      所以这里先用块差分的第一行——它此前只在"前面全都没挑到"时才跑，白白浪费。
            if (pixels is not null)
            {
                var firstRow = FindConversationRows(pixels, pw, ph, listLeftX, listTopY).FirstOrDefault();
                if (firstRow != default)
                {
                    spots.Add(firstRow);
                    _log?.Invoke($"[Action] 下拉第一行（块差分检测）= ({firstRow.X},{firstRow.Y})");
                }
            }

            // 1) 分区标题下方第一行（读得到标题时最准；与上面的候选重复就不再加）
            if (ddOk && pixels is not null
                && FindRowUnderSection(dropdown, searchBox, leftMaxX, kind,
                       pixels, pw, ph, listLeftX, listTopY, previewBandPx, _log) is { } sectionRow
                && !spots.Any(s => Math.Abs(s.Y - sectionRow.Y) <= 20 && Math.Abs(s.X - sectionRow.X) <= 220))
            {
                spots.Add(sectionRow);
            }

            // 兜底一：第一个不含放大镜的行（分区标题没读出来时用；判据比"猜分区"弱，所以排在后面）
            if (spots.Count == 0 && ddOk)
            {
                if (FindFirstRealChatRow(dropdown, searchBox, leftMaxX) is { } realRow)
                {
                    spots.Add((realRow.CenterX, realRow.CenterY));
                    _log?.Invoke($"[Action] 下拉首个非建议行 = 「{Normalize(realRow.Text)}」({realRow.CenterX},{realRow.CenterY})");
                }
            }

            if (spots.Count == 0)
            {
                // 把下拉区（搜索框下方、左栏内）识别到的行原样打出来——出问题时"到底 OCR 看到了什么"
                // 是唯一能解释"为什么没挑到行"的证据，否则只能猜（报错文案也说不清）。
                int dbgBelowY = searchBox.Y + Math.Max(searchBox.Height, 24);
                _log?.Invoke("[Action] 下拉区识别到：" + string.Join(" | ",
                    dropdown.Where(b => b.Y >= dbgBelowY && b.X < leftMaxX)
                            .OrderBy(b => b.Y)
                            .Select(b => $"{Normalize(b.Text)}@{b.X},{b.Y},w{b.Width}")));
                return new ActionResult
                {
                    Success = false,
                    Error = "搜索结果里没识别到会话行（没能从下拉里认出「" + person + "」那一行），已取消，未打开任何会话",
                };
            }

            // 逐个试：点行 → 确认下拉收起（= 真的点中了结果）→ OCR 回验标题；不对就试下一行（最多 3 条）。
            // 打开失败时 ActionSkill 会中止整条指令，绝不会继续发送。
            foreach (var spot in spots.Take(3))
            {
                ClickInWindow(hwnd, spot.X, spot.Y);
                _log?.Invoke($"[Action] 点击会话行 ({spot.X},{spot.Y})");
                Thread.Sleep(850);

                // **先确认下拉收起来了**：点中搜索结果微信一定会收起下拉；下拉还在 = 这一下没点中任何结果。
                // 为什么必须在标题回验之前确认：要搜的那个人**本来就是当前打开的会话**时，
                // 标题区一直就是他的名字——点空了标题回验也照样"通过"，
                // 上层于是以为打开了会话，接着往没打开的下拉里打字+回车，直接把微信卡住
                // （用户肉眼看到的就是"光标根本没碰到那一行"）。
                if (await DropdownStillOpenAsync(hwnd, searchBox, leftMaxX, ct))
                {
                    _log?.Invoke("[Action] 下拉还开着 → 这一下没点中搜索结果，试下一行");
                    continue;
                }

                if (await TitleMatchesAsync(hwnd, person, ct))
                {
                    _openedChatName = person;      // 记下来，发送前还要复核一次（见 SendMessageAsync）
                    return new ActionResult { Success = true, Detail = $"已通过会话搜索打开「{person}」" };
                }

                _log?.Invoke($"[Action] 这一行不是「{person}」，试下一行");
            }

            return new ActionResult
            {
                Success = false,
                Error = $"点了候选会话行都没能打开「{person}」，已放弃（未发送任何消息）",
            };
        }, ct);

    /// <summary>
    /// 诊断（维护者核对搜索下拉结构用）：复现"点会话搜索框 → 输入目标名"这一步，
    /// 然后转储**整窗 OCR 行**与**整窗截图**，供肉眼确认"哪些行是联网建议、哪些行是本地会话"。
    /// 只输入文字：不点任何结果、不回车、不发送，结束时**不按 Esc 收起搜索**
    /// （Esc 在普通聊天页会把微信收进托盘，且此后该进程不再接受注入按键，只能重启微信）。
    /// </summary>
    public async Task<(byte[]? Png, IReadOnlyList<string> Lines)> DumpSearchDropdownAsync(
        string person, CancellationToken ct = default)
    {
        return await Task.Run<(byte[]?, IReadOnlyList<string>)>(async () =>
        {
            var hwnd = LocateHandle(out var error);
            if (hwnd == IntPtr.Zero) return (null, [$"未找到微信窗口：{error}"]);
            if (!EnsureReady(hwnd, out error)) return (null, [$"窗口未就绪：{error}"]);
            if (!GetWindowRect(hwnd, out var rect)) return (null, ["无法获取微信窗口位置"]);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;

            var (ok, _, boxes, ocrError) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
            if (!ok) return (null, [$"无法读取微信窗口：{ocrError}"]);
            var searchBox = FindSearchBox(boxes, w, h, LeftColumnMaxX(hwnd, w));
            if (searchBox is null) return (null, ["未找到会话搜索框"]);

            // 停在"搜一搜"整页上就直接转储失败：这里也**不代按 Esc**（见 OpenChatAsync 里的说明）。
            // 同样把"下拉开着"的情况排除掉：下拉里的「搜索网络结果」分区标题会造成假阳性。
            if (LooksLikeGlobalSearch(boxes)
                && !DropdownLooksOpen(boxes, searchBox, (int)LeftColumnMaxX(hwnd, w)))
                return (null, ["微信停在「搜一搜」整页上；请先手动点左上角返回箭头回到聊天页再转储"]);

            int dumpLeftMaxX = (int)LeftColumnMaxX(hwnd, w);
            bool landed = await TypeIntoSearchAsync(hwnd, searchBox, person, dumpLeftMaxX, ct);
            Thread.Sleep(600);                       // 等下拉渲染稳定再抓

            var (allOk, _, all, _) = await OcrScreenReader.ReadBoxedAsync(hwnd, chatPanelOnly: false, ct);
            var png = OcrScreenReader.CapturePng(hwnd);
            var (px, pwid, phei) = OcrScreenReader.CaptureRaw(hwnd);

            var lines = new List<string>
            {
                $"窗口 {w}x{h} | DPI 缩放 {DpiScale(hwnd):0.00} | 左栏上限={LeftColumnMaxX(hwnd, w):0}",
                $"搜索框判定: x={searchBox.X} y={searchBox.Y} w={searchBox.Width} | {searchBox.Text}",
                $"搜索是否真的生效: {landed}（false 说明名字没打进搜索框，下面这些行不是搜索结果）",
            };
            if (!allOk) lines.Add("整窗 OCR 失败");
            else foreach (var b in all.OrderBy(b => b.Y).ThenBy(b => b.X))
                lines.Add($"y={b.Y,5} x={b.X,5} w={b.Width,4} h={b.Height,3} | {b.Text}");

            // 会话行候选（与 OpenChatAsync 用的是同一套判断）：直接看它认出了哪几行
            if (px is not null)
            {
                double scale = DpiScale(hwnd);
                int leftX = Math.Max(0, searchBox.X - (int)Math.Round(14 * scale));
                int topY = searchBox.Y + searchBox.Height + (int)Math.Round(10 * scale);
                var rows = FindConversationRows(px, pwid, phei, leftX, topY);
                lines.Add($"[会话行候选] 头像列=[{leftX},{leftX + 120}] 起点={topY} → {rows.Count} 个："
                          + string.Join("  ", rows.Select(r => $"({r.X},{r.Y})")));
                lines.Add($"[彩色度头像行] 起点={topY} → "
                          + string.Join("  ", FindDropdownAvatarRows(px, pwid, phei, leftX, topY, topY + 900)
                                .Select(r => $"({r.X},{r.Y})")));

                // 三个 kind 各会点到哪一行 —— 校准选行逻辑的唯一手段：
                // 真点下去就发消息了，不能拿线上试。
                var (lpOk, _, leftPanel, _) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
                if (!lpOk) lines.Add("[选行] 左栏 OCR 失败，无法预演选行");
                else foreach (var kind in new[] { ChatTargetKind.Contact, ChatTargetKind.Group, ChatTargetKind.Auto })
                {
                    var pick = FindRowUnderSection(leftPanel, searchBox, (int)LeftColumnMaxX(hwnd, w),
                        kind, px, pwid, phei, leftX, topY, (int)Math.Round(45 * scale),
                        s => lines.Add("  [判据] " + s));
                    lines.Add($"[选行] kind={kind} → {(pick is null ? "没找到" : $"({pick.Value.X},{pick.Value.Y})")}");
                }
            }

            // 不按 Esc 收尾（会把微信收进托盘并废掉输入），把搜索态留着即可

            return (png, lines);
        }, ct);
    }

    public async Task<ActionResult> SendMessageAsync(string text, CancellationToken ct = default)
    {
        // 硬校验：回复内容不能含换行——微信里回车即发送，含换行会把后半句提前发出去
        if (text.Contains('\n') || text.Contains('\r'))
            return new ActionResult
            {
                Success = false,
                Error = "回复内容不能包含换行符（回车会被微信当作「发送」，导致消息被截断发出）",
            };

        var hwnd = LocateHandle(out var error);
        if (hwnd == IntPtr.Zero)
            return new ActionResult { Success = false, Error = error };
        if (!EnsureReadyWithRetry(hwnd, out error))
            return new ActionResult { Success = false, Error = error };

        // **发送前复核**：当前打开的会话必须还是刚验证过的那个目标。
        // 为什么必须复核：OpenChatAsync 本质是"在下拉里挑一行点进去"，判据再严也可能点偏；
        // 而消息一旦回车发出去就收不回来了——文档验收里"绝不能出现错误会话发送"就靠这一关。
        // 只在"本次流程刚打开并回验过会话"时复核；用完即清，"直接回复当前会话"那条路不受影响。
        if (_openedChatName is { } expectedChat)
        {
            _openedChatName = null;
            if (!await TitleMatchesAsync(hwnd, expectedChat, ct))
            {
                _log?.Invoke($"[Action] 发送前复核未通过：当前会话不是「{expectedChat}」");
                return new ActionResult
                {
                    Success = false,
                    Error = $"当前打开的会话不是「{expectedChat}」（发送前复核未通过），已中止，未输入任何内容",
                };
            }
        }

        // **先点消息输入框**：经搜索打开会话后，键盘焦点通常还在搜索框/会话列表上，不在消息输入框里；
        // 不点就直接打字，字会掉到别处——实测现象正是"会话打开了、看着也在输入框上、一个字没进去"。
        // 同时**先把窗口拉回前台**：失焦时按键会打到别的窗口，等于把消息打进了别人的输入框。
        if (GetForegroundWindow() != hwnd)
        {
            _log?.Invoke("[Action] 微信不在前台，先把窗口拉回来再打字");
            ForceForeground(hwnd, out _);
        }
        ForceKeyboardFocus(hwnd);

        var (foundInput, inputSpot) = await LocateMessageInputAsync(hwnd, ct);
        if (foundInput)
        {
            ClickInWindow(hwnd, inputSpot.X, inputSpot.Y);
            _log?.Invoke($"[Action] 点击消息输入框 ({inputSpot.X},{inputSpot.Y})");
            await Task.Delay(Pacing.AfterClickMs, ct);      // 步进节奏：等焦点落到输入框
            ForceKeyboardFocus(hwnd);       // 点完再钉一次焦点，确保按键落到输入框
        }
        else
        {
            _log?.Invoke("[Action] 没能定位消息输入框，按「上一步已聚焦」继续尝试");
        }

        // 打字前的两条基线：输入框那一条带 + 聊天历史那一段。
        // 后面要靠"这两段有没有变"来判断字有没有进去、消息有没有发出去。
        var beforeTyping = await ReadChatLinesAsync(hwnd, ct);
        string inputBefore = BandText(beforeTyping, hwnd, above: false);
        string historyBefore = BandText(beforeTyping, hwnd, above: true);

        // 判据用"输入框占位提示"（"…语音输入文字"）而不是逐字读我们打的字：
        // 占位提示是空框才有的固定文案，OCR 认得稳；而用户消息字号小、易误识（实测"测试"会被读成"氵贝試"）。
        SendKeyCombo(VK_CONTROL, VK_A);   // 清空草稿，避免把旧内容发出去
        SendKey(VK_DELETE);
        await Task.Delay(380, ct);
        await Task.Delay(Pacing.BeforeTypeMs, ct);   // 步进节奏：清空后先停一下再打

        SendText(text);
        await Task.Delay(600, ct);                   // 打字后多给一点时间让输入框渲染

        var afterTyping = await ReadChatLinesAsync(hwnd, ct);
        bool stillEmpty = HasInputPlaceholder(afterTyping);
        bool textVisible = FuzzyHits(afterTyping, text) is not null;
        string inputAfter = BandText(afterTyping, hwnd, above: false);
        bool inputChanged = !string.Equals(inputBefore, inputAfter, StringComparison.Ordinal);
        _log?.Invoke($"[Action] 打字后：输入框带变化={inputChanged} 占位提示={stillEmpty} 读到正文={textVisible}"
                     + $"（输入框带长度 {inputBefore.Length}→{inputAfter.Length}）");

        // 有"字进去了"的迹象（读到正文 / 输入框那条带变了）就不要再重打，免得把已打好的内容打两遍；
        // 没迹象就换剪贴板粘贴重试一次——粘贴走 WM_PASTE，和 SendInput 直投是两条完全不同的路径。
        if (textVisible || inputChanged)
        {
            _log?.Invoke("[Action] 文字看着已经进去了，不再重打");
        }
        else
        {
            _log?.Invoke("[Action] 文字看着没进去，改用剪贴板粘贴重打一次");
            ForceForeground(hwnd, out _);
            ForceKeyboardFocus(hwnd);
            SendKeyCombo(VK_CONTROL, VK_A);
            SendKey(VK_DELETE);
            await Task.Delay(380, ct);
            PasteText(text);
            await Task.Delay(500, ct);
            afterTyping = await ReadChatLinesAsync(hwnd, ct);
            stillEmpty = HasInputPlaceholder(afterTyping);
            textVisible = FuzzyHits(afterTyping, text) is not null;
            inputAfter = BandText(afterTyping, hwnd, above: false);
            inputChanged = !string.Equals(inputBefore, inputAfter, StringComparison.Ordinal);
            _log?.Invoke($"[Action] 粘贴后：输入框带变化={inputChanged} 占位提示={stillEmpty} 读到正文={textVisible}"
                         + $"（输入框带长度 {inputBefore.Length}→{inputAfter.Length}）");
        }

        // 仍然没有任何"字进去了"的证据 → **绝不回车**。
        // 盲按回车最容易产生"以为发了、其实什么都没发"（这正是实测踩到的那个假成功）。
        if (!textVisible && !inputChanged)
        {
            _log?.Invoke("[Action] 输入框没有任何变化；当前聊天区识别到：" +
                string.Join(" | ", afterTyping.Take(20).Select(b => $"{b.Text}@{b.X},{b.Y}")));
            return new ActionResult
            {
                Success = false,
                Error = $"文字没有进入消息输入框（屏幕上看不到「{text}」，输入框也没有任何变化），已中止发送。"
                        + "直投和剪贴板两条输入路径都被吞掉了——这是微信输入通道卡住的特征。"
                        + "**下一步：先调用 wechat_wake_window 唤醒窗口，然后重试这次发送**"
                        + "（人手遇到同样情况就是在窗口空白处点一下；唤醒工具会自己做这件事）；"
                        + "若唤醒后仍失败，稍等一两分钟再试，或重启微信",
            };
        }

        await Task.Delay(Pacing.BeforeEnterMs, ct);   // 步进节奏：回车前留出渲染时间，别抢拍
        SendKey(VK_RETURN);           // 微信默认回车即发送
        await Task.Delay(Pacing.AfterEnterMs, ct);

        // 回验（双判据）：① 我们打的那句出现在**聊天历史**里；② 消息输入框回到"空"（占位提示回来了）。
        // 为什么必须两条都满足（此前只要一条成立就报成功，实测踩过假成功）：
        //   · 只看①：重复发送同样的内容时会命中上一条旧气泡，看不出这一条到底发出去没有；
        //   · 只看②："压根没打进去"的空框也是空的，回车白按，却会被算成发送成功；
        //   · 只看"聊天区变了"更危险：别人恰好发来一条新消息也会让它变 → 报个假成功。
        var afterSend = await ReadChatLinesAsync(hwnd, ct);
        var hit = FuzzyHits(afterSend, text);
        // 注意：OCR 行是**窗口内相对坐标**，所以这里只用窗口高度算分界，别再叠加 rc.Top（屏幕坐标）
        int inputTopY = GetWindowRect(hwnd, out var rc) ? (int)((rc.Bottom - rc.Top) * 0.78) : int.MaxValue;
        bool inHistory = hit is { } h2 && h2.Y < inputTopY;
        string historyAfter = BandText(afterSend, hwnd, above: true);
        bool historyChanged = !string.Equals(historyBefore, historyAfter, StringComparison.Ordinal);
        bool inputCleared = HasInputPlaceholder(afterSend);   // 空框才有"…语音输入文字"占位提示
        _log?.Invoke($"[Action] 回车后回验：历史里有该消息={inHistory} 聊天区有变化={historyChanged} 输入框已清空={inputCleared}");

        if (inHistory && inputCleared)
            return new ActionResult
            {
                Success = true,
                Detail = "已发送，并已在屏幕上确认到该消息（聊天历史里有它、输入框已清空）",
            };

        // 只拿到半条证据 → **不算成功，也绝不自动重发**：它很可能已经发出去了，
        // 再发一次对方就会收到两条——这比"报一次未确认"糟得多。
        if (inHistory != inputCleared)
        {
            _log?.Invoke("[Action] 回车后只拿到半条证据 → 判定「无法确认」，不重发");
            return new ActionResult
            {
                Success = false,
                Error = WriteIntentGuard.IndeterminateMarker
                        + $"：聊天历史里{(inHistory ? "看到了" : "没看到")}这句，输入框{(inputCleared ? "已清空" : "没清空")}"
                        + $"（聊天区有变化={historyChanged}）。**不要重发**——它可能已经发出去了："
                        + "请到微信里看一眼这句「" + text + "」在不在，再决定要不要让我重发",
            };
        }

        _log?.Invoke("[Action] 回车后聊天区没有变化 → 判定未发出；聊天区识别到：" +
            string.Join(" | ", afterSend.Take(20).Select(b => $"{b.Text}@{b.X},{b.Y}")));
        return new ActionResult
        {
            Success = false,
            Error = "回车后聊天区没有任何变化，消息没有发出去（多半是根本没打进输入框）",
        };
    }

    /// <summary>
    /// 定位消息输入框：优先用占位提示"…语音输入文字"的位置；
    /// 读不到占位提示（有些版本/主题就是没有）就退到"聊天面板底部、偏左"的估算点——
    /// 输入框横跨整个聊天面板，点这一带必中，同时避开发送按钮和表情/文件那一排按钮。
    /// </summary>
    private static async Task<(bool Ok, (int X, int Y) Spot)> LocateMessageInputAsync(
        IntPtr hwnd, CancellationToken ct)
    {
        if (!GetWindowRect(hwnd, out var rect)) return (false, (0, 0));
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;

        var (ok, _, boxes, _) = await OcrScreenReader.ReadBoxedAsync(hwnd, chatPanelOnly: true, ct);
        if (ok)
        {
            var ph = boxes
                .Where(b => b.X > 0 && b.Y > h * 0.5 && b.Width > 0)
                .FirstOrDefault(b => Normalize(b.Text).Contains("输入文字", StringComparison.Ordinal)
                                     || Normalize(b.Text).Contains("语音输入", StringComparison.Ordinal));
            if (ph is not null) return (true, (ph.X + ph.Width / 2, ph.Y + ph.Height / 2));
        }

        int fallbackY = h - (int)Math.Round(96 * DpiScale(hwnd));
        return (true, ((int)(w * 0.55) + 220, Math.Max(0, fallbackY)));
    }

    /// <summary>窗口内某一条带的 OCR 文本（above=true 取历史区，false 取底部输入区）。用来比对"变没变"。</summary>
    private static string BandText(IReadOnlyList<OcrScreenReader.OcrLineBox> lines, IntPtr hwnd, bool above)
    {
        if (!GetWindowRect(hwnd, out var r)) return "";
        int split = (int)((r.Bottom - r.Top) * 0.78);
        return string.Join("|", lines
            .Where(b => b.X >= 0 && (above ? b.Y < split : b.Y >= split))
            .OrderBy(b => b.Y).ThenBy(b => b.X)
            .Select(b => Normalize(b.Text)));
    }

    /// <summary>聊天区（底部）是否显示输入框占位提示——即输入框为空。</summary>
    private static bool HasInputPlaceholder(IReadOnlyList<OcrScreenReader.OcrLineBox> lines)
        => lines.Any(b => b.X >= 0 && b.Y > 0 && b.Text.Length > 0
            && (Normalize(b.Text).Contains("输入文字", StringComparison.Ordinal)
                || Normalize(b.Text).Contains("语音输入", StringComparison.Ordinal)));

    /// <summary>
    /// 在 OCR 行里模糊定位目标文字（容忍误识与换行）：命中率最高的一行的位置与得分。
    /// 用"最长公共子串占比"判定，OCR 把"测试"读成"氵贝"这类误识仍可命中。
    /// </summary>
    private static (int X, int Y, double Score)? FuzzyHits(IReadOnlyList<OcrScreenReader.OcrLineBox> lines, string text)
    {
        var target = Fingerprint(text);
        if (target.Length == 0) return null;

        (int X, int Y, double Score)? best = null;
        foreach (var b in lines.Where(x => x.X >= 0))
        {
            var candidate = Fingerprint(b.Text);
            if (candidate.Length == 0) continue;
            var score = LongestCommonSubstring(candidate, target) / (double)target.Length;
            if (score < 0.6) continue;
            if (best is null || score > best.Value.Score) best = (b.X, b.Y, score);
        }
        return best;
    }

    private static int LongestCommonSubstring(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        int best = 0;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                if (cur[j] > best) best = cur[j];
            }
            (prev, cur) = (cur, prev);
        }
        return best;
    }

    /// <summary>读取当前聊天区（含消息输入框）识别到的文本行。</summary>
    private static async Task<List<OcrScreenReader.OcrLineBox>> ReadChatLinesAsync(IntPtr hwnd, CancellationToken ct)
    {
        var (ok, _, boxes, _) = await OcrScreenReader.ReadBoxedAsync(hwnd, chatPanelOnly: true, ct);
        return ok ? boxes.Where(b => b.X >= 0).ToList() : [];
    }

    /// <summary>
    /// 用**文字**在下拉里找"名字就是搜索词"的候选行。
    ///
    /// 为什么这条最可靠：私聊命中的文字**就是名字本身**（「张晓明」「求一下通项公式」），
    /// 而群聊命中长成「群名 + 包含：xxx」，联网建议长成「🔍 搜索词 + 后缀」——
    /// 后两者分别靠"包含："和收尾分区字样排除。
    ///
    /// 过滤规则：
    /// - 只在搜索框下方、左栏内找（搜索框里也有同一个词，右侧聊天区还有标题和消息，都必须排除）；
    /// - 宽度 ≥ 60：排除左侧导航/头像/角标被 OCR 读成的孤独 "O"、"0"（宽度只有 16~36px）；
    /// - 排除带"包含："的群聊预览行、以及含「聊天记录/搜索网络结果/搜一搜」这些收尾分区字样的行
    ///   （那里是聊天记录命中与联网建议，点下去会跳到搜索页）；
    /// - 落在收尾分区标题**之下**的行也一并排除。
    ///
    /// ⚠ **长度只用来排序、不用来过滤**：命中行常被 OCR 在前后带上杂字符（实测「、”求一下通项公式」），
    /// 一旦把"最多多几个字"写成过滤条件，裁剪 OCR 稍微多带一个字符就整条判据失效（踩过）。
    /// 排序用"与目标名的长度差"，完全等于目标名的排最前，最多取 3 条交给调用方逐个点 + 标题回验。
    /// </summary>
    private static List<OcrScreenReader.OcrLineBox> FindRowsByNameText(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, string person,
        OcrScreenReader.OcrLineBox searchBox, int leftMaxX)
    {
        var target = Normalize(person);
        if (target.Length == 0) return [];
        int belowY = searchBox.Y + Math.Max(searchBox.Height, 24);

        // 收尾分区（搜索网络结果 / 聊天记录 / 搜一搜…）的起点，往下的行一律不当候选
        int stopY = boxes
            .Where(b => b.Y >= belowY && b.X < leftMaxX && b.Width >= 60)
            .Where(b => DropdownStopHeaders.Any(h => Normalize(b.Text).Contains(h, StringComparison.Ordinal)))
            .Select(b => b.Y)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        return boxes
            .Where(b => b.Y >= belowY && b.Y < stopY && b.X < leftMaxX && b.Width >= 60)
            .Select(b => (Box: b, Text: Normalize(b.Text)))
            .Where(x => x.Text.Contains(target, StringComparison.Ordinal)
                        && !x.Text.Contains("包含：", StringComparison.Ordinal)
                        && !DropdownStopHeaders.Any(h => x.Text.Contains(h, StringComparison.Ordinal)))
            .OrderBy(x => x.Text.Length - target.Length)           // 越接近目标名越靠前（完全相等排最前）
            .ThenBy(x => x.Box.Y)
            .Select(x => x.Box)
            .Take(3)
            .ToList();
    }

    /// <summary>下拉里的"分组标题"：跨过它们继续往下找（截图实测的分区顺序：最常使用 → 联系人 → 群聊）。</summary>
    private static readonly string[] DropdownSectionHeaders = ["最常使用", "功能", "联系人", "群聊", "聊天记录"];

    /// <summary>看到这些标题就说明"会话区"已经结束，后面全是建议/聊天记录命中，不必再找。</summary>
    private static readonly string[] DropdownStopHeaders = ["聊天记录", "搜索网络结果", "搜一搜", "网络结果", "搜索指定内容"];

    /// <summary>
    /// 取"指定分区标题下方的第一个可点行"（V4.7，用户实测判据）。
    ///
    /// 为什么必须"OCR 找标题 + 像素找行"两件事拼起来：
    /// 微信把**命中项**的名字渲染成绿色高亮，OCR 实测完全读不出那一行——
    /// 截图核对：搜"张晓明"时「联系人」标题下面第一行就是张晓明，可 OCR 行里根本没有它；
    /// 而分区标题是灰色小字，反而读得到。于是用标题定位"从哪儿开始"、用头像像素块定位"哪一行"。
    /// </summary>
    private static (int X, int Y)? FindRowUnderSection(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes,
        OcrScreenReader.OcrLineBox searchBox,
        int leftMaxX,
        ChatTargetKind kind,
        byte[] pixels, int imgW, int imgH,
        int listLeftX, int listTopY, int previewBandPx,
        Action<string>? log)
    {
        var rows = FindDropdownAvatarRows(pixels, imgW, imgH, listLeftX, listTopY, listTopY + 900);
        if (rows.Count == 0)
        {
            log?.Invoke("[Action] 下拉里没扫到任何头像行");
            return null;
        }

        bool wantContact = kind is ChatTargetKind.Contact or ChatTargetKind.Auto;
        bool wantGroup = kind is ChatTargetKind.Group or ChatTargetKind.Auto;

        // 1) 「联系人」分区标题读得到就用它——标题是明说的，最准
        if (wantContact && FindSectionHeader(boxes, "联系人", searchBox, leftMaxX) is { } ch
            && FirstRowBelow(rows, ch.Y + ch.Height) is { } cr)
        {
            log?.Invoke($"[Action] 分区「联系人」(y={ch.Y}) 下方第一个可点行 = ({cr.X},{cr.Y})");
            return cr;
        }

        // 2) 交叉判据（不依赖标题）：命中行**没有**"包含：xxx"预览 = 联系人。
        //    群聊/最常使用的命中都会带那行预览（"群里有人的消息提到过她"），联系人命中只有名字。
        //    必须排在「群聊」标题之前：只读到「群聊」标题时，按标题走会把群聊第一行当成联系人（实测踩过）。
        if (wantContact && FirstRowWithoutPreview(rows, boxes, previewBandPx, leftMaxX) is { } byPreview)
        {
            log?.Invoke($"[Action] 「联系人」标题没读到，按「无预览=联系人」取 = ({byPreview.X},{byPreview.Y})");
            return byPreview;
        }

        // 3) 「群聊」分区标题读得到
        if (wantGroup && FindSectionHeader(boxes, "群聊", searchBox, leftMaxX) is { } gh
            && FirstRowBelow(rows, gh.Y + gh.Height) is { } gr)
        {
            log?.Invoke($"[Action] 分区「群聊」(y={gh.Y}) 下方第一个可点行 = ({gr.X},{gr.Y})");
            return gr;
        }

        // 4) 群聊兜底：第一个"带预览"的行（群聊命中都带"包含：xxx"）
        if (wantGroup && FirstRowWithPreview(rows, boxes, previewBandPx, leftMaxX) is { } g3)
        {
            log?.Invoke($"[Action] 按「带预览=群聊」取 = ({g3.X},{g3.Y})");
            return g3;
        }

        // 5) 群聊末位兜底：联系人那一行的下一行（「联系人」分区里只放最佳匹配的那一个）
        if (wantGroup && FirstRowWithoutPreview(rows, boxes, previewBandPx, leftMaxX) is { } c2
            && FirstRowBelow(rows, c2.Y) is { } g2)
        {
            log?.Invoke($"[Action] 按结构兜底：联系人行({c2.Y}) 的下一行 = ({g2.X},{g2.Y})");
            return g2;
        }

        log?.Invoke("[Action] 没读到任何分区标题，交叉判据也没命中");
        return null;
    }

    /// <summary>
    /// 命中行是不是带"包含：xxx"预览。微信把**群聊 / 最常使用**的命中原因写成一行预览
    /// （"包含：张晓明"=群里有人的消息提到过她），而**联系人**命中只有名字、没有这行。
    /// 这是不依赖分区标题的交叉判据——「联系人」「群聊」那两行小灰字 OCR 时读得到时读不到。
    ///
    /// 只在这一行的**下方一小段**里找（<paramref name="bandPx"/>）：预览紧跟在名字下面，
    /// 而拿"到下一行为止"当范围会把**下一行的预览**算到这一行头上——
    /// 实测正因如此，把"张晓明"判成了"有预览"（其实是下面"四个人"那行的），于是跳过了正确的行。
    /// </summary>
    private static bool HasMatchPreview(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, (int X, int Y) row, int bandPx, int leftMaxX)
        => boxes.Any(b => b.X < leftMaxX && b.Y >= row.Y && b.Y < row.Y + bandPx
                          && Normalize(b.Text).Contains("包含：", StringComparison.Ordinal));

    /// <summary>第一个"没有命中预览"的头像行 = 联系人。</summary>
    private static (int X, int Y)? FirstRowWithoutPreview(
        List<(int X, int Y)> rows, IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, int bandPx, int leftMaxX)
    {
        foreach (var r in rows)
            if (!HasMatchPreview(boxes, r, bandPx, leftMaxX)) return r;
        return null;
    }

    /// <summary>第一个"带命中预览"的头像行 = 群聊 / 最常使用的命中。</summary>
    private static (int X, int Y)? FirstRowWithPreview(
        List<(int X, int Y)> rows, IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, int bandPx, int leftMaxX)
    {
        foreach (var r in rows)
            if (HasMatchPreview(boxes, r, bandPx, leftMaxX)) return r;
        return null;
    }

    /// <summary>头像行里第一个"整体落在 y 之下"的行。</summary>
    private static (int X, int Y)? FirstRowBelow(List<(int X, int Y)> rows, int y)
    {
        foreach (var r in rows)
            if (r.Y > y) return r;
        return null;
    }

    /// <summary>搜索下拉是否还开着——等价于"刚才那一下没点中任何搜索结果"。</summary>
    private async Task<bool> DropdownStillOpenAsync(
        IntPtr hwnd, OcrScreenReader.OcrLineBox searchBox, int leftMaxX, CancellationToken ct)
    {
        var (ok, _, boxes, _) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
        // 读不到就按"还开着"处理：宁可多重试一行，也不要误判成"打开了会话"
        if (!ok)
        {
            _log?.Invoke("[Action] 左栏读不到，按「下拉还开着」处理");
            return true;
        }
        if (!DropdownLooksOpen(boxes, searchBox, leftMaxX)) return false;

        // 这个判据出现过假阳性（下拉其实已经收起，却判成还开着 → 白白跳过一行正确的结果），
        // 所以把"它到底看到了什么"原样打出来，别再靠猜。
        int belowY = searchBox.Y + Math.Max(searchBox.Height, 24);
        _log?.Invoke("[Action] 判定「下拉还开着」，此时左栏识别到：" + string.Join(" | ",
            boxes.Where(b => b.Y >= belowY && b.X < leftMaxX)
                  .OrderBy(b => b.Y)
                  .Select(b => $"{Normalize(b.Text)}@{b.X},{b.Y},w{b.Width}")));
        return true;
    }

    // ===== 关于"注入按键被整个吞掉"这个坏态：本轮专门查过，结论写在这里，避免以后再走一遍弯路 =====
    //
    // 现象（实测抓到过 4 次）：窗口在前台，**Win32 焦点栈完全正常**——GetGUIThreadInfo 的 hwndFocus
    // 就是微信主窗、GetFocus() 也是它、没有任何卡住的修饰键、键盘布局没变；但注入的按键被整个吞掉：
    // 搜索框一直停在占位提示"搜索"，下拉面板却还开着，连剪贴板 Ctrl+V 都进不去。
    // **所以它没法用 Win32 焦点检测出来，只能用"打字 + 回读"探针判定**（本类各输入路径本来就是这么验的）。
    //
    // 排查结论：
    // 1) 文档里"Esc 把微信收进托盘 → 此后按键全被吞"这条**复现不出来**：Esc → 主窗 MainWindowHandle=0
    //    （确实进了托盘）→ SW_RESTORE 还原后，焦点栈与输入探针都正常。
    // 2) 也**不是**这些引起的：AttachThreadInput+SetFocus 连做 6 次、强杀前台运行的本程序、
    //    托盘/最小化来回切、"清空搜索框(Ctrl+A+Delete)"连做 14 次——全部探针 ALIVE。
    // 3) 它**会自己恢复**：两次观测中，坏掉之后 2~3 分钟内输入就恢复正常（不重启微信）。
    // 4) 试过 4 种"进程内自愈"（最小化→还原 / 等位置稳定 / 补一次预热点击 / 轻推窗口几何），
    //    **全部无效**——所以本类**不做**自愈动作，改为如实失败并让用户稍等重试（见 SendMessageAsync 的报错文案）。
    //    顺带记一笔：最小化→还原之后会短暂进入一个相似状态（光标在搜索框里但按键不进去），
    //    所以"失败之后顺手做点窗口动作"这种做法是危险的，别加。
    //
    // 也就是说：这个坏态目前**只能检测、不能治疗**。别再花时间找触发器了（本轮 30+ 次尝试都是 0 复现）。
    // ================================================================================================

    /// <summary>搜索这一步真的生效了没：下拉特征 + 搜索框里确实出现了目标名字，两条都要。</summary>
    private static bool SearchLanded(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, OcrScreenReader.OcrLineBox searchBox,
        int leftMaxX, string person)
        => DropdownLooksOpen(boxes, searchBox, leftMaxX) && SearchBoxShows(boxes, searchBox, person);

    /// <summary>搜索框里是不是真的出现了目标名字（而不是只剩"搜索"占位提示）。</summary>
    private static bool SearchBoxShows(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, OcrScreenReader.OcrLineBox searchBox, string person)
        => boxes.Any(b => b.Y >= searchBox.Y - 30 && b.Y <= searchBox.Y + searchBox.Height + 30
                          && b.X >= searchBox.X - 60 && b.X <= searchBox.X + 360
                          && NameSimilar(b.Text, person));

    /// <summary>
    /// 点搜索框 → 输入名字 → 确认名字真的进去了；没进去就换个位置再点一次重试。
    /// 第一次点开下拉时窗口可能还没被激活（SetForegroundWindow 会被系统拒绝），
    /// 模拟按键就打到了别的窗口；而"用鼠标点一下"本身能激活窗口，所以重试大概率能成。
    ///
    /// 四次机会：① Unicode 直投 ② 剪贴板粘贴（另一条完全不同的路径）
    /// ③ **等一等再直投**（针对"输入通道被整个吞掉"的坏态：它会自己恢复，而所有动窗口的自愈都无效，
    /// 所以这一步只等、不碰窗口）④ **点空白回正后再粘贴**（第三阶段补充：等也没救回来时的最后一条路）。
    /// 四次都拿不到"名字进了搜索框"的证据才如实失败。
    /// </summary>
    private async Task<bool> TypeIntoSearchAsync(
        IntPtr hwnd, OcrScreenReader.OcrLineBox searchBox,
        string person, int leftMaxX, CancellationToken ct)
    {
        // 4 次：直投 → 剪贴板 → 等 30 秒再直投 → **点空白回正后**再粘贴。
        // 前三次沿用原有经验（输入通道被吞时"动窗口反而更糟"，所以只等）；
        // 第 4 次是第三阶段补充的"空白点击回正"——人手遇到卡住就是点一下空白，
        // 放在"等也没救回来"之后当最后一条路，不会破坏前三次的稳定性。
        for (int attempt = 0; attempt < 4; attempt++)
        {
            // 第 3 次之前只等一等：不重启微信、不动窗口（动窗口反而更糟，见 InputSwallowedRetryDelayMs 的说明）
            if (attempt == 2)
            {
                _log?.Invoke($"[Action] 两次输入都被吞 → 这是微信输入通道卡住的特征，"
                             + $"等 {InputSwallowedRetryDelayMs / 1000} 秒再重投一次（不重启微信、不动窗口）");
                await Task.Delay(InputSwallowedRetryDelayMs, ct);
            }
            else if (attempt == 3)
            {
                // 等也等了、两条输入路径都被吞 → 最后一招：像人那样在窗口空白处点一下，把卡住的浮层/焦点收回来
                NudgeBlank(hwnd, "输入通道仍被吞 · 点空白回正后再试一次");
            }

            // 每一轮输入前都先确认窗口还在前台：失焦时按键会打到别的窗口，这一轮就是白做的
            if (GetForegroundWindow() != hwnd)
            {
                _log?.Invoke("[Action] 微信不在前台，先把窗口拉回来再输");
                ForceForeground(hwnd, out _);
            }

            // 第一次点文字中心；重试时往框内更靠左点——第一次可能点在了外层容器上
            int dx = attempt == 0 ? searchBox.CenterX : searchBox.X + Math.Max(20, searchBox.Width / 8);
            ClickInWindow(hwnd, dx, searchBox.CenterY);
            _log?.Invoke($"[Action] 点击会话搜索框 ({dx},{searchBox.CenterY})"
                         + (attempt > 0 ? $"（第 {attempt + 1} 次尝试）" : ""));
            Thread.Sleep(180);

            // 等下拉**真的展开**再打字。展开是异步的（动画 + 首帧），它会抢焦点：
            // 打字抢在它前面就被整段吞掉——实测现象正是"窗口是前台、下拉也开着、搜索框里一个字没有"。
            await WaitDropdownSettledAsync(hwnd, searchBox, leftMaxX, ct);

            // 点击让微信自己把光标放进搜索框；这里再把**系统级键盘焦点**钉死给它
            // （实测只置前不钉焦点时，Unicode 直投和剪贴板粘贴都会被吞）
            ForceKeyboardFocus(hwnd);

            SendKeyCombo(VK_CONTROL, VK_A);   // 清空残留
            Thread.Sleep(80);
            // 第一次用 SendInput 的 Unicode 直投；第二次改走剪贴板粘贴——两条完全不同的路径，
            // 直投被 Qt/输入法吞掉时粘贴往往还能进。第三次是"等它自己缓过来"之后再直投；
            // 第四次（点空白回正后）走粘贴。
            if (attempt is 1 or 3) PasteText(person);
            else SendText(person);
            Thread.Sleep(attempt == 1 ? 1000 : 900);
            ct.ThrowIfCancellationRequested();

            var (ok, _, boxes, _) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
            if (ok && SearchLanded(boxes, searchBox, leftMaxX, person)) return true;

            if (ok)
            {
                _log?.Invoke($"[Action] 第 {attempt + 1} 次输入没生效：前台={GetForegroundWindow() == hwnd}"
                             + $" 下拉特征={DropdownLooksOpen(boxes, searchBox, leftMaxX)}"
                             + $" 框内有名字={SearchBoxShows(boxes, searchBox, person)}");
                _log?.Invoke("[Action] 搜索框一带识别到：" + string.Join(" | ",
                    boxes.Where(b => b.Y >= searchBox.Y - 40 && b.Y <= searchBox.Y + 90 && b.X < leftMaxX)
                         .OrderBy(b => b.Y)
                         .Select(b => $"{b.Text}@{b.X},{b.Y}")));
            }
            else _log?.Invoke($"[Action] 第 {attempt + 1} 次输入后读不到左栏");
        }
        return false;
    }

    /// <summary>
    /// 搜索下拉是不是真的打开了——只看"下拉特有的东西"：
    /// 分区标题 / "🔍 关键词"建议行 / "包含：xxx"命中预览 / "查看全部(N)"。
    ///
    /// 不能拿"有没有会话行"当判据：搜索框没点中时，左边那片就是普通会话列表，
    /// 它的行和下拉里的行在 OCR 看来一模一样（实测因此点开了别的会话）。
    /// </summary>
    private static bool DropdownLooksOpen(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, OcrScreenReader.OcrLineBox searchBox, int leftMaxX)
    {
        int belowY = searchBox.Y + Math.Max(searchBox.Height, 24);
        foreach (var b in boxes)
        {
            if (b.Y < belowY || b.X >= leftMaxX) continue;
            var t = Normalize(b.Text);
            if (t.Length == 0) continue;
            // 分区标题是**下拉独有**的信号（容忍 1 个 OCR 错字）。
            // ⚠ 绝不能拿"带放大镜的行"当信号：微信会把会话头像和名字合并读成 "0 四个人" 这种样子，
            // 与"🔍 关键词"建议行在文本上无法区分——实测因此把"下拉已收起"误判成"还开着"，
            // 白白跳过一次正确点击（连续 5 次打开张晓明全部因此失败）。
            if (DropdownSectionHeaders.Any(h => LooksLikeHeader(t, h))) return true;
            if (t.StartsWith("查看全部", StringComparison.Ordinal)) return true;
            if (t.Contains("包含：", StringComparison.Ordinal)) return true;   // 命中预览只在下拉里出现
        }
        return false;
    }

    /// <summary>
    /// 某个 OCR 行看起来像不像这个分区标题。容忍 1 个错字（或一个多余的角标字符）：
    /// OCR 把「最常使用」读成「最常使岸」，严格相等会漏掉——而标题是判断"下拉开没开"、
    /// "某一区的第一行在哪"最可靠的信号，漏掉就会连环误判。长度差超过 1 的一律不认，
    /// 免得把会话预览里提到"群聊"三个字的消息当成标题。
    /// </summary>
    private static bool LooksLikeHeader(string text, string header)
    {
        if (text == header) return true;
        if (Math.Abs(text.Length - header.Length) > 1) return false;
        var chars = header.Distinct().ToList();
        var hit = chars.Count(c => text.Contains(c));
        return (double)hit / chars.Count >= 0.66;
    }

    /// <summary>找分区标题行。标题是独立的一小行，所以只看"整行约等于标题本身"（容忍 1 个错字），
    /// 免得把聊天预览里提到"联系人"三个字的消息当成标题。</summary>
    private static OcrScreenReader.OcrLineBox? FindSectionHeader(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, string title,
        OcrScreenReader.OcrLineBox searchBox, int leftMaxX)
    {
        int belowY = searchBox.Y + Math.Max(searchBox.Height, 24);
        return boxes
            .Where(b => b.Y >= belowY && b.X < leftMaxX)
            .Where(b => LooksLikeHeader(Normalize(b.Text), title))
            .OrderBy(b => b.Y)
            .FirstOrDefault();
    }

    /// <summary>OCR 把放大镜 🔍 读成的样子（实测出现 0 / Q / O / 八 / × / ⊙）。</summary>
    private const string MagnifierGlyphs = "0QOoØ×✕⊙八";

    /// <summary>
    /// 从搜索下拉里挑出**第一个"不是建议行"的会话行**。
    ///
    /// 依据（截图实测的下拉结构）：
    ///   功能 ─ **会话行**（带头像、行首没有前缀） ─ 聊天记录（分组） ─ 搜索网络结果（分组） ─ 一串"🔍 关键词"建议行
    /// 建议行的行首就是放大镜，被 OCR 读成 0/Q/O/八 这类字符；真正的会话行没有这个前缀。
    /// 用户明确要求的规则就是"从搜索结果的第一个不含 🔍 的行点击进去。"
    /// </summary>
    private static OcrScreenReader.OcrLineBox? FindFirstRealChatRow(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes,
        OcrScreenReader.OcrLineBox searchBox,
        int leftMaxX)
    {
        int belowY = searchBox.Y + Math.Max(searchBox.Height, 24);

        foreach (var b in boxes.OrderBy(b => b.Y))
        {
            if (b.Y < belowY) continue;              // 搜索框本身及其上方
            if (b.X >= leftMaxX) continue;            // 右侧聊天区（不是下拉）
            if (b.Width < 60) continue;               // 导航/头像/角标被 OCR 读成的孤独 "O"/"0"，不是行
            var text = Normalize(b.Text);
            if (text.Length == 0) continue;
            if (DropdownStopHeaders.Any(h => text.Contains(h, StringComparison.Ordinal))) break;
            if (DropdownSectionHeaders.Any(h => text.StartsWith(h, StringComparison.Ordinal))) continue;
            if (LooksLikeMagnifierRow(b)) continue;
            return b;
        }
        return null;
    }

    /// <summary>
    /// 是不是"带放大镜的建议行"：行首是放大镜（被 OCR 读成 0/Q/O/八…），后面跟空格/竖线再接关键词。
    ///
    /// ⚠ 两个必须守住的细节（原来就是因为没守，判据整个反了）：
    /// 1) 判据要在**原始文本**上做。调用方原来传的是 Normalize 之后的文本，而 Normalize 会删掉分隔空格，
    ///    于是"首字符 + 分隔符"这一条永远不成立 —— 函数只剩"单字符"能触发，也就是**只对图标误识生效**。
    /// 2) 必须要求够宽。左侧导航/头像/角标图标会被 OCR 读成孤独的 "O"、"0"（实测 O@465,84、0@80,92），
    ///    宽度只有 16~36px；而真正的建议行至少要有"放大镜 + 关键词"。不卡宽度的话，
    ///    屏幕上只要出现这种图标，DropdownLooksOpen 就恒为真 → 每次点中会话行都被判成"下拉还开着"
    ///    → 跳过标题回验、直接放弃（实测对着同一个目标连续 8 次全部失败）。
    /// </summary>
    private static bool LooksLikeMagnifierRow(OcrScreenReader.OcrLineBox box)
    {
        if (box.Width < 50) return false;                 // 图标/角标误识，不是建议行
        var raw = box.Text.Trim();
        if (raw.Length < 2) return false;                  // 单个字符不足以说明是"放大镜 + 关键词"
        if (!MagnifierGlyphs.Contains(raw[0], StringComparison.Ordinal)) return false;
        // "首字符 + 分隔"：避免把名字本身以 0 开头的会话（如"007群"）误判成建议行
        return char.IsWhiteSpace(raw[1]) || raw[1] is '/' or '|' or '丨';
    }

    /// <summary>
    /// 在下拉里找"会话行"的位置——**纯像素判断，不看 OCR 文字**：
    /// 头像列（下拉左缘往右 120px）里出现"一大块填充"的那一段就是会话行（行首是头像）；
    /// 放大镜建议行、区块标题在这条列里几乎是空的。从上往下第一条即对话对象。
    ///
    /// 为什么必须看像素：实测 OCR 可能**完全不产出**精确匹配那一行的文字框（绿色高亮的名字读不出来），
    /// 却把放大镜建议行读得好好的——照文字找必然找错行（还会点进搜索页）。
    /// 阈值取"本段墨迹中位数的 2.2 倍"，自校准，不怕 DPI/主题差异（实测头像块 ≈ 3 倍基线）。
    /// </summary>
    private static List<(int X, int Y)> FindConversationRows(
        byte[] bgra, int imgW, int imgH, int leftX, int topY, int maxY = int.MaxValue)
    {
        const int stripW = 120;
        const int bandH = 40;
        var bg = ModeColor(bgra, imgW, imgH, leftX, topY, Math.Min(stripW, imgW - leftX - 1), 300);

        var bands = new List<(int Y, int Ink)>();
        for (int y = topY; y + bandH < imgH && y < topY + 800 && y < maxY; y += 10)
            bands.Add((y, InkIn(bgra, imgW, imgH, leftX, y, stripW, bandH, bg)));
        if (bands.Count == 0) return [];

        var sorted = bands.Select(b => b.Ink).OrderBy(v => v).ToList();
        int median = sorted[sorted.Count / 2];
        int threshold = Math.Max((int)(median * 2.2), 400);

        var rows = new List<(int X, int Y)>();
        int? start = null;
        int lastHit = 0;
        foreach (var (y, ink) in bands)
        {
            if (ink >= threshold) { start ??= y; lastHit = y; }
            else if (start is not null)
            {
                rows.Add((leftX + stripW / 2, (start.Value + lastHit + bandH) / 2));
                start = null;
            }
        }
        if (start is not null) rows.Add((leftX + stripW / 2, (start.Value + lastHit + bandH) / 2));
        return rows;
    }

    /// <summary>
    /// 在下拉里找"头像行"（V4.7）——判据是**彩色度**，不是"与背景色的差"。
    ///
    /// 为什么不能用 FindConversationRows 那套"取众数背景色再比差值"：
    /// 下拉是浮在深色会话列表上的**浅色浮层**，整段一起统计时浮层内外底色完全不同，
    /// 阈值一算就偏——实测直接把"张晓明"那一行整个漏掉（漏的就是要点的那一行）。
    /// 彩色度（max-min 通道差）对"浅色浮层 / 深色底"都成立：头像是小块彩色图，
    /// 文字与留白都接近灰阶。实测行内每 20px 带 ≥ 400 个彩色像素、行间为 0，分得很干净。
    /// </summary>
    private static List<(int X, int Y)> FindDropdownAvatarRows(
        byte[] bgra, int imgW, int imgH, int leftX, int topY, int maxY)
    {
        const int stripW = 172;      // 覆盖头像 + 名字开头（实测头像在 leftX+30 起）
        const int bandH = 20;
        const int minColorful = 140; // 172x20=3440 采样里约 4%：行内实测 ≥400，行间为 0

        var rows = new List<(int X, int Y)>();
        int? start = null;
        int lastHit = 0;
        for (int y = topY; y + bandH < imgH && y < maxY; y += 10)
        {
            int colorful = ColorfulIn(bgra, imgW, imgH, leftX, y, stripW, bandH);
            if (colorful >= minColorful) { start ??= y; lastHit = y; }
            else if (start is not null)
            {
                rows.Add((leftX + stripW / 2, (start.Value + lastHit + bandH) / 2));
                start = null;
            }
        }
        if (start is not null) rows.Add((leftX + stripW / 2, (start.Value + lastHit + bandH) / 2));
        return rows;
    }

    /// <summary>统计一块矩形里"有颜色"（通道极差大）的像素数。头像是彩色图，文字/留白接近灰阶。</summary>
    private static int ColorfulIn(byte[] bgra, int imgW, int imgH, int x0, int y0, int w, int h)
    {
        const int minSpread = 18;
        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(imgH, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(imgW, x0 + w); x++)
            {
                int i = (y * imgW + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                if (max - min >= minSpread) n++;
            }
        return n;
    }

    /// <summary>统计一块矩形里与背景色明显不同的像素数。</summary>
    private static int InkIn(
        byte[] bgra, int imgW, int imgH, int x0, int y0, int w, int h, (int R, int G, int B) bg)
    {
        int ink = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(imgH, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(imgW, x0 + w); x++)
            {
                int i = (y * imgW + x) * 4;
                if (Math.Abs(bgra[i + 2] - bg.R) > 26 || Math.Abs(bgra[i + 1] - bg.G) > 26 || Math.Abs(bgra[i] - bg.B) > 26)
                    ink++;
            }
        return ink;
    }

    /// <summary>取一块区域里最常见的颜色（当背景基准）。</summary>
    private static (int R, int G, int B) ModeColor(byte[] bgra, int imgW, int imgH, int x0, int y0, int w, int h)
    {
        var hist = new Dictionary<int, int>();
        for (int y = Math.Max(0, y0); y < Math.Min(imgH, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(imgW, x0 + w); x++)
            {
                int i = (y * imgW + x) * 4;
                int key = (bgra[i + 2] >> 3) << 10 | (bgra[i + 1] >> 3) << 5 | (bgra[i] >> 3);
                hist[key] = hist.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        if (hist.Count == 0) return (255, 255, 255);
        var mode = hist.OrderByDescending(kv => kv.Value).First().Key;
        return (((mode >> 10) & 31) << 3, ((mode >> 5) & 31) << 3, (mode & 31) << 3);
    }

    /// <summary>去掉空白后的文本，用于比对（OCR 常在中文之间插空格）。</summary>
    private static string Normalize(string s) => s.Replace(" ", "").Replace("\u3000", "").Trim();

    // ---------- OCR 定位 ----------

    /// <summary>
    /// 会话列表（左栏）的右边界上限。搜索框一定在窗口最左侧这一条里；
    /// 用"逻辑像素 × DPI 缩放"而不是"窗口宽度的百分比"——窗口拉得很宽（最大化）时，
    /// 按百分比会把右边聊天面板的标题也算进来，于是点到一个假的"搜索框"（实测踩过）。
    /// </summary>
    private const double LeftColumnMaxLogicalX = 300;

    private static double DpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { /* 老系统没有这个 API，退化为 1.0 */ }
        return 1.0;
    }

    private static double LeftColumnMaxX(IntPtr hwnd, int w)
        => Math.Min(LeftColumnMaxLogicalX * DpiScale(hwnd), w * 0.35);

    /// <summary>
    /// 定位会话列表顶部的本地搜索框（两个判据都限定在左栏内）：
    /// 1) 占位文字"搜索"——框为空时最准；
    /// 2) 否则取左栏里最靠上的**宽**行（框里可能残留着上次的查询词）。
    /// 宽度门槛用于排除图标/角标这类很窄的零碎文字（常被 OCR 读成"0"）。
    /// </summary>
    private static OcrScreenReader.OcrLineBox? FindSearchBox(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, int w, int h, double maxX)
    {
        var byText = boxes
            .Where(b => b.X > 0 && b.X <= maxX && b.Y >= 0 && b.Y <= h * 0.35
                        && Normalize(b.Text) is { Length: > 0 and <= 4 } t
                        && t.Contains("搜索", StringComparison.Ordinal))
            .OrderBy(b => b.Y)
            .FirstOrDefault();
        if (byText is not null) return byText;

        return boxes
            .Where(b => b.X > 0 && b.X <= maxX && b.Y >= 0 && b.Y < h * 0.22 && b.Width >= 60)
            .OrderBy(b => b.Y)
            .FirstOrDefault();
    }

    /// <summary>在 OCR 结果中按文本找一行（可限定区域），用于定位搜索框等文字控件。</summary>
    private static OcrScreenReader.OcrLineBox? FindByText(
        IReadOnlyList<OcrScreenReader.OcrLineBox> boxes, string keyword, double maxX, double maxY)
        => boxes.FirstOrDefault(b =>
            b.X >= 0 && b.X <= maxX && b.Y >= 0 && b.Y <= maxY
            && b.Text.Replace(" ", "").Contains(keyword, StringComparison.Ordinal));

    /// <summary>读聊天面板顶部（标题区）并按名字匹配判断是否打开了目标会话。</summary>
    private async Task<bool> TitleMatchesAsync(IntPtr hwnd, string person, CancellationToken ct)
    {
        if (!GetWindowRect(hwnd, out var rect)) return false;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;

        var (ok, _, boxes, _) = await OcrScreenReader.ReadBoxedAsync(hwnd, chatPanelOnly: false, ct);
        if (!ok) return false;

        // 聊天面板顶部区域（标题所在带）：左边界用**面板真实左沿**而不是固定 25%——
        // 25% 会把左侧会话列表的右半边也圈进来，而会话列表里就有目标名字，
        // 于是"没打开会话"也会被判成标题匹配（实测因此把没点中的行当成打开了）。
        int panelLeft = OcrScreenReader.EstimateChatPanelLeft(boxes, w, h);
        var header = boxes
            .Where(b => b.X > panelLeft && b.Y >= 0 && b.Y < h * 0.15)
            .OrderBy(b => b.Y)
            .ToList();
        _log?.Invoke($"[Action] 标题区候选：{string.Join(" | ", header.Select(b => b.Text))}");
        if (header.Count == 0)
            _log?.Invoke($"[Action] 整窗识别 {boxes.Count} 行（窗 {w}x{h}）：" +
                string.Join(" | ", boxes.Take(15).Select(b => $"{b.Text}@{b.X},{b.Y}")));

        return header.Any(b => ChatTitleMatches(b.Text, person));
    }

    /// <summary>
    /// 聊天面板标题是否**就是**目标会话。比 <see cref="NameSimilar"/> 严得多，专门挡这一类事故：
    /// 微信的群名经常含有人名（「张晓明和朋友们」「小明的群」），宽松的"字符命中率"会把群判成她本人，
    /// 于是消息发进群里——文档验收明确禁止"错误会话发送"。
    ///
    /// 判据：整行归一化后**长度必须相同**，且字符命中率 ≥ 0.66（只放宽给"大字号标题被 OCR 误识一个字"）。
    /// 「张晓明和朋友们」长度 7 ≠ 3 → 直接否掉；「文件传输助手」完全相等 → 通过。
    /// 代价是标题 OCR 大面积出错时会误判成"没打开"——按"宁可失败，也不要假成功"，这个代价是对的。
    /// </summary>
    private static bool ChatTitleMatches(string screenText, string person)
    {
        var a = Normalize(screenText);
        var b = Normalize(person);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (a.Length != b.Length) return false;
        var chars = b.Distinct().ToList();
        var hit = chars.Count(c => a.Contains(c));
        return (double)hit / chars.Count >= 0.66;
    }

    /// <summary>OCR 常有误识，按"目标名字的字符命中率"判断是否同一个会话。</summary>
    private static bool NameSimilar(string screenText, string person)
    {
        var chars = person.Where(c => !char.IsWhiteSpace(c)).Distinct().ToList();
        if (chars.Count == 0) return false;
        var hit = chars.Count(c => screenText.Contains(c));
        return (double)hit / chars.Count >= 0.6;
    }

    // ---------- 窗口准备 ----------

    /// <summary>
    /// **统一起手**：不管微信现在是哪种状态——① 窗口没出现（在托盘）、② 出现但最小化在任务栏、
    /// ③ 出现且在屏幕上——都先"唤出并激活"，再等界面稳定，然后才允许往下操作。
    ///
    /// 为什么必须统一（用户实测得出的结论）：
    /// - 三种状态下窗口**位置不同、渲染时机也不同**，分开处理必然有漏；
    /// - 刚还原的瞬间窗口还在动画/首帧，这时 OCR 读到的下拉布局是**旧的**，
    ///   照它算出来的行去点，就会"点偏"（用户亲眼看到鼠标落在别的行上）。
    /// 所以这里统一做两件事：先唤出+激活，再**等位置连续两次读数一致**才认为稳了。
    /// </summary>
    private bool EnsureReady(IntPtr hwnd, out string error)
    {
        error = "";
        if (!ForceForeground(hwnd, out error)) return false;

        RECT prev = default;
        bool havePrev = false;
        for (int i = 0; i < 24; i++)
        {
            if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);      // 托盘 / 最小化 → 拉起来
                havePrev = false;
                Thread.Sleep(160);
                continue;
            }
            if (!TryGetUsableRect(hwnd, out var r))
            {
                havePrev = false;
                Thread.Sleep(120);
                continue;
            }
            if (havePrev && r.Left == prev.Left && r.Top == prev.Top
                && r.Right == prev.Right && r.Bottom == prev.Bottom)
            {
                Thread.Sleep(200);                 // 位置稳了，再给一帧渲染时间（让下拉画完）
                _log?.Invoke($"[Action] 微信窗口就绪：{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
                return true;
            }
            prev = r;
            havePrev = true;
            Thread.Sleep(130);
        }

        error = "微信窗口位置一直在变（可能还在还原动画中），稍后重试";
        return false;
    }

    // ================= 第三阶段补充：步进节奏 / 空白点击回正 / 自适应重试 =================

    /// <summary>
    /// UI 步进节奏。实测"操作太快"会带来两类失败：① 窗口还没画完就点 → 点偏；
    /// ② 连续注入点击/按键太快 → 部分事件被吞。所以关键节点都给足间隔：慢一点，但稳。
    /// </summary>
    private static class Pacing
    {
        public const int AfterForegroundMs = 280;   // 抢到前台后，等窗口真正接受焦点
        public const int AfterNudgeMs = 420;        // 空白点击后，等浮层收掉 / 界面重绘
        public const int AfterClickMs = 320;        // 点完控件，等焦点落定
        public const int BeforeTypeMs = 220;        // 打字前再稳一下
        public const int BeforeEnterMs = 380;       // 回车发送前留出渲染时间
        public const int AfterEnterMs = 1050;       // 发送后等消息上屏（回验判据要读屏）

        /// <summary>重试退避：一次比一次等得久（窗口一般 1~2 秒内自己恢复）。</summary>
        public static readonly int[] RetryBackoffMs = [500, 1200, 2500];
    }

    // ================= 底层输入节流：每一次最小动作之间至少 100ms（第三阶段补充） =================
    //
    // 为什么要做成"全局最小间隔"而不是在每个调用点手写 sleep：
    // 实测"动作太快"会让微信漏掉一部分注入事件（表现：点到了但没反应、字只进去一半、
    // 下拉没展开就把名字打进去了）。调用点只该关心"做什么"，节奏由这一层统一保证，
    // 这样新增的调用路径（例如后加的唤醒/重试）也自动遵守，不会漏掉某一条。

    /// <summary>两次底层输入动作之间的最小间隔（毫秒）。调慢就改这一个数。</summary>
    private const int MinActionGapMs = 100;

    /// <summary>上一次底层输入动作的时间戳（进程内共享：同一时刻只有一个桥在驱动输入）。</summary>
    private static readonly Stopwatch LastInputAt = Stopwatch.StartNew();

    /// <summary>
    /// 底层输入前的统一节流：距上次输入不足 <see cref="MinActionGapMs"/> 就补足，然后开始计时。
    /// 放在**动作之前**调用，于是"动作之间至少 100ms"天然成立。
    /// </summary>
    private static void Pace()
    {
        var elapsed = (int)LastInputAt.ElapsedMilliseconds;
        if (elapsed < MinActionGapMs) Thread.Sleep(MinActionGapMs - elapsed);
        LastInputAt.Restart();
    }

    /// <summary>
    /// 在窗口的**空白处**真点一下。实测"窗口卡住 / 浮层没收掉 / 焦点丢了"这三类毛病，
    /// 人手点一下空白就恢复（真实点击本身也能重新激活窗口）——这里把它自动化。
    /// 落点选右侧聊天区偏上方的空白：既不是按钮也不是列表，点错也不会触发任何功能。
    /// </summary>
    private void NudgeBlank(IntPtr hwnd, string reason)
    {
        if (!TryGetUsableRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        int x = (int)(w * 0.68);
        int y = (int)(h * 0.30);
        ClickInWindow(hwnd, x, y);
        _log?.Invoke($"[Action] 在窗口空白处点一下（{reason}）@{x},{y}");
        Thread.Sleep(Pacing.AfterNudgeMs);
    }

    /// <summary>
    /// 唤醒 / 回正微信窗口：还原（托盘、最小化）→ 抢前台 → 空白点击 → 等位置稳定。
    /// 作为 Agent 可调用的能力（工具 wechat_wake_window）暴露出去，
    /// 这样"未就绪/失焦/输入通道卡住"时它能自己先唤醒再重试，而不是直接放弃。
    /// </summary>
    public Task<ActionResult> WakeWindowAsync(CancellationToken ct = default)
        => Task.Run<ActionResult>(() =>
        {
            var hwnd = LocateHandle(out var error);
            if (hwnd == IntPtr.Zero)
                return new ActionResult { Success = false, Error = error };

            if (!ForceForeground(hwnd, out error))
            {
                // 抢前台失败也再试一次"点空白"：真实点击往往比 API 更能把窗口叫醒
                NudgeBlank(hwnd, "抢前台失败 · 改用真实点击");
                if (!ForceForeground(hwnd, out error))
                    return new ActionResult { Success = false, Error = error };
            }

            NudgeBlank(hwnd, "唤醒窗口");
            if (!EnsureReady(hwnd, out error))
                return new ActionResult { Success = false, Error = error };

            TryGetUsableRect(hwnd, out var r);
            return new ActionResult
            {
                Success = true,
                Detail = $"已把微信窗口唤到前台并激活（{r.Left},{r.Top}，{r.Right - r.Left}×{r.Bottom - r.Top}）",
            };
        }, ct);

    /// <summary>
    /// 自适应重试版"就绪检查"：失败一次就上一次力，每级之间按退避表等待——
    /// ① 空白点击 → ② 重新抢前台 + 空白点击 → ③ 还原窗口 + 更长等待 + 空白点击。
    /// 人手修"窗口卡住"也是这么修的，只是这里把它写成有节奏的自动重试。
    /// </summary>
    private bool EnsureReadyWithRetry(IntPtr hwnd, out string error)
    {
        if (EnsureReady(hwnd, out error)) return true;

        for (int i = 0; i < Pacing.RetryBackoffMs.Length; i++)
        {
            var wait = Pacing.RetryBackoffMs[i];
            _log?.Invoke($"[Action] 窗口未就绪（{error}）→ 回正第 {i + 1}/{Pacing.RetryBackoffMs.Length} 次，等 {wait}ms");
            Thread.Sleep(wait);

            switch (i)
            {
                case 0:
                    NudgeBlank(hwnd, "窗口未就绪 · 先点空白");
                    break;
                case 1:
                    ForceForeground(hwnd, out _);
                    NudgeBlank(hwnd, "窗口未就绪 · 重新抢前台后点空白");
                    break;
                default:
                    ShowWindow(hwnd, SW_RESTORE);
                    Thread.Sleep(500);
                    ForceForeground(hwnd, out _);
                    NudgeBlank(hwnd, "窗口未就绪 · 还原窗口后点空白");
                    break;
            }

            if (EnsureReady(hwnd, out error)) return true;
        }

        return false;
    }

    /// <summary>
    /// 把微信窗口弄到前台并拿到输入焦点——**"失焦"是这条链路上最容易翻车的一步**：
    /// 调用进程不是前台进程时 <c>SetForegroundWindow</c> 会被系统直接拒绝，
    /// 于是后面所有模拟按键都打到了别的窗口（实测：搜索框里什么都没有，
    /// 而下拉显示的是"最常使用"默认列表，看起来跟真搜索结果一模一样）。
    ///
    /// 三层兜底，逐层加力：
    /// ① 从托盘/最小化拉起来（SW_RESTORE）+ 常规置前；
    /// ② <c>AttachThreadInput</c> 把自己的输入队列接到当前前台线程上，绕过前台锁再置前；
    /// ③ **保底点击**：在窗口标题栏空白处真点一下——真实鼠标点击本身就能激活窗口。
    /// </summary>
    private bool ForceForeground(IntPtr hwnd, out string error)
    {
        error = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // ① 先"唤出"：托盘 / 最小化 / 隐藏都拉起来（统一动作，不先去分辨现在是哪种状态）
            if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                Thread.Sleep(420);
            }

            // ② 常规置前
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Thread.Sleep(200);

            // ③ 接上输入队列再置前：**SetForegroundWindow 成功 ≠ 拿到键盘焦点**。
            //    实测：窗口是前台、下拉也点开了，但模拟按键一个字都没进去
            //    （OCR 里搜索框还挂着占位提示"Q 搜索"）。Qt 窗口对这种"只置前不激活"很敏感。
            AttachToForegroundThread(hwnd);
            Thread.Sleep(200);

            // ④ 保底点击：真在窗口上点一下，系统才会把"活动窗口 + 键盘焦点"一起交给它。
            //    点标题栏中间的空白处：不碰任何按钮，也不会触发最大化/还原（那要双击）。
            if (TryGetUsableRect(hwnd, out var r))
                ClickInWindow(hwnd, (r.Right - r.Left) / 2, 18);
            Thread.Sleep(260);

            if (GetForegroundWindow() == hwnd) return true;
            _log?.Invoke($"[Action] 第 {attempt + 1} 次切前台没成功，重试");
        }

        error = "无法把微信窗口切到前台（请先手动点一下微信窗口再重试）";
        return false;
    }

    /// <summary>把自己的输入队列临时接到当前前台线程上再置前——这是绕过 SetForegroundWindow 前台锁的标准手法。</summary>
    private static void AttachToForegroundThread(IntPtr hwnd)
    {
        uint myThread = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        try
        {
            if (fgThread != 0) AttachThreadInput(myThread, fgThread, true);
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (fgThread != 0) AttachThreadInput(myThread, fgThread, false);
        }
    }

    /// <summary>
    /// 强行把**键盘焦点**交给微信窗口。为什么需要它：
    /// 实测出现过"GetForegroundWindow() 已经是微信、搜索下拉也点开了，但模拟按键一个字都进不去"
    /// （连剪贴板粘贴都进不去）——说明缺的是**焦点**而不是前台。
    /// 做法是把自己的输入队列**同时接到"当前前台线程"和"微信线程"**上，
    /// 然后 SetForegroundWindow + SetActiveWindow + SetFocus，最后再断开。
    /// </summary>
    private static void ForceKeyboardFocus(IntPtr hwnd)
    {
        uint myThread = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint target = GetWindowThreadProcessId(hwnd, out _);
        if (fgThread != 0) AttachThreadInput(myThread, fgThread, true);
        if (target != 0) AttachThreadInput(myThread, target, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
            SetFocus(hwnd);
            Thread.Sleep(120);
        }
        finally
        {
            if (target != 0) AttachThreadInput(myThread, target, false);
            if (fgThread != 0) AttachThreadInput(myThread, fgThread, false);
        }
    }

    /// <summary>
    /// 正向判据：屏幕上出现了全局搜索（搜一搜）特有的文字。
    /// ⚠️ 注意：它**只用来生成"请手动返回"的提示**，绝不用来决定"代按 Esc"——
    /// 实测它有假阳性（普通聊天内容里也可能出现"网络结果"这类字样），
    /// 而误按一次 Esc 就会把微信收进托盘、并让该进程之后所有注入按键失效（只能重启微信）。
    /// 入口处的逻辑也据此改成"检测到就如实失败"，而不是"检测到就退出覆盖层"。
    /// </summary>
    private static readonly string[] GlobalSearchMarkers =
        ["搜索网络结果", "搜一搜", "搜索指定内容", "网络结果", "搜索聊天记录"];

    private static bool LooksLikeGlobalSearch(IReadOnlyList<OcrScreenReader.OcrLineBox> boxes)
        => boxes.Any(b => GlobalSearchMarkers.Any(m => Normalize(b.Text).Contains(m, StringComparison.Ordinal)));

    // 说明：这里原来有个 DismissOverlay（按 Esc 退出搜一搜覆盖层），已经**删掉**。
    // 原因实测：Esc 在普通聊天页 = 把微信收进托盘，而收过托盘之后该进程**再也不接受注入按键**
    // （只能重启微信）。而它原来还被用在"打开会话失败"的收尾路径上——
    // 于是每失败一次就把微信弄坏一次，下一次必然也失败（用户看到的"重启后第一次能成、连着跑就废"就是这个）。
    // 现在改为：检测到覆盖层就如实失败、请用户手动返回；失败时不再做任何"收尾动作"。

    /// <summary>窗口矩形是否可用（可见且尺寸合理）。</summary>
    private static bool TryGetUsableRect(IntPtr hwnd, out RECT rect)
        => GetWindowRect(hwnd, out rect) && rect.Right - rect.Left > 300 && rect.Bottom - rect.Top > 200;

    /// <summary>
    /// 定位微信主窗句柄：
    /// 1) 先试 UIAutomation（窗口可见时最准）；
    /// 2) 再枚举顶层窗口——最小化到托盘/被隐藏时 UIA 看不到，但窗口仍然存在，这样才能"自己把托盘的微信拉出来"。
    /// </summary>
    private static IntPtr LocateHandle(out string error)
    {
        error = "";
        try
        {
            var win = UiAutomationWeChatBridge.FindMainWindow();
            if (win is not null)
            {
                var h = new IntPtr(win.Current.NativeWindowHandle);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { /* 落到枚举兜底 */ }

        var found = FindHandleByEnumeration();
        if (found != IntPtr.Zero) return found;

        error = "未找到微信窗口（请先登录并打开微信）";
        return IntPtr.Zero;
    }

    /// <summary>
    /// 枚举兜底：**必须能看见托盘/最小化的窗口**（否则"从托盘唤出"这段代码永远走不到）。
    /// 规则统一在 <see cref="WeChatWindowFinder"/>（UIA 与 Win32 两个桥共用一份）。
    /// </summary>
    private static IntPtr FindHandleByEnumeration() => WeChatWindowFinder.FindHandle();

    // ---------- 比对 ----------

    private static string Preview(string s) => s.Replace("\n", " ").Trim() is { Length: > 0 } t
        ? (t.Length <= 40 ? t : t[..40] + "…")
        : "(空)";

    /// <summary>去掉空白与常见标点后的指纹，用于宽松比对 OCR 回验结果。</summary>
    private static string Fingerprint(string s)
        => new(s.Where(c => !char.IsWhiteSpace(c) && !"，。！？,.:：;；、\"'“”".Contains(c)).ToArray());

    // ---------- Win32 输入 ----------

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    // 窗口枚举/类名/标题的 P/Invoke 已挪到 WeChatWindowFinder（两个桥共用一份规则）
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>
    /// 按"窗口内相对坐标"点击。**每次点击前都重新取一次窗口位置**——
    /// 这是"点偏"的根治办法：OCR 位图是从屏幕按 <c>rect</c> 抠下来的，所以
    /// 「位图坐标 == 窗口内坐标」，只要两者的 <c>rect</c> 是同一个就没偏移；
    /// 但窗口在"从托盘还原 / 最小化还原 / 被系统挪动"时位置会变，
    /// 拿**开头取的那份旧 rect** 去偏移，光标就落到别处（用户肉眼看到的就是这个）。
    ///
    /// 点完还会**回读光标位置**核对：实测出现过"光标只挪了一丢丢、根本没碰到目标行"，
    /// 而当时上层已经以为点中了（会话标题里恰好人名，回验立刻放行），
    /// 接着往没打开的下拉里打字+回车，直接把微信卡住。
    /// </summary>
    private bool ClickInWindow(IntPtr hwnd, int x, int y)
    {
        if (!TryGetUsableRect(hwnd, out var r)) return false;
        int sx = r.Left + x, sy = r.Top + y;
        Click(sx, sy);
        if (GetCursorPos(out var p) && (Math.Abs(p.X - sx) > 2 || Math.Abs(p.Y - sy) > 2))
            _log?.Invoke($"[Action] ⚠ 光标没落到目标：想要 ({sx},{sy})，实际 ({p.X},{p.Y})");
        return true;
    }

    private static void Click(int x, int y)
    {
        Pace();                       // 节流：与上一次最小动作至少隔 100ms
        SetCursorPos(x, y);
        Thread.Sleep(70);
        // 按下与抬起**分两次投递**并留一点间隔：一次性投递下沉/抬起两个事件时，
        // 某些 Qt 自绘控件会当成"没点中"（下拉能开、但不把光标放进输入框）。
        var down = new[] { new INPUT { Type = 0, U = new InputUnion { Mi = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTDOWN } } } };
        var up = new[] { new INPUT { Type = 0, U = new InputUnion { Mi = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTUP } } } };
        SendInput(1, down, Marshal.SizeOf<INPUT>());
        Thread.Sleep(45);
        SendInput(1, up, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk)
    {
        Pace();                       // 节流
        var inputs = new[]
        {
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk } } },
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_KEYUP } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyCombo(ushort modifier, ushort vk)
    {
        Pace();                       // 节流
        var inputs = new[]
        {
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = modifier } } },
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk } } },
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Flags = KEYEVENTF_KEYUP } } },
            new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Vk = modifier, Flags = KEYEVENTF_KEYUP } } },
        };
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>按 Unicode 逐字输入：中文不依赖输入法状态。逐字之间也走统一的 100ms 节流。</summary>
    private static void SendText(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n') { SendKey(VK_RETURN); continue; }
            Pace();                   // 节流：每个字之间至少 100ms（原来是 18ms，实测容易被吞）
            var inputs = new[]
            {
                new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = KEYEVENTF_UNICODE } } },
                new INPUT { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } },
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>
    /// 等下拉**真的展开**再往里打字。点击搜索框只是"点开下拉"，展开是异步的（动画 + 首帧），
    /// 展开过程会抢走输入焦点；打字抢在它前面就会被整段吞掉——
    /// 实测现象正是"窗口是前台、下拉特征也读到了、搜索框里却一个字都没有"。
    /// </summary>
    private async Task WaitDropdownSettledAsync(
        IntPtr hwnd, OcrScreenReader.OcrLineBox searchBox, int leftMaxX, CancellationToken ct)
    {
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(220, ct);
            var (ok, _, boxes, _) = await OcrScreenReader.ReadLeftPanelAsync(hwnd, ct);
            if (ok && DropdownLooksOpen(boxes, searchBox, leftMaxX))
            {
                _log?.Invoke($"[Action] 下拉已展开（等了 {(i + 1) * 220}ms）");
                return;
            }
        }
        _log?.Invoke("[Action] 等下拉展开超时，仍继续试输入");
    }

    /// <summary>
    /// 用剪贴板 + Ctrl+V 输入。走的是另一条路径（WM_PASTE），
    /// 在"SendInput 直投被 Qt 窗口/输入法吃掉"时能兜住。用完把用户原来的剪贴板还回去。
    ///
    /// 剪贴板必须在 **STA** 线程上访问（WPF 的 Clipboard 会抛"必须先设置 STA"），
    /// 而写操作桥跑在线程池线程（MTA）上，所以这里单开一个 STA 线程来做。
    /// </summary>
    private void PasteText(string text)
    {
        var old = GetClipboardText();
        if (!SetClipboardText(text))
        {
            _log?.Invoke("[Action] 剪贴板不可用，退回逐字输入");
            SendText(text);
            return;
        }

        Thread.Sleep(140);
        SendKeyCombo(VK_CONTROL, VK_V);
        if (old is not null) SetClipboardText(old);      // 还原用户原来的剪贴板
    }

    /// <summary>在专用 STA 线程上读写剪贴板（MTA 线程直接用 WPF Clipboard 会抛异常）。</summary>
    private static string? GetClipboardText() => OnStaThread(() => System.Windows.Clipboard.GetText());

    private static bool SetClipboardText(string text)
        => OnStaThread(() =>
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        });

    private static T? OnStaThread<T>(Func<T> action)
    {
        T? result = default;
        var t = new Thread(() =>
        {
            try { result = action(); } catch { /* 剪贴板被别的进程占着是常态，失败就算了 */ }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(1500)) return default;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    // MOUSEINPUT 仅用于让联合体尺寸与 Windows 期望的 INPUT 对齐
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
