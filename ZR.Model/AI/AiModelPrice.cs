using ZR.Model.System;

namespace ZR.Model.AI
{
    /// <summary>
    /// AI 模型单价。金额按每百万 Token 计，调用流水保存命中价格的快照。
    /// </summary>
    [SugarTable("ai_model_price", "AI模型单价")]
    [SugarIndex("uk_ai_model_price", nameof(Provider), OrderByType.Asc, true)]
    [SugarIndex("uk_ai_model_price", nameof(Model), OrderByType.Asc, true)]
    public class AiModelPrice : SysBase, IMainDbEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public long Id { get; set; }

        [SugarColumn(Length = 32)]
        public string Provider { get; set; }

        [SugarColumn(Length = 100)]
        public string Model { get; set; }

        [SugarColumn(ColumnDataType = "decimal(18,6)", DefaultValue = "0")]
        public decimal InputPricePerMillion { get; set; }

        [SugarColumn(ColumnDataType = "decimal(18,6)", DefaultValue = "0")]
        public decimal OutputPricePerMillion { get; set; }

        [SugarColumn(Length = 8, DefaultValue = "CNY")]
        public string Currency { get; set; } = "CNY";

        /// <summary>0 正常，1 停用。</summary>
        [SugarColumn(DefaultValue = "0")]
        public int Status { get; set; }
    }
}
