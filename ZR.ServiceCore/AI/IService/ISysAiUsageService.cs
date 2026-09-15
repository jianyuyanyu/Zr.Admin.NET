using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.Repository;

namespace ZR.ServiceCore.AI.IService
{
    /// <summary>
    /// AI token 用量统计：时间窗汇总、按用户聚合、调用明细。
    /// 个人视角（isAdmin=false）在 Service 内强制按当前登录人过滤，调用方无需传用户 ID。
    /// </summary>
    public interface ISysAiUsageService : IBaseService<AiCallLog>
    {
        /// <summary>
        /// 时间窗用量汇总（累计 + 按天 + 按能力场景）
        /// </summary>
        AiUsageSummaryDto GetSummary(AiUsageQueryDto parm, bool isAdmin);

        /// <summary>
        /// 按用户聚合时间段内 token 消耗（分页，token 倒序）
        /// </summary>
        PagedInfo<AiUsageUserDto> GetUserAggregate(AiUsageQueryDto parm, bool isAdmin);

        /// <summary>
        /// 调用流水分页（创建时间倒序）
        /// </summary>
        PagedInfo<AiUsageLogDto> GetList(AiUsageQueryDto parm, bool isAdmin);

        /// <summary>
        /// 导出调用流水（与列表同一筛选，最多 10000 条）
        /// </summary>
        List<AiUsageLogDto> GetExportList(AiUsageQueryDto parm, bool isAdmin);
    }
}
