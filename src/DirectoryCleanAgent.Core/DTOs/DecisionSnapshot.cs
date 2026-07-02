using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 决策快照 — 不可变删除操作的全量冻结视图。
///
/// 由 IDecisionEngine.DecideAndSnapshotAsync() 产出，
/// 包含所有待删除文件的不可变条目集合及操作元数据。
/// 快照一经生成便脱离缓存依赖，后续规则变更或 AI 回调不影响此快照。
///
/// 快照同时序列化为紧凑 JSON 写入 DeletionRecord.decision_snapshot 字段，
/// 超 4KB 时自动应用 GZip 压缩 + Base64 编码（由数据层处理）。
/// </summary>
public record DecisionSnapshot
{
    /// <summary>本次操作的唯一标识 UUID</summary>
    public required string OperationId { get; init; }

    /// <summary>待删除文件的不可变快照条目集合</summary>
    public required ReadOnlyCollection<DeleteSnapshotEntry> Entries { get; init; }

    /// <summary>快照冻结时间戳（UTC ISO8601）</summary>
    public required DateTime FrozenAt { get; init; }

    /// <summary>待删除文件总数</summary>
    public int TotalFileCount => Entries.Count;

    /// <summary>待删除文件总大小（字节）</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>因哈希失败被跳过的文件列表，默认空列表</summary>
    public ReadOnlyCollection<SkippedEntry> SkippedEntries { get; init; } =
        new ReadOnlyCollection<SkippedEntry>(Array.Empty<SkippedEntry>());

    /// <summary>被跳过的文件数量</summary>
    public int SkippedFileCount => SkippedEntries.Count;

    /// <summary>按 FinalAction 分组统计的文件数</summary>
    public IReadOnlyDictionary<FinalAction, int> ActionCounts => GetActionCounts();

    private IReadOnlyDictionary<FinalAction, int> GetActionCounts()
    {
        return Entries
            .GroupBy(e => e.FinalAction)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // ============================================================
    // JSON 序列化（用于写入 DeletionRecord.DecisionSnapshotJson）
    // ============================================================

    /// <summary>
    /// 将快照序列化为紧凑 JSON 字符串（无缩进），格式符合设计文档 5.1 节定义。
    ///
    /// 输出结构：
    /// {
    ///   "op": "&lt;operation_id&gt;",
    ///   "files": [{ "path": "...", "size": N, "hash": "...", "action": "...", "verdict": "..." }],
    ///   "frozen_at": "&lt;ISO8601_UTC&gt;"
    /// }
    ///
    /// 超 4KB 时由数据层（B4）负责 GZip + Base64 压缩并添加 "GZ:" 前缀。
    /// 用于写入 DeletionRecord.DecisionSnapshotJson 字段。
    /// </summary>
    /// <returns>紧凑 JSON 字符串</returns>
    public string ToCompactJson()
    {
        var wrapper = new SnapshotJsonWrapper
        {
            Op = OperationId,
            Files = Entries,
            FrozenAt = FrozenAt.ToString("O")
        };

        return JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
        {
            WriteIndented = false,
            // Unicode 字符不转义（中文路径等保持可读性）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 快照 JSON wrapper 结构 — 仅用于序列化，符合设计文档 5.1 节格式。
    /// </summary>
    private sealed class SnapshotJsonWrapper
    {
        [System.Text.Json.Serialization.JsonPropertyName("op")]
        public required string Op { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public required ReadOnlyCollection<DeleteSnapshotEntry> Files { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("frozen_at")]
        public required string FrozenAt { get; init; }
    }
}
