using Infrastructure;
using Infrastructure.Attribute;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using UAParser;
using ZR.Common;
using ZR.Infrastructure.Constant;
using ZR.Infrastructure.Helper;
using ZR.Infrastructure.IPTools;
using ZR.Model;
using ZR.Model.System;
using ZR.Model.System.Dto;
using ZR.Repository;
using ZR.ServiceCore.Model.Dto;
using ZR.ServiceCore.Resources;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 登录
    /// </summary>
    [AppService(ServiceType = typeof(ISysLoginService), ServiceLifetime = LifeTime.Transient)]
    public class SysLoginService : BaseService<SysLogininfor>, ISysLoginService
    {
        private readonly ISysUserService SysUserService;
        private readonly ISysUserMsgService sysUserMsgService;
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IStringLocalizer<SharedResource> _localizer;

        public SysLoginService(
            ISysUserService sysUserService, 
            ISysUserMsgService sysUserMsgService,
            IHttpContextAccessor httpContextAccessor,
            IStringLocalizer<SharedResource> localizer)
        {
            SysUserService = sysUserService;
            this.sysUserMsgService = sysUserMsgService;
            this.httpContextAccessor = httpContextAccessor;
            _localizer = localizer;
        }

        /// <summary>
        /// 登录验证
        /// </summary>
        /// <param name="logininfor"></param>
        /// <param name="loginBody"></param>
        /// <returns></returns>
        public SysUser Login(LoginBodyDto loginBody, SysLogininfor logininfor)
        {
            if (loginBody.Password.Length != 32)
            {
                loginBody.Password = NETCore.Encrypt.EncryptProvider.Md5(loginBody.Password);
            }
            SysUser user = SysUserService.Login(loginBody);
            logininfor.UserName = loginBody.Username;
            logininfor.Status = "1";
            logininfor.LoginTime = DateTime.Now;
            logininfor.Ipaddr = loginBody.LoginIP;
            logininfor.ClientId = loginBody.ClientId;

            ClientInfo clientInfo = httpContextAccessor.HttpContext.GetClientInfo();
            logininfor.Browser = clientInfo.ToString();
            logininfor.Os = clientInfo.OS.ToString();

            if (user == null || user.UserId <= 0)
            {
                logininfor.Msg = _localizer["login_pwd_error"].Value;
                AddLoginInfo(logininfor, loginBody.TenantId);
                throw new CustomException(ResultCode.LOGIN_ERROR, logininfor.Msg, false);
            }
            logininfor.UserId = user.UserId;
            if (user.Status == 1)
            {
                logininfor.Msg = _localizer["login_user_disabled"].Value;//該用戶已禁用
                AddLoginInfo(logininfor, loginBody.TenantId);
                throw new CustomException(ResultCode.LOGIN_ERROR, logininfor.Msg, false);
            }

            logininfor.Status = "0";
            logininfor.Msg = "登录成功";
            AddLoginInfo(logininfor, loginBody.TenantId);
            SysUserService.UpdateLoginInfo(loginBody.LoginIP, user.UserId);
            return user;
        }

        /// <summary>
        /// 登录验证
        /// </summary>
        /// <param name="logininfor"></param>
        /// <param name="loginBody"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public SysUserDto PhoneLogin(PhoneLoginDto loginBody, SysLogininfor logininfor, SysUserDto user)
        {
            logininfor.UserName = user.UserName;
            logininfor.Status = "1";
            logininfor.LoginTime = DateTime.Now;
            logininfor.Ipaddr = loginBody.LoginIP;

            ClientInfo clientInfo = httpContextAccessor.HttpContext.GetClientInfo();
            logininfor.Browser = clientInfo.ToString();
            logininfor.Os = clientInfo.OS.ToString();

            if (user.Status == 1)
            {
                logininfor.Msg = _localizer["login_user_disabled"].Value;
                AddLoginInfo(logininfor, loginBody.TenantId);
                throw new CustomException(ResultCode.LOGIN_ERROR, logininfor.Msg, false);
            }

            logininfor.Status = "0";
            logininfor.Msg = "登录成功";
            AddLoginInfo(logininfor, loginBody.TenantId);
            SysUserService.UpdateLoginInfo(loginBody.LoginIP, user.UserId);
            return user;
        }

        /// <summary>
        /// 查询登录日志
        /// </summary>
        /// <param name="logininfoDto"></param>
        /// <returns></returns>
        public PagedInfo<SysLogininfor> GetLoginLog(SysLogininfoQueryDto logininfoDto)
        {
            var db = ResolveTenantDb();
            var exp = Expressionable.Create<SysLogininfor>();

            exp.AndIF(logininfoDto.BeginTime == null, it => it.LoginTime >= DateTime.Now.ToShortDateString().ParseToDateTime());
            exp.AndIF(logininfoDto.BeginTime != null, it => it.LoginTime >= logininfoDto.BeginTime && it.LoginTime <= logininfoDto.EndTime);
            exp.AndIF(logininfoDto.UserId != null, it => it.UserId == logininfoDto.UserId);
            exp.AndIF(logininfoDto.Status.IfNotEmpty(), f => f.Status == logininfoDto.Status);
            exp.AndIF(logininfoDto.Ipaddr.IfNotEmpty(), f => f.Ipaddr == logininfoDto.Ipaddr);
            exp.AndIF(logininfoDto.UserName.IfNotEmpty(), f => f.UserName.Contains(logininfoDto.UserName));

            var query = db.Queryable<SysLogininfor>().ApplyScope().Where(exp.ToExpression())
                .OrderBy(it => it.InfoId, OrderByType.Desc);

            var list = query.ToPage(logininfoDto);
            list.Result.MaskField(
                HttpContextExtension.HasSensitivePerm(App.HttpContext, SensitivePerms.ViewRealIP),
                it => it.Ipaddr, (it, v) => it.Ipaddr = v, MaskUtil.MaskIp);
            return list;
        }

        /// <summary>
        /// 记录登录日志
        /// </summary>
        /// <param name="sysLogininfor"></param>
        /// <param name="tenantId">租户ID，多租户模式下写入对应租户库</param>
        /// <returns></returns>
        public void AddLoginInfo(SysLogininfor sysLogininfor, string tenantId = null)
        {
            var db = ResolveTenantDb(tenantId);
            db.Insertable(sysLogininfor).ExecuteCommand();
        }

        /// <summary>
        /// 清空登录日志
        /// </summary>
        public void TruncateLogininfo()
        {
            Truncate();
        }

        /// <summary>
        /// 删除登录日志
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public int DeleteLogininforByIds(long[] ids)
        {
            return Delete(ids);
        }

        public void CheckLockUser(string userName)
        {
            var lockTimeStamp = CacheService.GetLockUser(userName);
            var lockTime = DateTimeHelper.ToLocalTimeDateBySeconds(lockTimeStamp);
            var ts = lockTime - DateTime.Now;

            if (lockTimeStamp > 0 && ts.TotalSeconds > 0)
            {
                // 账号锁定提醒：登录态尚未建立，使用 ClearFilter 跨数据权限解析用户后发送系统消息
                var userId = Context.Queryable<SysUser>().ClearFilter().Where(u => u.UserName == userName).Select(u => u.UserId).First();
                if (userId > 0)
                {
                    var minutes = Math.Round(ts.TotalMinutes, 0);
                    sysUserMsgService.AddSysUserMsg(userId, $"您的账号因多次登录失败已被锁定，剩余{minutes}分钟，解锁后请重试", UserMsgType.SYSTEM);
                }
                throw new CustomException(ResultCode.LOGIN_ERROR, $"你的账号已被锁,剩余{Math.Round(ts.TotalMinutes, 0)}分钟");
            }
        }

        public List<StatiLoginLogDto> GetStatiLoginlog()
        {
            var db = ResolveTenantDb();
            var time = DateTime.Now;

            //如果是查询当月那么 time就是 DateTime.Now
            var days = (time.AddMonths(1) - time).Days;//获取当月天数
            var dayArray = Enumerable.Range(1, days).Select(it => Convert.ToDateTime(time.ToString("yyyy-MM-" + it))).ToList();//转成时间数组

            var queryableLeft = db.Reportable(dayArray)
                .ToQueryable<DateTime>();

            var queryableRight = db.Queryable<SysLogininfor>().ApplyScope();
            var list = db.Queryable(queryableLeft, queryableRight, JoinType.Left, (x1, x2)
                 => x2.LoginTime.ToString("yyyy-MM-dd") == x1.ColumnName.ToString("yyyy-MM-dd"))
                .GroupBy((x1, x2) => x1.ColumnName)
                .Where((x1, x2) => x1.ColumnName >= DateTime.Now.AddDays(-7) && x1.ColumnName <= DateTime.Now)
                .Select((x1, x2) => new StatiLoginLogDto()
                {
                    DeRepeatNum = SqlFunc.AggregateDistinctCount(x2.Ipaddr),
                    Num = SqlFunc.AggregateCount(x2.InfoId),
                    Date = x1.ColumnName,
                })
                .Mapper(it =>
                {
                    it.WeekName = Tools.GetWeekByDate(it.Date);//相当于ToList循环赋值
                }).ToList();
            return list;
        }

        /// <summary>
        /// 聚合登录日志安全指标供 AI 解读。走租户库 + 数据范围过滤，与登录日志列表同一口径；
        /// 指标由固定 SQL 聚合，AI 只解读结果，不接触原始日志也不生成任何 SQL。
        /// </summary>
        public LoginSecurityMetricsDto GetLoginSecurityMetrics(LogAiAnalysisInput input)
        {
            var (begin, end) = input.ResolveRange();
            var metrics = new LoginSecurityMetricsDto
            {
                TimeRange = $"{begin:yyyy-MM-dd} ~ {end:yyyy-MM-dd}"
            };

            var db = ResolveTenantDb();

            // 成功/失败总量（sys_logininfor.status：0成功 1失败）
            foreach (var row in BuildRangeQuery(db, begin, end).GroupBy(it => it.Status)
                .Select(it => new { Status = it.Status, Num = SqlFunc.AggregateCount(it.InfoId) })
                .ToList())
            {
                if (row.Status == "0")
                {
                    metrics.SuccessCount = row.Num;
                }
                else if (row.Status == "1")
                {
                    metrics.FailCount = row.Num;
                }
            }
            metrics.TotalCount = metrics.SuccessCount + metrics.FailCount;

            // 每日成功/失败趋势
            metrics.Daily = BuildRangeQuery(db, begin, end).GroupBy(it => it.LoginTime.ToString("yyyy-MM-dd"))
                .Select(it => new
                {
                    Date = it.LoginTime.ToString("yyyy-MM-dd"),
                    Success = SqlFunc.AggregateSum(SqlFunc.IIF(it.Status == "0", 1, 0)),
                    Fail = SqlFunc.AggregateSum(SqlFunc.IIF(it.Status == "1", 1, 0))
                })
                .ToList()
                .OrderBy(x => x.Date)
                .Select(x => new LoginDailyStat { Date = x.Date, Success = x.Success, Fail = x.Fail })
                .ToList();

            // 凌晨 0-6 点活跃（异常时段信号）
            metrics.NightCount = BuildRangeQuery(db, begin, end).Where(it => it.LoginTime.Hour < 6).Count();
            metrics.NightFailCount = BuildRangeQuery(db, begin, end).Where(it => it.LoginTime.Hour < 6 && it.Status == "1").Count();

            // 失败账号 Top：按账号+地点分组后在内存归并，才能拿到地点去重数（异地/共享账号信号）
            var failRows = BuildRangeQuery(db, begin, end).Where(it => it.Status == "1")
                .GroupBy(it => new { it.UserName, it.LoginLocation })
                .Select(it => new
                {
                    it.UserName,
                    it.LoginLocation,
                    Num = SqlFunc.AggregateCount(it.InfoId),
                    Last = SqlFunc.AggregateMax(it.LoginTime)
                })
                .ToList();
            metrics.FailedAccounts = failRows
                .GroupBy(x => x.UserName)
                .Select(g => new LoginFailAccountStat
                {
                    UserName = g.Key,
                    FailCount = g.Sum(x => x.Num),
                    LocationCount = g.Select(x => x.LoginLocation).Where(x => !string.IsNullOrEmpty(x)).Distinct().Count(),
                    Locations = string.Join("、", g.Select(x => x.LoginLocation).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(5)),
                    LastFailTime = g.Max(x => x.Last)
                })
                .OrderByDescending(x => x.FailCount)
                .Take(10)
                .ToList();

            // 失败 IP Top
            metrics.FailedIps = BuildRangeQuery(db, begin, end).Where(it => it.Status == "1")
                .GroupBy(it => new { it.Ipaddr, it.LoginLocation })
                .Select(it => new
                {
                    it.Ipaddr,
                    it.LoginLocation,
                    Num = SqlFunc.AggregateCount(it.InfoId),
                    Last = SqlFunc.AggregateMax(it.LoginTime)
                })
                .ToList()
                .GroupBy(x => x.Ipaddr)
                .Select(g => new LoginFailIpStat
                {
                    Ipaddr = g.Key,
                    FailCount = g.Sum(x => x.Num),
                    Location = g.Select(x => x.LoginLocation).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? string.Empty,
                    LastFailTime = g.Max(x => x.Last)
                })
                .OrderByDescending(x => x.FailCount)
                .Take(10)
                .ToList();

            // 客户端环境分布 Top5
            metrics.Browsers = BuildRangeQuery(db, begin, end).GroupBy(it => it.Browser)
                .Select(it => new { Name = it.Browser, Num = SqlFunc.AggregateCount(it.InfoId) })
                .ToList()
                .OrderByDescending(x => x.Num).Take(5)
                .Select(x => new NameCountStat { Name = x.Name, Count = x.Num })
                .ToList();
            metrics.Oses = BuildRangeQuery(db, begin, end).GroupBy(it => it.Os)
                .Select(it => new { Name = it.Os, Num = SqlFunc.AggregateCount(it.InfoId) })
                .ToList()
                .OrderByDescending(x => x.Num).Take(5)
                .Select(x => new NameCountStat { Name = x.Name, Count = x.Num })
                .ToList();

            // IP 面貌：区间去重 IP 与近 30 天未出现的"新 IP"
            var rangeIps = BuildRangeQuery(db, begin, end)
                .Where(it => it.Ipaddr != null && it.Ipaddr != "")
                .Select(it => it.Ipaddr).Distinct().ToList();
            metrics.DistinctIpCount = rangeIps.Count;
            var priorIps = db.Queryable<SysLogininfor>().ApplyScope()
                .Where(it => it.LoginTime >= begin.AddDays(-30) && it.LoginTime < begin && it.Ipaddr != null && it.Ipaddr != "")
                .Select(it => it.Ipaddr).Distinct().ToList()
                .ToHashSet(StringComparer.Ordinal);
            metrics.NewIpCount = rangeIps.Count(x => !priorIps.Contains(x));

            // 与登录日志列表同口径：无"真实 IP 查看"敏感权限时 IP 脱敏后再喂模型/展示，
            // 避免无权限用户绕过列表页拿到明文 IP
            if (!HttpContextExtension.HasSensitivePerm(App.HttpContext, SensitivePerms.ViewRealIP))
            {
                foreach (var ip in metrics.FailedIps)
                {
                    ip.Ipaddr = MaskUtil.MaskIp(ip.Ipaddr);
                }
                metrics.IpMasked = true;
            }

            return metrics;
        }

        /// <summary>
        /// 构建区间内登录日志查询（租户库 + 数据范围过滤）。每次调用返回全新 queryable，
        /// 避免 SqlSugar 同一实例在多次聚合之间复用时共享查询状态产生串扰。
        /// </summary>
        private ISugarQueryable<SysLogininfor> BuildRangeQuery(ISqlSugarClient db, DateTime begin, DateTime end)
        {
            return db.Queryable<SysLogininfor>().ApplyScope()
                .Where(it => it.LoginTime >= begin && it.LoginTime <= end);
        }

        public string GetAbnormalLoginNotice(SysUser user, string currentLoginIp)
        {
            if (user == null || user.UserId <= 0)
            {
                return string.Empty;
            }

            var currentLocation = NormalizeLoginLocation(GetLocationByIp(currentLoginIp));
            var previousLocation = NormalizeLoginLocation(GetLocationByIp(user.LoginIP));

            if (previousLocation.IsEmpty() || currentLocation.IsEmpty())
            {
                return string.Empty;
            }

            if (string.Equals(previousLocation, currentLocation, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var content = $"⚠️ 账号异地登录提醒\n\n检测到您的账号发生异地登录，请确认是否为本人操作：\n\n账号：{user.UserName}\n本次地点：{currentLocation}\n上次地点：{previousLocation}\n登录 IP：{currentLoginIp}\n\n如非本人操作，请立即修改密码！";
            sysUserMsgService.AddSysUserMsg(user.UserId, content, UserMsgType.SYSTEM);
            return content;
        }

        private static string GetLocationByIp(string ip)
        {
            if (ip.IsEmpty())
            {
                return string.Empty;
            }

            var ipInfo = IpTool.Search(ip);
            return $"{ipInfo?.Province}-{ipInfo?.City}-{ipInfo?.NetworkOperator}";
        }

        private static string NormalizeLoginLocation(string location)
        {
            if (location.IsEmpty())
            {
                return string.Empty;
            }

            var segments = location
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(segment => !segment.Equals("0", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();

            return segments.Length == 0 ? string.Empty : string.Join("-", segments);
        }

    }
}
