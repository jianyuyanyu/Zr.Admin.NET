using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.IService
{
    /// <summary>
    /// 全局 AI 助手（办工助手）：会话管理 + 工具调用编排。
    /// 数据落 ai_chat_session / ai_chat_message，按 UserId 隔离。
    /// 编排流程：历史上下文 + 系统提示 → LLM(function calling) → 执行工具回灌 → 得到最终回复。
    /// </summary>
    public interface ISysAiChatService
    {
        /// <summary>当前用户会话列表（更新时间倒序，最多 50）</summary>
        Task<List<SysAiChatSessionDto>> ListSessionsAsync(long userId);

        /// <summary>新建空会话</summary>
        Task<SysAiChatSessionDto> CreateSessionAsync(long userId);

        /// <summary>重命名会话</summary>
        Task<int> RenameSessionAsync(long sessionId, long userId, string title);

        /// <summary>删除会话（连同其全部消息）</summary>
        Task<int> DeleteSessionAsync(long sessionId, long userId);

        /// <summary>会话详情：标题 + 消息（升序）</summary>
        Task<SysAiChatDetailDto> GetMessagesAsync(long sessionId, long userId);

        /// <summary>
        /// 发送一条消息并获取回复。内部可能多次调用 LLM + 执行工具。
        /// SessionId&lt;=0 时自动新建；首轮会依据提问内容自动生成标题。
        /// </summary>
        Task<SysAiChatResultDto> ChatAsync(long sessionId, long userId, string message);
    }
}
