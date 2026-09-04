using ZR.Workflow.Service.Engine;

namespace ZR.Workflow.Service
{
    public partial class WfEngineService
    {
        #region 流程模拟试运行

        /// <summary>模拟推演递归深度上限（与引擎流转深度上限一致）。</summary>
        private const int MaxSimulateDepth = 200;

        /// <summary>模拟推演步数上限：深度之外再兜一道环（环+多入口场景步数先爆）。</summary>
        private const int MaxSimulateSteps = 500;

        /// <summary>
        /// 流程模拟试运行：按给定表单从首节点开始推演审批路线，不落库任何数据。
        /// 走向与引擎运行时一致（条件网关选路 / 并行分叉汇聚 / 空审批人兜底 / 分组成员条件激活）。
        /// assumeApprove=true 时假设每个审批点均通过、推演到流程终点，用于发布前发现后半段配置问题；
        /// false 时推演到首批待办产生即止，用于发起前预览待办归属。
        /// 条件求值失败 / 审批人解析失败不中断推演，转为告警汇总（一次试运行暴露全部问题）。
        /// </summary>
        public WfSimulationResultDto Simulate(WfSimulateInputDto input)
        {
            if (input == null || input.FlowId <= 0) throw new CustomException("请选择要模拟的流程");
            var def = Context.Queryable<WfFlowDefinition>().First(d => d.FlowId == input.FlowId)
                ?? throw new CustomException("流程定义不存在");
            var topo = LoadTopology(input.FlowId);
            var formValues = Engine.WfFormValueHelper.ParseObject(input.FormContent);
            var result = new WfSimulationResultDto { FlowId = def.FlowId, FlowName = def.FlowName };

            // 首节点口径与 Start 一致：网关可作为首节点（发起后立即分流/分叉）
            var first = topo.OrderedNodes.FirstOrDefault(n => WfFormValueHelper.IsAuditableNode(n.NodeType)
                || n.NodeType == (int)WfNodeType.Condition || n.NodeType == (int)WfNodeType.ParallelFork);
            if (first == null)
            {
                result.Warnings.Add("流程未配置有效节点（审批/抄送/条件网关/并行分叉）");
                result.Outcome = "terminated";
                return result;
            }

            var joinedOnce = new HashSet<long>();
            // blockedNodes：条件排他选路后，未选中的分支列头加入黑名单，防止存量错连（如 5→6）串进兄弟支
            var blockedNodes = new HashSet<long>();
            result.Outcome = SimulateWalk(first, topo, formValues, input.ApplyUserId, input.AssumeApprove, null, result, joinedOnce, blockedNodes, null, 0);
            return result;
        }

