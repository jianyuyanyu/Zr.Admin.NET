using Infrastructure;
using Infrastructure.AI;
using Infrastructure.Attribute;
using NLog;
using ZR.Model.AI;
using ZR.Repository;

namespace ZR.ServiceCore.AI
{
    /// <summary>
    /// AI token 用量审计落库：接收 AiLlmClient 统一采集点上报，
    /// 将每次模型调用的 token 用量写入 ai_call_log（场景/模型/输入输出/触发人）。
    /// 写库失败仅告警（采集点已降级为仅日志），不阻断 AI 调用主链路。
    /// </summary>
    [AppService(ServiceType = typeof(IAiUsageRecorder), ServiceLifetime = LifeTime.Transient)]
    public class AiUsageRecorderService : BaseService<AiCallLog>, IAiUsageRecorder
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 记录一次模型调用的 token 用量到 ai_call_log。
        /// 异步写入：采集点位于 AI 异步调用链路上，同步写库会阻塞线程池线程。
        /// 不接收取消令牌：已产生的 token 必须记账，客户端断开不应导致审计漏记。
        /// 写库失败仅告警，不阻断 AI 调用主链路。
        /// </summary>
        public async Task RecordAsync(AiUsageInfo usage)
        {
            if (usage == null) return;
            try
            {
                await Context.Insertable(new AiCallLog
                {
                    Scene = Clip(usage.Scene, 64),
                    Provider = Clip(usage.Provider, 32),
                    Model = Clip(usage.Model, 100),
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                    UserId = DataScopeExtensions.GetCurrentUserId(),
                    UserName = App.UserName,
                }).ExecuteReturnSnowflakeIdAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "写入 AI token 用量流水失败 scene={Scene}", usage.Scene);
            }
        }

        private static string Clip(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length <= maxLen ? s : s[..maxLen];
        }
    }
}
