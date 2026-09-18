using ZR.Model;
using ZR.Model.System.Tenant;

namespace ZR.ServiceCore.SqlSugar
{
    /// <summary>
    /// 租户数据隔离自检：对比「主库共享实体中带 TenantId 字段的类型」与「TenantFilter 已注册过滤的类型」，
    /// 输出差集（豁免白名单除外）。用于防止新增共享表时漏注册租户过滤导致跨租户串数据。
    /// 自检为报告式：不自动修复，由平台管理员根据差集判断是否需要补充过滤或加入豁免。
    /// </summary>
    public static class TenantIsolationChecker
    {
        /// <summary>
        /// 豁免清单：平台管理表，数据仅平台侧（system:tenant:* 权限）读写，租户维度无隔离需求。
        /// 新增此类表时在这里登记，否则会出现在可疑差集中。
        /// </summary>
        private static readonly HashSet<string> ExemptTables = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(SysTenant),
            nameof(SysTenantPlan),
            nameof(SysTenantPlanBinding),
            nameof(SysTenantPlanMenu),
            nameof(SysTenantOrder),
        };

        /// <summary>
        /// 执行隔离自检，返回已覆盖/候选/可疑三份清单。
        /// 扫描范围为 ZR.Model 程序集（主库共享实体所在程序集）中实现 IMainDbEntity 且带 TenantId 属性的类型。
        /// 已覆盖清单以 <see cref="TenantFilter.FilteredEntityTypes"/>（Apply 实际注册）为准，
        /// 并与 TenantFilter 上的 Expression 方法交叉核对，防止只写了方法却未挂 QueryFilter。
        /// </summary>
        public static TenantIsolationCheckResult Check()
        {
            var coveredNames = new HashSet<string>(
                TenantFilter.FilteredEntityTypes.Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            var filterMethodNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var returnType in typeof(TenantFilter)
                         .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                         .Where(m => m.Name != nameof(TenantFilter.Apply))
                         .Select(m => m.ReturnType))
            {
                // Expression<Func<TEntity, bool>> → 首个泛型参数为 Func<TEntity, bool> → 其首参即实体类型
                if (!returnType.IsGenericType) continue;
                var funcArg = returnType.GetGenericArguments()[0];
                if (funcArg.IsGenericType)
                {
                    filterMethodNames.Add(funcArg.GetGenericArguments()[0].Name);
                }
            }

            var registrationMismatch = coveredNames
                .Except(filterMethodNames, StringComparer.OrdinalIgnoreCase)
                .Concat(filterMethodNames.Except(coveredNames, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 候选实体：ZR.Model 程序集中 IMainDbEntity + TenantId 属性
            var modelAssembly = typeof(SysTenant).Assembly;
            var candidates = modelAssembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract
                    && typeof(IMainDbEntity).IsAssignableFrom(t)
                    && t.GetProperty("TenantId") != null)
                .Select(t => t.Name)
                .ToList();

            // 可疑差集：带 TenantId 主库实体但既未注册过滤也不在豁免清单
            var suspicious = candidates
                .Where(name => !coveredNames.Contains(name) && !ExemptTables.Contains(name))
                .ToList();

            return new TenantIsolationCheckResult
            {
                CoveredList = coveredNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                ExemptList = ExemptTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                CandidateList = candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                SuspiciousList = suspicious,
                RegistrationMismatchList = registrationMismatch,
                IsSafe = suspicious.Count == 0 && registrationMismatch.Count == 0
            };
        }
    }

    /// <summary>
    /// 隔离自检结果。
    /// </summary>
    public class TenantIsolationCheckResult
    {
        /// <summary>
        /// TenantFilter 已注册租户过滤的实体清单
        /// </summary>
        public List<string> CoveredList { get; set; } = new();

        /// <summary>
        /// 平台管理表豁免清单（无租户隔离需求）
        /// </summary>
        public List<string> ExemptList { get; set; } = new();

        /// <summary>
        /// 主库共享实体中带 TenantId 字段的全部候选实体
        /// </summary>
        public List<string> CandidateList { get; set; } = new();

        /// <summary>
        /// 可疑清单：带 TenantId 但未注册过滤且未豁免的实体，需人工确认
        /// </summary>
        public List<string> SuspiciousList { get; set; } = new();

        /// <summary>
        /// TenantFilter 方法与 FilteredEntityTypes（Apply 注册表）不一致的实体名
        /// </summary>
        public List<string> RegistrationMismatchList { get; set; } = new();

        /// <summary>
        /// 可疑清单与注册不一致清单均为空时为 true
        /// </summary>
        public bool IsSafe { get; set; }
    }
}
