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

        /// <summary>
        /// 一句话口语描述解析为日程字段，返回可编辑草稿，不落库
        /// </summary>
        Task<SysAiScheduleParseResult> ParseScheduleAsync(SysAiScheduleParseInput input);

        /// <summary>
        /// 汇总指定区间（默认本周）内当前用户的日程生成周报文本，不落库
        /// </summary>
        Task<SysAiWeeklyReportResult> GenerateWeeklyReportAsync(SysAiWeeklyReportInput input, long userId);

        /// <summary>
        /// 按代码生成表 id 推断各列配置建议（中文标签/控件类型/是否列表查询等）。
        /// 只返回建议不落库，采纳与否由用户在列配置页勾选后走既有保存接口。
        /// </summary>
        Task<SysAiGenColumnResult> SuggestGenColumnsAsync(long tableId);
    }
}
