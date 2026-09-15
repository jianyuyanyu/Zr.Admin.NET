using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.AI
{
    /// <summary>一次模型 HTTP 调用的治理输入。</summary>
    public sealed class AiCallRequest
    {
        public string Scene { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string TenantId { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; }
        public IReadOnlyList<long> RoleIds { get; set; } = Array.Empty<long>();
        public int EstimatedPromptTokens { get; set; }
        public int MaxCompletionTokens { get; set; }
        public bool IsStream { get; set; }
    }

    /// <summary>调用前取得的配额与并发租约，必须在 finally 中完成。</summary>
    public sealed class AiCallLease
    {
        public string RequestId { get; set; }
        public string TraceId { get; set; }
        public string Scene { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string TenantId { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; }
        public bool IsStream { get; set; }
        public DateTime StartedAt { get; set; }
        public decimal InputPricePerMillion { get; set; }
        public decimal OutputPricePerMillion { get; set; }
        public string Currency { get; set; }
        public IReadOnlyList<string> ConcurrencyKeys { get; set; } = Array.Empty<string>();
        public long ReservedTokens { get; set; }
        public decimal ReservedAmount { get; set; }
    }

    /// <summary>模型调用结束状态。</summary>
    public sealed class AiCallOutcome
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string ErrorType { get; set; }
        public string ErrorMessage { get; set; }
        public int? HttpStatusCode { get; set; }
        public string ProviderRequestId { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public bool HasUsage { get; set; }
    }

    /// <summary>当前登录用户在指定场景下的额度快照（只读，不占并发、不写流水）。</summary>
    public sealed class AiQuotaSnapshot
    {
        public bool Allowed { get; set; } = true;
        public string Scene { get; set; }
        public string DeniedReason { get; set; }
        public long DailyTokenUsed { get; set; }
        public long? DailyTokenLimit { get; set; }
        public long MonthlyTokenUsed { get; set; }
        public long? MonthlyTokenLimit { get; set; }
        public decimal DailyAmountUsed { get; set; }
        public decimal? DailyAmountLimit { get; set; }
        public decimal MonthlyAmountUsed { get; set; }
        public decimal? MonthlyAmountLimit { get; set; }
        public string Currency { get; set; } = "CNY";
    }

    public interface IAiCallGovernance
    {
        Task<AiCallLease> BeginAsync(AiCallRequest request);
        Task CompleteAsync(AiCallLease lease, AiCallOutcome outcome);
        void InvalidatePolicyCache(string tenantId = null);
        AiQuotaSnapshot GetMyQuota(string scene = "ai_chat");
    }

    public sealed class AiGovernanceDeniedException : InvalidOperationException
    {
        public string Reason { get; }

        public AiGovernanceDeniedException(string reason, string message) : base(message)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// 后台/异步 AI 调用的显式归属快照。HTTP 请求无需设置，治理服务会读取登录上下文。
    /// </summary>
    public sealed class AiCallActorScope : IDisposable
    {
        private static readonly AsyncLocal<AiCallActor> CurrentHolder = new();
        private readonly AiCallActor _previous;

        public static AiCallActor Current => CurrentHolder.Value;

        public AiCallActorScope(string tenantId, long userId, string userName)
        {
            _previous = CurrentHolder.Value;
            CurrentHolder.Value = new AiCallActor
            {
                TenantId = tenantId,
                UserId = userId,
                UserName = userName
            };
        }

        public void Dispose() => CurrentHolder.Value = _previous;
    }

    public sealed class AiCallActor
    {
        public string TenantId { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; }
    }
}
