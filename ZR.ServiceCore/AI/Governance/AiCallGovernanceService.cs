using Infrastructure;
using Infrastructure.AI;
using Infrastructure.Attribute;
using Infrastructure.Model;
using Microsoft.Extensions.Caching.Memory;
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
        private static readonly SemaphoreSlim QuotaGate = new(1, 1);
        private static readonly Dictionary<string, int> ConcurrentCounts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ActiveReservation> ActiveReservations = new(StringComparer.OrdinalIgnoreCase);
        private static volatile bool AccountingHealthy = true;
        private readonly IMemoryCache _cache;
        private readonly AiOptions _options;

        public AiCallGovernanceService(IMemoryCache cache, IOptions<OptionsSetting> options)
        {
            _cache = cache;
            _options = options.Value?.AiOptions ?? new AiOptions();
        }

        public async Task<AiCallLease> BeginAsync(AiCallRequest request)
        {
            request ??= new AiCallRequest();
            var actor = AiCallActorScope.Current;
            var tenantId = Normalize(request.TenantId, Normalize(actor?.TenantId, App.GetCurrentTenantId()));
            var user = App.HttpContext?.GetCurrentUser();
            var userId = request.UserId > 0 ? request.UserId : actor?.UserId > 0 ? actor.UserId : user?.UserId ?? 0;
            var userName = Normalize(request.UserName, Normalize(actor?.UserName, user?.UserName));
            var roleIds = request.RoleIds?.Count > 0
                ? request.RoleIds
                : user?.Roles?.Select(x => x.RoleId).ToArray() ?? Array.Empty<long>();
            var scene = Normalize(request.Scene, "unknown").ToLowerInvariant();
            var requestId = Guid.NewGuid().ToString("N");

            AiModelPrice price = null;
            try
            {
                price = Context.Queryable<AiModelPrice>()
                    .Where(x => x.Status == 0
                        && x.Provider.ToLower() == Normalize(request.Provider, "unknown").ToLowerInvariant()
                        && x.Model.ToLower() == Normalize(request.Model, "unknown").ToLowerInvariant())
                    .First();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取 AI 模型价格失败，将按 0 金额继续");
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

            var policies = GetPolicies(tenantId);
            var globalLimits = ResolveConstraints(policies, "global", string.Empty, [0], scene);
            var tenantLimits = ResolveConstraints(policies, "tenant", tenantId, [0], scene);
            var roleLimits = ResolveConstraints(policies, "role", tenantId, roleIds, scene);
            var userLimits = ResolveConstraints(policies, "user", tenantId, [userId], scene);
            var subjectLimits = roleLimits.Concat(userLimits).ToList();
            var allLimits = globalLimits.Concat(tenantLimits).Concat(subjectLimits).ToList();

            if (allLimits.Any(x => x.Disabled))
            {
                await RejectAsync(lease, "disabled", "当前账号或场景未启用 AI 功能");
            }
            if (price == null && allLimits.Any(x => x.HasAmountLimit))
            {
                await RejectAsync(lease, "price_missing", "当前模型未配置单价，无法安全执行金额额度校验");
            }

            // 无数据库角色/用户额度时，兼容原 AI 聊天月 Token 默认值。
            if (subjectLimits.Count == 0 && scene == "ai_chat" && _options.DefaultUserTotalTokens > 0)
            {
                subjectLimits.Add(new PolicyLimit
                {
                    HasPolicy = true,
                    MonthlyTokenLimit = _options.DefaultUserTotalTokens,
                    SceneFilter = "ai_chat"
                });
            }

            await QuotaGate.WaitAsync();
            try
            {
            var now = DateTime.Now;
            if (scene == "ai_chat" && userId > 0 && _options.ChatRateLimitPerMinute > 0)
            {
                var minuteBegin = now.AddMinutes(-1);
                var recent = 0;
                try
                {
                    recent = Context.Queryable<AiCallLog>()
                        .Where(x => x.TenantId == tenantId && x.UserId == userId
                            && x.Scene == "ai_chat" && x.CreateTime >= minuteBegin
                            && x.Status != "rejected")
                        .Count();
                }
                catch (Exception ex)
                {
                    AccountingHealthy = false;
                    Logger.Warn(ex, "读取 AI 分钟额度失败，已关闭普通 AI 调用");
                    await RejectAsync(lease, "governance_unavailable", "AI 额度计量暂不可用，请稍后重试");
                }
                var activeCalls = GetActiveCount($"user:{tenantId}:{userId}|ai_chat");
                if (recent + activeCalls >= _options.ChatRateLimitPerMinute)
                {
                    await RejectAsync(lease, "minute_calls", $"发送过于频繁，请稍后再试（每分钟最多 {_options.ChatRateLimitPerMinute} 次模型调用）");
                }
            }

            var dayBegin = now.Date;
            var monthBegin = new DateTime(now.Year, now.Month, 1);
            foreach (var limit in globalLimits)
                await CheckQuotaAsync(lease, limit, "global", dayBegin, monthBegin);
            foreach (var limit in tenantLimits)
                await CheckQuotaAsync(lease, limit, $"tenant:{tenantId}", dayBegin, monthBegin);
            foreach (var limit in subjectLimits)
                await CheckQuotaAsync(lease, limit, $"user:{tenantId}:{userId}", dayBegin, monthBegin);

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

            var concurrencyDenied = false;
            lock (ReservationLock)
            {
                foreach (var item in concurrencyKeys)
                {
                    ConcurrentCounts.TryGetValue(item.Key, out var current);
                    if (current >= item.Limit)
                    {
                        concurrencyDenied = true;
                        break;
                    }
                }

                if (!concurrencyDenied)
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
            if (concurrencyDenied)
                await RejectAsync(lease, "concurrency", "AI 并发请求数已达上限，请稍后重试");
            return lease;
            }
            finally
            {
                QuotaGate.Release();
            }
        }

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
                if (Context.Queryable<AiCallLog>().Any(x => x.RequestId == lease.RequestId))
                {
                    AccountingHealthy = true;
                    return;
                }
                await Context.Insertable(new AiCallLog
                {
                    Scene = Clip(lease.Scene, 64),
                    Provider = lease.Provider,
                    Model = lease.Model,
                    TenantId = lease.TenantId,
                    RequestId = Clip(lease.RequestId, 64),
                    TraceId = Clip(lease.TraceId, 64),
                    Success = outcome.Success ? 1 : 0,
                    Status = Clip(outcome.Status ?? (outcome.Success ? "success" : "unknown"), 32),
                    ErrorType = Clip(outcome.ErrorType, 64),
                    ErrorMsg = Clip(outcome.ErrorMessage, 1000),
                    HttpStatusCode = outcome.HttpStatusCode,
                    DurationMs = Math.Max(0, (long)(DateTime.Now - lease.StartedAt).TotalMilliseconds),
                    ProviderRequestId = Clip(outcome.ProviderRequestId, 128),
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
                AccountingHealthy = false;
                Logger.Warn(ex, "写入 AI 调用治理流水失败 requestId={RequestId}", lease.RequestId);
            }
            finally
            {
                Release(lease);
            }
        }

        public void InvalidatePolicyCache(string tenantId = null)
        {
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                _cache.Remove(CacheKey(tenantId));
                return;
            }
            // IMemoryCache 不支持按前缀删除；版本号使所有既有项立即失效。
            PolicyCacheVersion++;
        }

        private static int PolicyCacheVersion;

        private List<AiAccessPolicy> GetPolicies(string tenantId)
        {
            return _cache.GetOrCreate(CacheKey(tenantId), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                try
                {
                    return Context.Queryable<AiAccessPolicy>()
                        .Where(x => x.Status == 0 && (x.ScopeType == "global" || x.TenantId == tenantId))
                        .ToList();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "读取 AI 治理策略失败，已关闭普通 AI 调用 tenantId={TenantId}", tenantId);
                    AccountingHealthy = false;
                    throw new AiGovernanceDeniedException("governance_unavailable", "AI 治理策略暂不可用，请稍后重试");
                }
            }) ?? new List<AiAccessPolicy>();
        }

        private static string CacheKey(string tenantId) => $"ai:policies:{PolicyCacheVersion}:{tenantId}";

        private async Task CheckQuotaAsync(
            AiCallLease lease, PolicyLimit limit, string scopeKey, DateTime dayBegin, DateTime monthBegin)
        {
            if (!limit.HasAnyLimit) return;

            var reservationKey = $"{scopeKey}|{limit.SceneFilter}";
            var dayUsage = GetUsage(lease, scopeKey, limit.SceneFilter, dayBegin);
            var monthUsage = GetUsage(lease, scopeKey, limit.SceneFilter, monthBegin);
            var active = GetActiveUsage(reservationKey);
            if (limit.DailyTokenLimit.HasValue
                && dayUsage.Tokens + active.Tokens + lease.ReservedTokens > limit.DailyTokenLimit.Value)
            {
                await RejectAsync(lease, "daily_token", "今日 AI Token 额度已用尽");
            }
            if (limit.MonthlyTokenLimit.HasValue
                && monthUsage.Tokens + active.Tokens + lease.ReservedTokens > limit.MonthlyTokenLimit.Value)
            {
                await RejectAsync(lease, "monthly_token", "本月 AI Token 额度已用尽");
            }
            if (limit.DailyAmountLimit.HasValue
                && dayUsage.Amount + active.Amount + lease.ReservedAmount > limit.DailyAmountLimit.Value)
            {
                await RejectAsync(lease, "daily_amount", "今日 AI 金额额度已用尽");
            }
            if (limit.MonthlyAmountLimit.HasValue
                && monthUsage.Amount + active.Amount + lease.ReservedAmount > limit.MonthlyAmountLimit.Value)
            {
                await RejectAsync(lease, "monthly_amount", "本月 AI 金额额度已用尽");
            }
        }

        private UsageValue GetUsage(AiCallLease lease, string scopeKey, string sceneFilter, DateTime begin)
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
                return new UsageValue
                {
                    Tokens = query.Sum(x => x.TotalTokens),
                    Amount = query.Sum(x => x.EstimatedAmount)
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

        private static int GetActiveCount(string scopeKey)
        {
            lock (ReservationLock)
            {
                return ActiveReservations.Values.Count(x => x.ScopeKeys.Contains(scopeKey));
            }
        }

        private static IReadOnlyList<string> BuildQuotaScopeKeys(string tenantId, long userId, string scene) =>
            new[]
            {
                "global|*", $"global|{scene}",
                $"tenant:{tenantId}|*", $"tenant:{tenantId}|{scene}",
                $"user:{tenantId}:{userId}|*", $"user:{tenantId}:{userId}|{scene}"
            };

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

        internal static PolicyLimit ResolveSingleLayer(
            IEnumerable<AiAccessPolicy> policies, string scopeType, string tenantId, long subjectId, string scene)
        {
            var candidates = policies.Where(x =>
                string.Equals(x.ScopeType, scopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.TenantId ?? string.Empty, tenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && x.SubjectId == subjectId);
            var exact = candidates.FirstOrDefault(x => string.Equals(x.Scene, scene, StringComparison.OrdinalIgnoreCase));
            var wildcard = candidates.FirstOrDefault(x => x.Scene == "*");
            if (exact == null) return FromPolicy(wildcard);
            if (wildcard == null) return FromPolicy(exact);
            var layers = new[] { FromPolicy(wildcard), FromPolicy(exact) };
            return new PolicyLimit
            {
                HasPolicy = true,
                Disabled = layers.Any(x => x.Disabled),
                SceneFilter = layers.Any(x => x.SceneFilter != "*") ? scene : "*",
                DailyTokenLimit = Min(layers.Select(x => x.DailyTokenLimit)),
                MonthlyTokenLimit = Min(layers.Select(x => x.MonthlyTokenLimit)),
                DailyAmountLimit = Min(layers.Select(x => x.DailyAmountLimit)),
                MonthlyAmountLimit = Min(layers.Select(x => x.MonthlyAmountLimit)),
                ConcurrentLimit = Min(layers.Select(x => x.ConcurrentLimit))
            };
        }

        internal static PolicyLimit ResolveRoleLayer(
            IEnumerable<AiAccessPolicy> policies, string tenantId, IReadOnlyList<long> roleIds, string scene)
        {
            if (roleIds == null || roleIds.Count == 0) return PolicyLimit.Empty;
            var layers = roleIds
                .Select(id => ResolveSingleLayer(policies, "role", tenantId, id, scene))
                .Where(x => x.HasPolicy)
                .ToList();
            if (layers.Count == 0) return PolicyLimit.Empty;
            return new PolicyLimit
            {
                HasPolicy = true,
                Disabled = layers.Any(x => x.Disabled),
                SceneFilter = layers.Any(x => x.SceneFilter != "*") ? scene : "*",
                DailyTokenLimit = Min(layers.Select(x => x.DailyTokenLimit)),
                MonthlyTokenLimit = Min(layers.Select(x => x.MonthlyTokenLimit)),
                DailyAmountLimit = Min(layers.Select(x => x.DailyAmountLimit)),
                MonthlyAmountLimit = Min(layers.Select(x => x.MonthlyAmountLimit)),
                ConcurrentLimit = Min(layers.Select(x => x.ConcurrentLimit))
            };
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

        private static string Clip(string value, int length) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];

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
    }
}
