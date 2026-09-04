using Newtonsoft.Json.Linq;

namespace ZR.Workflow.Service.Engine
{
    /// <summary>
    /// 表单 JSON 值规整与条件求值辅助（推演/运行时共用）。
    /// 字符串不得走 SerializeObject，否则会被额外加引号导致比较失真。
    /// </summary>
    internal static class WfFormValueHelper
    {
        public static string Normalize(object value)
        {
            if (value == null) return string.Empty;
            if (value is string s) return s;
            if (value is JValue jv)
            {
                if (jv.Type == JTokenType.Null || jv.Type == JTokenType.Undefined) return string.Empty;
                if (jv.Type == JTokenType.String) return jv.ToString();
                if (jv.Type == JTokenType.Boolean)
                    return (bool)jv ? "true" : "false";
                if (jv.Type == JTokenType.Integer || jv.Type == JTokenType.Float)
                    return Convert.ToString(jv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                return jv.ToString();
            }
            if (value is JToken jt)
                return jt.ToString(Formatting.None);
            if (value is bool b) return b ? "true" : "false";
            if (value is IFormattable fmt)
                return fmt.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public static Dictionary<string, string> ParseObject(string formContent)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(formContent)) return dict;
            try
            {
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(formContent);
                if (obj != null)
                    foreach (var item in obj) dict[item.Key] = Normalize(item.Value);
            }
            catch { /* invalid JSON -> empty form */ }
            return dict;
        }

