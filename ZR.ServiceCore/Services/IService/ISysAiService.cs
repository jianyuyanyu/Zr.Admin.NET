using ZR.Model.System.Dto;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 系统模块 AI 能力：多语言批量翻译、自然语言生成 Cron 表达式。
    /// 复用工作流同款 AI 基建（AiLlmClient + PromptLoader + AiOptions），提示词见 Prompts/system/。
    /// </summary>
    public interface ISysAiService
    {
        /// <summary>
        /// AI 批量翻译界面文案，返回可编辑草稿，不落库
        /// </summary>
        Task<SysAiLangTranslateResult> TranslateLangAsync(SysAiLangTranslateInput input);

        /// <summary>
        /// 把译文写入 sys_common_lang。Cover=false 时只补空缺，不覆盖存量
        /// </summary>
        (string, object, object) ApplyLangTranslation(SysAiLangApplyInput input);

        /// <summary>
        /// 自然语言调度描述转 Quartz Cron 表达式
        /// </summary>
        Task<SysAiCronParseResult> ParseCronAsync(SysAiCronParseInput input);
    }
}
