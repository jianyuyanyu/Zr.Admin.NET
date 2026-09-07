namespace ZR.Model.AI.Dto
{
    /// <summary>图表字段定义（给模型选 xField / series.field）</summary>
    public class AiChartFieldDef
    {
        public string Field { get; set; }
        public string Label { get; set; }
        /// <summary>category | number</summary>
        public string Kind { get; set; }
    }

    /// <summary>单条系列配置（AI 产出，后端校验）</summary>
    public class AiChartSeriesSpec
    {
        public string Name { get; set; }
        public string Field { get; set; }
    }

    /// <summary>受约束的图表配置，禁止完整 ECharts option / formatter</summary>
    public class AiChartSpecDto
    {
        public string DatasetId { get; set; }
        /// <summary>line | bar | pie</summary>
        public string Type { get; set; }
        public string Title { get; set; }
        public string XField { get; set; }
        public List<AiChartSeriesSpec> Series { get; set; } = new();
    }

    /// <summary>工具查数结果：schema + 聚合行（数值以后端为准）</summary>
    public class AiChartQueryResult
    {
        public string DatasetId { get; set; }
        public string Title { get; set; }
        public string Grain { get; set; }
        public string TimeRange { get; set; }
        public string SuggestedType { get; set; }
        public string[] AllowedTypes { get; set; }
        public List<AiChartFieldDef> Fields { get; set; } = new();
        public List<Dictionary<string, object>> Rows { get; set; } = new();
    }

    /// <summary>下发给前端渲染的图表（配置 + 后端数据）</summary>
    public class AiChartViewDto
    {
        public string DatasetId { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string XField { get; set; }
        public List<AiChartSeriesSpec> Series { get; set; } = new();
        public List<Dictionary<string, object>> Rows { get; set; } = new();
    }
}
