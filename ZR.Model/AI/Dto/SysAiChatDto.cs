namespace ZR.Model.AI.Dto
{
    /// <summary>
    /// AI 助手会话项（列表 / 新建返回）
    /// </summary>
    public class SysAiChatSessionDto
    {
        [JsonConverter(typeof(ValueToStringConverter))]
        public long SessionId { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public DateTime? CreateTime { get; set; }
        public DateTime? UpdateTime { get; set; }
    }

    /// <summary>
    /// 历史消息项
    /// </summary>
    public class SysAiChatMessageDto
    {
        [JsonConverter(typeof(ValueToStringConverter))]
        public long MessageId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime? CreateTime { get; set; }
        /// <summary>本消息消耗输入 token（仅 assistant 消息有值；usage 缺失时为 null）</summary>
        public int? PromptTokens { get; set; }
        /// <summary>本消息输出 token</summary>
        public int? CompletionTokens { get; set; }
        /// <summary>本消息 token 合计</summary>
        public int? TotalTokens { get; set; }
        /// <summary>本条助手消息附带的图表（配置+后端数据），用户消息为空</summary>
        public List<AiChartViewDto> Charts { get; set; }
    }

    /// <summary>
    /// 会话详情（标题 + 历史消息）
    /// </summary>
    public class SysAiChatDetailDto
    {
        [JsonConverter(typeof(ValueToStringConverter))]
        public long SessionId { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public List<SysAiChatMessageDto> Messages { get; set; } = new();
    }

    /// <summary>
    /// 发起对话请求。SessionId 为 0 时服务端自动新建会话
    /// </summary>
    public class SysAiChatRequestDto
    {
        [JsonConverter(typeof(ValueToStringConverter))]
        public long SessionId { get; set; }
        [StringLength(2000, ErrorMessage = "消息长度不能超过 2000 字符")]
        public string Message { get; set; }
    }

    /// <summary>
    /// 对话结果
    /// </summary>
    public class SysAiChatResultDto
    {
        [JsonConverter(typeof(ValueToStringConverter))]
        public long SessionId { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        /// <summary>助手最终回复（markdown）</summary>
        public string Reply { get; set; }
        /// <summary>本次是否新建了会话</summary>
        public bool IsNewSession { get; set; }
        /// <summary>本次对话累计输入 token（多次工具调用合并；模型未返回 usage 时为 null）</summary>
        public int? PromptTokens { get; set; }
        /// <summary>本次对话累计输出 token</summary>
        public int? CompletionTokens { get; set; }
        /// <summary>本次对话累计 token 合计</summary>
        public int? TotalTokens { get; set; }
        /// <summary>本轮助手回复附带的图表（配置+数据），无图时为空</summary>
        public List<AiChartViewDto> Charts { get; set; }
    }

    /// <summary>
    /// 重命名会话
    /// </summary>
    public class SysAiChatRenameDto
    {
        [Required(ErrorMessage = "标题不能为空")]
        [StringLength(100)]
        public string Title { get; set; }
    }

    /// <summary>
    /// 供模型 function calling 识别的工具定义
    /// </summary>
    public class AiToolDef
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON Schema（object），例如 { type="object", properties=..., required=... }</summary>
        public object Parameters { get; set; }
    }

    /// <summary>
    /// 工具执行结果（回灌给模型的内容文本）
    /// </summary>
    public class AiToolExecResult
    {
        public bool Ok { get; set; }
        public string Content { get; set; }

        public AiChartQueryResult ChartQuery { get; set; }

        public static AiToolExecResult Success(string content) => new() { Ok = true, Content = content };
        public static AiToolExecResult Error(string content) => new() { Ok = false, Content = content };
    }

    /// <summary>
    /// AI 流式对话 SSE 事件。Type 决定本次事件读取哪些字段：
    /// <para>delta：文本增量（打字机逐字展示），读 Content；</para>
    /// <para>tool：工具执行状态，读 ToolName / ToolStatus(start|done) / ToolOk；</para>
    /// <para>done：整轮对话结束（已落库），读 SessionId/Title/Model/Reply/IsNewSession/tokens/Charts；</para>
    /// <para>error：异常终止，读 Error。</para>
    /// </summary>
    public class SysAiChatStreamDto
    {
        /// <summary>事件类型：delta | tool | done | error</summary>
        public string Type { get; set; }

        /// <summary>delta：本次增量文本</summary>
        public string Content { get; set; }

        /// <summary>tool：工具名</summary>
        public string ToolName { get; set; }

        /// <summary>tool：start=开始执行；done=执行结束</summary>
        public string ToolStatus { get; set; }

        /// <summary>tool(done)：工具是否执行成功</summary>
        public bool ToolOk { get; set; }

        /// <summary>done：会话 ID</summary>
        [JsonConverter(typeof(ValueToStringConverter))]
        public long SessionId { get; set; }

        /// <summary>done：会话标题</summary>
        public string Title { get; set; }

        /// <summary>done：实际使用模型</summary>
        public string Model { get; set; }

        /// <summary>done：助手最终回复（markdown，含图表占位由前端配合 Charts 渲染）</summary>
        public string Reply { get; set; }

        /// <summary>done：本次是否新建了会话</summary>
        public bool IsNewSession { get; set; }

        /// <summary>done：本次累计输入 token（无 usage 时为 null）</summary>
        public int? PromptTokens { get; set; }

        /// <summary>done：本次累计输出 token</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>done：本次累计 token 合计</summary>
        public int? TotalTokens { get; set; }

        /// <summary>done：本轮回复附带的图表（配置+数据），无图时为空</summary>
        public List<AiChartViewDto> Charts { get; set; }

        /// <summary>error：错误描述（可展示给用户）</summary>
        public string Error { get; set; }
    }
}
