using System;
using System.Threading.Tasks;

namespace Infrastructure.AI
{
    /// <summary>
    /// AI 单次模型调用的 token 用量信息（采集点：AiLlmClient 成功响应解析 usage 后）。
    /// 供审计留痕（ai_call_log）使用；仅作数据载体，不感知业务。
    /// </summary>
    public sealed class AiUsageInfo
    {
        /// <summary>能力标识，如 ai_chat / wf_generate / lang_translate / cron_parse</summary>
        public string Scene { get; set; }

        public string Provider { get; set; }

        public string Model { get; set; }

        /// <summary>输入 token（prompt）</summary>
        public int PromptTokens { get; set; }

        /// <summary>输出 token（completion）</summary>
        public int CompletionTokens { get; set; }

        /// <summary>合计 token</summary>
        public int TotalTokens { get; set; }

        public DateTime CreateTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// token 用量落库记录器。
    /// 接口定义在 Infrastructure，使 AiLlmClient（Infrastructure 层）能在统一采集点调用，
    /// 具体写库实现由宿主层（ZR.ServiceCore）注册；未注册或写库失败时自动降级为仅日志，不阻断对话。
    /// </summary>
    public interface IAiUsageRecorder
    {
        /// <summary>
        /// 记录一次模型调用的 token 用量（写 ai_call_log 审计流水）。
        /// 约定：实现必须内部消化异常（仅告警）后正常返回，不得向调用方抛出，
        /// 以免审计失败阻断 AI 调用主链路。
        /// 使用异步签名是因为采集点位于 AI 异步调用链路上，同步写库会阻塞线程池线程。
        /// 不接收取消令牌：已产生的 token 必须记账，客户端断开不应导致审计漏记。
        /// </summary>
        /// <param name="usage">本次调用的用量信息</param>
        Task RecordAsync(AiUsageInfo usage);
    }
}
