using System.Diagnostics;

namespace MemoryAssistant.Core.Agent.Skills;

/// <summary>
/// 内置 Skill（plan2 §5）。Skill = "确定性的能力封装"：输入请求 →
/// 编排 IMemoryBackend 的底层能力 → 产出 Summary + Evidence。
/// Agent 负责决定何时调用，Skill 负责把事情安全做对。
/// </summary>
public static class BuiltInSkills
{
    private const int RecallTopK = 5;
    private const int RecallReadLimit = 8;      // 每个候选会话读取条数
    private const int RecallReadSessions = 2;   // 精读的会话数（克制）
    private const int RecallMaxEvidence = 12;   // 证据上限（避免答案被灌满）
    private const int StatsLimit = 60;
    private const int StatsSessionLimit = 20;   // 扫描最近活跃的会话数：太少会被公众号推送占满，排不出"和谁聊得最多"
    private const int CommitmentLimit = 100;
    private const int TimelineRecentDays = 7;
    private const int TimelineDetailDays = 2;
    private const int TimelineMaxEvidence = 12;
    private const int TopicRecentDays = 30;
    private const int ProfileTop = 10;

    /// <summary>回忆：语义召回 → 定位会话/日期 → 精读原文 → Evidence（plan2 §5.1）。</summary>
    public sealed class RecallSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "recall";
        public string Description => "语义召回聊天内容并取证";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var window = request.Window ?? SearchWindow.Unbounded;
            var raw = await _backend.SemanticSearchAsync(request.Query, RecallTopK, ct);
            // V3.3：时间窗过滤 + 噪音过滤（"最近/上周/去年"等约束真正生效）
            var chunks = raw
                .Where(c => TimeWindowResolver.ContainsDate(window, c.Date))
                .Where(c => !ContentNoiseFilter.IsNoise(c.Text))
                .ToList();

            // 软回退：窗口内无候选时**扩大召回量**再试（top-k 太小会漏）。
            bool relaxed = false;
            if (chunks.Count == 0 && window.IsBounded)
            {
                var wider = await _backend.SemanticSearchAsync(request.Query, RecallTopK * 4, ct);
                chunks = wider
                    .Where(c => TimeWindowResolver.ContainsDate(window, c.Date))
                    .Where(c => !ContentNoiseFilter.IsNoise(c.Text))
                    .ToList();
                if (chunks.Count == 0)
                {
                    // 用户明确限定了时间范围（"今天/上周/9月11号"），而语义检索是按**相关度**排的，
                    // 对"今天都聊了什么"这种时间型问题基本必空。旧实现在这里把范围外的片段也塞进来，
                    // 于是回答会拿着别的日子的内容说"今天…"——**静默违背用户的时间约束**。
                    // 现在不混入范围外内容：交给下面的按天读取去拿真正的当天原文；
                    // 按天也拿不到就如实说没有，而不是换个日子顶上。
                    relaxed = true;
                }
            }

            // 原文优先于 rag：key=会话|内容（原文读到后覆盖 rag 片段，避免 rag/原文同文案重复引用）
            var byKey = new Dictionary<string, Evidence>(StringComparer.Ordinal);
            string Key(Evidence e) => $"{e.SessionId}|{e.Content}";
            void Add(Evidence e)
            {
                if (ContentNoiseFilter.IsNoise(e.Content)) return;
                var k = Key(e);
                if (byKey.TryGetValue(k, out var existing))
                {
                    if (e.Source == "微信原文" && existing.Source != "微信原文")
                        byKey[k] = e;
                    return;
                }
                byKey[k] = e;
            }

            foreach (var c in chunks)
            {
                Add(new Evidence
                {
                    SessionId = c.SessionId,
                    SessionDisplayName = string.IsNullOrEmpty(c.SessionName) ? c.SessionId : c.SessionName,
                    Date = c.Date,
                    Content = c.Text,
                    Source = "rag",
                });
            }

            // 明确问到"某一天"（如"我和张晓明9月11号聊了什么"）：直接按天取记录。
            // 语义检索只按相关度排序，跨年语料里常把用户问的那天排到十几名之外，靠 top-k 是碰运气；
            // 问某天就该去翻那天。
            foreach (var e in await ReadDaysAsync(request, window, ct)) Add(e);

