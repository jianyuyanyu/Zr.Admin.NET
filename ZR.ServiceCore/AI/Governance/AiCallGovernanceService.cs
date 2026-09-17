using Infrastructure;
using Infrastructure.AI;
using Infrastructure.Attribute;
using Infrastructure.Cache;
using Infrastructure.Model;
using Microsoft.Extensions.Options;
using NLog;
using ZR.Model.AI;

namespace ZR.ServiceCore.AI.Governance
{
    /// <summary>
    /// AI 统一治理入口：主库策略解析、额度检查、单实例并发控制及调用流水结算。
    /// 角色策略表示“具有该角色的每用户额度”，不作为角色成员共享池。
    /// </summary>
    [AppService(ServiceType = typeof(IAiCallGovernance), ServiceLifetime = LifeTime.Transient)]
    public class AiCallGovernanceService : BaseService<AiAccessPolicy>, IAiCallGovernance
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly object ReservationLock = new();
        // ⚠️ 单实例假设：并发计数与在途预留是进程内状态，多实例部署时各进程独立计数，
        // 实际并发上限 = 实例数 × 配置值（限流被放大）。需要严格全局并发时，应改用 Redis 等
        // 分布式计数（项目已有 Infrastructure/Cache/CacheStore、RedisServer 可复用）。
        private static readonly Dictionary<string, int> ConcurrentCounts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ActiveReservation> ActiveReservations = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>分钟限流键过期时间（分钟）：需覆盖一个分钟桶并留裕量，防止跨分钟边界丢计数。</summary>
        private const int ChatRateWindowExpireMinutes = 2;
        // ⚠️ 单实例/单库假设：计量断路器是进程内全局开关，任一 DB 读写失败即对所有租户/用户关闭普通 AI
        // 调用（fail-closed），由后续任一次成功落库自动恢复。多实例下各进程独立判定；若启用多租户分库，
        // 应改为按租户隔离的断路器，避免单个租户 DB 抖动熔断全站。
        private static volatile bool AccountingHealthy = true;
        private readonly AiOptions _options;

        public AiCallGovernanceService(IOptions<OptionsSetting> options)
        {
            _options = options.Value?.AiOptions ?? new AiOptions();
        }

