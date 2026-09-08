using Infrastructure.Attribute;
using ZR.Model.AI.Dto;
using ZR.Model.System;
using ZR.Repository;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>
    /// 白名单实体 + 固定 COUNT/SUM 分桶。禁止任意表名/列名/SQL 字符串。
    /// </summary>
    [AppService(ServiceType = typeof(AiChartMetricExecutor), ServiceLifetime = LifeTime.Transient)]
    public class AiChartMetricExecutor : BaseRepository<SysLogininfor>
    {
        public Task<AiChartQueryResult> QueryAsync(AiChartMetricDef def, DateTime begin, DateTime end, string grain)
        {
            ArgumentNullException.ThrowIfNull(def);
            grain = (grain ?? "day").ToLowerInvariant();
            var beginDate = begin.Date;
            var endInclusive = end.Date.AddDays(1).AddSeconds(-1);

            var daily = def.Entity switch
            {
                AiChartEntityKey.Logininfor => QueryLogininforDaily(def, beginDate, endInclusive),
                _ => throw new NotSupportedException($"未支持的图表实体 {def.Entity}")
            };

            var rows = AiChartTimeBuckets.RollupDaily(daily, grain, def.ValueField);
            return Task.FromResult(new AiChartQueryResult
            {
                DatasetId = def.DatasetId,
                Title = def.Title,
                Grain = grain,
                TimeRange = $"{beginDate:yyyy-MM-dd} ~ {end.Date:yyyy-MM-dd}",
                SuggestedType = def.SuggestedType,
                AllowedTypes = def.AllowedTypes,
                Fields = def.Fields.ToList(),
                Rows = rows
            });
        }

        /// <summary>登录次数 = 成功+失败合计，与原 login_daily 口径一致。</summary>
        private List<(string Date, long Value)> QueryLogininforDaily(AiChartMetricDef def, DateTime begin, DateTime endInclusive)
        {
            if (def.Agg != AiChartMetricAgg.Count)
            {
                throw new NotSupportedException("登录日志配置指标目前仅支持 Count");
            }

            var db = ResolveTenantDb();
            return db.Queryable<SysLogininfor>().ApplyScope()
                .Where(it => it.LoginTime >= begin && it.LoginTime <= endInclusive)
                .GroupBy(it => it.LoginTime.ToString("yyyy-MM-dd"))
                .Select(it => new { Date = it.LoginTime.ToString("yyyy-MM-dd"), Num = SqlFunc.AggregateCount(it.InfoId) })
                .ToList()
                .OrderBy(x => x.Date)
                .Select(x => (x.Date, (long)x.Num))
                .ToList();
        }
    }
}
