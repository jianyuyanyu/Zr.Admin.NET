using Microsoft.AspNetCore.Mvc;
using ZR.Model.AI.Dto;

namespace ZR.Admin.WebApi.Controllers.System
{
    /// <summary>
    /// 全局 AI 助手（办工助手）：会话管理与工具调用对话。
    /// 权限按登录用户（common），会话/消息/工具数据均按当前登录用户隔离。
    /// </summary>
    [Route("system/aichat")]
    [ApiExplorerSettings(GroupName = "sys")]
    public class SysAiChatController : BaseController
    {
        private readonly ISysAiChatService _aiChatService;

        public SysAiChatController(ISysAiChatService aiChatService)
        {
            _aiChatService = aiChatService;
        }

        /// <summary>
        /// 会话列表（按当前用户，最近更新倒序，最多 50 条）
        /// </summary>
        [HttpGet("sessions")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> Sessions()
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.ListSessionsAsync(userId));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 新建空会话
        /// </summary>
        [HttpPost("session")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> CreateSession()
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.CreateSessionAsync(userId));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 重命名会话
        /// </summary>
        [HttpPut("session/{sessionId}/rename")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> RenameSession(long sessionId, [FromBody] SysAiChatRenameDto parm)
        {
            if (parm == null || string.IsNullOrWhiteSpace(parm.Title))
            {
                return ToResponse(ResultCode.FAIL, "标题不能为空");
            }
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.RenameSessionAsync(sessionId, userId, parm.Title.Trim()));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 删除会话（连同历史消息）
        /// </summary>
        [HttpDelete("session/{sessionId}")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> DeleteSession(long sessionId)
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.DeleteSessionAsync(sessionId, userId));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 会话历史消息（升序）
        /// </summary>
        [HttpGet("session/{sessionId}/messages")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> Messages(long sessionId)
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.GetMessagesAsync(sessionId, userId));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 发送对话消息（SessionId 传 0 自动新建会话；内部自动调用工具）
        /// </summary>
        [HttpPost("chat")]
        [ActionPermissionFilter(Permission = "common")]
        public async Task<IActionResult> Chat([FromBody] SysAiChatRequestDto parm)
        {
            if (parm == null || string.IsNullOrWhiteSpace(parm.Message))
            {
                return ToResponse(ResultCode.FAIL, "消息内容不能为空");
            }
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(await _aiChatService.ChatAsync(parm.SessionId, userId, parm.Message));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }
    }
}
