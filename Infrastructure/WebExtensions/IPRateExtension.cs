using AspNetCoreRateLimit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace ZR.Infrastructure.WebExtensions
{
    public static class IPRateExtension
    {
        /// <summary>
        /// 启用接口限流（客户端模式，计数按“登录用户优先、匿名按 IP”分桶），
        /// 配置节见 iprate.json 的 ClientRateLimiting。
        /// </summary>
        public static void AddUserRateLimiting(this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddMemoryCache();

            //从 iprate.json 中加载常规配置，ClientRateLimiting 与配置文件中节点对应
            services.Configure<ClientRateLimitOptions>(configuration.GetSection("ClientRateLimiting"));

            //从 iprate.json 中加载客户端专项规则（可选，按客户端标识配置）
            services.Configure<ClientRateLimitPolicies>(configuration.GetSection("ClientRateLimitPolicies"));
            //注入计数器和规则存储
            services.AddSingleton<IClientPolicyStore, MemoryCacheClientPolicyStore>();
            services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
            //配置（自定义解析器：登录用户优先，匿名按 IP）
            services.AddSingleton<IRateLimitConfiguration, UserRateLimitConfiguration>();
            services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
        }
    }
}
