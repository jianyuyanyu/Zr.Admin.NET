# ZRAdmin AI 模块用户使用文档

本文档面向**系统管理员**与**普通用户**，说明 ZRAdmin 中 AI 能力的使用方式、权限划分与治理规则。涉及的功能包括：AI 对话助手、AI 用量统计、AI 访问治理（策略/价格/Provider 诊断）。

> 说明：本文档描述的是后端已提供的 API 能力及配套的规则。具体前端菜单入口以实际部署的 Vue3 管理端为准，权限编码见各小节。

---

## 1. 功能总览

| 模块 | 说明 | 主要受众 |
| --- | --- | --- |
| AI 对话助手 | 全局办公助手，支持多轮会话、工具调用、流式输出 | 已分配 `ai:chat` 入口权限的用户 |
| AI 用量统计 | 按用户 / 场景 / 天的 token 与金额消耗 | 管理员（全站）、用户（本人） |
| AI 访问治理 | 配置额度策略、模型价格、检查 Provider 配置、探活 | 管理员（超管 + 治理权限） |

---

## 2. AI 对话助手

> 路由前缀：`aichat`，接口权限：`common`（登录即可调用）；前端入口需分配 `ai:chat` 权限方可显示。
> 会话、消息、工具数据均按**当前登录用户隔离**，用户只能看到自己的会话。

### 2.1 会话管理

| 操作 | 方法 & 路径 | 说明 |
| --- | --- | --- |
| 会话列表 | `GET aichat/sessions` | 当前用户会话，最近更新倒序，最多 50 条 |
| 新建会话 | `POST aichat/session` | 创建空会话 |
| 重命名 | `PUT aichat/session/{sessionId}/rename` | Body：`{ "title": "新标题" }` |
| 删除会话 | `DELETE aichat/session/{sessionId}` | 连同历史消息一起删除 |
| 历史消息 | `GET aichat/session/{sessionId}/messages` | 升序返回消息 |

### 2.2 发送消息

- 普通对话：`POST aichat/chat`，Body：`{ "sessionId": 0, "message": "你好" }`
  - `sessionId` 传 `0` 时自动新建会话。
  - 返回本轮完整结果（含落库消息、图表等）。
- 流式对话（打字机）：`POST aichat/chat/stream`，入参同上，返回 `text/event-stream`（SSE）。
  - 事件类型：`delta`（增量文本）、`tool`（工具执行 start/done）、`done`（整轮结束）、`error`（异常终止）。
  - 前端需关闭代理缓冲以实时展示（后端已设置 `X-Accel-Buffering: no`，并内置 20s 心跳保活）。

### 2.3 工具清单（工具展示名）

| 操作 | 方法 & 路径 | 说明 |
| --- | --- | --- |
| 工具清单 | `GET aichat/tools` | 返回当前用户可见工具的 `{ name, label }` 列表 |

- **工具的中文展示名由后端统一维护**（各工具定义 `AiToolDef.Label`），前端通过本接口获取并渲染「正在调用 XX / 已调用 XX」，**新增或改名工具不需要改前端**。
- 声明了 `Permission` 的工具（如 `analyze_login_security` 需 `monitor:logininfor:ai`）仅对拥有该权限或管理员返回，避免受限能力名外泄；`Label` 未登记时前端降级显示工具名。
- 同一套权限过滤也作用于下发给模型的 function calling 工具数组，保证「前端清单所见即模型可用」；越权时工具仅提示「请联系管理员授权」，不再回显权限编码（避免经模型转述把权限码透给无权用户）。
- 接口失败不影响对话，仅回退为显示工具原名。

> **新增工具时的约定**：在 `IAiAssistantToolProvider.GetToolDefs()` 中定义工具时**必须填写 `Label`**（中文短名）；涉及权限的工具同时填写 `Permission`。前端无需任何改动。

### 2.4 使用提示

- 对话会按场景自动调用对应工具（如翻译、日程/周报生成、代码生成列推断、日志 AI 分析等），无需手动选择。
- 删除会话不可恢复，请谨慎操作。

### 2.5 对话请求流程图（用户输入 → 返回）

下面给出一次对话从前端发起到结果返回的完整链路（普通对话与流式对话共用同一套编排，仅返回形式不同）。

