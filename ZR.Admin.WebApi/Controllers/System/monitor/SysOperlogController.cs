using Microsoft.AspNetCore.Mvc;
using ZR.Model.System.Dto;

namespace ZR.Admin.WebApi.Controllers.monitor
{
    /// <summary>
    /// 操作日志记录
    /// </summary>
    [Route("/monitor/operlog")]
    [ApiExplorerSettings(GroupName = "sys")]
    public class SysOperlogController : BaseController
    {
        private ISysOperLogService sysOperLogService;
        private ISysAiService sysAiService;

        public SysOperlogController(ISysOperLogService sysOperLogService, ISysAiService sysAiService)
        {
            this.sysOperLogService = sysOperLogService;
            this.sysAiService = sysAiService;
        }

        /// <summary>
        /// 查询操作日志
        /// </summary>
        /// <param name="sysOperLog"></param>
        /// <returns></returns>
        [HttpGet("list")]
        public IActionResult OperList([FromQuery] SysOperLogQueryDto sysOperLog)
        {
            sysOperLog.OperName = !HttpContextExtension.IsAdmin(HttpContext) ? HttpContextExtension.GetName(HttpContext) : sysOperLog.OperName;
            var list = sysOperLogService.SelectOperLogList(sysOperLog);

            return SUCCESS(list);
        }

        /// <summary>
        /// 删除操作日志
        /// </summary>
        /// <param name="operIds"></param>
        /// <returns></returns>
        [Log(Title = "操作日志", BusinessType = BusinessType.DELETE)]
        [ActionPermissionFilter(Permission = "monitor:operlog:delete")]
        [HttpDelete("{operIds}")]
        public IActionResult Remove(string operIds)
        {
            if (!HttpContextExtension.IsAdmin(HttpContext))
            {
                return ToResponse(ApiResult.Error("操作失败"));
            }
            long[] operIdss = Tools.SpitLongArrary(operIds);
            return SUCCESS(sysOperLogService.DeleteOperLogByIds(operIdss));
        }

        /// <summary>
        /// 清空操作日志
        /// </summary>
        /// <returns></returns>
        [Log(Title = "清空操作日志", BusinessType = BusinessType.CLEAN)]
        [ActionPermissionFilter(Permission = "monitor:operlog:delete")]
        [HttpDelete("clean")]
        public IActionResult ClearOperLog()
        {
            if (!HttpContextExtension.IsAdmin(HttpContext))
            {
                return ToResponse(ResultCode.CUSTOM_ERROR,"操作失败");
            }
            sysOperLogService.CleanOperLog();

            return SUCCESS(1);
        }

        /// <summary>
        /// 导出操作日志
        /// </summary>
        /// <returns></returns>
        [Log(Title = "操作日志", BusinessType = BusinessType.EXPORT)]
        [ActionPermissionFilter(Permission = "monitor:operlog:export")]
        [HttpGet("export")]
        public IActionResult Export([FromQuery] SysOperLogQueryDto sysOperLog)
        {
            sysOperLog.PageSize = 100000;
            var list = sysOperLogService.SelectOperLogList(sysOperLog);
            var result = ExportExcelMini(list.Result, "操作日志", "操作日志");
            return ExportExcel(result.Item2, result.Item1);
        }

        /// <summary>
        /// AI 操作日志健康分析：慢操作/错误聚类等指标交由大模型解读，返回 Markdown 报告（不落库）。
        /// 非管理员强制只分析本人日志，与列表页口径一致。
        /// </summary>
        [HttpPost("aiHealth")]
        [ActionPermissionFilter(Permission = "monitor:operlog:ai")]
        [Log(Title = "AI 操作日志健康分析", BusinessType = BusinessType.OTHER, IsSaveRequestData = false)]
        public async Task<IActionResult> AiHealth([FromBody] LogAiAnalysisInput input)
        {
            try
            {
                var operName = !HttpContextExtension.IsAdmin(HttpContext) ? HttpContextExtension.GetName(HttpContext) : null;
                var metrics = sysOperLogService.GetOperHealthMetrics(input, operName);
                return SUCCESS(await sysAiService.AnalyzeOperHealthAsync(metrics));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

    }
}
