using System;

namespace ZR.Model.System.Dto
{
    /// <summary>
    /// 日志 AI 分析请求参数（登录安全分析 / 操作日志健康分析共用）
    /// </summary>
    public class LogAiAnalysisInput
    {
        /// <summary>
        /// 开始时间，为空默认近 7 天
        /// </summary>
        public DateTime? BeginTime { get; set; }

        /// <summary>
        /// 结束时间，为空默认到当前
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 解析统计区间：起止倒置自动交换，跨度超上限时以结束时间为准向前截断。
        /// 返回值为整天边界（begin 为当天 00:00:00，end 为当天 23:59:59）。
        /// </summary>
        public (DateTime Begin, DateTime End) ResolveRange(int maxDays = 90)
        {
            var end = (EndTime ?? DateTime.Now).Date;
            var begin = (BeginTime ?? end.AddDays(-6)).Date;
            if (begin > end)
            {
                (begin, end) = (end, begin);
            }
            if ((end - begin).TotalDays > maxDays)
            {
                begin = end.AddDays(-maxDays);
            }
            return (begin, end.Date.AddDays(1).AddSeconds(-1));
        }

        /// <summary>
        /// 异地/新地点成功登录账号返回条数上限，为空默认 15（服务端约束 1-50）。
        /// 页面默认不传保持 Top15；AI 助手"把异地登录账号全部查出来"类提问可传 50。
        /// </summary>
        public int? Limit { get; set; }
    }

    /// <summary>
    /// 登录日志安全分析聚合指标。由服务端固定 SQL 聚合，AI 只负责解读，不接触原始日志。
    /// </summary>
    public class LoginSecurityMetricsDto
    {
        /// <summary>统计时间范围</summary>
        public string TimeRange { get; set; }

        public long TotalCount { get; set; }

        public long SuccessCount { get; set; }

        public long FailCount { get; set; }

        /// <summary>凌晨 0-6 点登录次数（异常时段信号）</summary>
        public long NightCount { get; set; }

        /// <summary>凌晨 0-6 点登录失败次数</summary>
        public long NightFailCount { get; set; }

        /// <summary>区间内去重登录 IP 数</summary>
        public long DistinctIpCount { get; set; }

        /// <summary>近 30 天未出现过、本次区间首次出现的 IP 数</summary>
        public long NewIpCount { get; set; }

        /// <summary>IP 是否已脱敏（当前用户无真实 IP 查看权限时为 true，ipaddr 为掩码值）</summary>
        public bool IpMasked { get; set; }

        /// <summary>区间内异地/新地点成功登录的账号数（同账号多地点成功登录，或相对近 30 天成功历史出现新地点）</summary>
        public long RemoteLoginAccountCount { get; set; }

        /// <summary>异地/新地点成功登录账号明细 Top（含 2 小时内跨地点切换次数）</summary>
        public List<RemoteLoginAccountStat> RemoteLoginAccounts { get; set; } = new List<RemoteLoginAccountStat>();

        /// <summary>按天成功/失败趋势</summary>
        public List<LoginDailyStat> Daily { get; set; } = new List<LoginDailyStat>();

        /// <summary>登录失败次数 Top 账号</summary>
        public List<LoginFailAccountStat> FailedAccounts { get; set; } = new List<LoginFailAccountStat>();

        /// <summary>登录失败次数 Top IP</summary>
        public List<LoginFailIpStat> FailedIps { get; set; } = new List<LoginFailIpStat>();

        /// <summary>客户端浏览器分布 Top5</summary>
        public List<NameCountStat> Browsers { get; set; } = new List<NameCountStat>();

        /// <summary>客户端操作系统分布 Top5</summary>
        public List<NameCountStat> Oses { get; set; } = new List<NameCountStat>();
    }

    /// <summary>
    /// 登录日志按天统计
    /// </summary>
    public class LoginDailyStat
    {
        public string Date { get; set; }

        public long Success { get; set; }

        public long Fail { get; set; }
    }