        /// <summary>
        /// 开始一次 AI 调用治理流程，返回租约对象，调用方应在调用完成后调用 CompleteAsync 结算。
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<AiCallLease> BeginAsync(AiCallRequest request)
        {
            request ??= new AiCallRequest();
            var actor = AiCallActorScope.Current;
            var user = App.HttpContext?.GetCurrentUser();
            // 身份只取可信来源：后台/异步调用优先 AiCallActorScope（由可信代码显式声明），HTTP 请求回退登录上下文；
            // 不接受调用方在 AiCallRequest 中自报身份，避免越权归因或跨用户刷额度。
            var tenantId = Normalize(actor?.TenantId, App.GetCurrentTenantId());
            var userId = actor?.UserId > 0 ? actor.UserId : user?.UserId ?? 0;
            var userName = Normalize(actor?.UserName, user?.UserName);
            // 角色层策略依赖 RoleIds：HTTP 从登录上下文取，后台从 actor 快照取（缺则视为无角色）。
            var roleIds = actor?.RoleIds?.Count > 0
                ? actor.RoleIds
                : user?.Roles?.Select(x => x.RoleId).ToArray() ?? Array.Empty<long>();
            var scene = Normalize(request.Scene, "unknown").ToLowerInvariant();
            // 结算幂等键：RequestId 每租约生成一次，ai_call_log 上建有唯一索引 uk_ai_call_request。
            // 因此"重试"必须复用同一租约（重试落在 AiLlmClient 内部、BeginGovernedCallAsync 之后），
            // 若把重试提到网关 AiChatLlmGateway 层，每次重试都会重新 BeginAsync 生成新 GUID，
            // 唯一索引就挡不住重复落库与重复计费。
            var requestId = Guid.NewGuid().ToString("N");

            AiModelPrice price = null;
            try
            {
                // 价格表读多写极少，直接用 SqlSugar 查询缓存：缓存服务由 DbCache 配置决定
                // （启用 dbCache 走 Redis，否则内存），且 IsAutoRemoveDataCache=true 会在价格变更时自动清缓存。
                price = await Context.Queryable<AiModelPrice>()
                    .Where(x => x.Status == 0
                        && x.Provider.ToLower() == Normalize(request.Provider, "unknown").ToLowerInvariant()
                        && x.Model.ToLower() == Normalize(request.Model, "unknown").ToLowerInvariant())
                    .WithCache(60 * 5)
                    .FirstAsync();
            }
            catch (Exception ex)
            {
                // 读价失败（DB 或缓存后端异常）不再继续：金额额度会失去校验依据，用量还会被记成 0。
                // 与 GetPolicies / GetUsageAsync 同口径：记日志 + 置不健康 + 抛治理异常（fail-closed）。
                // 健康标志由后续任一次成功落库自动恢复（见 CompleteAsync）。
                Logger.Warn(ex, "读取 AI 模型价格失败，暂停普通 AI 调用");
                AccountingHealthy = false;
                throw new AiGovernanceDeniedException("governance_unavailable", "AI 计量服务暂不可用，请稍后重试");
            }

            var estimatedPrompt = Math.Max(0, request.EstimatedPromptTokens);
            var estimatedCompletion = Math.Max(0, request.MaxCompletionTokens);
            var reservedTokens = (long)estimatedPrompt + estimatedCompletion;
            var reservedAmount = CalculateAmount(price, estimatedPrompt, estimatedCompletion);

            var lease = new AiCallLease
            {
                RequestId = requestId,
                TraceId = App.HttpContext?.TraceIdentifier,
                Scene = scene,
                Provider = Normalize(request.Provider, "unknown"),
                Model = Normalize(request.Model, "unknown"),
                TenantId = tenantId,
                UserId = userId,
                UserName = userName,
                IsStream = request.IsStream,
                StartedAt = DateTime.Now,
                InputPricePerMillion = price?.InputPricePerMillion ?? 0,
                OutputPricePerMillion = price?.OutputPricePerMillion ?? 0,
                Currency = Normalize(price?.Currency, "CNY"),
                ReservedTokens = reservedTokens,
                ReservedAmount = reservedAmount
            };

            if (!_options.Enable)
            {
                await RejectAsync(lease, "disabled", "AI 功能当前已停用");
            }
            if (!AccountingHealthy)
            {
                await RejectAsync(lease, "governance_unavailable", "AI 计量服务暂不可用，请稍后重试");
            }

            var (globalLimits, tenantLimits, subjectLimits, allLimits) =
                await ResolvePolicyLayersAsync(tenantId, userId, roleIds, scene);

            if (allLimits.Any(x => x.Disabled))
            {
                await RejectAsync(lease, "disabled", "当前账号或场景未启用 AI 功能");
            }
            if (price == null && allLimits.Any(x => x.HasAmountLimit))
            {
                await RejectAsync(lease, "price_missing", "当前模型未配置单价，无法安全执行金额额度校验");
            }

            var now = DateTime.Now;
            var dayBegin = now.Date;
            var monthBegin = new DateTime(now.Year, now.Month, 1);

            // 分钟限流：CacheStore 原子计数（Redis 下为网络调用），本身即原子，不必占锁区，尽早拒绝。
            if (scene == AiSceneCatalog.AiChat && userId > 0
                && !TryAcquireChatRateSlot(tenantId, userId, now, out var rateMessage))
            {
                await RejectAsync(lease, "minute_calls", rateMessage);
            }

            // 额度用量预读：DB 查询放在锁区之外，避免把并发调用串行化（原先这些查询都在全局闸门内）。
            var probes = new List<QuotaProbe>();
            CollectProbes(probes, globalLimits, "global");
            CollectProbes(probes, tenantLimits, $"tenant:{tenantId}");
            CollectProbes(probes, subjectLimits, $"user:{tenantId}:{userId}");
            foreach (var probe in probes)
            {
                if (probe.Limit.DailyTokenLimit.HasValue || probe.Limit.DailyAmountLimit.HasValue)
                {
                    probe.Day = await GetUsageAsync(lease, probe.ScopeKey, probe.Limit.SceneFilter, dayBegin);
                }
                if (probe.Limit.MonthlyTokenLimit.HasValue || probe.Limit.MonthlyAmountLimit.HasValue)
                {
                    probe.Month = await GetUsageAsync(lease, probe.ScopeKey, probe.Limit.SceneFilter, monthBegin);
                }
            }

            var concurrencyKeys = new List<(string Key, int Limit)>();
            foreach (var limit in globalLimits)
                AddConcurrency(concurrencyKeys, $"global|{limit.SceneFilter}", limit.ConcurrentLimit);
            foreach (var limit in tenantLimits)
                AddConcurrency(concurrencyKeys, $"tenant:{tenantId}|{limit.SceneFilter}", limit.ConcurrentLimit);
            foreach (var limit in subjectLimits)
                AddConcurrency(concurrencyKeys, $"user:{tenantId}:{userId}|{limit.SceneFilter}", limit.ConcurrentLimit);
            concurrencyKeys = concurrencyKeys
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Key, x.Min(y => y.Limit)))
                .ToList();

            // 锁区内只做纯内存的复核与预留：在途预留（active）在锁内实时读取，判定口径与原先一致。
            string rejectReason = null, rejectMessage = null;
            lock (ReservationLock)
            {
                foreach (var probe in probes)
                {
                    var active = GetActiveUsage(probe.ReservationKey);
                    var limit = probe.Limit;
                    if (limit.DailyTokenLimit.HasValue
                        && probe.Day.Tokens + active.Tokens + lease.ReservedTokens > limit.DailyTokenLimit.Value)
                    {
                        rejectReason = "daily_token";
                        rejectMessage = "今日 AI Token 额度已用尽";
                        break;
                    }
                    if (limit.MonthlyTokenLimit.HasValue
                        && probe.Month.Tokens + active.Tokens + lease.ReservedTokens > limit.MonthlyTokenLimit.Value)
                    {
                        rejectReason = "monthly_token";
                        rejectMessage = "本月 AI Token 额度已用尽";
                        break;
                    }
                    if (limit.DailyAmountLimit.HasValue
                        && probe.Day.Amount + active.Amount + lease.ReservedAmount > limit.DailyAmountLimit.Value)
                    {
                        rejectReason = "daily_amount";
                        rejectMessage = "今日 AI 金额额度已用尽";
                        break;
                    }
                    if (limit.MonthlyAmountLimit.HasValue
                        && probe.Month.Amount + active.Amount + lease.ReservedAmount > limit.MonthlyAmountLimit.Value)
                    {
                        rejectReason = "monthly_amount";
                        rejectMessage = "本月 AI 金额额度已用尽";
                        break;
                    }
                }

                if (rejectReason == null)
                {
                    var concurrencyDenied = false;
                    foreach (var item in concurrencyKeys)
                    {
                        ConcurrentCounts.TryGetValue(item.Key, out var current);
                        if (current >= item.Limit)
                        {
                            concurrencyDenied = true;
                            break;
                        }
                    }

                    if (concurrencyDenied)
                    {
                        rejectReason = "concurrency";
                        rejectMessage = "AI 并发请求数已达上限，请稍后重试";
                    }
                    else
                    {
                        foreach (var item in concurrencyKeys)
                        {
                            ConcurrentCounts[item.Key] = ConcurrentCounts.GetValueOrDefault(item.Key) + 1;
                        }
                        lease.ConcurrencyKeys = concurrencyKeys.Select(x => x.Key).ToArray();
                        ActiveReservations[lease.RequestId] = new ActiveReservation
                        {
                            ScopeKeys = BuildQuotaScopeKeys(tenantId, userId, scene),
                            Tokens = reservedTokens,
                            Amount = reservedAmount
                        };
                    }
                }
            }

            // 拒答落库（DB 写入）移出锁区，避免拒答洪峰把全局串行时间拉长
            if (rejectReason != null)
            {
                await RejectAsync(lease, rejectReason, rejectMessage);
            }
            return lease;
        }

        /// <summary>
        /// 完成一次 AI 调用治理流程，记录调用结果和计量信息。
        /// </summary>
        /// <param name="lease"></param>
        /// <param name="outcome"></param>
        /// <returns></returns>
        public async Task CompleteAsync(AiCallLease lease, AiCallOutcome outcome)
        {
            if (lease == null) return;
            outcome ??= new AiCallOutcome { Status = "unknown", ErrorType = "unknown" };

            var totalTokens = outcome.TotalTokens;
            var estimatedAmount = 0m;
            var useReservation = !outcome.HasUsage
                && (outcome.Success || outcome.Status is "timeout" or "cancelled"
                    || outcome.ErrorType is "stream_interrupted" or "client_cancelled");
            if (useReservation)
            {
                totalTokens = (int)Math.Min(int.MaxValue, lease.ReservedTokens);
                estimatedAmount = lease.ReservedAmount;
                if (outcome.Success)
                {
                    outcome.Status = "metering_incomplete";
                    outcome.ErrorType = "usage_missing";
                }
                outcome.ErrorMessage = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                    ? "Provider 未返回 usage，按调用前预估值记账"
                    : $"{outcome.ErrorMessage}；Provider 未返回 usage，按预估值记账";
            }
            var inputAmount = lease.InputPricePerMillion * outcome.PromptTokens / 1_000_000m;
            var outputAmount = lease.OutputPricePerMillion * outcome.CompletionTokens / 1_000_000m;
            if (outcome.HasUsage) estimatedAmount = inputAmount + outputAmount;
            try
            {
                if (await Context.Queryable<AiCallLog>().AnyAsync(x => x.RequestId == lease.RequestId))
                {
                    AccountingHealthy = true;
                    return;
                }
                await Context.Insertable(new AiCallLog
                {
                    Scene = AiHelper.Truncate(lease.Scene, 64),
                    Provider = lease.Provider,
                    Model = lease.Model,
                    TenantId = lease.TenantId,
                    RequestId = AiHelper.Truncate(lease.RequestId, 64),
                    TraceId = AiHelper.Truncate(lease.TraceId, 64),
                    Success = outcome.Success ? 1 : 0,
                    Status = AiHelper.Truncate(outcome.Status ?? (outcome.Success ? "success" : "unknown"), 32),
                    ErrorType = AiHelper.Truncate(outcome.ErrorType, 64),
                    ErrorMsg = AiHelper.Truncate(outcome.ErrorMessage, 1000),
                    HttpStatusCode = outcome.HttpStatusCode,
                    DurationMs = Math.Max(0, (long)(DateTime.Now - lease.StartedAt).TotalMilliseconds),
                    ProviderRequestId = AiHelper.Truncate(outcome.ProviderRequestId, 128),
                    IsStream = lease.IsStream ? 1 : 0,
                    PromptTokens = outcome.PromptTokens,
                    CompletionTokens = outcome.CompletionTokens,
                    TotalTokens = totalTokens,
                    HasUsage = outcome.HasUsage ? 1 : 0,
                    InputAmount = inputAmount,
                    OutputAmount = outputAmount,
                    EstimatedAmount = estimatedAmount,
                    Currency = lease.Currency ?? "CNY",
                    UserId = lease.UserId,
                    UserName = lease.UserName
                }).ExecuteReturnSnowflakeIdAsync();
                AccountingHealthy = true;
            }
            catch (Exception ex)
            {
                // 并发/重入下同一 RequestId 双写时，唯一索引 uk_ai_call_request 会让其中一方插入失败。
                // 该租约已落库属于"幂等命中"，不能当作计量故障 —— 否则一次并发写失败会把全局会计标记
                // 置为不健康，熔断全站普通 AI 调用（要到下一次成功落库才恢复）。
                // 判定刻意不匹配各库各异的唯一键错误码，改为复查该 RequestId 是否已存在：与数据库无关，
                // 且能覆盖"第一次 AnyAsync 快路径本身失败"的回退场景。
                var persisted = false;
                try
                {
                    persisted = await Context.Queryable<AiCallLog>().AnyAsync(x => x.RequestId == lease.RequestId);
                }
                catch (Exception probeEx)
                {
                    // 复查自身失败（DB 不可用等）不得空 catch 吞掉：记录后走不健康分支
                    Logger.Warn(probeEx, "AI 调用流水唯一键冲突复查失败 requestId={RequestId}", lease.RequestId);
                }

                if (persisted)
                {
                    AccountingHealthy = true;
                    Logger.Info("AI 调用流水已由并发方写入，按幂等命中处理 requestId={RequestId}", lease.RequestId);
                }
                else
                {
                    AccountingHealthy = false;
                    Logger.Warn(ex, "写入 AI 调用治理流水失败 requestId={RequestId}", lease.RequestId);
                }
            }
            finally
            {
                Release(lease);
            }
        }

        /// <summary>
        /// 获取当前用户在指定场景下的 AI 额度快照，包含已用额度和剩余额度。
        /// </summary>
        /// <param name="scene"></param>
        /// <returns></returns>
        public async Task<AiQuotaSnapshot> GetMyQuotaAsync(string scene = AiSceneCatalog.AiChat)
        {
            scene = Normalize(scene, AiSceneCatalog.AiChat).ToLowerInvariant();
            var actor = AiCallActorScope.Current;
            var user = App.HttpContext?.GetCurrentUser();
            var tenantId = Normalize(actor?.TenantId, App.GetCurrentTenantId());
            var userId = actor?.UserId > 0 ? actor.UserId : user?.UserId ?? 0;
            // 与 BeginAsync 同口径：角色层策略也需覆盖后台/异步调用（actor 快照优先）。
            var roleIds = actor?.RoleIds?.Count > 0
                ? actor.RoleIds
                : user?.Roles?.Select(x => x.RoleId).ToArray() ?? Array.Empty<long>();

            var snapshot = new AiQuotaSnapshot
            {
                Scene = scene,
                Allowed = true,
                Currency = "CNY"
            };

            if (!_options.Enable)
            {
                snapshot.Allowed = false;
                snapshot.DeniedReason = "AI 功能当前已停用";
                return snapshot;
            }

            var (_, _, _, allLimits) = await ResolvePolicyLayersAsync(tenantId, userId, roleIds, scene);

            if (allLimits.Any(x => x.Disabled))
            {
                snapshot.Allowed = false;
                snapshot.DeniedReason = "当前账号或场景未启用 AI 功能";
            }

            snapshot.DailyTokenLimit = Min(allLimits.Select(x => x.DailyTokenLimit));
            snapshot.MonthlyTokenLimit = Min(allLimits.Select(x => x.MonthlyTokenLimit));
            snapshot.DailyAmountLimit = Min(allLimits.Select(x => x.DailyAmountLimit));
            snapshot.MonthlyAmountLimit = Min(allLimits.Select(x => x.MonthlyAmountLimit));

            var sceneFilter = allLimits.Any(x => !string.IsNullOrWhiteSpace(x.SceneFilter) && x.SceneFilter != "*")
                ? scene
                : "*";
            var lease = new AiCallLease { TenantId = tenantId, UserId = userId };
            var now = DateTime.Now;
            var dayUsage = await GetUsageAsync(lease, $"user:{tenantId}:{userId}", sceneFilter, now.Date);
            var monthUsage = await GetUsageAsync(lease, $"user:{tenantId}:{userId}", sceneFilter, new DateTime(now.Year, now.Month, 1));
            snapshot.DailyTokenUsed = dayUsage.Tokens;
            snapshot.DailyAmountUsed = dayUsage.Amount;
            snapshot.MonthlyTokenUsed = monthUsage.Tokens;
            snapshot.MonthlyAmountUsed = monthUsage.Amount;
            return snapshot;
        }

        private async Task<(List<PolicyLimit> Global, List<PolicyLimit> Tenant, List<PolicyLimit> Subject, List<PolicyLimit> All)>
            ResolvePolicyLayersAsync(string tenantId, long userId, IReadOnlyList<long> roleIds, string scene)
        {
            var policies = await GetPoliciesAsync(tenantId);
            var globalLimits = ResolveConstraints(policies, "global", string.Empty, [0], scene);
            var tenantLimits = ResolveConstraints(policies, "tenant", tenantId, [0], scene);
            var roleLimits = ResolveConstraints(policies, "role", tenantId, roleIds, scene);
            var userLimits = ResolveConstraints(policies, "user", tenantId, [userId], scene);
            var subjectLimits = roleLimits.Concat(userLimits).ToList();
            if (subjectLimits.Count == 0 && scene == AiSceneCatalog.AiChat && _options.DefaultUserTotalTokens > 0)
            {
                subjectLimits.Add(new PolicyLimit
                {
                    HasPolicy = true,
                    MonthlyTokenLimit = _options.DefaultUserTotalTokens,
                    SceneFilter = AiSceneCatalog.AiChat
                });
            }
            var allLimits = globalLimits.Concat(tenantLimits).Concat(subjectLimits).ToList();
            return (globalLimits, tenantLimits, subjectLimits, allLimits);
        }

        /// <summary>
        /// 读取生效中的治理策略。策略表读多写极少，用 SqlSugar 查询缓存（缓存服务由 DbCache 配置决定：
        /// 启用 dbCache 走 Redis，否则内存）；策略增删改由 IsAutoRemoveDataCache 自动清缓存，无需手动失效。
        /// 读取失败 fail-closed（与价格 / 用量读取同口径）。
        /// </summary>
        private async Task<List<AiAccessPolicy>> GetPoliciesAsync(string tenantId)
        {
            try
            {
                return await Context.Queryable<AiAccessPolicy>()
                    .Where(x => x.Status == 0 && (x.ScopeType == "global" || x.TenantId == tenantId))
                    .WithCache(30)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取 AI 治理策略失败，已关闭普通 AI 调用 tenantId={TenantId}", tenantId);
                AccountingHealthy = false;
                throw new AiGovernanceDeniedException("governance_unavailable", "AI 治理策略暂不可用，请稍后重试");
            }
        }

        /// <summary>把某一层级的有效额度策略收集为校验探针（保留 全局 → 租户 → 用户 的判定顺序）。</summary>
        private static void CollectProbes(List<QuotaProbe> target, IEnumerable<PolicyLimit> limits, string scopeKey)
        {
            foreach (var limit in limits)
            {
                if (!limit.HasAnyLimit) continue;
                target.Add(new QuotaProbe
                {
                    Limit = limit,
                    ScopeKey = scopeKey,
                    ReservationKey = $"{scopeKey}|{limit.SceneFilter}"
                });
            }
        }

        private async Task<UsageValue> GetUsageAsync(AiCallLease lease, string scopeKey, string sceneFilter, DateTime begin)
        {
            try
            {
                var query = Context.Queryable<AiCallLog>()
                    .Where(x => x.CreateTime >= begin && x.Status != "rejected");
                if (scopeKey.StartsWith("tenant:", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(x => x.TenantId == lease.TenantId);
                }
                else if (scopeKey.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(x => x.TenantId == lease.TenantId && x.UserId == lease.UserId);
                }
                if (!string.IsNullOrWhiteSpace(sceneFilter) && sceneFilter != "*")
                {
                    query = query.Where(x => x.Scene == sceneFilter);
                }
                // 同一次过滤条件只用一条 SQL 取回 Token 与金额两个合计
                // （原先 SUM(TotalTokens)、SUM(EstimatedAmount) 各发一条，同样的 WHERE 跑了两遍）
                var sum = await query
                    .Select(x => new UsageSumRow
                    {
                        Tokens = SqlFunc.AggregateSum(x.TotalTokens),
                        Amount = SqlFunc.AggregateSum(x.EstimatedAmount)
                    })
                    .FirstAsync();
                return new UsageValue
                {
                    Tokens = sum?.Tokens ?? 0,
                    Amount = sum?.Amount ?? 0
                };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取 AI 累计额度失败 scope={Scope}", scopeKey);
                AccountingHealthy = false;
                throw new AiGovernanceDeniedException("governance_unavailable", "AI 额度计量暂不可用，请稍后重试");
            }
        }

        private static UsageValue GetActiveUsage(string scopeKey)
        {
            lock (ReservationLock)
            {
                var matching = ActiveReservations.Values.Where(x => x.ScopeKeys.Contains(scopeKey));
                return new UsageValue
                {
                    Tokens = matching.Sum(x => x.Tokens),
                    Amount = matching.Sum(x => x.Amount)
                };
            }
        }

        /// <summary>分钟限流计数键：按「租户 + 用户 + 分钟桶」隔离，分钟切换即自然开新窗口。</summary>
        private static string ChatRateKey(string tenantId, long userId, DateTime now) =>
            $"ai:chatrate:{tenantId}:{userId}:{now:yyyyMMddHHmm}";

        /// <summary>
        /// 尝试占用一个分钟限流额度：基于 CacheStore 原子自增，超过上限返回 false。
        /// RedisServer:open=1 时为跨节点全局计数，否则自动回落进程内内存（单实例语义）。
        /// 计数异常时放行（fail-open），避免缓存抖动熔断全部对话。
        /// </summary>
        private bool TryAcquireChatRateSlot(string tenantId, long userId, DateTime now, out string message)
        {
            message = null;
            var limit = _options.ChatRateLimitPerMinute;
            if (limit <= 0) return true;
            long count;
            try
            {
                count = CacheStore.Default.Increment(ChatRateKey(tenantId, userId, now), ChatRateWindowExpireMinutes);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "AI 分钟限流计数失败，本次放行 tenantId={TenantId} userId={UserId}", tenantId, userId);
                return true;
            }
            if (count <= limit) return true;
            message = $"发送过于频繁，请稍后再试（每分钟最多 {limit} 次模型调用）";
            return false;
        }

        private static IReadOnlyList<string> BuildQuotaScopeKeys(string tenantId, long userId, string scene) =>
            [
                "global|*", $"global|{scene}",
                $"tenant:{tenantId}|*", $"tenant:{tenantId}|{scene}",
                $"user:{tenantId}:{userId}|*", $"user:{tenantId}:{userId}|{scene}"
            ];

        private static void AddConcurrency(List<(string Key, int Limit)> items, string key, int? limit)
        {
            if (limit > 0) items.Add((key, limit.Value));
        }

        private static void Release(AiCallLease lease)
        {
            lock (ReservationLock)
            {
                var hadReservation = ActiveReservations.Remove(lease.RequestId);
                if (!hadReservation) return;
                foreach (var key in lease.ConcurrencyKeys ?? Array.Empty<string>())
                {
                    if (!ConcurrentCounts.TryGetValue(key, out var count)) continue;
                    if (count <= 1) ConcurrentCounts.Remove(key);
                    else ConcurrentCounts[key] = count - 1;
                }
            }
        }

        private async Task RejectAsync(AiCallLease lease, string reason, string message)
        {
            await RecordRejectedWithoutThrowAsync(lease, reason, message);
            throw new AiGovernanceDeniedException(reason, message);
        }

        private Task RecordRejectedWithoutThrowAsync(AiCallLease lease, string reason, string message) =>
            CompleteAsync(lease, new AiCallOutcome
            {
                Success = false,
                Status = "rejected",
                ErrorType = reason,
                ErrorMessage = message
            });

        internal static List<PolicyLimit> ResolveConstraints(
            IEnumerable<AiAccessPolicy> policies, string scopeType, string tenantId,
            IReadOnlyList<long> subjectIds, string scene)
        {
            var ids = subjectIds?.ToHashSet() ?? new HashSet<long>();
            return policies
                .Where(x => string.Equals(x.ScopeType, scopeType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.TenantId ?? string.Empty, tenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && ids.Contains(x.SubjectId)
                    && (x.Scene == "*" || string.Equals(x.Scene, scene, StringComparison.OrdinalIgnoreCase)))
                .Select(FromPolicy)
                .ToList();
        }

        private static PolicyLimit FromPolicy(AiAccessPolicy policy) => policy == null
            ? PolicyLimit.Empty
            : new PolicyLimit
            {
                HasPolicy = true,
                Disabled = policy.IsEnabled == 0,
                SceneFilter = string.IsNullOrWhiteSpace(policy.Scene) ? "*" : policy.Scene,
                DailyTokenLimit = Positive(policy.DailyTokenLimit),
                MonthlyTokenLimit = Positive(policy.MonthlyTokenLimit),
                DailyAmountLimit = Positive(policy.DailyAmountLimit),
                MonthlyAmountLimit = Positive(policy.MonthlyAmountLimit),
                ConcurrentLimit = Positive(policy.ConcurrentLimit)
            };

        private static long? Positive(long? value) => value > 0 ? value : null;
        private static int? Positive(int? value) => value > 0 ? value : null;
        private static decimal? Positive(decimal? value) => value > 0 ? value : null;
        private static T? Min<T>(IEnumerable<T?> values) where T : struct, IComparable<T>
        {
            var present = values.Where(x => x.HasValue).Select(x => x.Value).ToList();
            return present.Count == 0 ? null : present.Min();
        }

        internal static decimal CalculateAmount(AiModelPrice price, int prompt, int completion) =>
            price == null ? 0 : price.InputPricePerMillion * prompt / 1_000_000m
                + price.OutputPricePerMillion * completion / 1_000_000m;

        private static string Normalize(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim();

        internal sealed class PolicyLimit
        {
            public static readonly PolicyLimit Empty = new();
            public bool HasPolicy { get; set; }
            public bool Disabled { get; set; }
            public string SceneFilter { get; set; } = "*";
            public long? DailyTokenLimit { get; set; }
            public long? MonthlyTokenLimit { get; set; }
            public decimal? DailyAmountLimit { get; set; }
            public decimal? MonthlyAmountLimit { get; set; }
            public int? ConcurrentLimit { get; set; }
            public bool HasAnyLimit => DailyTokenLimit.HasValue || MonthlyTokenLimit.HasValue
                || DailyAmountLimit.HasValue || MonthlyAmountLimit.HasValue;
            public bool HasAmountLimit => DailyAmountLimit.HasValue || MonthlyAmountLimit.HasValue;
        }

        private sealed class ActiveReservation
        {
            public IReadOnlyList<string> ScopeKeys { get; set; }
            public long Tokens { get; set; }
            public decimal Amount { get; set; }
        }

        private sealed class UsageValue
        {
            public long Tokens { get; set; }
            public decimal Amount { get; set; }
        }

        /// <summary>
        /// 额度校验探针：锁区外预读「当天/当月」用量，锁区内再结合在途预留做复核，
        /// 使 DB 查询不落在全局串行区内；只预读真正配了额度的周期。
        /// </summary>
        private sealed class QuotaProbe
        {
            public PolicyLimit Limit { get; set; }
            public string ScopeKey { get; set; }
            public string ReservationKey { get; set; }
            public UsageValue Day { get; set; } = new();
            public UsageValue Month { get; set; } = new();
        }

        /// <summary>
        /// 用量聚合查询投影：一条 SQL 同时返回 Token / 金额合计。
        /// 属性可空——区间内没有记录时 SUM 返回 NULL（SqlSugar 会把 NULL 映射为 null）。
        /// 公开类型：SqlSugar 构造投影对象需要类型可访问。
        /// </summary>
        public sealed class UsageSumRow
        {
            public long? Tokens { get; set; }
            public decimal? Amount { get; set; }
        }
    }
}
