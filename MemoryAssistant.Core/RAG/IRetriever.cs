namespace MemoryAssistant.Core.RAG;

/// <summary>一条检索到的聊天片段（带来源元数据，供 Evidence 引用）。</summary>
public sealed record RetrievedChunk
{
    public string SessionId { get; init; } = "";
    public string SessionName { get; init; } = "";
    public string Date { get; init; } = "";
    public string Text { get; init; } = "";
    public int MsgCount { get; init; }
    public double Score { get; init; }
    /// <summary>来源：vector | fts | hybrid。</summary>
    public string Source { get; init; } = "vector";

    public string Display => $"[{Date}] {SessionName}";
}

/// <summary>检索器统一抽象。RAG 与存储解耦（可换 NPZ/SQLite/FAISS）。</summary>
public interface IRetriever
{
    string Name { get; }
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query,
        int topK,
        CancellationToken ct = default);
}
