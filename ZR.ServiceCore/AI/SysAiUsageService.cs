using System.Linq.Expressions;
using Infrastructure.Attribute;
using SqlSugar;
using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.Repository;
using ZR.ServiceCore.AI.IService;

namespace ZR.ServiceCore.AI
{
    /// <summary>
    /// AI token 用量统计：时间窗汇总、按用户聚合、调用明细。
    /// 可见范围：普通用户强制当前登录人；管理员可看全量或指定用户。
    /// </summary>
    [AppService(ServiceType = typeof(ISysAiUsageService), ServiceLifetime = LifeTime.Transient)]
    public class SysAiUsageService : BaseService<AiCallLog>, ISysAiUsageService
    {
        /// <summary>
        /// 时间窗用量汇总（累计 + 按天 + 按能力场景）。
        /// 按天/按场景在库内 GroupBy 聚合，只返回汇总行，避免把时间窗内全量流水拉进内存。
        /// </summary>
        public AiUsageSummaryDto GetSummary(AiUsageQueryDto parm, bool isAdmin)
        {
            var (begin, end) = ResolveRange(parm);
            var query = Queryable().Where(BuildWhere(parm, isAdmin, begin, end));

            var result = new AiUsageSummaryDto();

            // 按天聚合（顺带取 prompt/completion，供 C# 累加总量）
            var dayRows = query
                .GroupBy(m => SqlFunc.ToDateShort(m.CreateTime.Value))
                .OrderBy(m => SqlFunc.ToDateShort(m.CreateTime.Value))
                .Select(m => new
                {
                    Day = SqlFunc.ToDateShort(m.CreateTime.Value),
                    Calls = SqlFunc.AggregateCount(m.Id),
                    PromptTokens = SqlFunc.AggregateSum(m.PromptTokens),
                    CompletionTokens = SqlFunc.AggregateSum(m.CompletionTokens),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens)
                })
                .ToList();

            result.Daily = new List<AiUsageDailyDto>(dayRows.Count);
            foreach (var day in dayRows)
            {
                result.TotalCalls += day.Calls;
                result.TotalPromptTokens += day.PromptTokens;
                result.TotalCompletionTokens += day.CompletionTokens;
                result.TotalTokens += day.TotalTokens;
                result.Daily.Add(new AiUsageDailyDto
                {
                    Day = day.Day.ToString("yyyy-MM-dd"),
                    Calls = day.Calls,
                    TotalTokens = day.TotalTokens
                });
            }

            // 按场景聚合（token 倒序）
            var scenes = query
                .GroupBy(m => m.Scene)
                .OrderBy(m => SqlFunc.AggregateSum(m.TotalTokens), OrderByType.Desc)
                .Select(m => new AiUsageSceneDto
                {
                    Scene = m.Scene,
                    Calls = SqlFunc.AggregateCount(m.Id),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens)
                })
                .ToList();
            foreach (var scene in scenes)
            {
                if (string.IsNullOrEmpty(scene.Scene))
                {
                    scene.Scene = "unknown";
                }
            }
            result.Scenes = scenes;

            return result;
        }

        /// <summary>
        /// 按用户聚合时间段内 token 消耗（库内 GroupBy，分页，token 倒序）
        /// </summary>
        public PagedInfo<AiUsageUserDto> GetUserAggregate(AiUsageQueryDto parm, bool isAdmin)
        {
            var (begin, end) = ResolveRange(parm);

            var pageIndex = parm.PageNum <= 0 ? 1 : parm.PageNum;
            var pageSize = parm.PageSize <= 0 ? 20 : parm.PageSize;
            var total = 0;

            var list = Queryable()
                .Where(BuildWhere(parm, isAdmin, begin, end))
                .WhereIF(!string.IsNullOrWhiteSpace(parm.UserName), m => m.UserName.Contains(parm.UserName.Trim()))
                .GroupBy(m => m.UserId)
                .Select(m => new AiUsageUserDto
                {
                    UserId = m.UserId,
                    UserName = SqlFunc.AggregateMax(m.UserName),
                    Calls = SqlFunc.AggregateCount(m.Id),
                    PromptTokens = SqlFunc.AggregateSum(m.PromptTokens),
                    CompletionTokens = SqlFunc.AggregateSum(m.CompletionTokens),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens)
                })
                .MergeTable()
                .OrderBy(x => x.TotalTokens, OrderByType.Desc)
                .ToPageList(pageIndex, pageSize, ref total);

            foreach (var item in list)
            {
                if (string.IsNullOrEmpty(item.UserName))
                {
                    item.UserName = item.UserId <= 0 ? "系统任务" : $"用户{item.UserId}";
                }
            }

            return new PagedInfo<AiUsageUserDto>
            {
                PageIndex = pageIndex,
                PageSize = pageSize,
                TotalNum = total,
                Result = list
            };
        }

        /// <summary>
        /// 调用流水分页（创建时间倒序）
        /// </summary>
        public PagedInfo<AiUsageLogDto> GetList(AiUsageQueryDto parm, bool isAdmin)
        {
            var (begin, end) = ResolveRange(parm);
            var page = GetPages(BuildWhere(parm, isAdmin, begin, end), parm, m => m.CreateTime, OrderByType.Desc);

            return new PagedInfo<AiUsageLogDto>
            {
                PageIndex = page.PageIndex,
                PageSize = page.PageSize,
                TotalNum = page.TotalNum,
                Result = page.Result?.Select(m => new AiUsageLogDto
                {
                    Scene = m.Scene,
                    Provider = m.Provider,
                    Model = m.Model,
                    PromptTokens = m.PromptTokens,
                    CompletionTokens = m.CompletionTokens,
                    TotalTokens = m.TotalTokens,
                    UserName = m.UserName,
                    ErrorMsg = m.ErrorMsg,
                    CreateTime = m.CreateTime
                }).ToList()
            };
        }

        private static (DateTime Begin, DateTime End) ResolveRange(AiUsageQueryDto parm)
        {
            var end = parm.EndTime ?? DateTime.Now;
            var begin = parm.BeginTime ?? end.Date.AddDays(-29);
            return (begin, end);
        }

        /// <summary>
        /// 组装查询条件：个人视角强制当前登录人（不信任调用方传入的 UserId）；
        /// 管理员未传 UserId 看全量，传了则精确匹配（含 UserId=0 系统任务）。
        /// </summary>
        private Expression<Func<AiCallLog, bool>> BuildWhere(
            AiUsageQueryDto parm, bool isAdmin, DateTime begin, DateTime end)
        {
            var exp = Expressionable.Create<AiCallLog>();
            exp = exp.And(m => m.CreateTime >= begin && m.CreateTime <= end);

            if (!isAdmin)
            {
                exp = exp.And(m => m.UserId == DataScopeExtensions.GetCurrentUserId());
            }
            else if (parm.UserId.HasValue)
            {
                exp = exp.And(m => m.UserId == parm.UserId.Value);
            }
            if (!string.IsNullOrWhiteSpace(parm.Scene))
            {
                exp = exp.And(m => m.Scene == parm.Scene);
            }
            return exp.ToExpression();
        }
    }
}
