using Infrastructure.Attribute;
using ZR.Model.AI.Dto;
using ZR.Model.System.Dto;
using ZR.ServiceCore.AI.IService;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>
    /// 按省份统计登录日志的地域分布（地域由登录 IP 解析写入，如"河南省""天津市"）。
    /// 复用 GetLoginRegionStats 聚合，不接触原始日志；仅输出省级粒度，不含 IP 等精确定位信息。
    /// </summary>
    [AppService(ServiceType = typeof(IAiChartDatasetProvider), ServiceLifetime = LifeTime.Transient)]
    public class LoginRegionChartDatasetProvider : IAiChartDatasetProvider
    {
        public const string Id = "login_region";

        /// <summary>返回的省份条数上限，长尾合并为"其他"以避免饼图出现几十个碎扇区</summary>
        private const int TopN = 12;

        private readonly ISysLoginService _loginService;

        public LoginRegionChartDatasetProvider(ISysLoginService loginService)
        {
            _loginService = loginService;
        }

        public string DatasetId => Id;
        public string Title => "登录地域分布";
        public string Description => "按省份统计登录次数与独立用户数（地域来自登录 IP 解析）。适用于“用户都在哪些省/哪个地区登录最多/地域分布图/各地登录占比”。";
        public string Permission => "monitor:logininfor:ai";

        // 地域分布不按时间分桶，grain 仅用于决定可查询的最大跨度（见 MaxDays / MaxMonths）
        public string[] Grains => ["day", "week", "month"];
        public int MaxDays => 180;
        public int MaxMonths => 6;
        public string[] AllowedTypes => ["pie", "bar"];

        public IReadOnlyList<AiChartFieldDef> Fields { get; } =
        [
            new AiChartFieldDef { Field = "region", Label = "地区", Kind = "category" },
            new AiChartFieldDef { Field = "loginCount", Label = "登录次数", Kind = "number" },
            new AiChartFieldDef { Field = "userCount", Label = "独立用户数", Kind = "number" }
        ];

        public Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain)
        {
            var input = new LogAiAnalysisInput { BeginTime = begin, EndTime = end };
            var stats = _loginService.GetLoginRegionStats(input, TopN) ?? [];
            var (b, e) = input.ResolveRange(MaxDays);

            return Task.FromResult(new AiChartQueryResult
            {
                DatasetId = DatasetId,
                Title = Title,
                Grain = grain,
                // 地域分布是区间汇总，时间范围按整天展示
                TimeRange = $"{b:yyyy-MM-dd} ~ {e:yyyy-MM-dd}",
                SuggestedType = "pie",
                AllowedTypes = AllowedTypes,
                Fields = Fields.ToList(),
                Rows = stats.Select(s => new Dictionary<string, object>
                {
                    ["region"] = s.Region,
                    ["loginCount"] = s.LoginCount,
                    ["userCount"] = s.UserCount
                }).ToList()
            });
        }
    }
}
