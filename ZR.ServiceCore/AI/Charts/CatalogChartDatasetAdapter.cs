using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>把目录项适配成 IAiChartDatasetProvider，供工具层与代码插件同一套查找/鉴权/截断。</summary>
    internal sealed class CatalogChartDatasetAdapter : IAiChartDatasetProvider
    {
        private readonly AiChartMetricDef _def;
        private readonly AiChartMetricExecutor _executor;

        public CatalogChartDatasetAdapter(AiChartMetricDef def, AiChartMetricExecutor executor)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public string DatasetId => _def.DatasetId;
        public string Title => _def.Title;
        public string Description => _def.Description;
        public string Permission => _def.Permission;
        public string[] Grains => _def.Grains;
        public int MaxDays => _def.MaxDays;
        public int MaxMonths => _def.MaxMonths;
        public string[] AllowedTypes => _def.AllowedTypes;
        public IReadOnlyList<AiChartFieldDef> Fields => _def.Fields;

        public Task<AiChartQueryResult> QueryAsync(long userId, DateTime begin, DateTime end, string grain)
        {
            return _executor.QueryAsync(_def, begin, end, grain);
        }
    }
}
