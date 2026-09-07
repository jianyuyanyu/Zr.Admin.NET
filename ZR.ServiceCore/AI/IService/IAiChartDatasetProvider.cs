using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.IService
{
    /// <summary>
    /// 图表数据集插件。新业务指标实现本接口即可接入助手，不必改对话编排与前端 ECharts。
    /// 查询必须走本模块既有 Service 的固定聚合，禁止拼接模型传入的 SQL/表名。
    /// </summary>
    public interface IAiChartDatasetProvider
    {
        string DatasetId { get; }
        string Title { get; }
        string Description { get; }
        /// <summary>权限码；空则仅需登录。管理员 *:*:* 隐含放行。</summary>
        string Permission { get; }
        /// <summary>day / week / month</summary>
        string[] Grains { get; }
        int MaxDays { get; }
        int MaxMonths { get; }
        string[] AllowedTypes { get; }
        IReadOnlyList<AiChartFieldDef> Fields { get; }

        Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain);
    }
}