            // 精读：最多 RecallReadSessions 个 (会话,日期) 组合，读当天原文验证
            var probed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in chunks)
            {
                if (string.IsNullOrEmpty(c.SessionId) || string.IsNullOrEmpty(c.Date)) continue;
                if (probed.Count >= RecallReadSessions) break;
                if (!probed.Add($"{c.SessionId}|{c.Date}")) continue;
                var msgs = await _backend.ReadDayMessagesAsync(c.SessionId, c.Date, RecallReadLimit, ct);
                var sessionName = string.IsNullOrEmpty(c.SessionName) ? c.SessionId : c.SessionName;
                foreach (var m in msgs)
                    Add(m with { SessionDisplayName = sessionName, Date = c.Date, Source = "微信原文" });
            }
            sw.Stop();

            // 有原文的会话就不再保留它的 rag 片段：回答要引用干净原文，而不是"多条消息拼在一起的检索块"
            var sessionsWithOriginal = byKey.Values
                .Where(e => e.Source == "微信原文")
                .Select(e => e.SessionId)
                .ToHashSet(StringComparer.Ordinal);

            var evidence = byKey.Values
                .Where(e => e.Source == "微信原文" || !sessionsWithOriginal.Contains(e.SessionId))
                .OrderByDescending(e => e.Source == "微信原文")   // 原文优先展示
                .Take(RecallMaxEvidence)
                .ToList();
            var ok = chunks.Count > 0 || evidence.Count > 0;
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = evidence.Count > 0,
                Summary = ok
                    ? $"召回 {chunks.Count} 个候选片段，精读后得到 {evidence.Count} 条证据。"
                        + (relaxed ? "（语义检索在时间窗内无候选，改用按天原文）" : "")
                    : "未找到相关候选，证据不足。",
                Evidence = evidence,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        /// <summary>
        /// 按天取记录：窗口内每一天直接查当日片段。
        ///
        /// 两种情况分开处理（"长点脑子"的关键）：
        ///  - 问到了具体的人（"我和张晓明9月11号"）→ 先按人定位**私聊**，直接读那个会话当天的原文；
        ///    找不到私聊再退到该人所在的群，最后才退到"当天所有人"的片段。用户说"我和某人"通常指一对一，
        ///    而按关键词检索会命中"她最后发言的那个群"，答案就跑偏了。
        ///  - 没提到具体的人（"我9月11号聊了什么"）→ 保留当天全部片段（即"所有记录里的最新"）。
        /// 提问里点了人时，若命中片段明显是该人相关则只保留这些，避免整天的无关群聊灌进答案。
        /// </summary>
        private async Task<List<Evidence>> ReadDaysAsync(SkillRequest request, SearchWindow window, CancellationToken ct)
        {
            var outList = new List<Evidence>();
            var days = TimeWindowResolver.Days(window);
            if (days.Count == 0) return outList;

            var entity = request.Hint?.Entity?.Trim();
            if (!string.IsNullOrWhiteSpace(entity))
            {
                var session = await ResolveSessionAsync(request.Query, entity, ct);
                if (session is not null)
                {
                    foreach (var day in days)
                    {
                        var msgs = await _backend.ReadDayMessagesAsync(session.Id, day, RecallReadLimit * 4, ct);
                        foreach (var m in msgs.Take(RecallMaxEvidence))
                            outList.Add(m with { SessionDisplayName = session.Label, Date = day, Source = "微信原文" });
                    }
                    if (outList.Count > 0) return outList;
                }
            }

            foreach (var day in days)
            {
                var snippets = await _backend.GetDaySnippetsAsync(day, ct);
                if (snippets.Count == 0) continue;

                var matched = string.IsNullOrWhiteSpace(entity)
                    ? new List<Evidence>()
                    : snippets.Where(s =>
                        s.SenderName.Contains(entity, StringComparison.Ordinal)
                        || s.SessionDisplayName.Contains(entity, StringComparison.Ordinal)
                        || s.Content.Contains(entity, StringComparison.Ordinal)).ToList();

                var chosen = matched.Count > 0 ? matched : snippets;
                foreach (var s in chosen.Take(RecallMaxEvidence))
                    outList.Add(s with { Date = day, Source = "微信原文" });
            }
            return outList;
        }

        /// <summary>
        /// 把"某个人"解析成会话：先私聊（"我和X"的默认理解），再退到不限类型；
        /// 提问里明确说了"群"就直接找群。解析不出来返回 null（上层退化为按天片段）。
        /// </summary>
        private async Task<SessionHit?> ResolveSessionAsync(string query, string entity, CancellationToken ct)
        {
            var wantsGroup = query.Contains("群", StringComparison.Ordinal);
            var candidates = await _backend.FindSessionsAsync(entity, wantsGroup ? false : true, 3, ct);
            if (candidates.Count == 0)
                candidates = await _backend.FindSessionsAsync(entity, privateOnly: null, 3, ct);
            // 优先精确同名（避免"张晓明"匹配到"张晓明的家人群"），再退到最近活跃的
            return candidates.FirstOrDefault(c => string.Equals(c.DisplayName, entity, StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault();
        }
    }

    /// <summary>统计：会话量级 / 活跃概况（plan2 §5.2，避免模型频繁裸调 get_session_stats）。</summary>
    public sealed class StatsSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "stats";
        public string Description => "会话统计：发言/活跃时间/消息量";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var text = await _backend.GetStatsTextAsync(StatsLimit, StatsSessionLimit, ct);
            sw.Stop();
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = !string.IsNullOrWhiteSpace(text),
                Summary = "统计完成。",
                Draft = text,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>时间线：最近活跃天 + 当日片段（plan2 §5.3）。</summary>
    public sealed class TimelineSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "timeline";
        public string Description => "按日期/时间范围查询与时间线";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var days = await _backend.GetRecentDaysAsync(TimelineRecentDays, ct);
            var evidence = new List<Evidence>();
            int detail = 0;
            foreach (var d in days)
            {
                if (detail >= TimelineDetailDays) break;
                detail++;
                var snippets = await _backend.GetDaySnippetsAsync(d.Date, ct);
                evidence.AddRange(snippets);
            }
            sw.Stop();
            var dayText = string.Join(", ", days.Select(x => $"{x.Date}({x.MessageCount})"));
            var capped = evidence.Take(TimelineMaxEvidence).ToList();
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = capped.Count > 0,
                Summary = $"最近 {days.Count} 天活跃概况：{dayText}；展开 {TimelineDetailDays} 天片段 {capped.Count} 条。",
                Evidence = capped,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>承诺：候选发现 → 原文证据（plan2 §5.4）。</summary>
    public sealed class CommitmentSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "commitment";
        public string Description => "查找承诺/待办";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var found = await _backend.ScanCommitmentsAsync(CommitmentLimit, ct);
            sw.Stop();
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                // 扫描本身成功即为"可回答"（0 命中 = 诚实结论"暂无承诺"）
                Sufficient = true,
                Summary = $"承诺扫描完成：命中 {found.Count} 条候选。",
                Evidence = found,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>话题：最近 N 天高频主题（plan2 §5.5；LLM 命名留到上层可选）。</summary>
    public sealed class TopicSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "topic";
        public string Description => "话题分析：高频主题与聚类";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var text = await _backend.GetTopicsTextAsync(TopicRecentDays, ct);
            sw.Stop();
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = !string.IsNullOrWhiteSpace(text),
                Summary = $"话题分析（近 {TopicRecentDays} 天）完成。",
                Draft = text,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>画像：会话/人物统计事实（plan2 §5.6，不推断人格）。</summary>
    public sealed class ProfileSkill(IMemoryBackend backend) : IAgentSkill
    {
        public string Name => "profile";
        public string Description => "会话画像：消息数/活跃时段/高频话题";
        private readonly IMemoryBackend _backend = backend;

        public async Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var text = await _backend.GetProfilesTextAsync(ProfileTop, ct);
            sw.Stop();
            return new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = !string.IsNullOrWhiteSpace(text),
                Summary = $"画像统计完成（前 {ProfileTop}）。",
                Draft = text,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>闲聊：最轻量，不访问本地数据（plan2 §5.7）。</summary>
    public sealed class ChitchatSkill : IAgentSkill
    {
        public string Name => "chitchat";
        public string Description => "闲聊寒暄：不查聊天记录";
        public Task<SkillResult> ExecuteAsync(SkillRequest request, CancellationToken ct)
            => Task.FromResult(new SkillResult
            {
                Skill = Name,
                Success = true,
                Sufficient = true,
                Summary = "闲聊：不查聊天记录。",
                ElapsedMs = 0,
            });
    }
}
