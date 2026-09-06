using System.Linq.Expressions;
using Infrastructure.Attribute;
using SqlSugar;
using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.ServiceCore.AI
{
    /// <summary>
    /// AI token 用量统计 service：基于 ai_call_log 汇总与明细查询。
    /// 可见范围规则：普通用户强制本人；管理员不指定 UserId 时看全量、指定时看该用户。
    /// </summary>
    [AppService(ServiceType = typeof(ISysAiUsageService), ServiceLifetime = LifeTime.Transient)]
    public class SysAiUsageService : BaseService<AiCallLog>, ISysAiUsageService
    {
        /// <summary>
        /// 时间窗用量汇总（累计 + 按天 + 按能力场景）
        /// </summary>
        public AiUsageSummaryDto GetSummary(AiUsageQueryDto parm, long currentUserId, bool isAdmin)
        {
            // 未显式指定时间范围时默认最近 30 天，避免大范围全表扫描
            var end = parm.EndTime ?? DateTime.Now;
            var begin = parm.BeginTime ?? end.Date.AddDays(-29);

            var rows = Queryable()
                .Where(BuildWhere(parm, currentUserId, isAdmin))
                .Where(m => m.CreateTime >= begin && m.CreateTime <= end)
                .Select(m => new AiCallLog
                {
                    Scene = m.Scene,
                    PromptTokens = m.PromptTokens,
                    CompletionTokens = m.CompletionTokens,
                    TotalTokens = m.TotalTokens,
                    CreateTime = m.CreateTime
                }).ToList();

            var result = new AiUsageSummaryDto();
            var daily = new Dictionary<string, AiUsageDailyDto>();
            var scenes = new Dictionary<string, AiUsageSceneDto>();

            foreach (var item in rows)
            {
                result.TotalCalls++;
                result.TotalPromptTokens += item.PromptTokens;
                result.TotalCompletionTokens += item.CompletionTokens;
                result.TotalTokens += item.TotalTokens;

                var dayKey = (item.CreateTime ?? end).ToString("yyyy-MM-dd");
                if (!daily.TryGetValue(dayKey, out var day))
                {
                    day = new AiUsageDailyDto { Day = dayKey };
                    daily.Add(dayKey, day);
                }
                day.Calls++;
                day.TotalTokens += item.TotalTokens;

                var sceneKey = string.IsNullOrEmpty(item.Scene) ? "unknown" : item.Scene;
                if (!scenes.TryGetValue(sceneKey, out var scene))
                {
                    scene = new AiUsageSceneDto { Scene = sceneKey };
                    scenes.Add(sceneKey, scene);
                }
                scene.Calls++;
                scene.TotalTokens += item.TotalTokens;
            }

            result.Daily = daily.Values.OrderBy(x => x.Day).ToList();
            result.Scenes = scenes.Values.OrderByDescending(x => x.TotalTokens).ToList();
            return result;
        }

        /// <summary>
        /// 调用流水分页（创建时间倒序）
        /// </summary>
        public PagedInfo<AiUsageLogDto> GetList(AiUsageQueryDto parm, long currentUserId, bool isAdmin)
        {
            var page = GetPages(BuildWhere(parm, currentUserId, isAdmin), parm, m => m.CreateTime, OrderByType.Desc);

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

        /// <summary>
        /// 组装查询条件：管理员可查指定用户，普通用户强制本人；后台任务流水（UserId=0）仅管理员可见。
        /// </summary>
        private Expression<Func<AiCallLog, bool>> BuildWhere(AiUsageQueryDto parm, long currentUserId, bool isAdmin)
        {
            var exp = Expressionable.Create<AiCallLog>();

            var targetUserId = isAdmin ? parm.UserId ?? 0 : currentUserId;
            if (targetUserId > 0)
            {
                exp = exp.And(m => m.UserId == targetUserId);
            }
            if (!string.IsNullOrWhiteSpace(parm.Scene))
            {
                exp = exp.And(m => m.Scene == parm.Scene);
            }
            if (parm.BeginTime.HasValue)
            {
                exp = exp.And(m => m.CreateTime >= parm.BeginTime);
            }
            if (parm.EndTime.HasValue)
            {
                exp = exp.And(m => m.CreateTime <= parm.EndTime);
            }
            return exp.ToExpression();
        }
    }
}