```mermaid
flowchart TD
    A([用户输入消息]) -->|POST aichat/chat<br/>或 aichat/chat/stream| B[ActionPermissionFilter 鉴权<br/>权限: common]
    B --> C[Controller 参数校验<br/>非空 / 长度 ≤ 2000]
    C --> D[PrepareChatContextAsync 准备上下文]

    subgraph C1[准备上下文]
        direction TB
        E[AiHelper.EnsureAiEnabled<br/>校验 AI 全局开关与配置]
        F[ResolveProvider<br/>解析 Provider / BaseUrl / ApiKey / Model]
        G[ResolveSession<br/>sessionId=0 自动建会话<br/>否则校验归属(本人)]
        H[BuildConversationMessages<br/>系统提示 + 历史(≤20) + 本轮]
        I[BuildToolObjects<br/>按用户权限过滤工具数组]
        E --> F --> G --> H --> I
    end
    D --> C1

    I --> J{工具调用编排循环<br/>最多 MaxToolRounds 轮}
    J --> K[调用模型 ChatWithToolsAsync<br/>function calling + 流式 delta]
    K --> L{模型返回<br/>含 tool_calls?}
    L -->|否| M[得到最终回复 reply]
    L -->|是| N[逐个执行工具 ExecuteToolSafelyAsync<br/>SSE 事件: tool start / done]
    N --> O[工具结果回灌消息列表]
    O --> J

    M --> P{回复为空?}
    P -->|是| Q[兜底文案: 请重新描述]
    P -->|否| R[图表组装 AiChartAssembler.Assemble]
    Q --> R
    R --> S[FinishTurnAsync<br/>落库 用户/助手消息 · 自动标题<br/>更新会话 · 累计 token/usage]
    S --> T{返回方式}
    T -->|普通 chat| U1([返回整轮结果 JSON])
    T -->|流式 stream| U2([SSE 事件流: delta / tool / done])
```

**链路要点：**

- **鉴权与隔离**：接口层仅校验 `common`（登录即可调用），前端入口另需 `ai:chat` 权限方可显示；会话、消息、工具数据均按当前登录用户隔离，越权访问在 `ResolveSession` / 消息查询处直接拒绝。
- **工具可见性**：下发给模型的工具数组与 `GET aichat/tools` 清单同源——均按用户权限过滤（`monitor:logininfor:ai`、`monitor:operlog:ai` 等），保证「前端清单所见即模型可用」。
- **多轮工具调用**：模型一轮可能返回多个工具调用，逐个执行并把结果回灌后再次请求模型，最多 `MaxToolRounds` 轮收敛出最终答复；流式场景下每一步以 `tool start / done` 事件实时推送。
- **配额与治理**：额度、并发、访问策略由 `IAiCallGovernance` 在模型调用层与 `GET aichat/quota`（只读快照）侧提供；治理策略优先级 `user > role > tenant > global`，30 秒缓存自动生效。
- **流式保活**：`aichat/chat/stream` 关闭代理缓冲（`X-Accel-Buffering: no`）并内置 20s 心跳，避免工具执行 / 模型换轮停顿导致链路被中间代理掐断。
- **用量落账**：每轮 token 由模型返回值累计，最终在 `FinishTurnAsync` 随助手消息落库，供「AI 用量统计」核对成本。

---

## 3. AI 用量统计

> 路由前缀：`aiUsage`。
> **管理端**：权限 `ai:usage:list`（导出为 `ai:usage:export`）。数据范围按「平台管理员」收窄——
> 仅平台管理员（主租户管理员）可跨租户查看全站；其他租户管理员仅能看本租户；
> **个人端**：权限 `ai:usage:mine`，Service 内强制按当前登录人过滤（即便管理员从「我的用量」进入也只看自己）。

### 3.1 管理员视角（全站）

| 操作 | 方法 & 路径 | 说明 |
| --- | --- | --- |
| 全站汇总 | `GET aiUsage/summary` | 累计 + 按天 + 按场景 |
| 按用户聚合 | `GET aiUsage/users` | 时间段内各用户 token 消耗 |
| 调用流水 | `GET aiUsage/list` | 分页流水，可指定用户 |
| 导出流水 | `GET aiUsage/export` | 当前筛选导出 Excel（最多 10000 条），权限 `ai:usage:export` |

### 3.2 个人视角（仅本人）