        /// <summary>
        /// 推演一个节点的走向，返回推演结局（completed / waiting / terminated）。
        /// joinedOnce 记录已推进过的汇聚网关：模拟只推演"被选中路线"，先到的分支触发汇聚推进，其余分支到此记汇合。
        /// blockedNodes 记录条件网关未选中的分支列头：后续选路若落到这些节点则旁路到其汇合后继，绝不走进未选支。
        /// prevNodeId 为进入当前节点的来源节点 id，随 Step 下发，前端据此在流程图上高亮走过的连线。
        /// </summary>
        private string SimulateWalk(WfFlowNode node, WorkflowTopology topo, Dictionary<string, string> formValues,
            long? applyUserId, bool assumeApprove, string branch, WfSimulationResultDto result, HashSet<long> joinedOnce,
            HashSet<long> blockedNodes, long? prevNodeId, int depth)
        {
            if (node == null) return "completed";
            // 未选中的条件支：直接跳过（不留步骤），避免错连把兄弟支又走一遍
            if (blockedNodes != null && blockedNodes.Contains(node.NodeId)) return "completed";
            if (depth > MaxSimulateDepth || result.Steps.Count > MaxSimulateSteps)
            {
                result.Warnings.Add($"节点「{node.NodeName}」之后推演深度/步数超过上限，疑似流程存在连线环路，已终止推演");
                return "terminated";
            }

            var kind = topo.NodeKind.TryGetValue(node.NodeId, out var k) ? k : (WfNodeType)node.NodeType;
            switch (kind)
            {
                // 条件网关(4)：排他选路，逐条出边评估条件；求值失败转告警按未命中处理
                case WfNodeType.Condition:
                {
                    var outLinks = topo.GetOutLinks(node.NodeId);
                    if (outLinks.Count == 0)
                    {
                        result.Warnings.Add($"条件网关「{node.NodeName}」未配置任何出边，流程将在此卡死");
                        AddSimStep(result, node, branch, "无出边终止", prevNodeId: prevNodeId);
                        return "terminated";
                    }

                    // 诊断：出边全部无条件时，"默认分支"就是唯一路线——若设计图中配了条件，
                    // 说明条件只存在设计图、未保存进连线表（wf_node_link.ConditionJson），需重新保存流程
                    var condEdgeCount = outLinks.Count(l => l.HasCondition);
                    if (condEdgeCount == 0)
                    {
                        result.Warnings.Add(
                            $"条件网关「{node.NodeName}」的 {outLinks.Count} 条出边均未配置条件，已全部按默认分支处理。" +
                            "若设计图中已配置条件，说明条件未保存进连线表，请打开流程设计器重新保存后再推演");
                    }

                    WfFlowNode hit = null;
                    var notes = new List<string>();
                    foreach (var link in outLinks)
                    {
                        if (!link.HasCondition) continue;
                        bool satisfied;
                        string evalDetail;
                        try
                        {
                            satisfied = WfFormValueHelper.EvalParsedCondition(link, formValues);
                            evalDetail = WfFormValueHelper.DescribeSimCondition(link, formValues, satisfied);
                        }
                        catch (Exception ex)
                        {
                            satisfied = false;
                            evalDetail = $"求值失败：{ex.Message}";
                            result.Warnings.Add($"条件网关「{node.NodeName}」出边条件求值失败：{ex.Message}");
                        }
                        var targetName = topo.GetNode(link.TargetNodeId)?.NodeName ?? link.TargetNodeId.ToString();
                        notes.Add(satisfied ? $"命中「{targetName}」（{evalDetail}）" : $"未命中「{targetName}」（{evalDetail}）");
                        if (satisfied && hit == null) hit = topo.GetNode(link.TargetNodeId);
                    }

                    // 无条件命中 → 落默认分支（首个无条件出边），与引擎 ResolveNextNode 语义一致
                    var defaultTarget = outLinks.Where(l => !l.HasCondition)
                        .Select(l => topo.GetNode(l.TargetNodeId)).FirstOrDefault(n => n != null);
                    var taken = hit ?? defaultTarget;

                    // 排他：未选中列从列头起收集整列到黑名单；遇到兄弟列头/已选列头立即停，
                    // 避免沿存量错连 5→6 把已选列也封掉。
                    var branchHeads = new HashSet<long>(outLinks.Select(l => l.TargetNodeId));
                    foreach (var link in outLinks)
                    {
                        if (taken != null && link.TargetNodeId == taken.NodeId) continue;
                        CollectConditionBranchBlocked(link.TargetNodeId, topo, blockedNodes, branchHeads);
                    }

                    if (taken == null)
                    {
                        AddSimStep(result, node, branch, "条件选路", prevNodeId: prevNodeId,
                            note: string.Join("，", notes) + "，无命中且无默认分支");
                        result.Warnings.Add($"条件网关「{node.NodeName}」所有条件均不满足且未配置默认分支，流程将无法流转");
                        return "terminated";
                    }

                    AddSimStep(result, node, branch, "条件选路", prevNodeId: prevNodeId,
                        note: string.Join("，", notes) + (hit != null
                            ? $"，走「{taken.NodeName}」"
                            : $"，无命中走默认分支「{taken.NodeName}」"));
                    return SimulateWalk(taken, topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                }

                // 并行分叉(7)：无条件/命中条件的出边全部并发激活（与引擎 ResolveParallelTargets 一致）
                case WfNodeType.ParallelFork:
                {
                    List<WfFlowNode> targets;
                    try { targets = ResolveParallelTargets(node, topo, formValues); }
                    catch (Exception ex)
                    {
                        targets = new List<WfFlowNode>();
                        result.Warnings.Add($"并行分叉「{node.NodeName}」出边条件求值失败：{ex.Message}");
                    }
                    AddSimStep(result, node, branch, targets.Count > 0 ? $"并行分叉（{targets.Count} 条分支）" : "无出边终止",
                        prevNodeId: prevNodeId,
                        note: targets.Count > 0 ? string.Join("、", targets.Select(t => t.NodeName)) : "分叉网关无可用出边，流程将在此卡死");
                    if (targets.Count == 0) return "terminated";
                    foreach (var t in targets)
                        SimulateWalk(t, topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                    return "completed";
                }

                // 并行汇聚(8)：模拟只走主线，先到的分支触发汇聚推进，其余分支到此记汇合
                case WfNodeType.ParallelJoin:
                {
                    if (!joinedOnce.Add(node.NodeId))
                    {
                        AddSimStep(result, node, branch, "并行汇合", prevNodeId: prevNodeId, note: "分支到此汇合（汇聚推进已由其他分支触发）");
                        return "completed";
                    }
                    AddSimStep(result, node, branch, "并行汇聚", prevNodeId: prevNodeId, note: "全部入边分支完成后继续推进");
                    return SimulateWalk(SimulateNext(node, topo, formValues, result, branch, prevNodeId, blockedNodes),
                        topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                }

                case WfNodeType.End:
                    AddSimStep(result, node, branch, "流程结束", prevNodeId: prevNodeId);
                    return "completed";

                case WfNodeType.Start:
                    return SimulateWalk(SimulateNext(node, topo, formValues, result, branch, prevNodeId, blockedNodes),
                        topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
            }

            // 并行分组（组内无显式分叉网关 7）：首次到达整组 fork，成员按出边条件激活（与引擎 ArriveNode 一致）
            if (node.ParallelGroup > 0
                && !(topo.ForkByGroup.TryGetValue(node.ParallelGroup, out var forkGw) && forkGw != null))
            {
                foreach (var g in topo.GetGroupNodes(node.ParallelGroup))
                {
                    if (g.NodeType == (int)WfNodeType.ParallelFork || g.NodeType == (int)WfNodeType.ParallelJoin) continue;
                    if (blockedNodes.Contains(g.NodeId)) continue;
                    bool activate;
                    try { activate = ShouldActivateForkMember(forkGw, g, topo, formValues); }
                    catch (Exception ex)
                    {
                        activate = false;
                        result.Warnings.Add($"并行分组「{node.NodeName}」成员「{g.NodeName}」出边条件求值失败：{ex.Message}");
                    }
                    if (!activate)
                    {
                        AddSimStep(result, g, branch, "自动跳过", prevNodeId: node.NodeId, note: "分支条件不满足，节点自动跳过");
                        continue;
                    }
                    SimulateBusinessNode(g, topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                }

                var exits = ResolveParallelGroupExit(node.ParallelGroup, topo);
                if (exits.Count == 0)
                {
                    result.Warnings.Add($"并行分组 {node.ParallelGroup} 未配置指向组外的汇聚出口，流程将在此卡死");
                    AddSimStep(result, node, branch, "无出边终止", prevNodeId: prevNodeId);
                    return "terminated";
                }
                foreach (var exitNode in exits)
                    SimulateWalk(exitNode, topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                return "completed";
            }

            // 普通业务节点（审批/抄送；组内成员且组有显式 fork 时也走到这里）
            return SimulateBusinessNode(node, topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, prevNodeId, depth);
        }

        /// <summary>
        /// 推演业务节点（审批/抄送）：解析人员 → 空审批人兜底 → 按 assumeApprove 决定是否继续向后推演。
        /// </summary>
        private string SimulateBusinessNode(WfFlowNode node, WorkflowTopology topo, Dictionary<string, string> formValues,
            long? applyUserId, bool assumeApprove, string branch, WfSimulationResultDto result, HashSet<long> joinedOnce,
            HashSet<long> blockedNodes, long? prevNodeId, int depth)
        {
            var kind = topo.NodeKind.TryGetValue(node.NodeId, out var k) ? k : (WfNodeType)node.NodeType;
            if (kind == WfNodeType.Cc)
            {
                var cc = ResolveApproversSafe(node, formValues, applyUserId, result);
                AddSimStep(result, node, branch, "抄送", cc.Count > 0 ? JoinNicks(cc) : null,
                    prevNodeId: prevNodeId, note: cc.Count == 0 ? "未解析到抄送人" : null);
                if (cc.Count == 0) result.Warnings.Add($"抄送节点「{node.NodeName}」按当前表单解析不到抄送人");
                return SimulateWalk(SimulateNext(node, topo, formValues, result, branch, prevNodeId, blockedNodes),
                    topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
            }

            var approvers = ResolveApproversSafe(node, formValues, applyUserId, result);
            if (approvers.Count == 0)
            {
                var useDefault = (WfEmptyApproverStrategy)node.EmptyApproverStrategy == WfEmptyApproverStrategy.DefaultUser
                    && node.DefaultApproverId.HasValue && node.DefaultApproverId.Value > 0;
                if (useDefault)
                {
                    AddSimStep(result, node, branch, "等待审批",
                        string.IsNullOrWhiteSpace(node.DefaultApproverName) ? $"默认审批人（{node.DefaultApproverId}）" : node.DefaultApproverName,
                        DescribeSignType(node), prevNodeId: prevNodeId, note: "审批人为空，按空审批人策略由默认审批人代审");
                    if (!assumeApprove) return "waiting";
                }
                else
                {
                    AddSimStep(result, node, branch, "自动跳过", prevNodeId: prevNodeId, note: "审批人为空，节点自动跳过");
                    result.Warnings.Add($"审批节点「{node.NodeName}」按当前表单解析不到审批人，运行时将自动跳过该节点");
                    return SimulateWalk(SimulateNext(node, topo, formValues, result, branch, prevNodeId, blockedNodes),
                        topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
                }
            }
            else
            {
                AddSimStep(result, node, branch, "等待审批", JoinNicks(approvers), DescribeSignType(node), prevNodeId: prevNodeId);
                if (!assumeApprove) return "waiting";
            }

            return SimulateWalk(SimulateNext(node, topo, formValues, result, branch, prevNodeId, blockedNodes),
                topo, formValues, applyUserId, assumeApprove, branch, result, joinedOnce, blockedNodes, node.NodeId, depth + 1);
        }

        /// <summary>推演选路：复用引擎 ResolveNextNode；若下一节点落在条件未选中支上则旁路到其汇合后继。</summary>
        private WfFlowNode SimulateNext(WfFlowNode current, WorkflowTopology topo, Dictionary<string, string> formValues,
            WfSimulationResultDto result, string branch, long? prevNodeId, HashSet<long> blockedNodes)
        {
            try
            {
                var next = ResolveNextNode(current, topo, formValues);
                // 再挡一层：即便 ResolveNextNode 未识别兄弟错连，只要目标是「任一条件网关的未选列头/列内节点」就旁路
                var guard = 0;
                while (next != null && blockedNodes != null && blockedNodes.Contains(next.NodeId) && guard++ < 8)
                {
                    WfFlowNode bypass = null;
                    foreach (var link in topo.GetOutLinks(next.NodeId))
                    {
                        if (blockedNodes.Contains(link.TargetNodeId)) continue;
                        bypass = topo.GetNode(link.TargetNodeId);
                        if (bypass != null) break;
                    }
                    // 被封节点自身无出边时，尝试从当前节点其它出边找未封锁目标
                    if (bypass == null)
                    {
                        foreach (var link in topo.GetOutLinks(current.NodeId))
                        {
                            if (blockedNodes.Contains(link.TargetNodeId)) continue;
                            if (WfFormValueHelper.IsSiblingConditionBranchLink(current.NodeId, link.TargetNodeId, topo)) continue;
                            bypass = topo.GetNode(link.TargetNodeId);
                            if (bypass != null) break;
                        }
                    }
                    next = bypass;
                }
                if (next != null && blockedNodes != null && blockedNodes.Contains(next.NodeId)) return null;
                return next;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"节点「{current.NodeName}」后续选路失败：{ex.Message}");
                AddSimStep(result, current, branch, "无出边终止", prevNodeId: prevNodeId, note: "后续选路失败，流程将在此卡死");
                return null;
            }
        }

        /// <summary>
        /// 将条件未选中列从列头起可达的节点全部加入 blocked（遇到汇聚/结束则停止且不封锁汇聚点本身）。
        /// branchHeads：同一条件网关的全部列头；遍历时不得跨进其它列头（防止 5→6 错连把已选列封掉）。
        /// </summary>
        private static void CollectConditionBranchBlocked(long startId, WorkflowTopology topo, HashSet<long> blockedNodes, HashSet<long> branchHeads)
        {
            if (blockedNodes == null || startId <= 0) return;
            var stack = new Stack<long>();
            var visited = new HashSet<long>();
            stack.Push(startId);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!visited.Add(id)) continue;
                // 误入其它列头：不封锁、不继续（其它列由自己的收集或已选列负责）
                if (id != startId && branchHeads != null && branchHeads.Contains(id)) continue;
                var n = topo.GetNode(id);
                if (n == null) continue;
                var kind = topo.NodeKind.TryGetValue(id, out var k) ? k : (WfNodeType)n.NodeType;
                if (kind == WfNodeType.ParallelJoin || kind == WfNodeType.End) continue;
                blockedNodes.Add(id);
                foreach (var link in topo.GetOutLinks(id))
                {
                    if (branchHeads != null && branchHeads.Contains(link.TargetNodeId) && link.TargetNodeId != startId)
                        continue;
                    stack.Push(link.TargetNodeId);
                }
            }
        }

        /// <summary>推演用审批人解析：解析异常（如主管未配置）转告警并按空处理，不中断整条路线推演。</summary>
        private List<ResolvedApprover> ResolveApproversSafe(WfFlowNode node, Dictionary<string, string> formValues, long? applyUserId, WfSimulationResultDto result)
        {
            try { return ResolveApprovers(node, formValues, applyUserId); }
            catch (Exception ex)
            {
                result.Warnings.Add($"节点「{node.NodeName}」审批人解析失败：{ex.Message}");
                return new List<ResolvedApprover>();
            }
        }

        private static void AddSimStep(WfSimulationResultDto result, WfFlowNode node, string branch, string stepResult,
            string approvers = null, string signTypeDesc = null, long? prevNodeId = null, string note = null)
        {
            result.Steps.Add(new WfSimulationStepDto
            {
                NodeId = node.NodeId,
                NodeName = node.NodeName,
                NodeType = node.NodeType,
                Result = stepResult,
                Approvers = approvers,
                SignTypeDesc = signTypeDesc,
                Branch = branch,
                PrevNodeId = prevNodeId,
                Note = note
            });
        }

        private static string JoinNicks(List<ResolvedApprover> approvers)
            => string.Join("、", approvers.Select(a => a.NickName));

        private static string DescribeSignType(WfFlowNode node)
        {
            return ((WfSignType)node.SignType) switch
            {
                WfSignType.And => "会签（全员通过才过）",
                WfSignType.Sequential => "依次审批（逐人顺序审批）",
                WfSignType.Percent => $"比例会签（通过率≥{(node.PassRatio ?? 1) * 100:0.#}%）",
                _ => "或签（任一人通过即过）",
            };
        }

        #endregion
    }
}
