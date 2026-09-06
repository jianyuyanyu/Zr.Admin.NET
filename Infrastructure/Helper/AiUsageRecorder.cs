using System;

namespace Infrastructure.Helper
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
        void Record(AiUsageInfo usage);
    }
}
