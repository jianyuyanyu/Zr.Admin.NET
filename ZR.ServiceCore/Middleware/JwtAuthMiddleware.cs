using Infrastructure;
using Infrastructure.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using ZR.Common;

namespace ZR.ServiceCore.Middleware
{
    /// <summary>
    /// jwt认证中间件
    /// </summary>
    public class JwtAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<JwtAuthMiddleware> _logger;
        private readonly OptionsSetting _options;
        private static readonly string[] _whitelistPaths = Array.Empty<string>();
        
        // Token 刷新阈值（分钟）
        private int TOKEN_REFRESH_THRESHOLD_MINUTES = 5;

        public JwtAuthMiddleware(RequestDelegate next, ILogger<JwtAuthMiddleware> logger, IOptions<OptionsSetting> options)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
            TOKEN_REFRESH_THRESHOLD_MINUTES = _options.JwtSettings.RefreshTokenTime;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            
            // 如果请求是带扩展名的（即静态资源）
            if (path.Contains('.'))
            {
                await _next(context);
                return;
            }
            //_logger.LogInformation($"处理请求: {path}");
            // 白名单路径检查
            if (_whitelistPaths.Any(p => !string.IsNullOrEmpty(p) && path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                await _next(context);
                return;
            }

            // 允许匿名访问的端点
            var endpoint = context.GetEndpoint();
            var allowAnonymous = endpoint?.Metadata?.GetMetadata<AllowAnonymousAttribute>() != null;

            if (allowAnonymous)
            {
                await _next(context);
                return;
            }

            TokenModel loginUser = JwtUtil.GetLoginUser(context);

            if (loginUser != null)
            {
                try
                {
                    // 尝试刷新 Token
                    await TryRefreshTokenAsync(context, loginUser);

                    // 挂载到 context.User
                    var identity = new ClaimsIdentity(JwtUtil.AddClaims(loginUser), JwtBearerDefaults.AuthenticationScheme);
                    context.User = new ClaimsPrincipal(identity);

                    await _next(context);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"处理用户 {loginUser.UserName} 的请求时发生异常");
                    await context.Response.WriteAsJsonAsync(ApiResult.Error(ResultCode.GLOBAL_ERROR, "请求处理失败"));
                }
            }
            else
            {
                string ip = HttpContextExtension.GetClientUserIp(context);
                string msg = $"请求访问[{path}]失败，Token无效或未登录，IP:{ip}";
                _logger.LogWarning(msg);
                
                await context.Response.WriteAsJsonAsync(ApiResult.Error(ResultCode.DENY, "Token无效或未登录"));
            }
        }

        /// <summary>
        /// 尝试刷新 Token
        /// </summary>
        /// <param name="context"></param>
        /// <param name="loginUser"></param>
        /// <returns></returns>
        private async Task TryRefreshTokenAsync(HttpContext context, TokenModel loginUser)
        {
            // 使用 UTC 对齐，确保与 JwtSecurityToken.ValidTo 对比一致
            var now = DateTime.UtcNow;
            var expireUtc = loginUser.ExpireTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(loginUser.ExpireTime, DateTimeKind.Utc)
                : loginUser.ExpireTime.ToUniversalTime();

            var ts = expireUtc - now;
            
            // Token 即将过期但还未过期时才刷新
            if (ts.TotalMinutes > 0 && ts.TotalMinutes < TOKEN_REFRESH_THRESHOLD_MINUTES)
            {
                var cacheKey = $"token_refresh_{loginUser.UserId}";
                
                // 使用缓存防止并发刷新
                if (!CacheHelper.Exists(cacheKey))
                {
                    try
                    {
                        // 设置缓存锁，防止并发刷新（锁定时间略长于阈值）
                        CacheHelper.SetCache(cacheKey, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), TOKEN_REFRESH_THRESHOLD_MINUTES + 1);
                        
                        var newToken = JwtUtil.GenerateJwtToken(JwtUtil.AddClaims(loginUser));
                        
                        // 设置响应头允许前端读取自定义 Header
                        string osType = context.Request.Headers["os"];
                        if (!string.IsNullOrEmpty(osType) || context.Request.Headers.ContainsKey("Origin"))
                        {
                            // 如果已有该 Header，避免重复追加 (防止多次刷新时重复)
                            if (!context.Response.Headers.ContainsKey("Access-Control-Expose-Headers"))
                            {
                                context.Response.Headers.Append("Access-Control-Expose-Headers", "X-Refresh-Token");
                            }
                        }
                        // 覆盖而不是多次追加
                        context.Response.Headers["X-Refresh-Token"] = newToken;
                        _logger.LogInformation($"刷新Token成功: UserId={loginUser.UserId}, UserName={loginUser.UserName}, 剩余时间={ts.TotalMinutes:F2}分钟");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"刷新Token失败: UserId={loginUser.UserId}, UserName={loginUser.UserName}");
                        // 刷新失败时移除缓存锁，允许下次重试
                        CacheHelper.Remove(cacheKey);
                    }
                }
            }
            await Task.CompletedTask;
        }
    }
}
