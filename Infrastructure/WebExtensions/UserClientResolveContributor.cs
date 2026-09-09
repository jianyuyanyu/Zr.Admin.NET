using AspNetCoreRateLimit;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace ZR.Infrastructure.WebExtensions
{
    /// <summary>
    /// 限流客户端标识解析器：认证请求按登录用户分桶（取自已通过签名校验的 JWT claims，
    /// 不信任前端可伪造的 userid 头），匿名请求（登录、验证码、白名单等）回退按客户端 IP
    /// 分桶，避免所有匿名流量共用一个限流桶。
    /// 分桶规则：已登录 user:{userId}；匿名 ip:{ip}。
    /// </summary>
    public class UserClientResolveContributor : IClientResolveContributor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<UserClientResolveContributor> _logger;

        public UserClientResolveContributor(IHttpContextAccessor httpContextAccessor, ILogger<UserClientResolveContributor> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public Task<string> ResolveClientAsync(HttpContext httpContext)
        {
            var context = httpContext ?? _httpContextAccessor?.HttpContext;

            // 已认证请求：context.User 由 JwtAuthMiddleware 基于签名校验后的 token 挂载
            var userId = context?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (long.TryParse(userId, out var uid) && uid > 0)
            {
                return Task.FromResult($"user:{uid}");
            }

            // 匿名请求：按客户端 IP 分桶，与原 IP 限流等价，保留登录/验证码等公开端点的防刷能力
            try
            {
                var ip = context?.GetClientUserIp();
                if (!string.IsNullOrEmpty(ip))
                {
                    return Task.FromResult($"ip:{ip}");
                }
            }
            catch (Exception ex)
            {
                // 解析失败属可恢复场景：退回统一匿名桶，仅记录不阻断请求
                _logger.LogWarning(ex, "限流解析客户端 IP 失败，按匿名桶兜底");
            }
            return Task.FromResult("ip:unknown");
        }
    }
}
