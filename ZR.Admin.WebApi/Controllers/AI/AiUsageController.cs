using Microsoft.AspNetCore.Mvc;
using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.Admin.WebApi.Controllers.AI
{
    /// <summary>
    /// AI token 用量：管理员全站统计 + 个人自助查询。
    /// 管理端双闸：HttpContext.IsAdmin()（仅超管账号）+ ai:usage:list 权限；
    /// 个人端权限 ai:usage:mine，Service 内强制按当前登录人过滤。
    /// </summary>
    [Route("aiUsage")]
    [ApiExplorerSettings(GroupName = "ai")]
    public class AiUsageController : BaseController
    {
        private readonly ISysAiUsageService _aiUsageService;

        public AiUsageController(ISysAiUsageService aiUsageService)
        {
            _aiUsageService = aiUsageService;
        }

        #region 管理员视角（全站）

        /// <summary>
        /// 【管理】全站用量汇总（累计 + 按天 + 按场景）
        /// </summary>
        [HttpGet("summary")]
        [ActionPermissionFilter(Permission = "ai:usage:list")]
        public IActionResult Summary([FromQuery] AiUsageQueryDto parm)
        {
            var denied = RequireAdmin("仅管理员可查看全站用量");
            if (denied != null)
            {
                return denied;
            }
            return SUCCESS(_aiUsageService.GetSummary(parm, isAdmin: true));
        }

        /// <summary>
        /// 【管理】按用户聚合时间段内 token 消耗
        /// </summary>
        [HttpGet("users")]
        [ActionPermissionFilter(Permission = "ai:usage:list")]
        public IActionResult Users([FromQuery] AiUsageQueryDto parm)
        {
            var denied = RequireAdmin("仅管理员可查看全站用户用量");
            if (denied != null)
            {
                return denied;
            }
            return SUCCESS(_aiUsageService.GetUserAggregate(parm, isAdmin: true));
        }

        /// <summary>
        /// 【管理】调用流水分页（可指定用户）
        /// </summary>
        [HttpGet("list")]
        [ActionPermissionFilter(Permission = "ai:usage:list")]
        public IActionResult List([FromQuery] AiUsageQueryDto parm)
        {
            var denied = RequireAdmin("仅管理员可查看全站调用明细");
            if (denied != null)
            {
                return denied;
            }
            return SUCCESS(_aiUsageService.GetList(parm, isAdmin: true));
        }

        #endregion

        #region 个人视角（仅本人）

        /// <summary>
        /// 【个人】本人用量汇总（累计 + 按天 + 按场景）
        /// </summary>
        [HttpGet("my/summary")]
        [ActionPermissionFilter(Permission = "ai:usage:mine")]
        public IActionResult MySummary([FromQuery] AiUsageQueryDto parm)
        {
            // 强制按当前登录人过滤，即使管理员从「我的用量」进入也只看自己
            return SUCCESS(_aiUsageService.GetSummary(parm, isAdmin: false));
        }

        /// <summary>
        /// 【个人】本人调用流水分页
        /// </summary>
        [HttpGet("my/list")]
        [ActionPermissionFilter(Permission = "ai:usage:mine")]
        public IActionResult MyList([FromQuery] AiUsageQueryDto parm)
        {
            return SUCCESS(_aiUsageService.GetList(parm, isAdmin: false));
        }

        #endregion

        /// <summary>
        /// 管理端闸门：仅超管账号（userName == admin）放行，与 ai:usage:list 权限构成双闸
        /// （普通角色即使配了权限也进不来，属有意为之）。放行返回 null，否则返回拒绝响应。
        /// </summary>
        private IActionResult RequireAdmin(string message)
        {
            return HttpContext.IsAdmin() ? null : ToResponse(ResultCode.FORBIDDEN, message);
        }
    }
}
