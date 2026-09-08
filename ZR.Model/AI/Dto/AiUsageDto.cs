namespace ZR.Model.AI.Dto
{
    /// <summary>
    /// AI 用量统计查询参数
    /// </summary>
    public class AiUsageQueryDto : PagerInfo
    {
        /// <summary>目标用户（0/null：管理员看全量，普通用户强制本人）</summary>
        public long? UserId { get; set; }

        /// <summary>用户名模糊筛选（按用户聚合列表用）</summary>
        public string UserName { get; set; }

        /// <summary>能力场景过滤（ai_chat / wf_generate / lang_translate 等），空为全部</summary>
        public string Scene { get; set; }

        /// <summary>统计开始时间（默认最近 30 天）</summary>
        public DateTime? BeginTime { get; set; }

        /// <summary>统计结束时间（默认当前时间）</summary>
        public DateTime? EndTime { get; set; }
    }

    /// <summary>
    /// AI 用量汇总结果（时间窗累计 + 按天 + 按能力场景）
    /// </summary>
    public class AiUsageSummaryDto
    {
        /// <summary>调用次数</summary>
        public int TotalCalls { get; set; }

        /// <summary>输入 token 合计</summary>
        public long TotalPromptTokens { get; set; }

        /// <summary>输出 token 合计</summary>
        public long TotalCompletionTokens { get; set; }

        /// <summary>总 token 合计</summary>
        public long TotalTokens { get; set; }

        /// <summary>按天分布（升序）</summary>
        public List<AiUsageDailyDto> Daily { get; set; } = new();

        /// <summary>按能力场景分布（token 倒序）</summary>
        public List<AiUsageSceneDto> Scenes { get; set; } = new();
    }

    /// <summary>
    /// 按用户聚合的时间窗 token 用量（每个用户一行）
    /// </summary>
    public class AiUsageUserDto
    {
        public long UserId { get; set; }

        /// <summary>用户名；后台任务（UserId=0）显示为系统任务</summary>
        public string UserName { get; set; }

        /// <summary>调用次数</summary>
        public int Calls { get; set; }

        /// <summary>输入 token 合计</summary>
        public long PromptTokens { get; set; }

        /// <summary>输出 token 合计</summary>
        public long CompletionTokens { get; set; }

        /// <summary>token 合计</summary>
        public long TotalTokens { get; set; }
    }

    /// <summary>
    /// 按天用量项
    /// </summary>
    public class AiUsageDailyDto
    {
        /// <summary>日期 yyyy-MM-dd</summary>
        public string Day { get; set; }

        /// <summary>调用次数</summary>
        public int Calls { get; set; }

        /// <summary>token 合计</summary>
        public long TotalTokens { get; set; }
    }

    /// <summary>
    /// 按能力场景用量项
    /// </summary>
    public class AiUsageSceneDto
    {
        public string Scene { get; set; }

        /// <summary>调用次数</summary>
        public int Calls { get; set; }

        /// <summary>token 合计</summary>
        public long TotalTokens { get; set; }
    }

    /// <summary>
    /// AI 调用流水行（明细列表）
    /// </summary>
    public class AiUsageLogDto
    {
        public string Scene { get; set; }

        public string Provider { get; set; }

        public string Model { get; set; }

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public int TotalTokens { get; set; }

        /// <summary>触发用户（后台任务无登录上下文时为空）</summary>
        public string UserName { get; set; }

        /// <summary>异常信息（成功调用为空）</summary>
        public string ErrorMsg { get; set; }

        public DateTime? CreateTime { get; set; }
    }
}
