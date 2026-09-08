using ZR.Model;
using ZR.Model.System;
using ZR.Model.System.Dto;
using ZR.ServiceCore.Model.Dto;

namespace ZR.ServiceCore.Services
{
    public interface ISysLoginService : IBaseService<SysLogininfor>
    {
        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="loginBody"></param>
        /// <param name="logininfor"></param>
        /// <returns></returns>
        public SysUser Login(LoginBodyDto loginBody, SysLogininfor logininfor);

        /// <summary>
        /// 生成异地登录提醒
        /// </summary>
        /// <param name="user"></param>
        /// <param name="currentLoginIp"></param>
        /// <returns></returns>
        string GetAbnormalLoginNotice(SysUser user, string currentLoginIp);

        /// <summary>
        /// 手机号登录
        /// </summary>
        /// <param name="loginBody"></param>
        /// <param name="logininfor"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        SysUserDto PhoneLogin(PhoneLoginDto loginBody, SysLogininfor logininfor, SysUserDto user);
        /// <summary>
        /// 查询操作日志
        /// </summary>
        PagedInfo<SysLogininfor> GetLoginLog(SysLogininfoQueryDto logininfoDto);
        /// <summary>
        /// 记录登录日志
        /// </summary>
        /// <param name="sysLogininfor"></param>
        /// <param name="tenantId">租户ID，多租户模式下写入对应租户库</param>
        /// <returns></returns>
        public void AddLoginInfo(SysLogininfor sysLogininfor, string tenantId = null);

        /// <summary>
        /// 清空登录日志
        /// </summary>
        public void TruncateLogininfo();

        /// <summary>
        /// 删除登录日志
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public int DeleteLogininforByIds(long[] ids);

        void CheckLockUser(string userName);
        /// <summary>
        /// 查询登录日志统计
        /// </summary>
        /// <returns></returns>
        List<StatiLoginLogDto> GetStatiLoginlog();

        /// <summary>
        /// 聚合登录日志安全指标（供 AI 安全分析解读，不返回原始日志）
        /// </summary>
        /// <param name="input">时间范围参数</param>
        /// <returns>聚合指标</returns>
        LoginSecurityMetricsDto GetLoginSecurityMetrics(LogAiAnalysisInput input);

        /// <summary>
        /// 按省份聚合登录日志的地域分布（供 AI 图表问答使用，不返回原始日志）。
        /// 地域取自登录时 IP 解析写入的 LoginLocation，按"省"归一化；
        /// 数据权限与登录日志列表一致（非管理员仅统计本人）。
        /// </summary>
        /// <param name="input">时间范围参数</param>
        /// <param name="topN">返回的省份条数上限，其余省份合并为"其他"</param>
        /// <returns>按登录次数降序的省份统计</returns>
        List<LoginRegionStat> GetLoginRegionStats(LogAiAnalysisInput input, int topN = 12);
    }
}