| 操作 | 方法 & 路径 | 说明 |
| --- | --- | --- |
| 本人汇总 | `GET aiUsage/my/summary` | 累计 + 按天 + 按场景 |
| 本人流水 | `GET aiUsage/my/list` | 分页流水 |
| 导出本人流水 | `GET aiUsage/my/export` | 当前筛选导出 Excel（最多 10000 条） |

### 3.3 查询参数（`AiUsageQueryDto`）

通用查询参数（管理端 / 个人端一致）：时间范围、分页、按场景 / 用户过滤等。时间窗默认 30 天；按天数据在后端累加，避免溢出。

### 3.4 使用提示

- 金额由调用时按「模型价格 × token 用量」实时计算并落账，管理员可在用量页面核对成本。
- 个人用户只能查看自己的消耗，无法看到他人数据。

---

## 4. AI 访问治理

> 路由前缀：`aiGovernance`。
> 治理功能面向**平台管理员**（超管）或具备 `ai:governance:*` 权限的角色。
> 策略集中存放于主库，支持 `global / tenant / role / user` 多级作用域。

### 4.1 能力概览（Capabilities）

`GET aiGovernance/capabilities` —— 返回当前用户可管理的范围：

- `isPlatformAdmin`：是否平台超管
- `canManageGlobalPolicy`：能否管理全局策略
- `canManageModelPrice`：能否管理模型价格
- `canCheckProvider`：能否检查 / 探活 Provider

权限：`ai:governance:list`。

### 4.2 模型价格（Model Price）

| 操作 | 方法 & 路径 | 权限 |
| --- | --- | --- |
| 价格列表 | `GET aiGovernance/price/list` | `ai:price:list` |
| 价格详情 | `GET aiGovernance/price/{id}` | `ai:price:query` |
| 新增 | `POST aiGovernance/price` | `ai:price:add` |
| 编辑 | `PUT aiGovernance/price` | `ai:price:edit` |
| 删除 | `DELETE aiGovernance/price/{id}` | `ai:price:remove` |

价格字段（保存 DTO）：

- `provider` / `model`：供应商与模型名
- `inputPricePerMillion` / `outputPricePerMillion`：每百万 token 的输入 / 输出单价
- `currency`：币种，默认 `CNY`
- `status`：0 停用 / 1 启用

### 4.3 访问与额度策略（Access Policy）

策略按「作用域 + 场景」定义额度上限，作用域优先级：`user > role > tenant > global`。命中时按 `ScopeType + TenantId + SubjectId` 过滤后取最具体的一档。

| 操作 | 方法 & 路径 | 权限 |
| --- | --- | --- |
| 策略列表 | `GET aiGovernance/policy/list` | `ai:governance:list` |
| 策略详情 | `GET aiGovernance/policy/{id}` | `ai:governance:query` |
| 新增 | `POST aiGovernance/policy` | `ai:governance:add` |
| 编辑 | `PUT aiGovernance/policy` | `ai:governance:edit` |
| 删除 | `DELETE aiGovernance/policy/{id}` | `ai:governance:remove` |

策略字段（保存 DTO）：

- `scopeType`：`global` / `tenant` / `role` / `user`
- `tenantId` / `subjectId`：租户标识 / 角色或用户 ID（全局、租户策略填空值等价项）
- `scene`：能力场景，`*` 表示该作用域默认规则（可用场景见 4.5）
- `isEnabled`：空=继承上级；0=禁用；1=启用
- `dailyTokenLimit` / `monthlyTokenLimit`：日 / 月 token 上限（null 表示不限制）
- `dailyAmountLimit` / `monthlyAmountLimit`：日 / 月金额上限（元）
- `concurrentLimit`：并发调用上限
- `status`：0 正常 / 1 停用（停用策略不参与计算）

> 策略变更后由 SqlSugar 自动清除查询缓存（`IsAutoRemoveDataCache`）并重新加载，异常情况下最迟 30 秒自动过期，无需重启。

### 4.4 Provider 配置检查与探活

| 操作 | 方法 & 路径 | 权限 | 说明 |
| --- | --- | --- | --- |
| 配置检查 | `GET aiGovernance/config/check` | `ai:governance:health` | 校验 AI 基础配置（启用状态、Provider、BaseUrl、ApiKey、视觉能力等），返回告警列表 |
| Provider 探活 | `POST aiGovernance/health` | `ai:governance:health` | 实测一次对话连通性，返回延迟、HTTP 状态码、Provider 请求 ID |

