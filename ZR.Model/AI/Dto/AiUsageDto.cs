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

        public string TenantId { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string Status { get; set; }
        public string ErrorType { get; set; }

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

        public int SuccessCalls { get; set; }
        public int FailedCalls { get; set; }
        public int TimeoutCalls { get; set; }
        public int RejectedCalls { get; set; }
        public decimal SuccessRate { get; set; }
        public long AverageDurationMs { get; set; }
        public long P95DurationMs { get; set; }
        public decimal EstimatedAmount { get; set; }
        public string Currency { get; set; } = "CNY";

        /// <summary>按天分布（升序）</summary>
        public List<AiUsageDailyDto> Daily { get; set; } = new();

        /// <summary>按能力场景分布（token 倒序）</summary>
        public List<AiUsageSceneDto> Scenes { get; set; } = new();

        public List<AiUsageErrorDto> Errors { get; set; } = new();
    }

    /// <summary>
    /// 按用户聚合的时间窗 token 用量（每个用户一行）
    /// </summary>
    public class AiUsageUserDto
    {
        public string TenantId { get; set; }
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
        public decimal EstimatedAmount { get; set; }
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
        public int SuccessCalls { get; set; }
        public int FailedCalls { get; set; }
        public long AverageDurationMs { get; set; }
        public decimal EstimatedAmount { get; set; }
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
        public int SuccessCalls { get; set; }
        public int FailedCalls { get; set; }
        public decimal EstimatedAmount { get; set; }
    }

    public class AiUsageErrorDto
    {
        public string ErrorType { get; set; }
        public int Calls { get; set; }
    }

    /// <summary>
    /// AI 调用流水行（明细列表）
    /// </summary>
    public class AiUsageLogDto
    {
        [ExcelColumn(Name = "调用时间", Format = "yyyy-MM-dd HH:mm:ss", Width = 20, Index = 0)]
        public DateTime? CreateTime { get; set; }

        [ExcelColumn(Name = "用户", Width = 16, Index = 1)]
        public string UserName { get; set; }

        [ExcelColumn(Name = "租户", Width = 14, Index = 2)]
        public string TenantId { get; set; }

        [ExcelColumn(Name = "场景", Width = 18, Index = 3)]
        public string Scene { get; set; }

        [ExcelColumn(Name = "Provider", Width = 14, Index = 4)]
        public string Provider { get; set; }

        [ExcelColumn(Name = "模型", Width = 24, Index = 5)]
        public string Model { get; set; }

        [ExcelColumn(Name = "状态", Width = 16, Index = 6)]
        public string Status { get; set; }

        [ExcelColumn(Name = "错误类型", Width = 16, Index = 7)]
        public string ErrorType { get; set; }

        [ExcelColumn(Name = "HTTP 状态", Width = 12, Index = 8)]
        public int? HttpStatusCode { get; set; }

        [ExcelColumn(Name = "耗时(ms)", Width = 12, Index = 9)]
        public long DurationMs { get; set; }

        [ExcelColumn(Name = "输入 Token", Width = 12, Index = 10)]
        public int PromptTokens { get; set; }

        [ExcelColumn(Name = "输出 Token", Width = 12, Index = 11)]
        public int CompletionTokens { get; set; }

        [ExcelColumn(Name = "合计 Token", Width = 12, Index = 12)]
        public int TotalTokens { get; set; }

        [ExcelColumn(Name = "金额", Width = 14, Index = 13)]
        public decimal EstimatedAmount { get; set; }

        [ExcelColumn(Name = "请求 ID", Width = 28, Index = 14)]
        public string RequestId { get; set; }

        [ExcelColumn(Name = "错误信息", Width = 40, Index = 15)]
        public string ErrorMsg { get; set; }

        [ExcelIgnore]
        public string TraceId { get; set; }
        [ExcelIgnore]
        public int? Success { get; set; }
        [ExcelIgnore]
        public string ProviderRequestId { get; set; }
        [ExcelIgnore]
        public bool IsStream { get; set; }
        [ExcelIgnore]
        public string Currency { get; set; }
    }
}
