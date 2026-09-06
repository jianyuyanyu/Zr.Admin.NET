using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.Repository;

namespace ZR.ServiceCore.AI.IService
{
    /// <summary>
    /// AI token 用量统计 service 接口：基于 ai_call_log 流水做汇总与明细查询。
    /// 普通用户仅可见自己的调用；管理员可指定 UserId 查看任意用户或全量。
    /// </summary>
    public interface ISysAiUsageService : IBaseService<AiCallLog>
    {
        /// <summary>
        /// 时间窗用量汇总（累计 + 按天 + 按能力场景）
        /// </summary>
        /// <param name="parm">查询条件；未给时间时默认最近 30 天</param>
        /// <param name="currentUserId">当前登录用户</param>
        /// <param name="isAdmin">是否平台管理员（决定能否查他人/全量）</param>
        AiUsageSummaryDto GetSummary(AiUsageQueryDto parm, long currentUserId, bool isAdmin);

        /// <summary>
        /// 调用流水分页（创建时间倒序），可见范围同 GetSummary
        /// </summary>
        PagedInfo<AiUsageLogDto> GetList(AiUsageQueryDto parm, long currentUserId, bool isAdmin);
    }
}
