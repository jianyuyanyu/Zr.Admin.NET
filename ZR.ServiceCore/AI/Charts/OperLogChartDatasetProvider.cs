using Infrastructure;
using Infrastructure.Attribute;
using NLog;
using ZR.Model.AI.Dto;
using ZR.Model.System.Dto;
using ZR.ServiceCore.AI.IService;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>
    /// 操作日志多维统计（按操作模块/操作类型/操作人/风险等级），供 AI 助手画柱状/饼图。
    /// 复用 GetOperDimensionStats 服务端固定聚合，不接触原始日志、不生成 SQL；
    /// 权限口径与操作日志 AI 健康分析一致：管理员统计全量，非管理员仅统计本人操作。
    /// </summary>
    [AppService(ServiceType = typeof(IAiChartDatasetProvider), ServiceLifetime = LifeTime.Transient)]
    public class OperLogChartDatasetProvider : IAiChartDatasetProvider
    {
        public const string Id = "oper";

        /// <summary>单维度返回条数上限，长尾合并为"其他"（风险固定 3 档、操作类型最多 11 种不合并）</summary>
        private const int TopN = 12;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly ISysOperLogService _operLogService;
        private readonly ISysUserService _userService;
        private readonly ISysPermissionService _permissionService;

        public OperLogChartDatasetProvider(
            ISysOperLogService operLogService,
            ISysUserService userService,
            ISysPermissionService permissionService)
        {
            _operLogService = operLogService;
            _userService = userService;
            _permissionService = permissionService;
        }

        public string DatasetId => Id;
        public string Title => "操作日志统计";

        public string Description =>
            "操作日志多维统计：可按操作模块（module）/操作类型（type）/操作人（user）/风险等级（risk）切片，"
            + "看操作次数与其中失败次数。模块=操作页面标题；操作类型=新增/修改/删除等；"
            + "风险等级由操作类型推导（删除/清空=高危，导出/导入/授权/强退/生成代码=中危，其余低危）。"
            + "适用于“哪个模块操作最多/操作类型分布/谁操作最频繁/高风险操作占比”等。";

        public string Permission => "monitor:operlog:ai";

        // 维度统计不按时间分桶，grain 仅用于决定可查询的最大跨度（见 MaxDays / MaxMonths）
        public string[] Grains => ["day", "week", "month"];
        public int MaxDays => 90;
        public int MaxMonths => 3;
        public string[] AllowedTypes => ["bar", "pie"];

        /// <summary>支持的统计维度：Key 同时作为该类目字段名</summary>
        public IReadOnlyList<AiChartDimension> Dimensions { get; } =
        [
            new AiChartDimension { Key = OperDimensionKinds.Module, Label = "操作模块", Hint = "哪个模块/页面被操作最多、失败最多" },
            new AiChartDimension { Key = OperDimensionKinds.Type, Label = "操作类型", Hint = "新增/修改/删除/导出等业务类型分布" },
            new AiChartDimension { Key = OperDimensionKinds.User, Label = "操作人", Hint = "哪个用户操作最频繁、失败最多" },
            new AiChartDimension { Key = OperDimensionKinds.Risk, Label = "风险等级", Hint = "高/中/低危操作占比" }
        ];

        /// <summary>字段随查询维度变化，此处仅作默认维度（module）示例</summary>
        public IReadOnlyList<AiChartFieldDef> Fields { get; } =
        [
            new AiChartFieldDef { Field = OperDimensionKinds.Module, Label = "操作模块", Kind = "category" },
            new AiChartFieldDef { Field = "total", Label = "操作次数", Kind = "number" },
            new AiChartFieldDef { Field = "errors", Label = "失败次数", Kind = "number" }
        ];

        public Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain)
            => QueryAsync(userId, begin, end, grain, null);

        public Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain, string dimension)
        {
            var input = new LogAiAnalysisInput { BeginTime = begin, EndTime = end };
            var (b, e) = input.ResolveRange(MaxDays);

            var kind = OperDimensionKinds.Normalize(dimension);
            var dim = Dimensions.FirstOrDefault(x => x.Key == kind) ?? Dimensions[0];
            var scope = ResolveScope(userId, out var operName);
            var title = $"操作日志·按{dim.Label}{scope}";

            // 非管理员但无法识别当前用户（异常兜底）：不返回任何行，避免退化成全量统计
            if (scope != null && string.IsNullOrWhiteSpace(operName))
            {
                return Task.FromResult(new AiChartQueryResult
                {
                    DatasetId = DatasetId,
                    Title = title,
                    Grain = grain,
                    TimeRange = $"{b:yyyy-MM-dd} ~ {e:yyyy-MM-dd}",
                    SuggestedType = "bar",
                    AllowedTypes = AllowedTypes,
                    Fields = BuildFields(dim),
                    Rows = []
                });
            }

            var stats = _operLogService.GetOperDimensionStats(input, kind, operName, TopN) ?? [];
            return Task.FromResult(new AiChartQueryResult
            {
                DatasetId = DatasetId,
                Title = title,
                Grain = grain,
                TimeRange = $"{b:yyyy-MM-dd} ~ {e:yyyy-MM-dd}",
                // 风险等级固定 3 档适合占比饼图，其余维度对比更适合柱状
                SuggestedType = kind == OperDimensionKinds.Risk ? "pie" : "bar",
                AllowedTypes = AllowedTypes,
                Fields = BuildFields(dim),
                Rows = stats.Select(s => new Dictionary<string, object>
                {
                    [dim.Key] = s.Name,
                    ["total"] = s.Total,
                    ["errors"] = s.Errors
                }).ToList()
            });
        }

        private static List<AiChartFieldDef> BuildFields(AiChartDimension dim)
        {
            return
            [
                new AiChartFieldDef { Field = dim.Key, Label = dim.Label, Kind = "category" },
                new AiChartFieldDef { Field = "total", Label = "操作次数", Kind = "number" },
                new AiChartFieldDef { Field = "errors", Label = "失败次数", Kind = "number" }
            ];
        }

        /// <summary>
        /// 管理员（*:*:*）统计全量返回 scope=null；非管理员限定本人（operName=当前登录用户名）。
        /// 计算权限失败时按非管理员处理（安全优先），此时若再取不到用户名即返回空结果。
        /// </summary>
        private string ResolveScope(long userId, out string operName)
        {
            try
            {
                var perms = _permissionService.GetMenuPermission(new SysUserDto { UserId = userId });
                if (perms != null && perms.Contains(GlobalConstant.AdminPerm))
                {
                    operName = null;
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "OperLogChartDatasetProvider 计算用户权限失败，按非管理员处理 userId={UserId}", userId);
            }

            try
            {
                operName = _userService.SelectUserById(userId)?.UserName;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "OperLogChartDatasetProvider 解析当前用户名失败 userId={UserId}", userId);
                operName = null;
            }
            return "（仅本人）";
        }
    }
}
