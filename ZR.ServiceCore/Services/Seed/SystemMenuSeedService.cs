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

        /// <summary>
        /// 确保"AI 用量统计"菜单 + "AI 办公助手"入口权限存在并授权所有角色（幂等）。
        /// 用量页为 C 类型、挂在"系统监控"(monitor) 目录下，component 对应前端 monitor/AiUsage.vue；
        /// "AI 办公助手"为挂在用量页下的 F 按钮（Perms=system:ai:chat），是前端顶栏助手入口
        /// v-hasPermi="['system:ai:chat']" 的权限来源，供角色管理按按钮粒度回收。
        /// 仅新增 data.xlsx 未覆盖的菜单，父级目录未初始化时跳过，下次执行种子时自动补上。
        /// </summary>
        public string EnsureAiUsageMenuSeedData()
        {
            var db = DbScoped.SugarScope;
            var now = DateTime.Now;

            // 父级目录：系统监控（data.xlsx 一级 M 目录，Path=monitor）
            var monitorMenu = db.Queryable<SysMenu>()
                .First(x => x.MenuType == "M" && x.ParentId == 0 && x.Path == "monitor");
            if (monitorMenu == null)
            {
                return "[AI 用量菜单] 系统监控目录尚未初始化，跳过";
            }

            // 幂等：已存在则只补授权
            var menu = db.Queryable<SysMenu>()
                .First(x => x.MenuType == "C" && x.Component == "monitor/AiUsage");
            if (menu == null)
            {
                menu = new SysMenu
                {
                    MenuName = "AI 用量统计",
                    ParentId = monitorMenu.MenuId,
                    OrderNum = 99,
                    Path = "aiUsage",
                    Component = "monitor/AiUsage",
                    IsCache = "0",
                    IsFrame = "0",
                    MenuType = "C",
                    Visible = "0",
                    Status = "0",
                    Perms = "",
                    Icon = "chart",
                    MenuNameKey = "",
                    Create_by = "system",
                    Create_time = now
                };
                menu.MenuId = db.Insertable(menu).ExecuteReturnIdentity();
            }
            else if (menu.ParentId != monitorMenu.MenuId)
            {
                menu.ParentId = monitorMenu.MenuId;
                menu.Update_by = "system";
                menu.Update_time = now;
                db.Updateable(menu).UpdateColumns(x => new { x.ParentId, x.Update_by, x.Update_time }).ExecuteCommand();
            }

            // AI 办公助手入口按钮（F）：顶栏助手 v-hasPermi 的权限来源，挂在用量页下，角色管理可单独回收
            var chatBtn = db.Queryable<SysMenu>()
                .First(x => x.MenuType == "F" && x.Perms == "system:ai:chat");
            if (chatBtn == null)
            {
                chatBtn = new SysMenu
                {
                    MenuName = "AI 办公助手",
                    ParentId = menu.MenuId,
                    OrderNum = 99,
                    Path = "",
                    Component = null,
                    IsCache = "0",
                    IsFrame = "0",
                    MenuType = "F",
                    Visible = "0",
                    Status = "0",
                    Perms = "system:ai:chat",
                    Icon = "#",
                    MenuNameKey = "",
                    Create_by = "system",
                    Create_time = now
                };
                chatBtn.MenuId = db.Insertable(chatBtn).ExecuteReturnIdentity();
            }
            else if (chatBtn.ParentId != menu.MenuId)
            {
                chatBtn.ParentId = menu.MenuId;
                chatBtn.Update_by = "system";
                chatBtn.Update_time = now;
                db.Updateable(chatBtn).UpdateColumns(x => new { x.ParentId, x.Update_by, x.Update_time }).ExecuteCommand();
            }

            // 授权所有角色（C 页 + F 按钮），保证普通用户默认可用（后端已按用户隔离数据）
            var menuIds = new List<long> { menu.MenuId, chatBtn.MenuId };
            var roleIds = db.Queryable<SysRole>().Select(r => r.RoleId).ToList();
            var grantedCount = 0;
            if (roleIds.Count > 0)
            {
                var existRoleMenus = db.Queryable<SysRoleMenu>()
                    .Where(rm => menuIds.Contains(rm.Menu_id))
                    .ToList();

                var toInsert = new List<SysRoleMenu>();
                foreach (var roleId in roleIds)
                {
                    foreach (var menuId in menuIds)
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
                    grantedCount = toInsert.Count;
                }
            }

            return $"[AI 用量菜单] 菜单已就绪，新增授权{grantedCount}条角色关系";
        }
    }
}
