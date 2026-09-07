using Infrastructure.Attribute;
using ZR.Model.AI.Dto;
using ZR.Model.System.Dto;
using ZR.ServiceCore.AI.IService;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>
    /// 按天统计用户登录次数（成功+失败合计）。复用 GetLoginSecurityMetrics 的 Daily 聚合，不接触原始日志。
    /// </summary>
    [AppService(ServiceType = typeof(IAiChartDatasetProvider), ServiceLifetime = LifeTime.Transient)]
    public class LoginDailyChartDatasetProvider : IAiChartDatasetProvider
    {
        public const string Id = "login_daily";

        private readonly ISysLoginService _loginService;

        public LoginDailyChartDatasetProvider(ISysLoginService loginService)
        {
            _loginService = loginService;
        }

        public string DatasetId => Id;
        public string Title => "每日用户登录数";
        public string Description => "按天/周/月统计用户登录次数（成功与失败合计）。适用于“每天登录数/最近登录量/登录趋势图”。";
        public string Permission => "monitor:logininfor:ai";
        public string[] Grains => ["day", "week", "month"];
        public int MaxDays => 90;
        public int MaxMonths => 3;
        public string[] AllowedTypes => ["line", "bar"];

        public IReadOnlyList<AiChartFieldDef> Fields { get; } =
        [
            new AiChartFieldDef { Field = "date", Label = "时间", Kind = "category" },
            new AiChartFieldDef { Field = "loginCount", Label = "用户登录数", Kind = "number" }
        ];

        public Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain)
        {
            var input = new LogAiAnalysisInput { BeginTime = begin, EndTime = end };
            var metrics = _loginService.GetLoginSecurityMetrics(input);
            var daily = metrics?.Daily ?? [];
            var rows = Aggregate(daily, grain);
            var (b, e) = input.ResolveRange();
            return Task.FromResult(new AiChartQueryResult
            {
                DatasetId = DatasetId,
                Title = Title,
                Grain = grain,
                TimeRange = $"{b:yyyy-MM-dd} ~ {e:yyyy-MM-dd}",
                SuggestedType = "line",
                AllowedTypes = AllowedTypes,
                Fields = Fields.ToList(),
                Rows = rows
            });
        }

        private static List<Dictionary<string, object>> Aggregate(List<LoginDailyStat> daily, string grain)
        {
            if (daily == null || daily.Count == 0)
            {
                return [];
            }
            if (grain == "day")
            {
                return daily.Select(d => Row(d.Date, d.Success + d.Fail)).ToList();
            }

            var grouped = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in daily)
            {
                if (!DateTime.TryParse(d.Date, out var dt))
                {
                    continue;
                }
                var key = grain == "month" ? dt.ToString("yyyy-MM") : WeekStart(dt).ToString("yyyy-MM-dd");
                grouped.TryGetValue(key, out var acc);
                grouped[key] = acc + d.Success + d.Fail;
            }
            return grouped.Select(kv => Row(kv.Key, kv.Value)).ToList();
        }

        private static Dictionary<string, object> Row(string date, long loginCount) => new()
        {
            ["date"] = date,
            ["loginCount"] = loginCount
        };

        /// <summary>周一为一周起始（与国内办公习惯一致）</summary>
        private static DateTime WeekStart(DateTime dt)
        {
            var offset = ((int)dt.DayOfWeek + 6) % 7;
            return dt.Date.AddDays(-offset);
        }
    }
}