    /// <summary>
    /// 操作日志的可选统计维度。同一份操作日志数据的不同切片，
    /// 由图表数据集以 dimension 参数切换，Key 同时作为该类目字段名。
    /// </summary>
    public static class OperDimensionKinds
    {
        /// <summary>按操作模块（SysOperLog.Title）</summary>
        public const string Module = "module";

        /// <summary>按操作类型（BusinessType 中文名）</summary>
        public const string Type = "type";

        /// <summary>按操作人（SysOperLog.OperName）</summary>
        public const string User = "user";

        /// <summary>按风险等级（由 BusinessType 推导的高/中/低危）</summary>
        public const string Risk = "risk";

        /// <summary>默认维度</summary>
        public const string Default = Module;

        /// <summary>归一化维度取值，非法或空返回默认维度</summary>
        public static string Normalize(string dimension)
        {
            if (string.IsNullOrWhiteSpace(dimension))
            {
                return Default;
            }
            var value = dimension.Trim().ToLowerInvariant();
            return value switch
            {
                Module or Type or User or Risk => value,
                // 兼容模型可能传的中文/别称
                "模块" or "操作模块" => Module,
                "类型" or "操作类型" or "业务类型" => Type,
                "人" or "用户" or "操作人" or "操作人员" => User,
                "风险" or "风险等级" or "风险级别" => Risk,
                _ => Default
            };
        }
    }

    /// <summary>
    /// 操作日志按维度聚合的单项（维度见 OperDimensionKinds，如模块/操作类型/操作人/风险等级）
    /// </summary>
    public class OperDimensionStat
    {
        /// <summary>维度名称（模块名/操作类型中文名/操作人/风险等级）</summary>
        public string Name { get; set; }

        /// <summary>操作次数</summary>
        public long Total { get; set; }

        /// <summary>其中失败次数（Status=1）</summary>
        public long Errors { get; set; }
    }

    /// <summary>
    /// 登录日志按省份（IP 解析出的地域）统计。
    /// Region 为省级名称（如"河南省""天津市"），无法解析时归入"未知"。
    /// </summary>
    public class LoginRegionStat
    {
        /// <summary>省份名称；含"未知"与"其他"（长尾合并项）</summary>
        public string Region { get; set; }

        /// <summary>登录次数（成功+失败）</summary>
        public long LoginCount { get; set; }

        /// <summary>独立用户数（按账号去重）</summary>
        public long UserCount { get; set; }
    }

    /// <summary>
    /// 登录失败账号统计
    /// </summary>
    public class LoginFailAccountStat
    {
        public string UserName { get; set; }

        public long FailCount { get; set; }

        /// <summary>失败来源地点去重数，大于 1 提示异地/共享账号可能</summary>
        public long LocationCount { get; set; }

        /// <summary>失败来源地点列表（最多 5 个）</summary>
        public string Locations { get; set; }

        public DateTime? LastFailTime { get; set; }
    }

    /// <summary>
    /// 异地/新地点成功登录账号统计（成功侧异地信号）。
    /// 地点按省-市归一化（忽略运营商与未知段），与登录时的异地提醒同一口径。
    /// </summary>
    public class RemoteLoginAccountStat
    {
        /// <summary>账号</summary>
        public string UserName { get; set; }

        /// <summary>区间内成功登录次数</summary>
        public long SuccessCount { get; set; }

        /// <summary>成功登录来源地点去重数（大于 1 提示异地/共享账号可能）</summary>
        public long LocationCount { get; set; }

        /// <summary>成功登录来源地点列表（最多 5 个）</summary>
        public string Locations { get; set; }

        /// <summary>相对近 30 天成功历史新增的地点数</summary>
        public long NewLocationCount { get; set; }

        /// <summary>相对近 30 天成功历史新增的地点列表（最多 3 个）</summary>
        public string NewLocations { get; set; }

        /// <summary>2 小时内切换不同地点成功登录的次数（盗号/共享高置信信号）</summary>
        public long RapidSwitchCount { get; set; }

