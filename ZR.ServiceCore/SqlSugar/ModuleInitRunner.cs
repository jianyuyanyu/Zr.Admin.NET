using Infrastructure;
using Infrastructure.Model;
using Microsoft.Extensions.DependencyInjection;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.SqlSugar
{
    /// <summary>
    /// 业务模块初始化运行器。
    /// 统一管理各业务模块（商城、工作流等）的"建表 + 可选种子"初始化，
    /// 消除 InitTable 中每新增一个模块就复制一遍查找 / 调用 / 错误包装代码的问题。
    /// 新增独立模块只需在 Modules 字典注册一项（含 DisplayName、可选 Seed、可选开关），
    /// 全量初始化与按开关的独立初始化均从此处驱动，调用方不再逐模块写重复分支。
    /// </summary>
    public static class ModuleInitRunner
    {
        /// <summary>
        /// 模块初始化规格：建表由同名 ITenantModuleInitializer 完成；
        /// Seed 为建表后可选执行的"非菜单"种子逻辑（为 null 表示无需）；
        /// IsEnabled 返回该模块对应的 appsettings 开关是否开启（为 null 表示无独立开关，随全量初始化）。
        /// 菜单种子（InitXxxMenuSeedData）不在此处，统一由 MenuSeeds + SeedMenu 按开关写入，确保"启用模块才写菜单"。
        /// </summary>
        private sealed class ModuleSpec
        {
            public string DisplayName { get; init; }
            public Action Seed { get; init; }
            public Func<OptionsSetting, bool> IsEnabled { get; init; }
        }

        /// <summary>
        /// 独立模块注册表（唯一开关声明处，开关来自 appsettings 的 ModuleInit 分组）。
        /// Key 必须与对应 ITenantModuleInitializer.ModuleName 一致
        /// （Pro/Saas 无同名 initializer，RunTables 查不到时安全跳过）。
        /// 全量初始化会自动跳过此处注册的模块，改由 ModuleInit.Mall/Workflow/Pro/Saas 驱动；
        /// Seed 用于写入"随模块开关"的模块级数据（如模块内置定时任务），未启用则不落地。
        /// <para>
        /// <b>枚举顺序即执行顺序</b>：RunEnabledModules 按声明顺序逐项执行「建表 → Seed → 菜单种子」。
        /// 因此写菜单的模块必须排在"消费菜单"的模块之前——Saas 的 EnsureTenantPlanMenuSeedData 会把
        /// 当时的非平台菜单写进默认套餐，故 Pro（AI 菜单）必须排在 Saas 之前，否则首次 --initdb
        /// 时 AI 菜单尚未存在、进不了套餐（只能靠再次执行才增量补上）。调整顺序前请先确认此约束。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, ModuleSpec> Modules = new()
        {
            ["Mall"] = new ModuleSpec
            {
                DisplayName = "商城",
                IsEnabled = o => o.ModuleInit.Mall,
                // 商城内置定时任务随模块开关写入；未启用则不落地，避免调度器反复执行"无表可查"的僵尸任务
                Seed = () => Log.WriteLine(ConsoleColor.White, new MallSeedService().EnsureTasksSeedData())
            },
            ["Workflow"] = new ModuleSpec
            {
                DisplayName = "工作流",
                IsEnabled = o => o.ModuleInit.Workflow,
                Seed = () => Log.WriteLine(ConsoleColor.White, new SystemTaskSeedService().EnsureWorkflowTimeoutTaskSeedData())
            },
            // Pro 必须先于 Saas：AI 菜单需先写入，Saas 的默认套餐同步才能把租户可见的 AI 菜单纳入套餐
            ["Pro"] = new ModuleSpec
            {
                DisplayName = "专业版(AI)",
                IsEnabled = o => o.ModuleInit.Pro
            },
            ["Saas"] = new ModuleSpec
            {
                DisplayName = "SaaS",
                IsEnabled = o => o.ModuleInit.Saas
            }
        };

        /// <summary>
        /// 各模块的"菜单种子"工厂：仅在对应模块开关开启时才调用，写入菜单与按钮权限。
        /// 与建表解耦，避免模块未启用时误写菜单数据。顺序与 <see cref="Modules"/> 保持一致以便对照。
        /// </summary>
        private static readonly Dictionary<string, Func<List<string>>> MenuSeeds = new()
        {
            ["Mall"] = () => new SeedDataService().InitMallMenuSeedData(),
            ["Workflow"] = () => new SeedDataService().InitWorkflowMenuSeedData(),
            ["Pro"] = () => new SeedDataService().InitProMenuSeedData(),
            ["Saas"] = () => new SeedDataService().InitSaasMenuSeedData()
        };

        /// <summary>判断某模块名是否为已注册独立模块（供全量初始化跳过用）。</summary>
        public static bool Contains(string moduleName) => Modules.ContainsKey(moduleName);

        /// <summary>
        /// 按 OptionsSetting 中各模块开关，批量运行所有开启的独立模块初始化（建表 + 菜单种子）。
        /// 取代 InitTable.RunInitDb 中逐模块写 if (options.InitXxx) Run("Xxx") 的重复分支；
        /// 新增带开关的模块只需在 Modules 字典注册并填 IsEnabled，此处自动覆盖。
        /// 菜单种子严格在 IsEnabled 确认后写入，未启用模块绝不写菜单数据。
        /// </summary>
        public static void RunEnabledModules(OptionsSetting options)
        {
            foreach (var (name, spec) in Modules)
            {
                Log.WriteLine(ConsoleColor.Cyan, $"==== 检查 {spec.DisplayName} 模块是否启用{spec.IsEnabled?.Invoke(options)} ====");
                if (spec.IsEnabled?.Invoke(options) == true)
                {
                    Run(name);
                    SeedMenu(name);
                }
            }
        }

        /// <summary>
        /// 仅写入指定模块的菜单种子（InitXxxMenuSeedData）。供 RunEnabledModules 在确认模块启用后调用；
        /// 未注册的模块静默跳过。菜单种子与建表解耦，确保"启用模块才写菜单"。
        /// </summary>
        public static void SeedMenu(string moduleName)
        {
            if (MenuSeeds.TryGetValue(moduleName, out var seed))
            {
                foreach (var line in seed())
                    Log.WriteLine(ConsoleColor.White, line);
            }
        }

        /// <summary>
        /// 运行指定独立模块的建表初始化（ITenantModuleInitializer.InitializeNonSaaS）+ 可选非菜单种子，
        /// 并打印完成日志。任一环节失败会打印并终止进程（与原 ModuleInit.Mall/Workflow 行为一致）。
        /// 菜单种子不在此处，统一由 SeedMenu 按开关写入。
        /// </summary>
        public static void Run(string moduleName)
        {
            if (!Modules.TryGetValue(moduleName, out var spec))
            {
                Log.WriteLine(ConsoleColor.Yellow, $"[ModuleInit] 未注册的模块: {moduleName}，跳过");
                return;
            }

            SafeRun(() =>
            {
                RunTables(moduleName);
                spec.Seed?.Invoke();

                Log.WriteLine(ConsoleColor.Green, $"==== {spec.DisplayName}模块初始化完成 ====");
            }, spec.DisplayName);
        }

        /// <summary>
        /// 仅执行建表（通过 ITenantModuleInitializer.InitializeNonSaaS）。用于全量初始化中
        /// 逐个调度非独立模块的场景，避免调用方重复查找 initializer。
        /// </summary>
        public static void RunTables(string moduleName)
        {
            if (InternalApp.ServiceProvider == null) return;
            using var scope = InternalApp.ServiceProvider.CreateScope();
            ITenantModuleInitializer target = null;
            foreach (var mi in scope.ServiceProvider.GetServices<ITenantModuleInitializer>())
            {
                if (mi.ModuleName == moduleName)
                {
                    target = mi;
                    break;
                }
            }
            target?.InitializeNonSaaS();
        }

        private static void SafeRun(Action action, string displayName)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.WriteLine(ConsoleColor.Red, $"{displayName}模块初始化失败：{ex.Message}");
                Log.WriteLine(ConsoleColor.Red, ex.StackTrace);
                Environment.Exit(1);
            }
        }
    }
}
