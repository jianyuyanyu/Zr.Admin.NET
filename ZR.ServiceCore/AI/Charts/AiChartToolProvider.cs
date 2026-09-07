using Infrastructure;
using Infrastructure.Attribute;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using System.Text;
using ZR.Model.AI.Dto;
using ZR.Model.System.Dto;
using ZR.ServiceCore.AI.IService;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>
    /// 通用图表工具：列出/查询已注册数据集。模型只选 datasetId 与粒度，SQL 由各 Provider 写死。
    /// </summary>
    [AppService(ServiceType = typeof(IAiAssistantToolProvider), ServiceLifetime = LifeTime.Transient)]
    public class AiChartToolProvider : IAiAssistantToolProvider
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly IReadOnlyList<IAiChartDatasetProvider> _datasets;
        private readonly ISysPermissionService _permissionService;

        public string ProviderName => "chart";

        public AiChartToolProvider(
            IEnumerable<IAiChartDatasetProvider> datasets,
            ISysPermissionService permissionService)
        {
            _datasets = datasets?.ToList() ?? [];
            _permissionService = permissionService;
        }

        public List<AiToolDef> GetToolDefs()
        {
            var catalog = string.Join("；", _datasets.Select(d =>
                $"{d.DatasetId}（{d.Title}，粒度 {string.Join("/", d.Grains)}，图 {string.Join("/", d.AllowedTypes)}：{d.Description}）"));
            if (string.IsNullOrEmpty(catalog))
            {
                catalog = "当前未注册任何图表数据集";
            }

            return
            [
                new AiToolDef
                {
                    Name = "query_chart_dataset",
                    Description = "查询后台预注册的聚合图表数据（折线/柱状/饼图）。禁止用于任意表查询或写 SQL。"
                        + "不传 datasetId 时返回当前用户有权的数据集清单。"
                        + "已注册：" + catalog
                        + "。适用于“趋势图/对比/最近N天登录量/画个折线图”等。查到数据后，最终回复须追加 ```zr-chart 配置块（只填 type/title/xField/series，不要改数据行）。",
                    Parameters = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["datasetId"] = new { type = "string", description = "数据集 Id，如 login_daily；省略则只列出清单" },
                            ["days"] = new { type = "integer", description = "day/week 粒度的天数，默认 7，最大以数据集为准（通常 90）" },
                            ["months"] = new { type = "integer", description = "month 粒度的月数，默认 12，最大通常 24" },
                            ["grain"] = new { type = "string", description = "时间粒度 day|week|month，默认 day" },
                            ["chartType"] = new { type = "string", description = "建议图类型 line|bar|pie，须属于该数据集允许类型" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            ];
        }

        public async Task<AiToolExecResult> ExecuteAsync(string toolName, string argsJson, long userId)
        {
            if (toolName != "query_chart_dataset")
            {
                return null;
            }

            var perms = LoadPerms(userId);
            if (perms == null)
            {
                return AiToolExecResult.Error("无法校验权限，请稍后重试");
            }

            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson);
            }
            catch
            {
                args = new JObject();
            }

            var datasetId = args["datasetId"]?.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(datasetId))
            {
                return AiToolExecResult.Success(FormatCatalog(perms));
            }

            var provider = _datasets.FirstOrDefault(d =>
                string.Equals(d.DatasetId, datasetId, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
            {
                return AiToolExecResult.Error(
                    $"未注册数据集 {datasetId}。{FormatCatalog(perms)} 请到对应模块查看，不要编写 SQL。");
            }
            if (!HasDatasetPerm(perms, provider.Permission))
            {
                return AiToolExecResult.Error(
                    $"你没有查看「{provider.Title}」图表的权限（需要 {provider.Permission}），请联系管理员授权。");
            }

            var grain = NormalizeGrain(args["grain"]?.Value<string>(), provider);
            var (begin, end, rangeNote) = ResolveRange(args, grain, provider);
            var result = await provider.QueryAsync(userId, begin, end, grain);
            var chartType = args["chartType"]?.Value<string>()?.Trim()?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(chartType) && provider.AllowedTypes.Contains(chartType))
            {
                result.SuggestedType = chartType;
            }

            if (result.Rows == null || result.Rows.Count == 0)
            {
                return AiToolExecResult.Success(
                    $"数据集 {provider.DatasetId} 在 {result.TimeRange}（{grain}）没有数据。{rangeNote}".Trim());
            }

            var content = FormatQueryForModel(result, rangeNote);
            return new AiToolExecResult { Ok = true, Content = content, ChartQuery = result };
        }

        private string FormatCatalog(List<string> perms)
        {
            var allowed = _datasets.Where(d => HasDatasetPerm(perms, d.Permission)).ToList();
            if (allowed.Count == 0)
            {
                return "当前没有你有权限使用的图表数据集，请到对应模块查看或联系管理员授权。";
            }
            var sb = new StringBuilder("你可查询的图表数据集：\n");
            foreach (var d in allowed)
            {
                sb.AppendLine(
                    $"- {d.DatasetId}：{d.Title}。{d.Description} 粒度 {string.Join("/", d.Grains)}，图 {string.Join("/", d.AllowedTypes)}，字段 {string.Join(",", d.Fields.Select(f => f.Field + "(" + f.Label + ")"))}");
            }
            sb.Append("选一个 datasetId 再次调用本工具查询数据。未列出的业务（如充值）表示尚未注册，请到对应模块查看，不要写 SQL。");
            return sb.ToString();
        }

        private static string FormatQueryForModel(AiChartQueryResult result, string rangeNote)
        {
            var fields = string.Join("，", result.Fields.Select(f => $"{f.Field}({f.Label}/{f.Kind})"));
            var types = string.Join("|", result.AllowedTypes ?? []);
            var json = JsonConvert.SerializeObject(result.Rows);
            var sb = new StringBuilder();
            sb.AppendLine($"图表数据已由后端聚合（datasetId={result.DatasetId}，{result.TimeRange}，grain={result.Grain}，{result.Rows.Count} 行）。{rangeNote}".Trim());
            sb.AppendLine($"标题建议：{result.Title}；建议图类型：{result.SuggestedType}；允许：{types}");
            sb.AppendLine($"字段：{fields}");
            sb.AppendLine("最终回复请用自然语言解读（不要编造数字），并追加唯一代码块：");
            sb.AppendLine("```zr-chart");
            var xField = result.Fields.FirstOrDefault(f => f.Kind == "category")?.Field ?? "date";
            var seriesField = result.Fields.FirstOrDefault(f => f.Kind == "number");
            var seriesName = seriesField?.Label ?? "数值";
            var seriesKey = seriesField?.Field ?? "value";
            sb.AppendLine($"{{\"datasetId\":\"{result.DatasetId}\",\"type\":\"{result.SuggestedType}\",\"title\":\"{result.Title}\",\"xField\":\"{xField}\",\"series\":[{{\"name\":\"{seriesName}\",\"field\":\"{seriesKey}\"}}]}}");
            sb.AppendLine("```");
            sb.AppendLine("series.field / xField 必须是上面的字段名；不要输出完整 ECharts option，不要修改数据行。");
            sb.AppendLine("数据行：");
            sb.Append(json);
            return sb.ToString();
        }

        private static string NormalizeGrain(string grain, IAiChartDatasetProvider provider)
        {
            grain = (grain ?? "day").Trim().ToLowerInvariant();
            if (!provider.Grains.Contains(grain))
            {
                return provider.Grains.FirstOrDefault() ?? "day";
            }
            return grain;
        }

        private static (DateTime begin, DateTime end, string note) ResolveRange(JObject args, string grain, IAiChartDatasetProvider provider)
        {
            var end = DateTime.Today;
            string note = "";
            DateTime begin;
            if (grain == "month")
            {
                var months = 12;
                if (args["months"] != null)
                {
                    months = args["months"].Value<int>();
                }
                else if (args["days"] != null)
                {
                    months = Math.Max(1, (args["days"].Value<int>() + 29) / 30);
                }
                var max = Math.Max(1, provider.MaxMonths);
                if (months < 1) months = 1;
                if (months > max)
                {
                    note = $"月跨度已截断为 {max} 个月。";
                    months = max;
                }
                begin = end.AddMonths(1 - months);
            }
            else
            {
                var days = args["days"]?.Value<int>() ?? 7;
                var max = Math.Max(1, provider.MaxDays);
                if (days < 1) days = 1;
                if (days > max)
                {
                    note = $"天数已截断为 {max} 天。";
                    days = max;
                }
                begin = end.AddDays(1 - days);
            }
            return (begin, end, note);
        }

        private static bool HasDatasetPerm(List<string> perms, string permission)
        {
            if (perms.Contains(GlobalConstant.AdminPerm))
            {
                return true;
            }
            if (string.IsNullOrWhiteSpace(permission) || permission == "common")
            {
                return true;
            }
            return perms.Contains(permission);
        }

        private List<string> LoadPerms(long userId)
        {
            try
            {
                return _permissionService.GetMenuPermission(new SysUserDto { UserId = userId });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "AiChartToolProvider 计算用户权限失败 userId={UserId}", userId);
                return null;
            }
        }
    }
}
