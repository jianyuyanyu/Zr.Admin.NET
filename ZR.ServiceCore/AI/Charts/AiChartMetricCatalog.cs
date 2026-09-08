namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>简单趋势图指标目录。新增 COUNT/SUM 时间序列在此加一条即可。</summary>
    public static class AiChartMetricCatalog
    {
        public static readonly IReadOnlyList<AiChartMetricDef> All =
        [
            new AiChartMetricDef
            {
                DatasetId = "login_daily",
                Title = "每日用户登录数",
                Description = "按天/周/月统计用户登录次数（成功与失败合计）。适用于“每天登录数/最近登录量/登录趋势图”。",
                Permission = "monitor:logininfor:ai",
                Entity = AiChartEntityKey.Logininfor,
                Agg = AiChartMetricAgg.Count,
                ValueField = "loginCount",
                ValueLabel = "用户登录数",
                Grains = ["day", "week", "month"],
                MaxDays = 90,
                MaxMonths = 3,
                AllowedTypes = ["line", "bar"],
                SuggestedType = "line"
            }
        ];
    }
}
