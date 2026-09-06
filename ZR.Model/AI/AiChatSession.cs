namespace ZR.Model.AI
{
    /// <summary>
    /// AI 助手会话
    /// </summary>
    [SugarTable("ai_chat_session")]
    [Tenant(0)]
    public class AiChatSession
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
        public long SessionId { get; set; }

        public long UserId { get; set; }

        [SugarColumn(Length = 100)]
        public string Title { get; set; }

        [SugarColumn(Length = 50, IsNullable = true)]
        public string Model { get; set; }

        [SugarColumn(InsertServerTime = true)]
        public DateTime? CreateTime { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? UpdateTime { get; set; }
    }
}
