using Infrastructure.Attribute;
using Infrastructure.Helper;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using NLog;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.Model.System.Dto;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 全局 AI 助手（办工助手）：
    /// 会话管理（ai_chat_session / ai_chat_message，按 UserId 隔离）+
    /// 对话编排：历史上下文 + 系统提示 → LLM(function calling) → 执行工具回灌 → 最终回复。
    /// 内置办工工具（日程/周报），并聚合各模块 IAiAssistantToolProvider 注册的扩展工具（如工作流待办）。
    /// </summary>
    [AppService(ServiceType = typeof(ISysAiChatService), ServiceLifetime = LifeTime.Transient)]
    public class SysAiChatService : BaseService<AiChatSession>, ISysAiChatService
    {
        private readonly ISysAiService _sysAi;
        private readonly IDailyScheduleService _scheduleService;
        private readonly IEnumerable<IAiAssistantToolProvider> _toolProviders;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>最大工具轮询次数（防止模型反复要求调用工具造成死循环）</summary>
        private const int MaxToolRounds = 5;
        /// <summary>回灌历史消息最大条数</summary>
        private const int MaxHistoryMessages = 20;
        /// <summary>单条工具结果回灌给模型的最大长度</summary>
        private const int ToolResultMaxLen = 4000;

        public SysAiChatService(ISysAiService sysAi, IDailyScheduleService scheduleService,
            IEnumerable<IAiAssistantToolProvider> toolProviders)
        {
            _sysAi = sysAi;
            _scheduleService = scheduleService;
            _toolProviders = toolProviders;
        }

        #region 会话 CRUD

        /// <summary>
        /// 列出用户的会话列表
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <returns>会话列表</returns>
        public async Task<List<SysAiChatSessionDto>> ListSessionsAsync(long userId)
        {
            var list = await Queryable()
                .Where(m => m.UserId == userId)
                .OrderBy(m => m.CreateTime, OrderByType.Desc)
                .Take(50)
                .ToListAsync();
            return [.. list.Select(m => new SysAiChatSessionDto
            {
                SessionId = m.SessionId,
                Title = m.Title,
                Model = m.Model,
                CreateTime = m.CreateTime,
                UpdateTime = m.UpdateTime
            })];
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<SysAiChatSessionDto> CreateSessionAsync(long userId)
        {
            var session = new AiChatSession { UserId = userId, Title = "新对话" };
            session.SessionId = await Context.Insertable(session).ExecuteReturnSnowflakeIdAsync();
            return new SysAiChatSessionDto
            {
                SessionId = session.SessionId,
                Title = session.Title,
                CreateTime = session.CreateTime
            };
        }

        /// <summary>
        /// 重命名会话
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="userId">用户ID</param>
        /// <param name="title">新的会话标题</param>
        /// <returns>受影响的行数</returns>
        /// <exception cref="Exception"></exception>
        public async Task<int> RenameSessionAsync(long sessionId, long userId, string title)
        {
            var rows = await UpdateAsync(
                s => s.SessionId == sessionId && s.UserId == userId,
                s => new AiChatSession { Title = title, UpdateTime = DateTime.Now });
            if (rows <= 0)
            {
                throw new Exception("会话不存在或无权操作");
            }
            return rows;
        }

        /// <summary>
        /// 删除会话及其消息
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="userId">用户ID</param>
        /// <returns>受影响的行数</returns>
        public async Task<int> DeleteSessionAsync(long sessionId, long userId)
        {
            // 先删消息，再删会话；仅允许操作本人会话
            await Context.Deleteable<AiChatMessage>()
                .Where(m => m.SessionId == sessionId && m.UserId == userId)
                .ExecuteCommandAsync();
            var rows = await Context.Deleteable<AiChatSession>()
                .Where(m => m.SessionId == sessionId && m.UserId == userId)
                .ExecuteCommandAsync();
            if (rows <= 0)
            {
                throw new Exception("会话不存在或无权操作");
            }
            return rows;
        }

        /// <summary>
        /// 获取会话的消息列表
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="userId">用户ID</param>
        /// <returns>会话详情</returns>
        /// <exception cref="Exception"></exception>
        public async Task<SysAiChatDetailDto> GetMessagesAsync(long sessionId, long userId)
        {
            var session = await Queryable()
                .Where(m => m.SessionId == sessionId && m.UserId == userId)
                .FirstAsync();
            if (session == null)
            {
                throw new Exception("会话不存在或无权查看");
            }
            var msgs = await Context.Queryable<AiChatMessage>()
                .Where(m => m.SessionId == sessionId && m.UserId == userId)
                .OrderBy(m => m.CreateTime, OrderByType.Asc)
                .ToListAsync();
            return new SysAiChatDetailDto
            {
                SessionId = session.SessionId,
                Title = session.Title,
                Model = session.Model,
                Messages = msgs.Select(m => new SysAiChatMessageDto
                {
                    MessageId = m.MessageId,
                    Role = m.Role,
                    Content = m.Content,
                    CreateTime = m.CreateTime
                }).ToList()
            };
        }

        #endregion 会话 CRUD

        #region 对话编排

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="userId">用户ID</param>
        /// <param name="message">消息内容</param>
        /// <returns>聊天结果</returns>
        /// <exception cref="Exception"></exception>
        public async Task<SysAiChatResultDto> ChatAsync(long sessionId, long userId, string message)
        {
            message = (message ?? "").Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new Exception("消息内容不能为空");
            }
            var options = AiHelper.EnsureAiEnabled();

            // 1. 会话归属
            var isNewSession = sessionId <= 0;
            AiChatSession session;
            if (isNewSession)
            {
                session = new AiChatSession { UserId = userId, Title = "新对话" };
                session.SessionId = await Context.Insertable(session).ExecuteReturnSnowflakeIdAsync();
            }
            else
            {
                session = await Queryable()
                    .Where(m => m.SessionId == sessionId && m.UserId == userId)
                    .FirstAsync() ?? throw new Exception("会话不存在或无权访问");
            }

            // 2. 组装消息（系统提示 + 历史 + 当前提问）
            var resolved = AiLlmClient.ResolveProvider(options);
            var model = string.IsNullOrWhiteSpace(resolved.Model) ? options.Model : resolved.Model;
            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt(userId) }
            };

            var history = await Context.Queryable<AiChatMessage>()
                .Where(m => m.SessionId == session.SessionId && m.UserId == userId && m.Role != "tool")
                .OrderBy(m => m.CreateTime, OrderByType.Desc)
                .Take(MaxHistoryMessages)
                .ToListAsync();
            history.Reverse();// 历史按时间正序回灌
            foreach (var h in history)
            {
                messages.Add(new { role = h.Role, content = AiHelper.ClipText(h.Content, 2000) });
            }
            messages.Add(new { role = "user", content = message });

            // 3. 工具调用编排
            var tools = BuildToolObjects();
            string reply = "";
            var roundDiag = new List<string>();
            for (var round = 0; round < MaxToolRounds; round++)
            {
                AiLlmClient.ChatToolResult turn;
                try
                {
                    turn = await AiLlmClient.ChatWithToolsAsync(options, messages.ToArray(), tools);
                }
                catch (Exception ex)
                {
                    // 详情只写后端日志；异常上抛，由上层返回友好提示，不让内部细节透出到前端
                    _logger.Error($"AI 模型调用异常 sessionId={session.SessionId} userId={userId} model={model} msg={AiHelper.ClipText(message, 200)} err={ex}");
                    throw;
                }
                var contentLen = turn.Content?.Length ?? 0;
                var toolNames = turn.ToolCalls?.Select(x => x.Name) ?? new List<string>();
                roundDiag.Add($"r{round + 1}:finish={turn.FinishReason ?? "null"},contentLen={contentLen},tools=[{string.Join(",", toolNames)}]");

                if (turn.ToolCalls == null || turn.ToolCalls.Count == 0)
                {
                    reply = turn.Content ?? "";
                    break;
                }
                // 记录 assistant 的工具调用请求
                messages.Add(new
                {
                    role = "assistant",
                    content = turn.Content ?? "",
                    tool_calls = turn.ToolCalls.Select(c => new
                    {
                        id = c.Id,
                        type = "function",
                        function = new { name = c.Name, arguments = c.Arguments }
                    }).ToArray()
                });
                // 逐条执行工具并把结果回灌
                foreach (var call in turn.ToolCalls)
                {
                    AiToolExecResult exec;
                    try
                    {
                        exec = await ExecuteToolAsync(call.Name, call.Arguments, userId);
                    }
                    catch (Exception ex)
                    {
                        // 详情只写后端日志便于排查；回灌给模型的是通用失败提示，
                        // 避免把内部异常细节经模型转述暴露给前端。
                        _logger.Error($"AI 工具执行异常 tool={call.Name} args={AiHelper.ClipText(call.Arguments ?? "", 300)} err={ex}");
                        exec = AiToolExecResult.Error("该操作执行失败，请告知用户稍后重试");
                    }
                    var content = exec.Content ?? "";
                    if (!exec.Ok)
                    {
                        content = $"[错误] {content}";
                    }
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = AiHelper.ClipText(content, ToolResultMaxLen) });
                }
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                _logger.Error($"AI 回复为空(触发兜底文案) userId={userId} sessionId={session.SessionId} model={model} " +
                    $"msg={AiHelper.ClipText(message, 200)} history={history.Count} rounds={string.Join(" | ", roundDiag)}");
                reply = "抱歉，我这边没有正常生成回答，请重新描述一下你的问题。";
            }

            // 4. 落库 + 会话元信息维护
            await SaveMessageAsync(session.SessionId, userId, "user", message, model);
            await SaveMessageAsync(session.SessionId, userId, "assistant", reply, model);

            var needAutoTitle = session.Title.IsNullOrEmpty() || session.Title == "新对话";
            var newTitle = session.Title;
            if (needAutoTitle)
            {
                newTitle = AiHelper.AutoTitle(message);
            }
            await UpdateAsync(s => s.SessionId == session.SessionId,
                s => new AiChatSession { Title = newTitle, UpdateTime = DateTime.Now });

            return new SysAiChatResultDto
            {
                SessionId = session.SessionId,
                Title = newTitle,
                Model = model,
                Reply = reply,
                IsNewSession = isNewSession
            };
        }

        private async Task SaveMessageAsync(long sessionId, long userId, string role, string content, string model)
        {
            var msg = new AiChatMessage
            {
                SessionId = sessionId,
                UserId = userId,
                Role = role,
                MsgType = "text",
                Content = content ?? "",
                Model = model
            };
            msg.MessageId = await Context.Insertable(msg).ExecuteReturnSnowflakeIdAsync();
        }

        #endregion 对话编排

        #region 工具定义与执行

        private object[] BuildToolObjects()
        {
            var tools = new List<object>();

            void Add(AiToolDef def)
            {
                tools.Add(new
                {
                    type = "function",
                    function = new { name = def.Name, description = def.Description, parameters = def.Parameters }
                });
            }

            Add(new AiToolDef
            {
                Name = "query_my_schedules",
                Description = "查询当前登录用户指定日期范围内的日程安排（按截止/完成/创建时间任一命中，含已完成）。不传参数默认查最近 7 天。用于“我今天有什么安排/本周日程/某天日程”。",
                Parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["startDate"] = new { type = "string", description = "开始日期，格式 yyyy-MM-dd，可省略" },
                        ["endDate"] = new { type = "string", description = "结束日期（含当天），格式 yyyy-MM-dd，可省略" }
                    },
                    required = Array.Empty<string>()
                }
            });
            Add(new AiToolDef
            {
                Name = "parse_schedule",
                Description = "把用户口语化的日程描述解析成结构化日程草稿（标题/内容/优先级/截止/提醒）。只生成草稿不落库。用于“帮我记一个日程：...”等表达。",
                Parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["text"] = new { type = "string", description = "用户原始口语描述" }
                    },
                    required = new[] { "text" }
                }
            });
            Add(new AiToolDef
            {
                Name = "generate_weekly_report",
                Description = "汇总当前用户本周日程自动生成工作周报草稿（Markdown）。用于“帮我写周报/本周总结”。",
                Parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            });

            if (_toolProviders != null)
            {
                foreach (var provider in _toolProviders)
                {
                    var defs = provider.GetToolDefs();
                    if (defs == null) continue;
                    foreach (var def in defs)
                    {
                        Add(def);
                    }
                }
            }
            return tools.ToArray();
        }

        /// <summary>
        /// 执行指定工具
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数</param>
        /// <param name="userId">用户ID</param>
        /// <returns>工具执行结果</returns>
        private async Task<AiToolExecResult> ExecuteToolAsync(string toolName, string arguments, long userId)
        {
            var args = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments);
            switch (toolName)
            {
                case "query_my_schedules":
                    return await QueryMySchedulesAsync(args, userId);
                case "parse_schedule":
                    return await ParseScheduleAsync(args);
                case "generate_weekly_report":
                    return await GenerateWeeklyReportAsync(userId);
            }

            if (_toolProviders != null)
            {
                foreach (var provider in _toolProviders)
                {
                    var exec = await provider.ExecuteAsync(toolName, arguments, userId);
                    if (exec != null)
                    {
                        return exec;
                    }
                }
            }
            return AiToolExecResult.Error($"未知工具：{toolName}");
        }

        /// <summary>
        /// 查询本人日程
        /// </summary>
        /// <param name="args">查询参数</param>
        /// <param name="userId">用户ID</param>
        /// <returns>工具执行结果</returns>
        private async Task<AiToolExecResult> QueryMySchedulesAsync(JObject args, long userId)
        {
            var startDate = AiHelper.TryParseDate(args["startDate"]?.Value<string>()) ?? DateTime.Today;
            var endDate = AiHelper.TryParseDate(args["endDate"]?.Value<string>()) ?? startDate.AddDays(6);
            endDate = endDate.Date.AddDays(1).AddSeconds(-1);

            var list = await _scheduleService.GetByDateRangeAsync(userId, startDate, endDate);
            if (list == null || list.Count == 0)
            {
                return AiToolExecResult.Success(
                    $"在 {startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd} 期间未查询到日程安排。");
            }
            var lines = list.Take(50).Select(s =>
            {
                var status = s.Status == "1" ? "已完成" : "未完成";
                var pri = s.Priority switch { 1 => "低", 3 => "高", _ => "中" };
                var due = s.DueTime?.ToString("MM-dd HH:mm") ?? "无截止";
                var content = string.IsNullOrWhiteSpace(s.Content) ? "" : $" | {AiHelper.ClipText(s.Content, 120)}";
                return $"- [{status}][优先级{pri}] {s.Title}（截止 {due}）{content}";
            });
            var tail = list.Count > 50 ? $"\n（共 {list.Count} 条，仅显示前 50 条）" : "";
            return AiToolExecResult.Success(
                $"查询到 {list.Count} 条日程：\n" + string.Join("\n", lines) + tail);
        }

        /// <summary>
        /// 日程口语解析（草稿，不落库）
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task<AiToolExecResult> ParseScheduleAsync(JObject args)
        {
            var text = args["text"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return AiToolExecResult.Error("缺少待解析的日程描述 text");
            }
            var result = await _sysAi.ParseScheduleAsync(new SysAiScheduleParseInput { Text = text });
            var pri = result.Priority switch { 1 => "低", 3 => "高", _ => "中" };
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"日程草稿解析完成（未保存，展示给用户确认即可）：");
            sb.AppendLine($"- 标题：{result.Title}");
            if (!string.IsNullOrWhiteSpace(result.Content)) sb.AppendLine($"- 内容：{result.Content}");
            sb.AppendLine($"- 优先级：{result.Priority}({pri})");
            if (!string.IsNullOrWhiteSpace(result.DueTime)) sb.AppendLine($"- 截止时间：{result.DueTime}");
            if (!string.IsNullOrWhiteSpace(result.ReminderTime)) sb.AppendLine($"- 提醒时间：{result.ReminderTime}");
            if (!string.IsNullOrWhiteSpace(result.Warnings)) sb.AppendLine($"- 说明：{result.Warnings}");
            return AiToolExecResult.Success(sb.ToString());
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        private async Task<AiToolExecResult> GenerateWeeklyReportAsync(long userId)
        {
            var result = await _sysAi.GenerateWeeklyReportAsync(new SysAiWeeklyReportInput(), userId);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"本周周报已生成（统计周期 {result.PeriodStart} ~ {result.PeriodEnd}，共 {result.Total} 条日程）：");
            if (!string.IsNullOrWhiteSpace(result.Summary)) sb.AppendLine($"\n【整体情况】\n{result.Summary}");
            if (result.Completed.Count > 0) sb.AppendLine($"\n【已完成】\n" + string.Join("\n", result.Completed.Select(x => $"- {x}")));
            if (result.Pending.Count > 0) sb.AppendLine($"\n【未完成/待办】\n" + string.Join("\n", result.Pending.Select(x => $"- {x}")));
            if (result.Risks.Count > 0) sb.AppendLine($"\n【风险提示】\n" + string.Join("\n", result.Risks.Select(x => $"- {x}")));
            return AiToolExecResult.Success(sb.ToString());
        }

        #endregion 工具定义与执行

        #region helpers

        private static string BuildSystemPrompt(long userId)
        {
            var text = AiHelper.LoadPrompt("system/ai-chat-system.md");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception("AI 助手提示词文件缺失：system/ai-chat-system.md（请检查 Prompts 目录）");
            }
            return text
                .Replace("{{now}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                .Replace("{{userId}}", userId.ToString());
        }

        #endregion helpers
    }
}
