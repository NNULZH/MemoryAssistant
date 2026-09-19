namespace MemoryAssistant.Core.Agent;

/// <summary>
/// 任务级证据库（plan2 §8.1/8.2）：维护稳定编号 1..N、按内容去重，
/// 负责生命周期迁移 Observed→Verified→Cited，并支持"仅事实/仅已核实"过滤，
/// 成为 Agent 的"事实层"——回答引用 [N] 永远指向这里可回溯的条目。
/// 记录不可变：迁移通过 with{} 替换旧条目完成。
/// </summary>
public sealed class EvidenceStore
{
    private readonly List<Evidence> _items = [];
    private int _nextIndex = 1;

    public IReadOnlyList<Evidence> Items => _items;
    public int Count => _items.Count;

    /// <summary>内容键（会话|时间|内容），跨轮去重依据。</summary>
    private static string Key(Evidence e) => $"{e.SessionId}|{e.CreateTime}|{e.Content}";

    /// <summary>
    /// 入库一条"已观测"证据：去重后分配稳定 Index，打上发现工具与类型。
    /// originTool 为空时保留证据自带来源判断（原文类默认 Fact）。
    /// </summary>
    public void Observe(Evidence e, string? originTool = null, EvidenceKind? kind = null, double confidence = 0.8)
    {
        var k = Key(e);
        if (_items.Any(x => Key(x) == k)) return; // 已存在：不重复入库

        var resolvedKind = kind ?? ClassifyKind(e.Source);
        _items.Add(e with
        {
            Index = _nextIndex++,
            OriginTool = originTool ?? e.OriginTool,
            Kind = resolvedKind,
            Confidence = confidence,
            Stage = EvidenceStage.Observed,
        });
    }

    /// <summary>从规则初筛进入候选（未精读）。</summary>
    public void AddCandidate(Evidence e, string? originTool = null)
    {
        var k = Key(e);
        if (_items.Any(x => Key(x) == k)) return;
        _items.Add(e with
        {
            Index = _nextIndex++,
            OriginTool = originTool ?? e.OriginTool,
            Stage = EvidenceStage.Candidate,
        });
    }

    /// <summary>按原文类型推断事实/推测（微信原文、时间线片段、承诺 = 事实；统计类 = 推测）。</summary>
    public static EvidenceKind ClassifyKind(string source)
        => source switch
        {
            "stats" => EvidenceKind.Inference,
            "topic" or "profile" or "统计" or "话题" or "画像" => EvidenceKind.Inference,
            _ => EvidenceKind.Fact,
        };

    /// <summary>把第 index 条标记为已验证（核对原文/交叉确认）。</summary>
    public bool Verify(int index)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Index != index) continue;
            _items[i] = _items[i] with { Stage = EvidenceStage.Verified };
            return true;
        }
        return false;
    }

    /// <summary>全部标记已验证（评估通过、进入作答时调用）。</summary>
    public int VerifyAll()
    {
        int n = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Stage is EvidenceStage.Verified or EvidenceStage.Cited) continue;
            _items[i] = _items[i] with { Stage = EvidenceStage.Verified };
            n++;
        }
        return n;
    }

    /// <summary>把 (第 index 条) 标记为已引用（写入回答）。</summary>
    public void MarkCited()
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i] = _items[i] with { Stage = EvidenceStage.Cited };
    }

    /// <summary>已验证/已引用的证据（进入作答引用的候选）。</summary>
    public IReadOnlyList<Evidence> VerifiedItems => _items.Where(e => e.Verified).ToList();

    /// <summary>仅客观事实。</summary>
    public IReadOnlyList<Evidence> FactItems => _items.Where(e => e.Kind == EvidenceKind.Fact).ToList();

    /// <summary>仅推断类（统计/聚类等，作答时需说明依据）。</summary>
    public IReadOnlyList<Evidence> InferenceItems => _items.Where(e => e.Kind == EvidenceKind.Inference).ToList();
}