        /// <summary>最近一次成功登录时间</summary>
        public DateTime? LastLoginTime { get; set; }
    }

    /// <summary>
    /// 登录失败 IP 统计
    /// </summary>
    public class LoginFailIpStat
    {
        public string Ipaddr { get; set; }

        public long FailCount { get; set; }

        public string Location { get; set; }

        public DateTime? LastFailTime { get; set; }
    }

    /// <summary>
    /// 通用名称计数项（浏览器/OS 分布、敏感操作 Top 账号等）
    /// </summary>
    public class NameCountStat
    {
        public string Name { get; set; }

        public long Count { get; set; }
    }

    /// <summary>
    /// 操作日志健康分析聚合指标。错误在服务端归一化聚类后才交给模型。
    /// </summary>
    public class OperHealthMetricsDto
    {
        /// <summary>统计时间范围</summary>
        public string TimeRange { get; set; }

        public long TotalCount { get; set; }

        public long ErrorCount { get; set; }

        /// <summary>凌晨 0-6 点操作次数</summary>
        public long NightCount { get; set; }

        /// <summary>耗时 P95（毫秒），数据量过大未计算时为 null</summary>
        public double? P95Elapsed { get; set; }

        /// <summary>按天总量/错误量趋势</summary>
        public List<OperDailyStat> Daily { get; set; } = new List<OperDailyStat>();

        /// <summary>业务类型分布</summary>
        public List<OperTypeStat> BusinessTypes { get; set; } = new List<OperTypeStat>();

        /// <summary>慢操作 Top（按平均耗时排序，仅统计调用次数达门槛的操作）</summary>
        public List<SlowOperStat> SlowOps { get; set; } = new List<SlowOperStat>();

        /// <summary>导出/删除/强退/清空等敏感操作 Top 账号</summary>
        public List<NameCountStat> RiskAccounts { get; set; } = new List<NameCountStat>();

        /// <summary>错误聚类（错误消息归一化后分组）</summary>
        public List<ErrorClusterStat> ErrorClusters { get; set; } = new List<ErrorClusterStat>();

        /// <summary>数据截断说明（如错误样本只取最近 N 条），无则空</summary>
        public string SampleNote { get; set; }
    }

    /// <summary>
    /// 操作日志按天统计
    /// </summary>
    public class OperDailyStat
    {
        public string Date { get; set; }

        public long Total { get; set; }

        public long Errors { get; set; }
    }

    /// <summary>
    /// 业务类型分布统计
    /// </summary>
    public class OperTypeStat
    {
        /// <summary>业务类型中文名</summary>
        public string TypeName { get; set; }

        public long Total { get; set; }

        public long Errors { get; set; }
    }

    /// <summary>
    /// 慢操作统计
    /// </summary>
    public class SlowOperStat
    {
        public string Title { get; set; }

        public string Method { get; set; }

        public long Count { get; set; }

        /// <summary>平均耗时（毫秒）</summary>
        public double AvgElapsed { get; set; }

        /// <summary>最大耗时（毫秒）</summary>
        public long MaxElapsed { get; set; }

        public DateTime? LastOperTime { get; set; }
    }

    /// <summary>
    /// 错误聚类统计
    /// </summary>
    public class ErrorClusterStat
    {
        /// <summary>归一化后的错误特征（数字/GUID 已替换为占位符）</summary>
        public string Pattern { get; set; }

        public long Count { get; set; }

        /// <summary>该类最近一条原始错误样例</summary>
        public string SampleErrorMsg { get; set; }

        public string SampleMethod { get; set; }

        public string SampleTitle { get; set; }

        public DateTime? FirstTime { get; set; }

        public DateTime? LastTime { get; set; }
    }

    /// <summary>
    /// 日志 AI 分析结果（Markdown 报告文本，不落库）
    /// </summary>
    public class SysAiLogReportResult
    {
        public string Report { get; set; }
    }
}
