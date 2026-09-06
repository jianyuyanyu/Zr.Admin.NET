using Infrastructure;
using Infrastructure.Attribute;
using Infrastructure.Helper;
using NLog;
using ZR.Model.AI;

namespace ZR.ServiceCore.Services
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

        public void Record(AiUsageInfo usage)
        {
            if (usage == null) return;
            try
            {
                Context.Insertable(new AiCallLog
                {
                    Scene = Clip(usage.Scene, 64),
                    Provider = Clip(usage.Provider, 32),
                    Model = Clip(usage.Model, 100),
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                    UserName = App.UserName
                }).ExecuteReturnSnowflakeId();
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
