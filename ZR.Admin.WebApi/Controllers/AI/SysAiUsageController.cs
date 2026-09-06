using Microsoft.AspNetCore.Mvc;
using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.Admin.WebApi.Controllers.AI
{
    /// <summary>
    /// AI token 用量统计：基于 ai_call_log 提供按天/按能力汇总与调用流水。
    /// 普通用户仅能查自己的用量；管理员（admin）可查全量或指定用户。
    /// </summary>
    [Route("system/aiUsage")]
    [ApiExplorerSettings(GroupName = "ai")]
    public class SysAiUsageController : BaseController
    {
        private readonly ISysAiUsageService _aiUsageService;

        public SysAiUsageController(ISysAiUsageService aiUsageService)
        {
            _aiUsageService = aiUsageService;
        }

        /// <summary>
        /// 用量汇总（累计 + 按天分布 + 按能力场景分布）
        /// </summary>
        [HttpGet("summary")]
        [ActionPermissionFilter(Permission = "common")]
        public IActionResult Summary([FromQuery] AiUsageQueryDto parm)
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(_aiUsageService.GetSummary(parm, userId, HttpContext.IsAdmin()));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 调用流水分页列表（创建时间倒序）
        /// </summary>
        [HttpGet("list")]
        [ActionPermissionFilter(Permission = "common")]
        public IActionResult List([FromQuery] AiUsageQueryDto parm)
        {
            try
            {
                var userId = HttpContext.GetUId();
                return SUCCESS(_aiUsageService.GetList(parm, userId, HttpContext.IsAdmin()));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }
    }
}
