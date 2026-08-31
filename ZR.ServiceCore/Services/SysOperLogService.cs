using Infrastructure;
using Infrastructure.Attribute;
using Infrastructure.Enums;
using System.Text.RegularExpressions;
using ZR.Infrastructure.Constant;
using ZR.Infrastructure.Helper;
using ZR.Model;
using ZR.Model.System;
using ZR.Model.System.Dto;
using ZR.Model.System.Generate;
using ZR.Repository;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 操作日志
    /// </summary>
    [AppService(ServiceType = typeof(ISysOperLogService), ServiceLifetime = LifeTime.Transient)]
    public class SysOperLogService : BaseService<SysOperLog>, ISysOperLogService
    {
        /// <summary>计算耗时 P95 的最大样本量，超过则放弃计算避免大表扫描</summary>
        private const int MaxElapsedSampleRows = 20000;

        /// <summary>错误聚类采样条数上限（取最近的错误），避免把海量错误拉进内存</summary>
        private const int MaxErrorSampleRows = 2000;

        /// <summary>慢操作统计的最小调用次数门槛，避免偶发慢查询刷榜</summary>
        private const int MinSlowOperCount = 3;

        /// <summary>
        /// 新增操作日志操作
        /// </summary>
        /// <param name="operLog">日志对象</param>
        public void InsertOperlog(SysOperLog operLog)
        {
            if (operLog.OperParam != null && operLog.OperParam.Length >= 1000)
            {
                operLog.OperParam = operLog.OperParam[..1000];
            }
            //sysOperLogRepository.AddSysOperLog(operLog);
            Insert(operLog);
        }

        /// <summary>
        /// 查询系统操作日志集合
        /// </summary>
        /// <param name="sysOper">操作日志对象</param>
        /// <returns>操作日志集合</returns>
        public PagedInfo<SysOperLog> SelectOperLogList(SysOperLogQueryDto sysOper)
        {
            var exp = Expressionable.Create<SysOperLog>();
            exp.AndIF(sysOper.BeginTime == null, it => it.OperTime >= DateTime.Now.ToShortDateString().ParseToDateTime());
            exp.AndIF(sysOper.BeginTime != null, it => it.OperTime >= sysOper.BeginTime && it.OperTime <= sysOper.EndTime);
            exp.AndIF(sysOper.Title.IfNotEmpty(), it => it.Title.Contains(sysOper.Title));
            exp.AndIF(sysOper.OperName.IfNotEmpty(), it => it.OperName.Contains(sysOper.OperName));
            exp.AndIF(sysOper.BusinessType != null, it => it.BusinessType == sysOper.BusinessType);
            exp.AndIF(sysOper.Status != null, it => it.Status == sysOper.Status);
            exp.AndIF(sysOper.OperParam != null, it => it.OperParam.Contains(sysOper.OperParam));
            exp.AndIF(sysOper.BusinessTypes != null, it => sysOper.BusinessTypes.Contains(it.BusinessType));

            var list = Queryable()
                .Where(exp.ToExpression())
                .OrderBy(x => x.OperId, OrderByType.Desc)
                .ToPage(sysOper);
            list.Result.MaskField(
                HttpContextExtension.HasSensitivePerm(App.HttpContext, SensitivePerms.ViewRealIP),
                it => it.OperIp, (it, v) => it.OperIp = v, MaskUtil.MaskIp);
            return list;
        }

        /// <summary>
        /// 清空操作日志
        /// </summary>
        public void CleanOperLog()
        {
            var newTableName = $"sys_oper_log_{DateTime.Now:yyyyMMdd}";
            if (Queryable().Any() && !Context.DbMaintenance.IsAnyTable(newTableName))
            {
                Context.DbMaintenance.BackupTable("sys_oper_log", newTableName);
            }

            Truncate();
        }

        /// <summary>
        /// 批量删除系统操作日志
        /// </summary>
        /// <param name="operIds">需要删除的操作日志ID</param>
        /// <returns>结果</returns>
        public int DeleteOperLogByIds(long[] operIds)
        {
            return Delete(operIds);
        }

        /// <summary>
        /// 查询操作日志详细
        /// </summary>
        /// <param name="operId">操作ID</param>
        /// <returns>操作日志对象</returns>
        public SysOperLog SelectOperLogById(long operId)
        {
            return GetById(operId);
        }

        /// <summary>
        /// 聚合操作日志健康指标供 AI 解读。错误先归一化聚类再交给模型，
        /// 避免把海量原始错误（含参数、堆栈）直接喂给模型；operName 非空时只统计该用户（非管理员与列表页同口径）。
        /// </summary>
        public OperHealthMetricsDto GetOperHealthMetrics(LogAiAnalysisInput input, string operName = null)
        {
            var (begin, end) = input.ResolveRange();
            var metrics = new OperHealthMetricsDto
            {
                TimeRange = $"{begin:yyyy-MM-dd} ~ {end:yyyy-MM-dd}"
            };
            var notes = new List<string>();

            metrics.TotalCount = BuildRangeQuery(begin, end, operName).Count();
            metrics.ErrorCount = BuildRangeQuery(begin, end, operName).Where(it => it.Status == 1).Count();
            metrics.NightCount = BuildRangeQuery(begin, end, operName).Where(it => it.OperTime.Value.Hour < 6).Count();

            // 耗时 P95：数据量过大时放弃计算，宁可缺省也不拖垮查询；
            // 样本行数与计数可能因状态/并发不一致，取到空样本时跳过而不是索引越界
            if (metrics.TotalCount > 0 && metrics.TotalCount <= MaxElapsedSampleRows)
            {
                var elapsed = BuildRangeQuery(begin, end, operName).Select(it => it.Elapsed).ToList();
                if (elapsed.Count == 0)
                {
                    notes.Add("未能取到耗时样本，未计算耗时 P95");
                }
                else
                {
                    elapsed.Sort();
                    var idx = Math.Max((int)Math.Ceiling(elapsed.Count * 0.95) - 1, 0);
                    metrics.P95Elapsed = elapsed[idx];
                }
            }
            else if (metrics.TotalCount > MaxElapsedSampleRows)
            {
                notes.Add($"操作量超过 {MaxElapsedSampleRows} 条，未计算耗时 P95");
            }

            // 每日总量/错误量趋势
            metrics.Daily = BuildRangeQuery(begin, end, operName).GroupBy(it => it.OperTime.Value.ToString("yyyy-MM-dd"))
                .Select(it => new
                {
                    Date = it.OperTime.Value.ToString("yyyy-MM-dd"),
                    Total = SqlFunc.AggregateCount(it.OperId),
                    Errors = SqlFunc.AggregateSum(SqlFunc.IIF(it.Status == 1, 1, 0))
                })
                .ToList()
                .OrderBy(x => x.Date)
                .Select(x => new OperDailyStat { Date = x.Date, Total = x.Total, Errors = x.Errors })
                .ToList();

            // 业务类型分布
            metrics.BusinessTypes = BuildRangeQuery(begin, end, operName).GroupBy(it => it.BusinessType)
                .Select(it => new
                {
                    it.BusinessType,
                    Total = SqlFunc.AggregateCount(it.OperId),
                    Errors = SqlFunc.AggregateSum(SqlFunc.IIF(it.Status == 1, 1, 0))
                })
                .ToList()
                .OrderBy(x => x.BusinessType)
                .Select(x => new OperTypeStat
                {
                    TypeName = DescribeBusinessType(x.BusinessType),
                    Total = x.Total,
                    Errors = x.Errors
                })
                .ToList();

            // 慢操作 Top：仅统计调用次数达门槛的操作
            metrics.SlowOps = BuildRangeQuery(begin, end, operName).GroupBy(it => new { it.Title, it.Method })
                .Having(it => SqlFunc.AggregateCount(it.OperId) >= MinSlowOperCount)
                .Select(it => new
                {
                    it.Title,
                    it.Method,
                    Count = SqlFunc.AggregateCount(it.OperId),
                    Avg = SqlFunc.AggregateAvg(it.Elapsed),
                    Max = SqlFunc.AggregateMax(it.Elapsed),
                    Last = SqlFunc.AggregateMax(it.OperTime)
                })
                .ToList()
                .OrderByDescending(x => x.Avg)
                .Take(20)
                .Select(x => new SlowOperStat
                {
                    Title = x.Title,
                    Method = x.Method,
                    Count = x.Count,
                    AvgElapsed = Math.Round(Convert.ToDouble(x.Avg), 1),
                    MaxElapsed = x.Max,
                    LastOperTime = x.Last
                })
                .ToList();

            // 敏感操作（删除/导出/强退/清空）Top 账号
            int[] riskTypes = { (int)BusinessType.DELETE, (int)BusinessType.EXPORT, (int)BusinessType.FORCE, (int)BusinessType.CLEAN };
            metrics.RiskAccounts = BuildRangeQuery(begin, end, operName).Where(it => riskTypes.Contains(it.BusinessType))
                .GroupBy(it => it.OperName)
                .Select(it => new { Name = it.OperName, Num = SqlFunc.AggregateCount(it.OperId) })
                .ToList()
                .OrderByDescending(x => x.Num).Take(10)
                .Select(x => new NameCountStat { Name = x.Name, Count = x.Num })
                .ToList();

            // 错误聚类：先取最近样本，归一化特征后内存分组，每类只保留一条原始样例
            var errorRows = BuildRangeQuery(begin, end, operName).Where(it => it.Status == 1)
                .OrderByDescending(it => it.OperId)
                .Take(MaxErrorSampleRows)
                .Select(it => new { it.ErrorMsg, it.Method, it.Title, it.OperTime })
                .ToList();
            if (metrics.ErrorCount > MaxErrorSampleRows)
            {
                notes.Add($"错误样本仅取最近 {MaxErrorSampleRows} 条聚类");
            }
            metrics.ErrorClusters = errorRows
                .Where(x => !string.IsNullOrWhiteSpace(x.ErrorMsg))
                .GroupBy(x => NormalizeErrorMsg(x.ErrorMsg))
                .Select(g => new ErrorClusterStat
                {
                    Pattern = g.Key,
                    Count = g.Count(),
                    SampleErrorMsg = ClipText(g.OrderByDescending(x => x.OperTime).First().ErrorMsg, 500),
                    SampleMethod = g.First().Method,
                    SampleTitle = g.First().Title,
                    FirstTime = g.Min(x => x.OperTime),
                    LastTime = g.Max(x => x.OperTime)
                })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToList();

            metrics.SampleNote = string.Join("；", notes);
            return metrics;
        }

        /// <summary>
        /// 构建区间内操作日志查询。每次调用返回全新 queryable，
        /// 避免 SqlSugar 同一实例在 Count()/ToList() 之间复用时共享查询状态产生串扰。
        /// </summary>
        private ISugarQueryable<SysOperLog> BuildRangeQuery(DateTime begin, DateTime end, string operName)
        {
            return Queryable()
                .Where(it => it.OperTime >= begin && it.OperTime <= end)
                .WhereIF(!string.IsNullOrEmpty(operName), it => it.OperName == operName);
        }

        /// <summary>
        /// 错误消息归一化：取首行去掉堆栈细节，把数字/GUID 替换为占位符，
        /// 使同一根因的错误聚到一类，而不是每条自成一类。
        /// </summary>
        private static string NormalizeErrorMsg(string errorMsg)
        {
            var text = errorMsg.Trim();
            var lineBreak = text.IndexOfAny(new[] { '\r', '\n' });
            if (lineBreak >= 0)
            {
                text = text[..lineBreak];
            }
            text = Regex.Replace(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "{guid}");
            text = Regex.Replace(text, "\\d+", "#");
            text = Regex.Replace(text, "\\s+", " ").Trim();
            return text.Length <= 200 ? text : text[..200];
        }

        private static string ClipText(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }

        private static string DescribeBusinessType(int businessType)
        {
            return businessType switch
            {
                1 => "新增",
                2 => "修改",
                3 => "删除",
                4 => "授权",
                5 => "导出",
                6 => "导入",
                7 => "强退",
                8 => "生成代码",
                9 => "清空数据",
                10 => "下载",
                _ => "其它"
            };
        }
    }
}
