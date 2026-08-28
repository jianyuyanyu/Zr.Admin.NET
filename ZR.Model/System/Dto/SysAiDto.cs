namespace ZR.Model.System.Dto
{
    /// <summary>
    /// AI 多语言翻译：单条待翻译条目
    /// </summary>
    public class SysAiLangItem
    {
        /// <summary>文案键（对应 sys_common_lang.lang_key）</summary>
        public string LangKey { get; set; }

        /// <summary>源语言文案</summary>
        public string Text { get; set; }

        /// <summary>可选语境，如「菜单名」「按钮文案」「错误提示」，用于消歧</summary>
        public string Context { get; set; }
    }

    /// <summary>
    /// AI 多语言翻译入参
    /// </summary>
    public class SysAiLangTranslateInput
    {
        /// <summary>源语言代码，如 zh-cn</summary>
        public string SourceLang { get; set; }

        /// <summary>目标语言代码列表，如 ["en","zh-tw"]</summary>
        public List<string> TargetLangs { get; set; }

        /// <summary>待翻译条目</summary>
        public List<SysAiLangItem> Items { get; set; }
    }

    /// <summary>
    /// 单条译文
    /// </summary>
    public class SysAiLangTranslation
    {
        public string LangCode { get; set; }
        public string LangName { get; set; }
    }

    /// <summary>
    /// 单个文案键的翻译结果（含全部目标语言）
    /// </summary>
    public class SysAiLangEntry
    {
        public string LangKey { get; set; }

        /// <summary>源语言文案，便于前端对照展示</summary>
        public string Text { get; set; }

        public List<SysAiLangTranslation> Translations { get; set; } = new();
    }

    /// <summary>
    /// AI 多语言翻译结果（草稿，未落库）
    /// </summary>
    public class SysAiLangTranslateResult
    {
        public string SourceLang { get; set; }

        public List<string> TargetLangs { get; set; } = new();

        public List<SysAiLangEntry> Entries { get; set; } = new();

        /// <summary>AI 返回非结构化 JSON 时承载原文，便于前端排查与降级展示</summary>
        public string Raw { get; set; }
    }

    /// <summary>
    /// AI 译文落库入参
    /// </summary>
    public class SysAiLangApplyInput
    {
        /// <summary>前端可编辑后的译文条目</summary>
        public List<SysAiLangEntry> Entries { get; set; }

        /// <summary>true 覆盖已存在的译文，false 只补空缺（默认 false，避免误改存量）</summary>
        public bool Cover { get; set; }
    }

    /// <summary>
    /// AI 自然语言转 Cron 入参
    /// </summary>
    public class SysAiCronParseInput
    {
        /// <summary>自然语言调度描述，如「每天凌晨2点执行」</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// AI 自然语言转 Cron 结果
    /// </summary>
    public class SysAiCronParseResult
    {
        /// <summary>Quartz 格式 Cron 表达式</summary>
        public string Cron { get; set; }

        /// <summary>执行规律的中文说明，供用户核对</summary>
        public string Description { get; set; }

        /// <summary>AI 对歧义描述所做的假设说明，无则为空</summary>
        public string Warnings { get; set; }

        /// <summary>由服务端按 Quartz 本地计算的下几次执行时间，非 AI 生成</summary>
        public List<string> NextTimes { get; set; } = new();

        /// <summary>AI 返回非结构化 JSON 时承载原文</summary>
        public string Raw { get; set; }
    }

    /// <summary>
    /// AI 一句话解析日程入参
    /// </summary>
    public class SysAiScheduleParseInput
    {
        /// <summary>口语化描述，如「下周三下午三点前把季度报表发给张总，加急」</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// AI 一句话解析日程结果（草稿，需用户确认后才落库）
    /// </summary>
    public class SysAiScheduleParseResult
    {
        public string Title { get; set; }

        public string Content { get; set; }

        /// <summary>优先级：1低 2中 3高</summary>
        public int Priority { get; set; } = 2;

        /// <summary>截止时间，格式 yyyy-MM-dd HH:mm，无则为空</summary>
        public string DueTime { get; set; }

        /// <summary>提醒时间，格式 yyyy-MM-dd HH:mm，无则为空</summary>
        public string ReminderTime { get; set; }

        /// <summary>AI 对模糊表述所做的假设说明</summary>
        public string Warnings { get; set; }

        /// <summary>AI 返回非结构化 JSON 时承载原文</summary>
        public string Raw { get; set; }
    }

    /// <summary>
    /// AI 周报汇总入参（不传时间则默认取本周，周一为起始日）
    /// </summary>
    public class SysAiWeeklyReportInput
    {
        public DateTime? BeginTime { get; set; }

        public DateTime? EndTime { get; set; }
    }

    /// <summary>
    /// AI 周报汇总结果（纯文本，不落库）
    /// </summary>
    public class SysAiWeeklyReportResult
    {
        public string PeriodStart { get; set; }

        public string PeriodEnd { get; set; }

        /// <summary>整体情况概述</summary>
        public string Summary { get; set; }

        /// <summary>已完成事项</summary>
        public List<string> Completed { get; set; } = new();

        /// <summary>未完成事项</summary>
        public List<string> Pending { get; set; } = new();

        /// <summary>可观察到的风险项</summary>
        public List<string> Risks { get; set; } = new();

        /// <summary>参与统计的日程总数，便于用户核对是否与页面一致</summary>
        public int Total { get; set; }

        /// <summary>AI 返回非结构化 JSON 时承载原文</summary>
        public string Raw { get; set; }
    }
}
