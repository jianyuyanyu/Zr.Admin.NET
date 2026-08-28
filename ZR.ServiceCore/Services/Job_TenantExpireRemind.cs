using Infrastructure;
using Infrastructure.Attribute;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 租户到期前阶梯提醒任务。
    /// 由统一分发器 Job_Dispatcher 通过反射调用 Run()（TenantId 设为主库，仅执行一次）。
    /// 提醒节奏与幂等去重见 SysTenantService.RemindExpiringTenants（30/15/7/3/1 天各一次）。
    /// </summary>
    [AppService(ServiceType = typeof(Job_TenantExpireRemind), ServiceLifetime = LifeTime.Scoped)]
    public class Job_TenantExpireRemind
    {
        private readonly ISysTenantService _tenantService;

        public Job_TenantExpireRemind(ISysTenantService tenantService)
        {
            _tenantService = tenantService;
        }

        public void Run()
        {
            var count = _tenantService.RemindExpiringTenants("system");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[定时任务] 租户到期提醒：本次发送 {count} 个租户");
            Console.ResetColor();
        }
    }
}
