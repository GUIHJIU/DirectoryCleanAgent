namespace DirectoryCleanAgent.Decision;

/// <summary>
/// 决策引擎主实现 — 结合规则裁决、AI标签和用户策略仲裁最终操作决策，并生成不可变删除快照。
///
/// 核心职责：
/// 1. 按照设计文档 6.3 节完整决策表（4 种情况）仲裁 final_action
/// 2. 对非 Protected 文件并发计算 SHA-256 哈希（可取消、有进度上报）
/// 3. 生成不可变 ReadOnlyCollection&lt;DeleteSnapshotEntry&gt; 快照，脱离缓存依赖
///
/// 线程安全：
///   - Arbitrate() 为纯函数，无共享可变状态，天然线程安全
///   - DecideAndSnapshotAsync() 内部状态均为方法局部变量
///   - 阶段 2 使用 Interlocked.Increment + lock 保护共享集合
///   - 多次并发调用互不干扰
/// </summary>
public sealed class DecisionEngine : IDecisionEngine
{
    private readonly IConfigService _configService;
    private readonly ILogger<DecisionEngine> _logger;

    // ============================================================
    // 构造与依赖注入
    // ============================================================

    public DecisionEngine(IConfigService configService, ILogger<DecisionEngine> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogInformation("决策引擎初始化完成");
    }

    // ============================================================
    // 公开接口：单文件实时仲裁（Arbitrate）
    // ============================================================

    /// <inheritdoc />
    public FileDecisionCache Arbitrate(
        FileDecisionCache cache,
        bool aiEnabled,
        AITrustLevel aiTrustLevel)
    {
        ArgumentNullException.ThrowIfNull(cache);

        // 调用纯函数计算最终决策
        var newAction = ComputeFinalAction(
            cache.RuleVerdict, aiEnabled, aiTrustLevel,
            cache.AiLabel, cache.AiConfidence, cache.UserDecision);

        _logger.LogDebug(
            "Arbitrate: {Path} | 规则裁决={Verdict} | AI标签={AiLabel} | 最终决策={Action}",
            cache.FilePath, cache.RuleVerdict, cache.AiLabel ?? "无", newAction);

        // 通过 with 表达式返回新实例，不修改传入的 cache（不可变语义）
        return cache with { FinalAction = newAction };
    }

    // ============================================================
    // 公开接口：批量决策 + 哈希 + 快照生成（DecideAndSnapshotAsync）
    // ============================================================

