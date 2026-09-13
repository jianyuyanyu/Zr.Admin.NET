using System.Linq.Expressions;
using Infrastructure;
using Infrastructure.Attribute;
using Infrastructure.Extensions;
using SqlSugar;
using ZR.Common;
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
            EnsureUsageAccess(isAdmin);
            var (begin, end) = ResolveRange(parm);
            // SqlSugar 的 Queryable 可变：一次 GroupBy 会留在同一对象上。
            // 按天聚合后再按场景聚合，会变成 GROUP BY 日期+场景，前端只显示场景名就像重复。
            ISugarQueryable<AiCallLog> Filtered() => Queryable().Where(BuildWhere(parm, isAdmin, begin, end));

            var result = new AiUsageSummaryDto();

            var dayRows = Filtered()
                .GroupBy(m => SqlFunc.ToDateShort(m.CreateTime.Value))
                .OrderBy(m => SqlFunc.ToDateShort(m.CreateTime.Value))
                .Select(m => new
                {
                    Day = SqlFunc.ToDateShort(m.CreateTime.Value),
                    Calls = SqlFunc.AggregateCount(m.Id),
                    PromptTokens = SqlFunc.AggregateSum(m.PromptTokens),
                    CompletionTokens = SqlFunc.AggregateSum(m.CompletionTokens),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens),
                    SuccessCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Success == 1, 1, 0)),
                    FailedCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Success == 0, 1, 0)),
                    TimeoutCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Status == "timeout", 1, 0)),
                    RejectedCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Status == "rejected", 1, 0)),
                    DurationMs = SqlFunc.AggregateSum(m.DurationMs),
                    EstimatedAmount = SqlFunc.AggregateSum(m.EstimatedAmount)
                })
                .ToList();

            result.Daily = new List<AiUsageDailyDto>(dayRows.Count);
            foreach (var day in dayRows)
            {
                result.TotalCalls += day.Calls;
                result.TotalPromptTokens += day.PromptTokens;
                result.TotalCompletionTokens += day.CompletionTokens;
                result.TotalTokens += day.TotalTokens;
                result.SuccessCalls += day.SuccessCalls;
                result.FailedCalls += day.FailedCalls;
                result.TimeoutCalls += day.TimeoutCalls;
                result.RejectedCalls += day.RejectedCalls;
                result.EstimatedAmount += day.EstimatedAmount;
                result.Daily.Add(new AiUsageDailyDto
                {
                    Day = day.Day.ToString("yyyy-MM-dd"),
                    Calls = day.Calls,
                    TotalTokens = day.TotalTokens,
                    SuccessCalls = day.SuccessCalls,
                    FailedCalls = day.FailedCalls,
                    AverageDurationMs = day.Calls > 0 ? day.DurationMs / day.Calls : 0,
                    EstimatedAmount = day.EstimatedAmount
                });
            }

            var knownCalls = result.SuccessCalls + result.FailedCalls;
            result.SuccessRate = knownCalls > 0
                ? Math.Round(result.SuccessCalls * 100m / knownCalls, 2)
                : 0;
            result.AverageDurationMs = result.TotalCalls > 0
                ? dayRows.Sum(x => x.DurationMs) / result.TotalCalls
                : 0;
            result.P95DurationMs = GetP95Duration(parm, isAdmin, begin, end);

            var scenes = Filtered()
                .GroupBy(m => m.Scene)
                .OrderBy(m => SqlFunc.AggregateSum(m.TotalTokens), OrderByType.Desc)
                .Select(m => new AiUsageSceneDto
                {
                    Scene = m.Scene,
                    Calls = SqlFunc.AggregateCount(m.Id),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens),
                    SuccessCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Success == 1, 1, 0)),
                    FailedCalls = SqlFunc.AggregateSum(SqlFunc.IIF(m.Success == 0, 1, 0)),
                    EstimatedAmount = SqlFunc.AggregateSum(m.EstimatedAmount)
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

            result.Errors = Filtered()
                .Where(x => x.Success == 0)
                .GroupBy(x => x.ErrorType)
                .OrderBy(x => SqlFunc.AggregateCount(x.Id), OrderByType.Desc)
                .Select(x => new AiUsageErrorDto
                {
                    ErrorType = x.ErrorType,
                    Calls = SqlFunc.AggregateCount(x.Id)
                })
                .ToList();
            foreach (var error in result.Errors)
            {
                if (string.IsNullOrWhiteSpace(error.ErrorType)) error.ErrorType = "unknown";
            }

            return result;
        }

        /// <summary>
        /// 按用户聚合时间段内 token 消耗（库内 GroupBy，分页，token 倒序）
        /// </summary>
        public PagedInfo<AiUsageUserDto> GetUserAggregate(AiUsageQueryDto parm, bool isAdmin)
        {
            EnsureUsageAccess(isAdmin);
            var (begin, end) = ResolveRange(parm);

            var pageIndex = parm.PageNum <= 0 ? 1 : parm.PageNum;
            var pageSize = parm.PageSize <= 0 ? 20 : parm.PageSize;
            var total = 0;

            // 官方分组分页：聚合排序写在 Select 前，避免 MergeTable + ToPageList 的 COUNT 包装
            var list = Queryable()
                .Where(BuildWhere(parm, isAdmin, begin, end))
                .WhereIF(!string.IsNullOrWhiteSpace(parm.UserName), m => m.UserName.Contains(parm.UserName.Trim()))
                .GroupBy(m => new { m.TenantId, m.UserId })
                .OrderBy(m => SqlFunc.AggregateSum(m.TotalTokens), OrderByType.Desc)
                .Select(m => new AiUsageUserDto
                {
                    TenantId = m.TenantId,
                    UserId = m.UserId,
                    UserName = SqlFunc.AggregateMax(m.UserName),
                    Calls = SqlFunc.AggregateCount(m.Id),
                    PromptTokens = SqlFunc.AggregateSum(m.PromptTokens),
                    CompletionTokens = SqlFunc.AggregateSum(m.CompletionTokens),
                    TotalTokens = SqlFunc.AggregateSum(m.TotalTokens),
                    EstimatedAmount = SqlFunc.AggregateSum(m.EstimatedAmount)
                })
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
            EnsureUsageAccess(isAdmin);
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
                    TenantId = m.TenantId,
                    RequestId = m.RequestId,
                    TraceId = m.TraceId,
                    Success = m.Success,
                    Status = m.Status,
                    ErrorType = m.ErrorType,
                    HttpStatusCode = m.HttpStatusCode,
                    DurationMs = m.DurationMs,
                    ProviderRequestId = m.ProviderRequestId,
                    IsStream = m.IsStream == 1,
                    PromptTokens = m.PromptTokens,
                    CompletionTokens = m.CompletionTokens,
                    TotalTokens = m.TotalTokens,
                    EstimatedAmount = m.EstimatedAmount,
                    Currency = m.Currency,
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
                exp = exp.And(m => m.TenantId == App.GetCurrentTenantId()
                    && m.UserId == DataScopeExtensions.GetCurrentUserId());
            }
            else if (!IsPlatformAdmin())
            {
                exp = exp.And(m => m.TenantId == App.GetCurrentTenantId());
                if (parm.UserId.HasValue) exp = exp.And(m => m.UserId == parm.UserId.Value);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(parm.TenantId)) exp = exp.And(m => m.TenantId == parm.TenantId);
                if (parm.UserId.HasValue) exp = exp.And(m => m.UserId == parm.UserId.Value);
            }
            if (!string.IsNullOrWhiteSpace(parm.Scene))
            {
                exp = exp.And(m => m.Scene == parm.Scene);
            }
            if (!string.IsNullOrWhiteSpace(parm.Provider)) exp = exp.And(m => m.Provider == parm.Provider);
            if (!string.IsNullOrWhiteSpace(parm.Model)) exp = exp.And(m => m.Model == parm.Model);
            if (!string.IsNullOrWhiteSpace(parm.Status)) exp = exp.And(m => m.Status == parm.Status);
            if (!string.IsNullOrWhiteSpace(parm.ErrorType)) exp = exp.And(m => m.ErrorType == parm.ErrorType);
            return exp.ToExpression();
        }

        private long GetP95Duration(AiUsageQueryDto parm, bool isAdmin, DateTime begin, DateTime end)
        {
            var query = Queryable()
                .Where(BuildWhere(parm, isAdmin, begin, end))
                .Where(x => x.Success == 1);
            var count = query.Count();
            if (count <= 0) return 0;
            var page = Math.Max(1, (int)Math.Ceiling(count * 0.95m));
            var total = 0;
            var row = query.OrderBy(x => x.DurationMs, OrderByType.Asc)
                .ToPageList(page, 1, ref total)
                .FirstOrDefault();
            return row?.DurationMs ?? 0;
        }

        private static bool IsPlatformAdmin()
        {
            var user = App.HttpContext?.GetCurrentUser();
            return user?.IsAdmin() == true
                && string.Equals(App.GetCurrentTenantId(), App.MainDbConfigId, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureUsageAccess(bool adminView)
        {
            if (!adminView) return;
            var user = App.HttpContext?.GetCurrentUser();
            if (user == null || (user.IsAdmin() != true && !user.HasPermission("ai:usage:list")))
                throw new CustomException("当前账号没有 AI 调用监控权限");
        }
    }
}
