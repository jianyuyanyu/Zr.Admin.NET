namespace ZR.Model.AI
{
    /// <summary>
    /// AI 调用 token 用量审计流水（每次模型 HTTP 调用一条，供审计/统计）
    /// </summary>
    [SugarTable("ai_call_log")]
    [Tenant(0)]
    public class AiCallLog
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
        public long Id { get; set; }

        /// <summary>能力标识：ai_chat / wf_generate / lang_translate / login_security 等</summary>
        [SugarColumn(Length = 64)]
        public string Scene { get; set; }

        [SugarColumn(Length = 32)]
        public string Provider { get; set; }

        [SugarColumn(Length = 100, IsNullable = true)]
        public string Model { get; set; }

        /// <summary>输入 token（prompt）</summary>
        public int PromptTokens { get; set; }

        /// <summary>输出 token（completion）</summary>
        public int CompletionTokens { get; set; }

        /// <summary>合计 token</summary>
        public int TotalTokens { get; set; }

        /// <summary>触发用户（请求登录人；后台任务无登录上下文时为空）</summary>
        [SugarColumn(Length = 64, IsNullable = true)]
        public string UserName { get; set; }

        [SugarColumn(IsNullable = true)]
        public string ErrorMsg { get; set; }

        [SugarColumn(InsertServerTime = true)]
        public DateTime? CreateTime { get; set; }
    }
}
