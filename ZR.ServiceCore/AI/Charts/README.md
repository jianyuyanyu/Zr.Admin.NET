# AI 助手图表：新增数据集接入说明

图表管道只实现一次。对话编排、ECharts、`query_chart_dataset` **不要改**。

```text
用户自然语言
  → 模型调用 query_chart_dataset(datasetId, days/months, grain)
  → 工具按权限选「配置目录」或「代码插件」
  → 后端固定聚合（禁止 NL2SQL）
  → 模型只出 zr-chart 配置
  → 前端 ECharts
```

---

## 选哪条路

| 场景 | 做法 |
| --- | --- |
| 一张表、按天/周/月 COUNT 或 SUM | 在 [AiChartMetricCatalog.cs](AiChartMetricCatalog.cs) **加一条配置**；若是新实体，再在 [AiChartMetricExecutor.cs](AiChartMetricExecutor.cs) 加一个 `EntityKey` 分支 |
| 多维度、连表、脱敏、TopN 合并 | 实现 [IAiChartDatasetProvider](../IService/IAiChartDatasetProvider.cs) 写一个类 |

同一 `datasetId` 不能两边都有：冲突时 **以代码插件为准**，配置项被忽略并打日志。

参考：

- 配置：`login_daily`（每日登录次数）
- 插件：[OperLogChartDatasetProvider.cs](OperLogChartDatasetProvider.cs)、[LoginRegionChartDatasetProvider.cs](LoginRegionChartDatasetProvider.cs)

---

## 1. 必须遵守的安全边界

- 模型**不能**指定表名、列名、WHERE、SQL。目录的 `Entity` 是枚举，执行器 `switch` 落到表达式。
- 只回灌聚合行，禁止用户明细、密码、Token、无权限的完整 IP。
- 权限与对应页面一致；管理员 `*:*:*` 工具层放行。
- 时间跨度用 `MaxDays` / `MaxMonths` 截断。

未注册指标：引导去模块，**不要写 SQL**。

---

## 2. 简单图：加一条目录（推荐）

在 `AiChartMetricCatalog.All` 追加 `AiChartMetricDef`：

```csharp
new AiChartMetricDef
{
    DatasetId = "login_daily",
    Title = "每日用户登录数",
    Description = "按天统计登录次数……",
    Permission = "monitor:logininfor:ai",
    Entity = AiChartEntityKey.Logininfor, // 执行器白名单，禁止任意表
    Agg = AiChartMetricAgg.Count,
    ValueField = "loginCount",
    ValueLabel = "用户登录数",
    Grains = ["day", "week", "month"],
    MaxDays = 90,
    MaxMonths = 3,
    AllowedTypes = ["line", "bar"],
    SuggestedType = "line"
}
```

当前执行器已支持：`Logininfor` + `Count`（成功+失败合计）。

新实体（如 `SysUser` 按创建时间 COUNT）：

1. `AiChartEntityKey` 加枚举值
2. `AiChartMetricExecutor` 增加对应 `Queryable<T>().ApplyScope()` 分支（时间字段写死在 case 里）
3. 目录加一行

不要把表名/列名做成字符串拼进 SQL。

---

## 3. 复杂图：写 Provider 类

放在业务程序集，打 `[AppService(ServiceType = typeof(IAiChartDatasetProvider))]`。

- 多维度：实现 `Dimensions` 并重写 5 参 `QueryAsync`，见 oper
- 地域 TopN / 脱敏：见 login_region
- 跨模块连表（登录且充值）：新插件，固定查询，不要开放 JOIN

`InjectClass` 已含 `ZR.ServiceCore` / `ZR.Mall` / `ZR.Workflow`。

### Query 约定

- `Rows` 的 key 与 `Fields.Field` 一致；时间类目建议 `date`
- 无数据返回空 `Rows`
- `SuggestedType` 须属于 `AllowedTypes`（`line` / `bar` / `pie`）

### 不必改

`SysAiChatService`、Vue3 `AiChatAssistant`、一般也不改 `ai-chat-system.md`。重启 API 即可。

---

## 4. 联调

1. 当前用户有权限码或管理员。
2. 「统计最近 7 天每天的用户登录数」出折线，口径为**登录次数**（非去重人数）。
3. oper / login_region 行为不变。
4. 未注册问法引导模块，不写 SQL。

---

## 5. 常见问题

**提示词要写死 datasetId 吗？**  
不用。工具描述会拼目录 + 插件。

**MaxMonths 怎么定？**  
跟真实查询能力走。登录日志配置为 3 个月，避免承诺做不到的跨度。

**第二期才做：** 每月新增用户、商城充值、指标热更新表。
