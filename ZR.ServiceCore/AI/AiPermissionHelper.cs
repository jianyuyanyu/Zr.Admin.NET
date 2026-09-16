using Infrastructure;
using NLog;
using ZR.Model.System.Dto;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI
{
    /// <summary>
    /// AI 模块鉴权口径 helper：统一"读取菜单权限码 / 权限码命中判断 / 平台管理员判定"，
    /// 避免对话编排与各工具 Provider 各写一份导致判断与错误文案漂移。
    /// 分工约定：
    /// ① 菜单/接口级权限码校验归 Controller 的 ActionPermissionFilter（单一闸门）；
    /// ② 工具 Provider 内部的权限校验归本 helper——工具名与参数由模型决定，Controller 无法按工具粒度授权。
    /// </summary>
    public static class AiPermissionHelper
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 读取用户菜单权限码（含管理员隐含 *:*:* 与租户套餐交集过滤，口径与 ActionPermissionFilter 一致）。
        /// 失败返回 null，语义严格限定为"权限计算失败"；调用方必须与"无权限"（返回非 null 但不含所需码）
        /// 分开表达——前者提示稍后重试，后者提示联系管理员授权，两者给用户的行动指引完全不同。
        /// </summary>
        public static List<string> TryLoadPerms(ISysPermissionService permissionService, long userId)
        {
            try
            {
                return permissionService.GetMenuPermission(new SysUserDto { UserId = userId });
            }
            catch (Exception ex)
            {
                // 异常堆栈已含调用方，日志无需再传组件名
                _logger.Error(ex, "计算用户权限失败 userId={UserId}", userId);
                return null;
            }
        }

        /// <summary>
        /// 是否拥有管理员通配权限（*:*:*）。用于"看全量还是仅本人"这类数据范围判定。
        /// </summary>
        public static bool IsAdminPerms(List<string> perms)
            => perms != null && perms.Contains(GlobalConstant.AdminPerm);

        /// <summary>
        /// 权限码命中判断：管理员 *:*:* 隐含放行，否则精确匹配。
        /// perms 为 null（权限计算失败）时一律返回 false，由调用方按"无法校验"单独提示。
        /// </summary>
        public static bool HasPerm(List<string> perms, string permission)
        {
            if (perms == null) return false;
            if (IsAdminPerms(perms)) return true;
            return !string.IsNullOrWhiteSpace(permission) && perms.Contains(permission);
        }

        /// <summary>
        /// 是否平台管理员（主租户的管理员）。仅平台管理员可维护全局策略与模型价格、可跨租户查看用量。
        /// </summary>
        public static bool IsPlatformAdmin()
        {
            var user = App.HttpContext?.GetCurrentUser();
            return user?.IsAdmin() == true
                && string.Equals(App.GetCurrentTenantId(), App.MainDbConfigId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
