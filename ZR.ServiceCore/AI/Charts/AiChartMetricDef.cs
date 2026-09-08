using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>配置指标允许绑定的实体。执行器 switch，禁止任意表名。</summary>
    public enum AiChartEntityKey
    {
        Logininfor = 1
    }

    /// <summary>配置指标聚合方式。禁止模型指定。</summary>
    public enum AiChartMetricAgg
    {
        Count = 1,
        Sum = 2
    }

    /// <summary>简单趋势图的代码目录项（第一期不加库表，避免误配越权）。</summary>
    public class AiChartMetricDef
    {
        public string DatasetId { get; init; }
        public string Title { get; init; }
        public string Description { get; init; }
        public string Permission { get; init; }
        public AiChartEntityKey Entity { get; init; }
        public AiChartMetricAgg Agg { get; init; } = AiChartMetricAgg.Count;
        /// <summary>数值列名，须与 Rows 的 key 一致</summary>
        public string ValueField { get; init; } = "value";
        public string ValueLabel { get; init; } = "数值";
        public string[] Grains { get; init; } = ["day", "week", "month"];
        public int MaxDays { get; init; } = 90;
        public int MaxMonths { get; init; } = 3;
        public string[] AllowedTypes { get; init; } = ["line", "bar"];
        public string SuggestedType { get; init; } = "line";

        public IReadOnlyList<AiChartFieldDef> Fields =>
        [
            new AiChartFieldDef { Field = "date", Label = "时间", Kind = "category" },
            new AiChartFieldDef { Field = ValueField, Label = ValueLabel, Kind = "number" }
        ];
    }
}
