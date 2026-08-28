namespace ZR.Model.System.Tenant
{
    /// <summary>
    /// 租户套餐计费流水。记录租户开通/套餐分配/续费/到期降级的账目事实，供平台对账与租户消费追溯。
    /// 流水只增不改：降级、退订等均以新记录表达，不做物理删除。
    /// </summary>
    [SugarTable("sys_tenant_order", "租户套餐计费流水表")]
    [Tenant("0")]
    public class SysTenantOrder : SysBase, IMainDbEntity
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [SugarColumn(IsIdentity = true, IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 流水单号，TO+时间戳+随机片段，业务唯一
        /// </summary>
        [SugarColumn(Length = 64, ExtendedAttribute = ProteryConstant.NOTNULL)]
        public string OrderNo { get; set; }

        /// <summary>
        /// 租户标识
        /// </summary>
        [SugarColumn(Length = 64, ExtendedAttribute = ProteryConstant.NOTNULL)]
        public string TenantId { get; set; }

        /// <summary>
        /// 动作类型：plan-assign=套餐分配/变更（含开通初始分配），renew=续费，degrade=套餐到期降级
        /// </summary>
        [SugarColumn(Length = 32, ExtendedAttribute = ProteryConstant.NOTNULL)]
        public string ActionType { get; set; }

        /// <summary>
        /// 涉及的套餐编码（降级流水记录的是被降掉的套餐）
        /// </summary>
        [SugarColumn(Length = 64)]
        public string PlanCode { get; set; }

        /// <summary>
        /// 本次套餐/续费生效开始时间
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 本次套餐/续费到期时间
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 本次计费金额。当前业务无支付闭环，由操作方在续费/分配时选填，为空表示未记账
        /// </summary>
        public decimal? Amount { get; set; }

        /// <summary>
        /// 操作人（平台管理员或 system）
        /// </summary>
        [SugarColumn(Length = 64)]
        public string OperatorName { get; set; }

        /// <summary>
        /// 支付状态：0=待支付 1=已支付（在线支付单使用；线下记账直接置已支付或留空）
        /// </summary>
        [SugarColumn(ExtendedAttribute = ProteryConstant.NOTNULL)]
        public int PayStatus { get; set; }

        /// <summary>
        /// 支付渠道：wechat 等
        /// </summary>
        [SugarColumn(Length = 32)]
        public string PayChannel { get; set; }

        /// <summary>
        /// 第三方支付交易号（微信 transaction_id）
        /// </summary>
        [SugarColumn(Length = 64)]
        public string TransactionId { get; set; }

        /// <summary>
        /// 本次续费时长（天），在线续费下单时写入，支付成功后据此执行续费
        /// </summary>
        public int? DurationDays { get; set; }

        /// <summary>
        /// 支付完成时间
        /// </summary>
        public DateTime? PayTime { get; set; }
    }
}