        /// <summary>
        /// 比较表单字段值与条件目标值（数值优先 / 否则按字符串）。
        /// 双方均可解析为 double 时用数值比较；Eq/Ne 始终用 OrdinalIgnoreCase 字符串比较。
        /// - Eq/Ne 不做数值归一，故 "1" vs "1.0" 不相等（刻意）。
        /// - 未知 op 返回 false；比较失败不抛异常。
        /// 运算符取值见 <see cref="WfConditionOp"/>。
        /// </summary>
        public static bool CompareValue(WfConditionOp op, string raw, string target)
        {
            raw = (raw ?? string.Empty).Trim();
            target = (target ?? string.Empty).Trim();
            var leftOk = double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var left);
            var rightOk = double.TryParse(target, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var right);
            var bothNum = leftOk && rightOk;
            switch (op)
            {
                case WfConditionOp.Lt: return bothNum ? left < right : string.CompareOrdinal(raw, target) < 0;
                case WfConditionOp.Le: return bothNum ? left <= right : string.CompareOrdinal(raw, target) <= 0;
                case WfConditionOp.Gt: return bothNum ? left > right : string.CompareOrdinal(raw, target) > 0;
                case WfConditionOp.Ge: return bothNum ? left >= right : string.CompareOrdinal(raw, target) >= 0;
                case WfConditionOp.Eq: return string.Equals(raw, target, StringComparison.OrdinalIgnoreCase);
                case WfConditionOp.Ne: return !string.Equals(raw, target, StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        /// <summary>
        /// 推演诊断：描述单条条件边与当前表单值的比较快照。
        /// </summary>
        /// <param name="link"></param>
        /// <param name="formValues"></param>
        /// <param name="satisfied"></param>
        /// <returns></returns>
        public static string DescribeSimCondition(ResolvedOutLink link, Dictionary<string, string> formValues, bool satisfied)
        {
            if (link?.ConditionError != null) return link.ConditionError;
            var cond = link?.Condition;
            if (cond == null) return "无条件内容";
            if (cond.IsComposite)
                return $"组合条件 logic={cond.Logic ?? "and"} ×{cond.Conditions?.Count ?? 0} → {(satisfied ? "true" : "false")}";
            var field = (cond.Field ?? "").Trim();
            formValues.TryGetValue(field, out var raw);
            var op = cond.Op.HasValue ? ((WfConditionOp)cond.Op.Value).ToString() : "?";
            return $"{field} {op} {cond.Value} ，表单={raw ?? "(缺字段)"} → {(satisfied ? "true" : "false")}";
        }

        /// <summary>
        /// 判断 source→target 是否为同一条件网关下兄弟分支之间的错连。
        /// 用于忽略并行扁平化产生的错误 link，必要时旁路到 JOIN/汇合后继而不经过兄弟节点。
        /// </summary>
        public static bool IsSiblingConditionBranchLink(long sourceId, long targetId, WorkflowTopology topo)
        {
            if (sourceId == targetId) return false;
            foreach (var node in topo.OrderedNodes)
            {
                if (node.NodeType != (int)WfNodeType.Condition) continue;
                var outs = topo.GetOutLinks(node.NodeId);
                if (outs.Count < 2) continue;

                // 两列头互连
                var sourceIsHead = outs.Any(l => l.TargetNodeId == sourceId);
                var targetIsHead = outs.Any(l => l.TargetNodeId == targetId);
                if (sourceIsHead && targetIsHead) return true;

                // 列内节点 → 另一列头（存量 5→6 的变体：中间节点串到兄弟列）
                if (!targetIsHead) continue;
                foreach (var head in outs.Select(l => l.TargetNodeId))
                {
                    if (head == targetId) continue;
                    if (IsReachableWithoutJoin(head, sourceId, topo)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 从 start 沿出边能否到达 goal（不穿越汇聚/结束）。
        /// </summary>
        /// <param name="startId"></param>
        /// <param name="goalId"></param>
        /// <param name="topo"></param>
        /// <returns></returns>
        private static bool IsReachableWithoutJoin(long startId, long goalId, WorkflowTopology topo)
        {
            if (startId <= 0 || goalId <= 0) return false;
            if (startId == goalId) return true;
            var stack = new Stack<long>();
            var visited = new HashSet<long>();
            stack.Push(startId);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!visited.Add(id)) continue;
                if (id == goalId) return true;
                var n = topo.GetNode(id);
                if (n == null) continue;
                var kind = topo.NodeKind.TryGetValue(id, out var k) ? k : (WfNodeType)n.NodeType;
                if (kind == WfNodeType.ParallelJoin || kind == WfNodeType.End) continue;
                foreach (var link in topo.GetOutLinks(id))
                    stack.Push(link.TargetNodeId);
            }
            return false;
        }

        /// <summary>
        /// 取首个非「兄弟条件支错连」的出边目标；用于旁路到 JOIN 等汇合后继。
        /// </summary>
        /// <param name="nodeId"></param>
        /// <param name="topo"></param>
        /// <returns></returns>
        public static WfFlowNode FirstNonSiblingOutTarget(long nodeId, WorkflowTopology topo)
        {
            foreach (var link in topo.GetOutLinks(nodeId))
            {
                if (IsSiblingConditionBranchLink(nodeId, link.TargetNodeId, topo)) continue;
                var n = topo.GetNode(link.TargetNodeId);
                if (n != null) return n;
            }
            return null;
        }

        /// <summary>
        /// 审批/抄送节点，用于首节点查找与顺序 fallback。
        /// </summary>
        /// <param name="nodeType"></param>
        /// <returns></returns>
        public static bool IsAuditableNode(int nodeType) =>
            nodeType == (int)WfNodeType.Audit || nodeType == (int)WfNodeType.Cc;

        /// <summary>
        /// 运行时评估已预解析的连线条件（拓扑构建时已完成 JSON 反序列化与静态校验，此处只取表单值并比较）。
        /// 业务不满足返回 false；配置错误抛 CustomException（避免条件全失败被当成流程正常结束）。
        /// </summary>
        public static bool EvalParsedCondition(ResolvedOutLink link, Dictionary<string, string> formValues)
        {
            if (!link.HasCondition) return false;
            if (link.ConditionError != null)
                throw new CustomException(link.ConditionError);
            var cond = link.Condition;
            if (cond == null) return false;
            return EvalLinkCondition(cond, formValues);
        }

        /// <summary>
        /// 递归评估连线条件（叶子比较或 And/Or）。logic=and 全满足才 true；logic=or 任一满足即 true。
        /// </summary>
        /// <param name="cond"></param>
        /// <param name="formValues"></param>
        /// <returns></returns>
        public static bool EvalLinkCondition(WfLinkCondition cond, Dictionary<string, string> formValues)
        {
            if (cond.IsComposite)
            {
                var logic = (cond.Logic ?? string.Empty).ToLowerInvariant() == "or";
                foreach (var sub in cond.Conditions)
                {
                    var hit = EvalLinkCondition(sub, formValues);
                    if (logic && hit) return true;
                    if (!logic && !hit) return false;
                }
                return logic ? false : true;
            }
            var field = (cond.Field ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(field))
                throw new CustomException("条件配置错误：连线条件缺少条件字段 field");
            if (!formValues.TryGetValue(field, out var raw))
                throw new CustomException($"条件配置错误：连线条件引用的表单字段【{field}】不在提交的表单中");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!cond.Op.HasValue)
                throw new CustomException($"条件配置错误：连线条件[{field}]缺少运算符 op");
            return CompareValue((WfConditionOp)cond.Op.Value, raw, cond.Value ?? string.Empty);
        }
    }
}