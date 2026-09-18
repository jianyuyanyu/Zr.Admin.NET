using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ZR.Model;
using ZR.Model.System.Dto;
using ZR.ServiceCore.Signalr;

namespace ZR.Admin.WebApi.Controllers.monitor
{
    /// <summary>
    /// 在线用户
    /// </summary>
    [Route("monitor/online")]
    [ApiExplorerSettings(GroupName = "sys")]
    public class SysUserOnlineController : BaseController
    {
        private readonly IHubContext<MessageHub> HubContext;

        public SysUserOnlineController(IHubContext<MessageHub> hubContext)
        {
            HubContext = hubContext;
        }

        /// <summary>
        /// 获取在线用户列表（多租户模式下按当前租户过滤）
        /// </summary>
        /// <param name="parm"></param>
        /// <returns></returns>
        [HttpGet("list")]
        public IActionResult Index([FromQuery] PagerInfo parm)
        {
            var query = MessageHub.OnlineClients.Values.AsEnumerable();

            if (App.IsTenantEnabled())
            {
                var currentTenantId = App.GetCurrentTenantId();
                query = query.Where(u => string.Equals(u.TenantId, currentTenantId, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = query.OrderByDescending(f => f.LoginTime).ToList();

            var result = filtered
                .Skip((parm.PageNum - 1) * parm.PageSize)
                .Take(parm.PageSize);

            return SUCCESS(new { result, totalNum = filtered.Count });
        }

        /// <summary>
        /// 单个强退
        /// </summary>
        /// <returns></returns>
        [HttpDelete("force")]
        [Log(Title = "强退", BusinessType = BusinessType.FORCE)]
        [ActionPermissionFilter(Permission = "monitor:online:forceLogout")]
        public async Task<IActionResult> Force([FromBody] LockUserDto dto)
        {
            if (dto == null) { return ToResponse(ResultCode.PARAM_ERROR); }

            var target = MessageHub.OnlineClients.Values
                .FirstOrDefault(u => string.Equals(u.ConnnectionId, dto.ConnnectionId, StringComparison.Ordinal));
            if (target == null)
            {
                return ToResponse(ResultCode.FAIL, "在线连接不存在或已断开");
            }

            if (App.IsTenantEnabled()
                && !string.Equals(target.TenantId, App.GetCurrentTenantId(), StringComparison.OrdinalIgnoreCase))
            {
                return ToResponse(ResultCode.FORBIDDEN, "无权强退其他租户的在线用户");
            }

            await HubContext.Clients.Client(dto.ConnnectionId)
                .SendAsync(HubsConstant.ForceUser, new { dto.Reason, dto.Time });

            return SUCCESS(1);
        }

        /// <summary>
        /// 批量强退
        /// </summary>
        /// <returns></returns>
        [HttpDelete("batchForce")]
        [Log(Title = "强退", BusinessType = BusinessType.FORCE)]
        [ActionPermissionFilter(Permission = "monitor:online:batchLogout")]
        public async Task<IActionResult> BatchforceLogout([FromBody] LockUserDto dto)
        {
            if (dto == null) { return ToResponse(ResultCode.PARAM_ERROR); }

            var query = MessageHub.OnlineClients.Values.AsEnumerable();
            if (App.IsTenantEnabled())
            {
                var currentTenantId = App.GetCurrentTenantId();
                query = query.Where(u => string.Equals(u.TenantId, currentTenantId, StringComparison.OrdinalIgnoreCase));
            }

            var connIds = query.Select(u => u.ConnnectionId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (connIds.Count == 0)
            {
                return SUCCESS(0);
            }

            await HubContext.Clients.Clients(connIds).SendAsync(HubsConstant.ForceUser, new { dto.Reason });

            return SUCCESS(connIds.Count);
        }
    }
}
