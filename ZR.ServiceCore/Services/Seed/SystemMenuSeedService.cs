using SqlSugar.IOC;
using ZR.Model.System;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 系统菜单种子：为 data.xlsx 之外的系统级菜单/按钮权限补种（与具体业务模块无关）。
    /// 与工作流/商城种子一致：SeedButton/SeedPage 记录定义 + 幂等写入 + 授权所有角色。
    /// 注意：本类只补 F 按钮与"AI 用量统计"页；父级系统菜单本体（多语言/定时任务/代码生成/日志等）
    /// 由 data.xlsx 维护，父级缺失时跳过、不自动建页，避免与 data.xlsx 冲突。
    /// </summary>
    internal sealed class SystemMenuSeedService
    {
        /// <summary>
        /// AI 能力按钮：挂在系统既有 C 页（本体由 data.xlsx 维护）下的 F 按钮。
        /// 元组含义：(父 C 页权限串, 按钮定义)。
        /// </summary>
        private static readonly (string ParentPerms, SeedButton Button)[] AiButtonDefs =
        {
            ("system:lang:list", new SeedButton("AI 翻译", "system:lang:ai", OrderNum: 99)),
            ("monitor:job:list", new SeedButton("AI 生成 Cron", "monitor:job:ai", OrderNum: 99)),
            ("tool:gen:list", new SeedButton("AI 推断列配置", "tool:gen:ai", OrderNum: 99)),
            ("monitor:logininfor:list", new SeedButton("AI 安全分析", "monitor:logininfor:ai", OrderNum: 99)),
            ("monitor:operlog:list", new SeedButton("AI 健康分析", "monitor:operlog:ai", OrderNum: 99)),
        };

        /// <summary>
        /// "AI 用量统计"C 页（挂在系统监控 M 目录下）+ 其下"AI 办公助手"F 按钮。
        /// 仅当页面缺失时自建；父级 M 目录（monitor）由 data.xlsx 维护，缺失则跳过。
        /// </summary>
        private static readonly SeedPage AiUsagePage = new(
            Name: "AI 用量统计",
            Path: "aiUsage",
            Component: "monitor/AiUsage",
            Perms: "",
            OrderNum: 99,
            Buttons:
            [
                new SeedButton("AI 办公助手", "system:ai:chat", OrderNum: 99),
            ],
            Icon: "chart");

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

            var inserted = 0;
            var buttonIds = new List<long>();

            foreach (var (parentPerms, btn) in AiButtonDefs)
            {
                var parent = db.Queryable<SysMenu>()
                    .First(x => x.MenuType == "C" && x.Perms == parentPerms);
                if (parent == null)
                {
                    continue; // 父 C 页（data.xlsx）尚未初始化
                }

                var (menuId, isNew) = EnsureButton(parent.MenuId, btn, now, fixParent: false);
                if (isNew) inserted++;
                buttonIds.Add(menuId);
            }

            if (buttonIds.Count == 0)
            {
                return "[系统 AI 权限] 无新增（所属菜单尚未初始化）";
            }

            var granted = GrantToAllRoles(buttonIds, now);
            return $"[系统 AI 权限] 新增{inserted}个按钮，授权{granted}条角色关系";
        }

        /// <summary>
        /// 确保"AI 用量统计"C 页 + "AI 办公助手"F 按钮存在并授予所有角色（幂等）。
        /// 用量页挂在"系统监控"(monitor) 目录下，component 对应前端 monitor/AiUsage.vue；
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

            var inserted = 0;
            var menuIds = new List<long>();

            // C 页：AI 用量统计
            var (pageId, pageNew) = EnsurePage(monitorMenu.MenuId, AiUsagePage, now);
            if (pageNew) inserted++;
            menuIds.Add(pageId);

            // 其下 F 按钮：AI 办公助手
            foreach (var btn in AiUsagePage.Buttons)
            {
                var (menuId, isNew) = EnsureButton(pageId, btn, now, fixParent: true);
                if (isNew) inserted++;
                menuIds.Add(menuId);
            }

            var granted = GrantToAllRoles(menuIds, now);
            return $"[AI 用量菜单] 菜单已就绪，新增授权{granted}条角色关系";
        }

        /// <summary>
        /// 确保 C 类型页面存在并挂到指定父菜单下（幂等）：缺失则插入，
        /// 已存在但父级不一致时校正父级。
        /// </summary>
        private static (long MenuId, bool Inserted) EnsurePage(long parentId, SeedPage p, DateTime now)
        {
            var db = DbScoped.SugarScope;
            var exist = db.Queryable<SysMenu>()
                .First(x => x.MenuType == "C" && x.Component == p.Component);
            if (exist != null)
            {
                if (exist.ParentId != parentId)
                {
                    exist.ParentId = parentId;
                    exist.Update_by = "system";
                    exist.Update_time = now;
                    db.Updateable(exist).UpdateColumns(x => new { x.ParentId, x.Update_by, x.Update_time }).ExecuteCommand();
                }
                return (exist.MenuId, false);
            }

            var menu = new SysMenu
            {
                MenuName = p.Name,
                ParentId = parentId,
                OrderNum = p.OrderNum,
                Path = p.Path,
                Component = p.Component,
                IsCache = "0",
                IsFrame = "0",
                MenuType = "C",
                Visible = p.Visible,
                Status = "0",
                Perms = p.Perms,
                Icon = p.Icon,
                MenuNameKey = "",
                Create_by = "system",
                Create_time = now
            };
            menu.MenuId = db.Insertable(menu).ExecuteReturnIdentity();
            return (menu.MenuId, true);
        }

        /// <summary>
        /// 确保 F 按钮存在（幂等）：缺失则插入到指定父菜单下；已存在且 fixParent=true 时校正父级。
        /// </summary>
        private static (long MenuId, bool Inserted) EnsureButton(long parentId, SeedButton btn, DateTime now, bool fixParent)
        {
            var db = DbScoped.SugarScope;
            var exist = db.Queryable<SysMenu>()
                .First(x => x.MenuType == "F" && x.Perms == btn.Perms);
            if (exist != null)
            {
                if (fixParent && exist.ParentId != parentId)
                {
                    exist.ParentId = parentId;
                    exist.Update_by = "system";
                    exist.Update_time = now;
                    db.Updateable(exist).UpdateColumns(x => new { x.ParentId, x.Update_by, x.Update_time }).ExecuteCommand();
                }
                return (exist.MenuId, false);
            }

            var menu = new SysMenu
            {
                MenuName = btn.Name,
                ParentId = parentId,
                OrderNum = btn.OrderNum,
                Path = string.Empty,
                Component = null,
                IsCache = "0",
                IsFrame = "0",
                MenuType = "F",
                Visible = "0",
                Status = "0",
                Perms = btn.Perms,
                Icon = "#",
                MenuNameKey = "",
                Create_by = "system",
                Create_time = now
            };
            menu.MenuId = db.Insertable(menu).ExecuteReturnIdentity();
            return (menu.MenuId, true);
        }

        /// <summary>
        /// 将给定菜单（C 页 + F 按钮）授权给所有角色，返回新增角色-菜单关系条数（幂等）。
        /// 原因：Controller 上声明 [ActionPermissionFilter] 的接口，普通角色必须配套拥有该权限，
        /// 否则访问即被拦截。超管（admin）天然放行，此处补齐其余角色。
        /// </summary>
        private static int GrantToAllRoles(List<long> menuIds, DateTime now)
        {
            if (menuIds.Count == 0)
            {
                return 0;
            }

            var db = DbScoped.SugarScope;
            var roleIds = db.Queryable<SysRole>().Select(r => r.RoleId).ToList();
            if (roleIds.Count == 0)
            {
                return 0;
            }

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
                        toInsert.Add(new SysRoleMenu { Role_id = roleId, Menu_id = menuId, Create_by = "system", Create_time = now });
                    }
                }
            }

            if (toInsert.Count > 0)
            {
                db.Insertable(toInsert).ExecuteCommand();
            }
            return toInsert.Count;
        }
    }
}
