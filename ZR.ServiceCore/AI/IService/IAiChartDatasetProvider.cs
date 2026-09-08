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

        /// <summary>
        /// 字段定义。数据集支持多维度时（<see cref="Dimensions"/> 非空），
        /// 应按当前维度返回对应字段，类目字段须与该维度的 <see cref="AiChartDimension.Key"/> 一致。
        /// </summary>
        IReadOnlyList<AiChartFieldDef> Fields { get; }

        Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain);

        /// <summary>
        /// 该数据集支持的统计维度（同一份数据的不同切片，如按模块/按操作类型/按人）。
        /// 返回空表示该数据集不支持维度切换，模型不应传 dimension。
        /// </summary>
        IReadOnlyList<AiChartDimension> Dimensions => null;

        /// <summary>
        /// 指定维度的查询。默认实现忽略 dimension 转发到 <see cref="QueryAsync(long, DateTime, DateTime, string)"/>，
        /// 因此不支持维度的既有数据集无需改动；支持维度的数据集应重写本方法。
        /// </summary>
        /// <param name="userId">当前登录用户 Id（工具层已校验权限）</param>
        /// <param name="begin">区间开始（含）</param>
        /// <param name="end">区间结束（含）</param>
        /// <param name="grain">时间粒度 day/week/month</param>
        /// <param name="dimension">维度 Key，取值须落在 <see cref="Dimensions"/> 内；非法值由实现自行降级到默认维度</param>
        Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain, string dimension)
            => QueryAsync(userId, begin, end, grain);
    }
}