    /// <inheritdoc />
    public async Task<DecisionSnapshot> DecideAndSnapshotAsync(
        IReadOnlyList<FileDecisionCache> candidates,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        _logger.LogMethodEntry($"候选文件数={candidates?.Count ?? 0}");
        ArgumentNullException.ThrowIfNull(candidates);

        // 捕获配置快照 — 保证批次内 AI 设置一致性
        // 配置在快照期间被外部修改不会影响本次决策
        var config = _configService.Current;
        var aiEnabled = config.AIEnabled;
        var aiTrustLevel = config.AITrustLevel;
        var operationId = Guid.NewGuid().ToString("N");
        var frozenAt = DateTime.UtcNow;

        var completedCount = 0;       // 已完成计数（含 Protected，用于进度上报）
        var protectedCount = 0;

        try
        {
            using var _ = _logger.LogTiming("DecideAndSnapshotAsync");

            // ========================================================
            // 阶段 1：遍历候选列表，仲裁每个文件的最终操作决策
            // 按是否 Protected 分流：Protected 无需哈希，不计入快照
            // ========================================================
            var nonProtected = new List<(FileDecisionCache Cache, FinalAction Action)>(candidates.Count);

            foreach (var cache in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var action = ComputeFinalAction(
                    cache.RuleVerdict, aiEnabled, aiTrustLevel,
                    cache.AiLabel, cache.AiConfidence, cache.UserDecision);

                if (action == FinalAction.Protected)
                {
                    // Protected 文件：无需哈希计算，立即计入进度但不入快照
                    protectedCount++;
                    completedCount++;
                    progress?.Report(completedCount);
                }
                else
                {
                    // 非 Protected 文件：暂存，进入阶段 2 进行哈希计算
                    nonProtected.Add((cache, action));
                }
            }

            _logger.LogDebug(
                "阶段1 完成: 总数={Total}, Protected={Protected}, 待哈希={ToHash}",
                candidates.Count, protectedCount, nonProtected.Count);

            // ========================================================
            // 阶段 2：并发计算 SHA-256 哈希
            // 使用 Parallel.ForEachAsync 控制并发度 CpuCount
            // 单个文件失败（IO/权限/不存在）记录警告并跳过，不中断整体流程
            // ========================================================
            var entryLock = new object();  // 保护 entries 列表的线程安全写入
            var entries = new List<DeleteSnapshotEntry>(nonProtected.Count);
            var failedCount = 0;

            if (nonProtected.Count > 0)
            {
                await Parallel.ForEachAsync(
                    nonProtected,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = ct
                    },
                    async (tuple, token) =>
                    {
                        var (cache, action) = tuple;
                        try
                        {
                            // 流式 SHA-256 计算 —— 不将整个文件加载到内存
                            var hash = await ComputeSha256HashAsync(cache.FilePath, token);

                            var entry = new DeleteSnapshotEntry
                            {
                                FilePath = cache.FilePath,
                                FileSize = cache.SizeBytes,
                                Sha256Hash = hash,
                                FinalAction = action,
                                OperationId = operationId
                            };

                            lock (entryLock)
                            {
                                entries.Add(entry);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw; // 取消请求穿透传播，由 Parallel.ForEachAsync 统一抛出
                        }
                        catch (Exception ex)
                        {
                            // 单个文件哈希失败不中断整体快照 —— 记录警告后跳过
                            var failedIndex = Interlocked.Increment(ref failedCount);
                            _logger.LogWarning(ex,
                                "文件 {Path} 的 SHA-256 哈希计算失败（第{Count}个跳过）: {Message}",
                                cache.FilePath, failedIndex, ex.Message);
                        }
                        finally
                        {
                            // 每完成一个文件（无论成功/失败）都上报一次进度
                            var current = Interlocked.Increment(ref completedCount);
                            progress?.Report(current);
                        }
                    });
            }

            // ========================================================
            // 构建不可变快照 — 深拷贝为 ReadOnlyCollection
            // 从此快照脱离所有外部可变缓存依赖
            // ========================================================
            var snapshot = new DecisionSnapshot
            {
                OperationId = operationId,
                Entries = new ReadOnlyCollection<DeleteSnapshotEntry>(entries),
                FrozenAt = frozenAt,
                TotalSizeBytes = entries.Sum(e => e.FileSize)
            };

            _logger.LogOperation(
                "DecisionSnapshot",
                $"OperationId={operationId}",
                $"条目={entries.Count}, 受保护={protectedCount}, 失败={failedCount}, 总大小={snapshot.TotalSizeBytes} 字节");

            _logger.LogMethodExit($"完成: {entries.Count} 条目, {failedCount} 失败");
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DecideAndSnapshotAsync 被用户取消——已处理 {Completed}/{Total} 个文件",
                completedCount, candidates.Count);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DecideAndSnapshotAsync 执行失败");
            throw;
        }
    }

    // ============================================================
    // 决策表纯函数：输入 ruleVerdict + AI上下文 → 输出 FinalAction
    // 完全无副作用，可单独单元测试所有组合
    // ============================================================

    /// <summary>
    /// 根据完整决策表（设计文档 6.3 节）计算最终操作决策。
    ///
    /// 决策优先级：
    /// 1. 用户手动决策（UserDecision）—— 用户明确勾选/取消，直接采纳
    /// 2. 情况 1（决定性规则）—— 不依赖 AI，直接映射
    /// 3. 情况 2/3（AI 参与）—— 仅在 AI 启用且有数据时生效
    /// 4. 情况 4（AI 不可用）—— SUGGEST_DELETE→SuggestDelete，KEEP→Protected
    /// </summary>
    /// <param name="ruleVerdict">规则引擎裁决结果</param>
    /// <param name="aiEnabled">是否启用 AI 顾问</param>
    /// <param name="aiTrustLevel">用户 AI 信任等级</param>
    /// <param name="aiLabel">AI 标签：safe / unknown / risky，无数据时为 null</param>
    /// <param name="aiConfidence">AI 置信度 0.0~1.0，无数据时为 null</param>
    /// <param name="userDecision">用户手动决策覆盖，无干预时为 null</param>
    /// <returns>最终的 FinalAction 枚举值</returns>
    private static FinalAction ComputeFinalAction(
        RuleVerdict ruleVerdict,
        bool aiEnabled,
        AITrustLevel aiTrustLevel,
        string? aiLabel,
        double? aiConfidence,
        FinalAction? userDecision)
    {
        // 优先级最高：用户手动决策 — 用户明确的操作直接采纳
        if (userDecision.HasValue)
            return userDecision.Value;

        // 情况 1：决定性规则 — 不依赖 AI，直接映射
        // 设计文档 6.3 节情况 1
        return ruleVerdict switch
        {
            RuleVerdict.Forbid => FinalAction.Protected,
            RuleVerdict.AutoDelete => FinalAction.AutoDelete,
            RuleVerdict.SuggestKeep => FinalAction.Protected,
            RuleVerdict.Exclude => FinalAction.Protected,
            RuleVerdict.Protect => FinalAction.Protected,

            // 情况 2：SUGGEST_DELETE — AI 启用时需结合 AI 标签判断
            RuleVerdict.SuggestDelete when aiEnabled =>
                ArbitrateSuggestDeleteWithAi(aiLabel, aiConfidence, aiTrustLevel),

            // 情况 3：KEEP — AI 启用时仅在 safe+high+高置信度时升级为建议删除
            RuleVerdict.Keep when aiEnabled =>
                ArbitrateKeepWithAi(aiLabel, aiConfidence, aiTrustLevel),

            // 情况 4：AI 禁用 或 无 AI 数据时 — 直接按规则裁决
            RuleVerdict.SuggestDelete => FinalAction.SuggestDelete,
            RuleVerdict.Keep => FinalAction.Protected,

            _ => FinalAction.ManualReview // 防御性兜底
        };
    }

    /// <summary>
    /// 情况 2（设计文档 6.3 节）：SUGGEST_DELETE + AI 启用
    ///
    /// 决策矩阵：
    /// | AI Label | AI Confidence | AI Trust       | FinalAction   |
    /// |----------|---------------|----------------|---------------|
    /// | safe     | 任意          | 任意           | SuggestDelete |
    /// | unknown  | <0.8          | 任意           | SuggestDelete |
    /// | unknown  | >=0.8         | Low/Medium     | SuggestDelete |
    /// | unknown  | >=0.8         | High           | ManualReview  |
    /// | risky    | <0.6          | 任意           | SuggestDelete |
    /// | risky    | >=0.6         | Low            | SuggestDelete |
    /// | risky    | >=0.6         | Medium/High    | ManualReview  |
    ///
    /// 核心思想：AI 标签不可信度越高、置信度越高、用户信任度越高 → 越倾向人工复核
    /// </summary>
    private static FinalAction ArbitrateSuggestDeleteWithAi(
        string? aiLabel, double? aiConfidence, AITrustLevel aiTrustLevel)
    {
        // AI 数据缺失 — 无法评估，人工复核
        if (aiLabel == null || aiConfidence == null)
            return FinalAction.ManualReview;

        // AI 标记为 safe — 无条件建议删除（AI 确认安全）
        if (aiLabel.Equals("safe", StringComparison.OrdinalIgnoreCase))
            return FinalAction.SuggestDelete;

        // AI 标记为 unknown — 高置信度 + 高信任度时才人工复核
        if (aiLabel.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (aiConfidence.Value >= 0.8 && aiTrustLevel == AITrustLevel.High)
                return FinalAction.ManualReview;
            return FinalAction.SuggestDelete;
        }

        // AI 标记为 risky — 高置信度 + 中/高信任度时人工复核
        if (aiLabel.Equals("risky", StringComparison.OrdinalIgnoreCase))
        {
            if (aiConfidence.Value >= 0.6 &&
                (aiTrustLevel == AITrustLevel.Medium || aiTrustLevel == AITrustLevel.High))
                return FinalAction.ManualReview;
            return FinalAction.SuggestDelete;
        }

        // 无法识别的 AI 标签 — 保守处理，人工复核
        return FinalAction.ManualReview;
    }

    /// <summary>
    /// 情况 3（设计文档 6.3 节）：KEEP + AI 启用
    ///
    /// 规则引擎默认保留的文件，AI 仅在极端条件下可升级为建议删除：
    ///   - AI 标记 safe
    ///   - 置信度 >= 0.7
    ///   - 用户信任度为 High
    ///
    /// 所有其他组合均保持 ManualReview（需人工介入，不能自动删除）
    /// </summary>
    private static FinalAction ArbitrateKeepWithAi(
        string? aiLabel, double? aiConfidence, AITrustLevel aiTrustLevel)
    {
        // 仅当 AI 认为 safe + 高置信度 + 用户高信任度时，升级为建议删除
        if (aiLabel != null
            && aiConfidence.HasValue
            && aiLabel.Equals("safe", StringComparison.OrdinalIgnoreCase)
            && aiTrustLevel == AITrustLevel.High
            && aiConfidence.Value >= 0.7)
        {
            return FinalAction.SuggestDelete;
        }

        // 其他所有情况：人工复核（规则默认为保留，AI 无法充分推翻）
        return FinalAction.ManualReview;
    }

    // ============================================================
    // SHA-256 流式哈希计算（静态方法，纯 I/O，无副作用）
    // ============================================================

    /// <summary>
    /// 对指定文件进行流式 SHA-256 哈希计算。
    ///
    /// 关键设计：
    /// - FileShare.Read：不独占文件锁，允许其他进程同时读取
    /// - bufferSize=4096：4KB 缓冲区，避免将大文件整体加载到内存
    /// - useAsync=true：利用 Windows IOCP 真异步 I/O，不阻塞线程池
    /// - Convert.ToHexStringLower：.NET 8 原生零分配小写十六进制转换
    ///
    /// 内存占用：O(1)，与文件大小无关
    /// </summary>
    /// <param name="filePath">文件完整路径（\\?\ 格式）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>小写十六进制 SHA-256 哈希字符串</returns>
    private static async Task<string> ComputeSha256HashAsync(
        string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        using var sha256 = SHA256.Create();

        // ComputeHashAsync 内部流式读取，不会一次加载整个文件
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);

        // .NET 8 原生方法，无需额外分配字符串
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
