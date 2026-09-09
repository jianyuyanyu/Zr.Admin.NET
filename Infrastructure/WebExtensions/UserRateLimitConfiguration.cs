using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZR.Infrastructure.WebExtensions
{
    /// <summary>
    /// 限流配置：将限流维度从 IP 切换为“登录用户优先、匿名按 IP”。
    /// 库默认（ClientHeaderResolveContributor）只从 X-ClientId 头解析且依赖配置才注册，
    /// 此处把用户解析器插入 ClientResolvers 首位，使其始终生效。
    /// </summary>
    public class UserRateLimitConfiguration : RateLimitConfiguration
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<UserClientResolveContributor> _logger;

        public UserRateLimitConfiguration(
            IOptions<IpRateLimitOptions> ipOptions,
            IOptions<ClientRateLimitOptions> clientOptions,
            IHttpContextAccessor httpContextAccessor,
            ILogger<UserClientResolveContributor> logger)
            : base(ipOptions, clientOptions)
        {
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public override void RegisterResolvers()
        {
            // 先注册库默认解析（X-Real-IP 等），保证 ClientIp 可被解析
            base.RegisterResolvers();

            // 用户解析器放到首位：总是返回非空分桶键（user:{id} / ip:{ip}），
            // 避免命中默认 X-ClientId 头或退化为全匿名共享一个桶
            ClientResolvers.Insert(0, new UserClientResolveContributor(_httpContextAccessor, _logger));
        }
    }
}
