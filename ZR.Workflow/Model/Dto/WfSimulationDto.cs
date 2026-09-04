namespace ZR.Workflow.Model.Dto
{
    /// <summary>
    /// 流程模拟试运行入参。试运行为纯推演、不落库，用于发布/发起前验证流程配置：
    /// 审批人能否解析出人、条件分支走向、并行分叉汇聚、空审批人兜底是否按预期工作。
    /// </summary>
    public class WfSimulateInputDto
    {
        /// <summary>流程定义 Id（草稿/正式版本均可模拟，便于发布前验证）</summary>
        public long FlowId { get; set; }

        /// <summary>模拟表单内容（JSON 字符串，结构与 FormContent 一致；为空时条件分支按字段缺失告警处理）</summary>
        public string FormContent { get; set; }

        /// <summary>模拟申请人 userId（影响"发起人主管/表单字段"类审批人解析；为空由 Controller 取当前登录人）</summary>
        public long? ApplyUserId { get; set; }

        /// <summary>是否推演全程：true=假设每个审批点都通过、一直推演到终点；false=推演到首批待办产生即止</summary>
        public bool AssumeApprove { get; set; } = true;
    }

    /// <summary>
    /// 模拟推演步骤（按路线顺序输出，审批/抄送节点带人员解析结果）
    /// </summary>
    public class WfSimulationStepDto
    {
        /// <summary>节点 Id</summary>
        public long NodeId { get; set; }

        /// <summary>节点名称</summary>
        public string NodeName { get; set; }

        /// <summary>节点类型（同 WfNodeType）</summary>
        public int NodeType { get; set; }

        /// <summary>步骤结果：等待审批/自动跳过/抄送/条件选路/并行分叉/并行汇聚/流程结束/无出边终止</summary>
        public string Result { get; set; }

        /// <summary>审批人/抄送人昵称（逗号分隔）</summary>
        public string Approvers { get; set; }

        /// <summary>签类型描述（仅审批节点）</summary>
        public string SignTypeDesc { get; set; }

        /// <summary>并行分支标签（null=主线；"分支1""分支2"…标识并行分支）</summary>
        public string Branch { get; set; }

        /// <summary>来源节点 Id（从哪个节点流到本节点；首节点为 null）。前端据此高亮走过的连线。</summary>
        public long? PrevNodeId { get; set; }

        /// <summary>补充说明（跳过原因/条件命中情况/告警详情）</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// 模拟试运行结果
    /// </summary>
    public class WfSimulationResultDto
    {
        /// <summary>流程定义 Id</summary>
        public long FlowId { get; set; }

        /// <summary>流程名称</summary>
        public string FlowName { get; set; }

        /// <summary>推演步骤（按路线顺序）</summary>
        public List<WfSimulationStepDto> Steps { get; set; } = new List<WfSimulationStepDto>();

        /// <summary>推演发现的配置问题（审批人为空/条件求值失败/无默认分支/疑似环等），发布前应逐项消除</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>推演结论：completed=已走到终点 / waiting=停在等待审批 / terminated=异常终止</summary>
        public string Outcome { get; set; }
    }
}
