using SqlSugar.IOC;
using ZR.Model.System;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 系统菜单种子（与具体业务模块无关，如监控下的"数据库同步"、系统模块的 AI 能力按钮权限）。
    /// </summary>
    internal sealed class SystemMenuSeedService
    {
        /// <summary>
        /// 确保系统模块 AI 能力按钮权限存在并授予所有角色（幂等）。
        /// 多语言、定时任务等系统菜单本体由 data.xlsx 种子维护，此处只补齐新增的 F 类型按钮权限；
        /// 若不补齐，Controller 上新增的 [ActionPermissionFilter] 会因角色无该权限而一律被拦截（超管除外）。
        /// 菜单未初始化时跳过，下次执行种子时自动补上。
        /// </summary>
        public string EnsureAiPermSeedData()
        {
            var db = DbScoped.SugarScope;
            var now = DateTime.Now;

            // (所属菜单权限, 按钮名称, 按钮权限)
            var defs = new[]
            {
                ("system:lang:list", "AI 翻译", "system:lang:ai"),
                ("monitor:job:list", "AI 生成 Cron", "monitor:job:ai"),
                ("tool:gen:list", "AI 推断列配置", "tool:gen:ai"),
                ("monitor:logininfor:list", "AI 安全分析", "monitor:logininfor:ai"),
                ("monitor:operlog:list", "AI 健康分析", "monitor:operlog:ai"),
            };

            var inserted = 0;
            var buttonMenuIds = new List<long>();

            foreach (var (parentPerms, btnName, btnPerms) in defs)
            {
                var parent = db.Queryable<SysMenu>()
                    .First(x => x.MenuType == "C" && x.Perms == parentPerms);
                if (parent == null)
                {
                    continue;
                }

                var button = db.Queryable<SysMenu>()
                    .First(x => x.MenuType == "F" && x.Perms == btnPerms);
                if (button == null)
                {
                    button = new SysMenu
                    {
                        MenuName = btnName,
                        ParentId = parent.MenuId,
                        OrderNum = 99,
                        Path = "",
                        Component = null,
                        IsCache = "0",
                        IsFrame = "0",
                        MenuType = "F",
                        Visible = "0",
                        Status = "0",
                        Perms = btnPerms,
                        Icon = "#",
                        MenuNameKey = "",
                        Create_by = "system",
                        Create_time = now
                    };
                    button.MenuId = db.Insertable(button).ExecuteReturnIdentity();
                    inserted++;
                }
                buttonMenuIds.Add(button.MenuId);
            }

            if (buttonMenuIds.Count == 0)
            {
                return "[系统 AI 权限] 无新增（所属菜单尚未初始化）";
            }

            var roleIds = db.Queryable<SysRole>().Select(r => r.RoleId).ToList();
            var existRoleMenus = db.Queryable<SysRoleMenu>()
                .Where(rm => buttonMenuIds.Contains(rm.Menu_id))
                .ToList();

            var toInsert = new List<SysRoleMenu>();
            foreach (var roleId in roleIds)
            {
                foreach (var menuId in buttonMenuIds)
                {
                    if (!existRoleMenus.Any(rm => rm.Role_id == roleId && rm.Menu_id == menuId))
                    {
                        toInsert.Add(new SysRoleMenu { Role_id = roleId, Menu_id = menuId });
                    }
                }
            }

            if (toInsert.Count > 0)
            {
                db.Insertable(toInsert).ExecuteCommand();
            }

            return $"[系统 AI 权限] 新增{inserted}个按钮，授权{toInsert.Count}条角色关系";
        }
    }
}