- 配置检查为只读校验，不影响线上调用。
- 探活会真实发起一次轻量调用（场景 `health_check`），偶发执行，用于确认 Provider 当前可用。

### 4.5 能力场景目录

策略与用量统计中的 `scene` 取值来自系统内置目录（`AiSceneCatalog`），包括：

```
*                       默认规则
ai_chat                 全局 AI 对话助手
lang_translate          翻译
cron_parse              Cron 表达式解析
schedule_parse          日程解析
weekly_report           周报生成
gen_columns             代码生成列推断
login_security          登录安全日志 AI 分析
oper_health             操作日志 AI 健康分析
wf_generate             工作流流程生成
wf_approval_suggest     工作流审批建议
wf_flow_optimize        工作流流程优化
wf_intent_match         工作流意图匹配
wf_instance_summary     工作流实例摘要
wf_risk_check           工作流风险检查
wf_approval_summary     工作流审批汇总
health_check            Provider 探活
```

> 管理员可在策略中用具体场景名做精细化额度控制，或用 `*` 作为该作用域的兜底规则。

---

## 5. 权限编码一览

| 权限编码 | 说明 |
| --- | --- |
| `common` | 接口级：任意登录用户可调用对话接口（前端入口需 `ai:chat`） |
| `ai:usage:list` | 管理端全站用量 |
| `ai:usage:export` | 管理端用量导出 |
| `ai:usage:mine` | 个人用量查询 |
| `ai:chat` | AI 办公助手入口权限（分配后前端才显示助手入口） |
| `ai:governance:list` | 治理列表 / 能力概览 / 目录 |
| `ai:governance:query` | 策略详情 |
| `ai:governance:add` / `edit` / `remove` | 策略增改删 |
| `ai:price:list` / `query` / `add` / `edit` / `remove` | 模型价格管理 |
| `ai:governance:health` | Provider 配置检查 / 探活 |

> 用量管理端的权限码由对应 Controller 的 `ActionPermissionFilter` 统一校验（单一闸门，服务层不再重复校验）；跨租户全站视角仅对平台管理员（主租户管理员）开放，其他租户管理员自动收窄到本租户。

---

## 6. 鉴权边界说明

为避免「权限判断在控制器与服务层各写一份」导致口径漂移，AI 模块统一采用**单一闸门 + 分层职责**：

- **菜单 / 接口级权限码**（`ai:usage:list`、`ai:governance:edit` 等）：唯一由对应 Controller 的 `ActionPermissionFilter` 校验。服务层**不再重复校验**权限码。
- **数据归属 / 范围判断**：由服务层负责，因为过滤器只能识别「权限码集合」，回答不了「这条数据你能不能碰」：
  - 用量统计按平台管理员 / 租户管理员 / 个人收窄数据范围；
  - 治理策略的租户归属由 `AuthorizePolicy` 校验（global / tenant 级仅平台管理员可触达）；
  - 会话、消息、工具调用均按当前登录用户隔离。
- **工具级权限**：对话助手是「一个接口挂 N 个工具」，调哪个工具、带什么参数由模型生成，Controller 只能校验「能否使用 AI 助手」，无法按工具粒度授权。因此 `monitor:logininfor:ai`、`monitor:operlog:ai`、各数据集权限**只能在工具 Provider 内部校验**。工具按权限过滤后，既不下发到模型工具数组、也不在工具清单暴露；越权时仅提示「请联系管理员授权」，不再回显权限编码。

> 非 HTTP 调用方（后台任务、其他模块）直连 AI 服务时，须自行完成鉴权后再调用，服务层不再兜底接口级权限。

## 7. 常见问题

**Q：普通用户能看到的 AI 数据范围？**
A：对话助手只看自己的会话；用量统计（`ai:usage:mine`）只看本人的消耗汇总与流水。

**Q：策略改了为什么不立即生效？**
A：策略有 30 秒缓存窗口，变更后最多 30 秒自动生效，无需重启服务。

**Q：为什么我查不到全站用量？**
A：全站用量（`aiUsage/summary|users|list`）需要 `ai:usage:list` 权限且为平台管理员（主租户管理员）；其他租户管理员仅能看本租户，普通管理员请使用个人视角或申请对应权限。

**Q：Provider 探活失败代表什么？**
A：仅表示探活时刻该 Provider 不可达（网络、ApiKey、额度或模型名问题）。可用 `config/check` 看具体告警项。
