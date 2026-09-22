using Infrastructure.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.AI
{
    /// <summary>
    /// 轻量 LLM 客户端：封装 OpenAI 兼容 chat/completions 调用，仅依赖 Infrastructure，
    /// 避免业务层反向引用 ZR.Service。URL 拼装 / 响应读取 / 错误解析逻辑对齐 ZR.Service.AI.AiHelper。
    /// </summary>
    public static class AiLlmClient
    {
        private static readonly ILogger Logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("Infrastructure.AI.AiLlmClient");

        public static Uri BuildRequestUri(AiOptions options)
        {
            return BuildRequestUriWith(options, ResolveProvider(options));
        }

        /// <summary>
        /// 基于已解析的 provider 元组构造聊天接口 URI。多模态场景传入已解析的视觉 provider，
        /// 实现文本/视觉模型指向不同端点。
        /// </summary>
        private static Uri BuildRequestUriWith(AiOptions options, (string Provider, string BaseUrl, string ChatEndpoint, string Model, string ApiKey) resolved)
        {
            var provider = resolved.Provider;
            var baseUrl = (resolved.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            var endpoint = (resolved.ChatEndpoint ?? string.Empty).Trim();

            if (!endpoint.StartsWith("/"))
            {
                endpoint = "/" + endpoint;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = GetDefaultBaseUrl(provider);
            }

            return new Uri(new Uri(baseUrl + "/"), endpoint.TrimStart('/'));
        }

        /// <summary>
        /// 发起一次非流式对话，返回模型文本回复。timeout 由 options.TimeoutSeconds 控制。
        /// 优先从 Providers 数组匹配当前 Provider 取配置，空字段回退顶层与硬编码默认值。
        /// </summary>
        public static async Task<string> ChatAsync(AiOptions options, string systemPrompt, string userPrompt, string scene = null)
        {
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            };
            return await ChatCoreAsync(options, messages, scene: scene).ConfigureAwait(false);
        }

        /// <summary>
        /// 多轮对话重载：直接传入完整 messages 数组（含 system / user / assistant 历史），
        /// 用于校验失败后的纠错重试（self-correction）等需要回灌上下文的场景。
        /// </summary>
        public static async Task<string> ChatWithMessagesAsync(AiOptions options, object[] messages, string scene = null)
        {
            return await ChatCoreAsync(options, messages, scene: scene).ConfigureAwait(false);
        }

        /// <summary>
        /// ChatAsync / ChatWithMessagesAsync 共用的请求核心：拼装负载、POST、解析 content、记录 token 用量。
        /// 非标准 JSON 响应按纯文本兜底返回。
        /// resolvedOverride 非空时（多模态场景）覆盖 model/baseUrl/endpoint，实现文本与视觉模型解耦。
        /// </summary>
        private static async Task<string> ChatCoreAsync(AiOptions options, object messages,
            (string Provider, string BaseUrl, string ChatEndpoint, string Model, string ApiKey)? resolvedOverride = null,
            string scene = null, bool skipThinking = false)
        {
            var resolved = resolvedOverride ?? ResolveProvider(options);
            var uri = BuildRequestUriWith(options, resolved);
            var payload = new Dictionary<string, object>
            {
                ["model"] = resolved.Model,
                ["messages"] = messages,
                ["temperature"] = options.Temperature,
                ["max_tokens"] = options.MaxTokens,
                ["stream"] = false
            };
            ApplyThinkingOptions(payload, resolved.Provider, resolved.Model, options.EnableThinking, skipThinking);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + (resolved.ApiKey ?? string.Empty)
            };

            var lease = await BeginGovernedCallAsync(scene, resolved.Provider, resolved.Model, json, options.MaxTokens, false);
            AiCallOutcome outcome = null;
            try
            {
                var response = await HttpHelper.HttpPostDetailedAsync(
                    uri.ToString(), json, "application/json", ResolveTimeoutSeconds(options, resolved.Provider), headers).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var errDetail = BuildAiErrorMessage(response.StatusCode, response.Content);
                    outcome = Failed("http", "http_error", errDetail, response.StatusCode, response.RequestId);
                    Logger.LogError(
                        "AI 请求失败 status={Status} uri={Uri} provider={Provider} model={Model} body={Body}",
                        response.StatusCode, uri, resolved.Provider, resolved.Model, TruncateForLog(response.Content, 800));
                    throw new HttpRequestException(
                        $"AI 服务调用失败（HTTP {response.StatusCode}，model={resolved.Model}）：{TruncateForLog(errDetail, 240)}");
                }
                if (string.IsNullOrWhiteSpace(response.Content))
                {
                    outcome = Failed("empty", "empty_response", "AI 服务返回空响应",
                        response.StatusCode, response.RequestId);
                    return string.Empty;
                }

                try
                {
                    using var doc = JsonDocument.Parse(response.Content);
                    if (HasProviderError(doc.RootElement))
                    {
                        outcome = Failed("provider", "provider_error",
                            TryReadProviderErrorMessage(response.Content), response.StatusCode, response.RequestId);
                        EnsureNoProviderError(response.Content, doc.RootElement);
                    }
                    var content = ReadContent(doc.RootElement);
                    var usage = ReadUsageTokens(doc.RootElement, resolved.Provider);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        outcome = Failed("empty", "unusable_response", "AI 服务响应无可用内容",
                            response.StatusCode, response.RequestId);
                        outcome.PromptTokens = usage.Prompt;
                        outcome.CompletionTokens = usage.Completion;
                        outcome.TotalTokens = usage.Total > 0 ? usage.Total : usage.Prompt + usage.Completion;
                        outcome.HasUsage = usage.Prompt > 0 || usage.Completion > 0 || usage.Total > 0;
                        throw new HttpRequestException("AI 服务返回了无法解析的响应，请稍后重试或联系管理员");
                    }
                    outcome = Succeeded(usage, response.StatusCode, response.RequestId);
                    return content;
                }
                catch (JsonException ex)
                {
                    Logger.LogError(ex, "解析 AI 响应失败，按纯文本处理");
                    outcome = Succeeded((0, 0, 0), response.StatusCode, response.RequestId, hasUsage: false);
                    return response.Content;
                }
            }
            catch (OperationCanceledException ex)
            {
                outcome ??= Failed("timeout", "timeout", ex.Message);
                throw new HttpRequestException("AI 服务响应超时，请稍后重试", ex);
            }
            catch (Exception ex)
            {
                outcome ??= Failed("failed", ClassifyError(ex), ex.Message);
                throw;
            }
            finally
            {
                await CompleteGovernedCallAsync(lease, outcome);
            }
        }

        /// <summary>
        /// 按当前 Provider 从 Providers 数组匹配，覆盖顶层空字段；未命中则保持顶层值。
        /// 返回解析后用于实际请求的 provider/baseUrl/endpoint/model/apiKey。
        /// 公开供调用方做配置有效性校验，保证校验路径与真实请求路径一致（避免顶层 ApiKey
        /// 为空但 Providers 分项已配置时被误判为未启用）。
        /// </summary>
        public static (string Provider, string BaseUrl, string ChatEndpoint, string Model, string ApiKey) ResolveProvider(AiOptions options)
        {
            var provider = (options.Provider ?? "openai").Trim().ToLowerInvariant();
            var baseUrl = (options.BaseUrl ?? string.Empty).Trim();
            var endpoint = (options.ChatEndpoint ?? string.Empty).Trim();
            var model = (options.Model ?? string.Empty).Trim();
            var apiKey = (options.ApiKey ?? string.Empty).Trim();

            var matched = (options.Providers ?? new List<AiProviderOptions>())
                .FirstOrDefault(p => string.Equals((p.Provider ?? string.Empty).Trim(), provider, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = (matched.BaseUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(endpoint)) endpoint = (matched.ChatEndpoint ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(model)) model = (matched.Model ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiKey)) apiKey = (matched.ApiKey ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = GetDefaultBaseUrl(provider);
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = GetDefaultEndpoint(provider);
            if (string.IsNullOrWhiteSpace(model)) model = GetDefaultModel(provider);
            Logger.LogInformation("解析 AI provider: {Provider}, baseUrl={BaseUrl}, endpoint={Endpoint}, model={Model}", provider, baseUrl, endpoint, model);
            return (provider, baseUrl, endpoint, model, apiKey);
        }

        /// <summary>
        /// 千问(DashScope)兼容 OpenAI 协议时 usage 字段名为 input_tokens/output_tokens，
        /// 与 OpenAI 标准的 prompt_tokens/completion_tokens 不同；deepseek 等仍用标准字段。
        /// 目标字段缺失时回退读取另一套字段名；仍缺失按 0。
        /// </summary>
        private static (int Prompt, int Completion, int Total) ReadUsageTokens(JsonElement root, string provider)
        {
            var isQwen = string.Equals((provider ?? "").Trim(), "qwen", StringComparison.OrdinalIgnoreCase);
            var inputKey = isQwen ? "input_tokens" : "prompt_tokens";
            var outputKey = isQwen ? "output_tokens" : "completion_tokens";

            var promptTokens = ReadUsage(root, inputKey);
            var completionTokens = ReadUsage(root, outputKey);

            if (promptTokens == 0) promptTokens = ReadUsage(root, "prompt_tokens");
            if (completionTokens == 0) completionTokens = ReadUsage(root, "completion_tokens");

            var totalTokens = ReadUsage(root, "total_tokens");
            return (promptTokens, completionTokens, totalTokens);
        }

        /// <summary>
        /// 读取 usage 节点下指定 token 计数，缺字段返回 0。
        /// </summary>
        private static int ReadUsage(JsonElement root, string key)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            if (!usage.TryGetProperty(key, out var tokenEl))
            {
                return 0;
            }

            return tokenEl.TryGetInt32(out var value) ? value : 0;
        }

        /// <summary>
        /// 解析视觉模型配置。优先级：
        /// 顶层 Vision* 覆盖 → 目标 Provider 分项（VisionModel / BaseUrl / ApiKey）→ 该 Provider 默认地址。
        /// 目标 Provider = VisionProvider（若填）否则顶层 Provider。
        /// 不会把文本 Model 当成视觉模型，避免把图片发给不支持多模态的模型。
        /// </summary>
        public static (string Provider, string BaseUrl, string ChatEndpoint, string Model, string ApiKey) ResolveVisionProvider(AiOptions options)
        {
            var provider = (options.VisionProvider ?? options.Provider ?? "openai").Trim().ToLowerInvariant();
            var baseUrl = (options.VisionBaseUrl ?? string.Empty).Trim();
            var endpoint = (options.VisionChatEndpoint ?? string.Empty).Trim();
            var model = (options.VisionModel ?? string.Empty).Trim();
            var apiKey = (options.VisionApiKey ?? options.ApiKey ?? string.Empty).Trim();

            var matched = (options.Providers ?? new List<AiProviderOptions>())
                .FirstOrDefault(p => string.Equals((p.Provider ?? string.Empty).Trim(), provider, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = (matched.BaseUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(endpoint)) endpoint = (matched.ChatEndpoint ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(model)) model = (matched.VisionModel ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(apiKey)) apiKey = (matched.ApiKey ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = GetDefaultBaseUrl(provider);
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = GetDefaultEndpoint(provider);
            Logger.LogInformation("解析 AI 视觉 provider: {Provider}, baseUrl={BaseUrl}, endpoint={Endpoint}, model={Model}", provider, baseUrl, endpoint, model);
            return (provider, baseUrl, endpoint, model, apiKey);
        }

        /// <summary>
        /// 多模态对话：把文本提示与一组图片 URL 一并发送给支持视觉的模型（如 gpt-4o-mini）。
        /// 图片 URL 须为完整 http(s) 地址（由调用方确保已下载可达）。VisionModel 为空时抛友好异常。
        /// </summary>
        public static async Task<string> ChatWithImagesAsync(AiOptions options, string systemPrompt, string textPrompt, List<string> imageUrls, string scene = null)
        {
            var resolved = ResolveVisionProvider(options);
            if (string.IsNullOrWhiteSpace(resolved.Model))
            {
                throw new InvalidOperationException("未配置视觉模型。请在当前 Provider 的 VisionModel（或顶层 AiOptions:VisionModel）中指定支持多模态的模型，如 qwen-vl-plus / gpt-4o-mini。");
            }

            var content = new List<object>
            {
                new { type = "text", text = textPrompt }
            };
            foreach (var url in imageUrls ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                content.Add(new { type = "image_url", image_url = new { url } });
            }

            var messages = new[]
            {
                new { role = "system", content = (object)systemPrompt },
                new { role = "user", content = (object)content }
            };
            return await ChatCoreAsync(options, messages, resolved, scene, skipThinking: true).ConfigureAwait(false);
        }

        /// <summary>
        /// 单个工具调用（tool_calls 元素）。
        /// </summary>
        public class ToolCall
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Arguments { get; set; } // JSON 字符串
        }

        /// <summary>
        /// 单次 chat/completions（tools 模式）的返回：文本内容与待执行的工具调用。
        /// 当 ToolCalls 非空时，上层应执行工具并把结果以 role=tool 回灌，再发起下一轮。
        /// </summary>
        public class ChatToolResult
        {
            /// <summary>模型最终文本（finish 时可能为草稿 JSON 或说明）</summary>
            public string Content { get; set; }

            /// <summary>本轮模型请求执行的工具调用；为空表示模型已直接给出最终结果</summary>
            public List<ToolCall> ToolCalls { get; set; } = new();

            /// <summary>终止原因：stop / tool_calls / length 等</summary>
            public string FinishReason { get; set; }

            /// <summary>本轮调用输入 token（prompt）；usage 缺失时为 0</summary>
            public int PromptTokens { get; set; }

            /// <summary>本轮调用输出 token（completion）；usage 缺失时为 0</summary>
            public int CompletionTokens { get; set; }

            /// <summary>本轮调用合计 token；usage 缺失时为 0</summary>
            public int TotalTokens { get; set; }
        }

        /// <summary>
        /// 带 tools 的多轮对话：支持 OpenAI 兼容 function calling / tool use。
        /// messages 为完整消息数组（调用方持有并维护，含 system/user/assistant/tool）。
        /// tools 为 function 描述数组；模型可请求调用，返回 ToolCalls 后由调用方执行回灌。
        /// 本方法不自动回灌，仅完成单次 HTTP 请求与解析，便于上层控制纠错循环轮次。
        /// </summary>
        public static async Task<ChatToolResult> ChatWithToolsAsync(AiOptions options, object[] messages, object[] tools, string scene = null)
        {
            var resolved = ResolveProvider(options);
            var uri = BuildRequestUri(options);
            var payload = BuildChatPayload(options, resolved, messages, tools, stream: false);
            ApplyThinkingOptions(payload, resolved.Provider, resolved.Model, options.EnableThinking);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + (resolved.ApiKey ?? string.Empty)
            };

            var lease = await BeginGovernedCallAsync(scene, resolved.Provider, resolved.Model, json, options.MaxTokens, false);
            AiCallOutcome outcome = null;
            try
            {
                var response = await HttpHelper.HttpPostDetailedAsync(
                    uri.ToString(), json, "application/json", ResolveTimeoutSeconds(options, resolved.Provider), headers).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var errDetail = BuildAiErrorMessage(response.StatusCode, response.Content);
                    outcome = Failed("http", "http_error", errDetail, response.StatusCode, response.RequestId);
                    Logger.LogError(
                        "AI 请求失败 status={Status} uri={Uri} provider={Provider} model={Model} body={Body}",
                        response.StatusCode, uri, resolved.Provider, resolved.Model, TruncateForLog(response.Content, 800));
                    throw new HttpRequestException(
                        $"AI 服务调用失败（HTTP {response.StatusCode}，model={resolved.Model}）：{TruncateForLog(errDetail, 240)}");
                }
                if (string.IsNullOrWhiteSpace(response.Content))
                {
                    outcome = Failed("empty", "empty_response", "AI 服务返回空响应",
                        response.StatusCode, response.RequestId);
                    throw new HttpRequestException("AI 服务返回空响应，请稍后重试或联系管理员");
                }

                try
                {
                    using var doc = JsonDocument.Parse(response.Content);
                    if (HasProviderError(doc.RootElement))
                    {
                        outcome = Failed("provider", "provider_error",
                            TryReadProviderErrorMessage(response.Content), response.StatusCode, response.RequestId);
                        EnsureNoProviderError(response.Content, doc.RootElement);
                    }
                    var result = ReadToolResult(doc.RootElement);
                    var usage = ReadUsageTokens(doc.RootElement, resolved.Provider);
                    result.PromptTokens = usage.Prompt;
                    result.CompletionTokens = usage.Completion;
                    result.TotalTokens = usage.Total;

                    if (string.IsNullOrWhiteSpace(result.Content) && (result.ToolCalls == null || result.ToolCalls.Count == 0))
                    {
                        outcome = Failed("parse", "unusable_response", "AI 服务响应无可用内容",
                            response.StatusCode, response.RequestId);
                        Logger.LogError("AI 服务响应无可用内容（无 content 且无 tool_calls）。原始响应={Response}, uri={Uri}, model={Model}",
                            TruncateForLog(response.Content, 600), uri, resolved.Model);
                        throw new HttpRequestException("AI 服务返回了无法解析的响应，请稍后重试或联系管理员");
                    }
                    outcome = Succeeded(usage, response.StatusCode, response.RequestId);
                    return result;
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "解析 AI tool 响应失败，按纯文本处理");
                    outcome = Succeeded((0, 0, 0), response.StatusCode, response.RequestId, hasUsage: false);
                    return new ChatToolResult { Content = response.Content };
                }
            }
            catch (OperationCanceledException ex)
            {
                outcome ??= Failed("timeout", "timeout", ex.Message);
                throw new HttpRequestException("AI 服务响应超时，请稍后重试", ex);
            }
            catch (Exception ex)
            {
                outcome ??= Failed("failed", ClassifyError(ex), ex.Message);
                throw;
            }
            finally
            {
                await CompleteGovernedCallAsync(lease, outcome);
            }
        }

        /// <summary>
        /// 流式(SSE)返回事件：delta=本轮文本增量（供打字机实时展示）；
        /// finish=本轮结束，Result 为与 ChatWithToolsAsync 同构的聚合结果。
        /// </summary>
        public sealed class AiStreamChunk
        {
            public string Type { get; set; }
            public string Text { get; set; }
            public ChatToolResult Result { get; set; }
        }

        /// <summary>
        /// 流式(SSE)版 ChatWithToolsAsync：OpenAI 兼容 stream=true 的"单轮"对话。
        /// 上层自行控制纠错轮次与工具结果回灌（每轮迭代到 finish 后判断 ToolCalls）。
        /// 迭代中逐块 yield delta；HTTP/解析/服务错误直接抛 HttpRequestException，由上层决定展示策略。
        /// <paramref name="messages"/> 为完整消息数组（含 system/user/assistant/tool），<paramref name="tools"/> 为 function 描述数组。
        /// <paramref name="options"/> 控制模型、温度、token 限制、超时等；<paramref name="scene"/> 用于调用治理。
        /// <paramref name="tools"/>
        /// <paramref name="options"/>
        /// </summary>
        public static async IAsyncEnumerable<AiStreamChunk> StreamChatWithToolsAsync(AiOptions options, object[] messages, object[] tools, string scene = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = ResolveProvider(options);
            var uri = BuildRequestUri(options);
            var payload = BuildChatPayload(options, resolved, messages, tools, stream: true);
            ApplyThinkingOptions(payload, resolved.Provider, resolved.Model, options.EnableThinking);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + (resolved.ApiKey ?? string.Empty)
            };

            var lease = await BeginGovernedCallAsync(scene, resolved.Provider, resolved.Model, json, options.MaxTokens, true);
            AiCallOutcome outcome = null;
            try
            {
                using var streamResp = await HttpHelper.HttpPostReadStreamAsync(uri.ToString(), json, "application/json", ResolveTimeoutSeconds(options, resolved.Provider), headers);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(streamResp.Token, cancellationToken);
                var token = linkedCts.Token;

                var response = streamResp.Response;
                var providerRequestId = HttpHelper.ReadRequestId(response);
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(token);
                    outcome = Failed("http", "http_error", TryReadProviderErrorMessage(errBody),
                        (int)response.StatusCode, providerRequestId);
                    Logger.LogError("AI 流式请求失败 status={Status} uri={Uri} model={Model} body={Body}",
                        (int)response.StatusCode, uri, resolved.Model, TruncateForLog(errBody, 400));
                    throw new HttpRequestException("AI 服务调用失败，请稍后重试或检查 AI 配置");
                }

                var content = new StringBuilder();
                string finishReason = null;
                var toolAcc = new Dictionary<int, ToolCallBuilder>();
                var promptTokens = 0;
                var completionTokens = 0;
                var totalTokens = 0;

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[8192];
                var lineBytes = new MemoryStream();
                var done = false;
                while (!done)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            outcome = Failed("cancelled", "client_cancelled", "客户端取消了 AI 流式请求",
                                (int)response.StatusCode, providerRequestId);
                            throw;
                        }
                        outcome = Failed("timeout", "timeout", "AI 流式响应超时",
                            (int)response.StatusCode, providerRequestId);
                        throw new HttpRequestException("AI 流式响应超时，连接已中断，请重试");
                    }
                    if (read <= 0) break;

                    for (var i = 0; i < read && !done; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            var line = Encoding.UTF8.GetString(lineBytes.GetBuffer(), 0, (int)lineBytes.Length).Trim();
                            lineBytes.SetLength(0);
                            if (line.Length == 0) continue;

                            var data = line;
                            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) data = data.Substring(5).Trim();
                            if (data.Length == 0 || data.StartsWith(":")) continue;
                            if (data == "[DONE]")
                            {
                                done = true;
                                break;
                            }

                            if (data.StartsWith("{") || data.StartsWith("["))
                            {
                                JsonDocument doc;
                                try
                                {
                                    doc = JsonDocument.Parse(data);
                                }
                                catch (JsonException)
                                {
                                    // 个别不完整块忽略，等待后续行
                                    continue;
                                }
                                using (doc)
                                {
                                    var root = doc.RootElement;
                                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
                                    {
                                        var msg = TryReadProviderErrorMessage(data);
                                        outcome = Failed("provider", "provider_error", msg,
                                            (int)response.StatusCode, providerRequestId);
                                        Logger.LogError("AI 流式返回错误：{Message}", msg);
                                        throw new HttpRequestException("AI 服务调用失败，请稍后重试或检查 AI 配置");
                                    }

                                    // usage 可能在最后一块与 choices 同场，或独立成块；后到覆盖
                                    var (p, c, t) = ReadUsageTokens(root, resolved.Provider);
                                    if (p > 0) promptTokens = p;
                                    if (c > 0) completionTokens = c;
                                    if (t > 0) totalTokens = t;

                                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                                    {
                                        continue;
                                    }
                                    var choice = choices[0];

                                    if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                                    {
                                        var frVal = frEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(frVal)) finishReason = frVal;
                                    }

                                    if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                                    {
                                        continue;
                                    }

                                    if (delta.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                                    {
                                        var txt = cEl.GetString() ?? string.Empty;
                                        if (txt.Length > 0)
                                        {
                                            content.Append(txt);
                                            yield return new AiStreamChunk { Type = "delta", Text = txt };
                                        }
                                    }

                                    if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tc in tcs.EnumerateArray())
                                        {
                                            var idx = 0;
                                            if (tc.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idxVal)) idx = idxVal;
                                            if (!toolAcc.TryGetValue(idx, out var acc))
                                            {
                                                acc = new ToolCallBuilder();
                                                toolAcc[idx] = acc;
                                            }
                                            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                            {
                                                var idVal = idEl.GetString();
                                                if (!string.IsNullOrWhiteSpace(idVal)) acc.Id = idVal;
                                            }
                                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                                            {
                                                // OpenAI 规范：function.name 仅出现在首个 fragment，且为完整名
                                                if (string.IsNullOrEmpty(acc.Name)
                                                    && fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                                                {
                                                    var nameVal = nEl.GetString();
                                                    if (!string.IsNullOrWhiteSpace(nameVal))
                                                    {
                                                        acc.Name = nameVal;
                                                        yield return new AiStreamChunk { Type = "tool", Text = nameVal };
                                                    }
                                                }
                                                if (fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                                                {
                                                    acc.Args.Append(aEl.GetString());
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                content.Append(data);
                                yield return new AiStreamChunk { Type = "delta", Text = data };
                            }
                        }
                        else
                        {
                            lineBytes.WriteByte(buffer[i]);
                        }
                    }
                }

                if (!done && string.IsNullOrWhiteSpace(finishReason))
                {
                    outcome = Failed("failed", "stream_interrupted", "AI 流式连接未正常结束",
                        (int)response.StatusCode, providerRequestId);
                    outcome.PromptTokens = promptTokens;
                    outcome.CompletionTokens = completionTokens;
                    outcome.TotalTokens = totalTokens > 0 ? totalTokens : promptTokens + completionTokens;
                    outcome.HasUsage = promptTokens > 0 || completionTokens > 0 || totalTokens > 0;
                    throw new HttpRequestException("AI 流式响应中断，请重试");
                }

                var toolCalls = toolAcc.Values
                    .Where(b => !string.IsNullOrWhiteSpace(b.Name) || b.Args.Length > 0)
                    .Select(b => new ToolCall { Id = b.Id, Name = b.Name, Arguments = b.Args.ToString() })
                    .ToList();

                Logger.LogInformation("AI token usage [provider={Provider}, model={Model}, scene={Scene}] 输入(prompt)={PromptTokens} 输出(completion)={CompletionTokens} 合计(total)={TotalTokens}",
                    resolved.Provider, resolved.Model, scene, promptTokens, completionTokens, totalTokens);

                var result = new ChatToolResult
                {
                    Content = content.ToString(),
                    ToolCalls = toolCalls,
                    FinishReason = finishReason,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens
                };

                if (string.IsNullOrWhiteSpace(result.Content) && (result.ToolCalls == null || result.ToolCalls.Count == 0))
                {
                    outcome = Failed("parse", "unusable_response", "AI 服务响应无可用内容",
                        (int)response.StatusCode, providerRequestId);
                    Logger.LogError("AI 流式响应无可用内容（无 content 且无 tool_calls）。uri={Uri}, model={Model}", uri, resolved.Model);
                    throw new HttpRequestException("AI 服务返回了无法解析的响应，请稍后重试或联系管理员");
                }
                outcome = Succeeded((promptTokens, completionTokens, totalTokens),
                    (int)response.StatusCode, providerRequestId,
                    promptTokens > 0 || completionTokens > 0 || totalTokens > 0);
                yield return new AiStreamChunk { Type = "finish", Result = result };
            }
            finally
            {
                outcome ??= Failed(cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                    cancellationToken.IsCancellationRequested ? "client_cancelled" : "stream_interrupted",
                    cancellationToken.IsCancellationRequested ? "客户端取消了 AI 流式请求" : "AI 流式请求未正常完成");
                await CompleteGovernedCallAsync(lease, outcome);
            }
        }

        /// <summary>
        /// 流式 tool_calls 分片累积器：OpenAI 兼容 SSE 按 index 分片推送，name/arguments 可能跨块拼接。
        /// </summary>
        private sealed class ToolCallBuilder
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public readonly StringBuilder Args = new StringBuilder();
        }

        /// <summary>
        /// 开始治理调用：若 governance 为 null 则返回 null。
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="provider"></param>
        /// <param name="model"></param>
        /// <param name="payload"></param>
        /// <param name="maxTokens"></param>
        /// <param name="isStream"></param>
        /// <returns></returns>
        private static async Task<AiCallLease> BeginGovernedCallAsync(
            string scene, string provider, string model, string payload, int maxTokens, bool isStream)
        {
            var governance = App.GetService<IAiCallGovernance>();
            if (governance == null) return null;
            return await governance.BeginAsync(new AiCallRequest
            {
                Scene = scene,
                Provider = provider,
                Model = model,
                EstimatedPromptTokens = EstimatePromptTokensForGovernance(payload),
                MaxCompletionTokens = Math.Max(0, maxTokens),
                IsStream = isStream
            });
        }

        /// <summary>
        /// 结算治理调用：无论成功/失败/异常，均尝试调用 CompleteAsync 结算；若 governance 为 null 则忽略。
        /// </summary>
        /// <param name="lease"></param>
        /// <param name="outcome"></param>
        /// <returns></returns>
        private static async Task CompleteGovernedCallAsync(AiCallLease lease, AiCallOutcome outcome)
        {
            if (lease == null) return;
            try
            {
                var governance = App.GetService<IAiCallGovernance>();
                if (governance != null)
                {
                    await governance.CompleteAsync(lease, outcome ?? Failed("failed", "unknown", "AI 调用未正常完成"));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "AI 调用治理结算失败 requestId={RequestId}", lease.RequestId);
            }
        }

        /// <summary>
        /// 构建成功的 AiCallOutcome，供 CompleteGovernedCallAsync 结算使用。
        /// </summary>
        /// <param name="usage"></param>
        /// <param name="statusCode"></param>
        /// <param name="requestId"></param>
        /// <param name="hasUsage"></param>
        /// <returns></returns>
        private static AiCallOutcome Succeeded(
            (int Prompt, int Completion, int Total) usage, int? statusCode, string requestId, bool? hasUsage = null) =>
            new()
            {
                Success = true,
                Status = "success",
                HttpStatusCode = statusCode,
                ProviderRequestId = requestId,
                PromptTokens = usage.Prompt,
                CompletionTokens = usage.Completion,
                TotalTokens = usage.Total > 0 ? usage.Total : usage.Prompt + usage.Completion,
                HasUsage = hasUsage ?? (usage.Prompt > 0 || usage.Completion > 0 || usage.Total > 0)
            };

        /// <summary>
        /// 构建失败的 AiCallOutcome，供 CompleteGovernedCallAsync 结算使用。
        /// </summary>
        /// <param name="status"></param>
        /// <param name="errorType"></param>
        /// <param name="message"></param>
        /// <param name="statusCode"></param>
        /// <param name="requestId"></param>
        /// <returns></returns>
        private static AiCallOutcome Failed(
            string status, string errorType, string message, int? statusCode = null, string requestId = null) =>
            new()
            {
                Success = false,
                Status = status,
                ErrorType = errorType,
                ErrorMessage = TruncateForLog(message, 1000),
                HttpStatusCode = statusCode,
                ProviderRequestId = requestId
            };

        private static bool HasProviderError(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _);

        private static string ClassifyError(Exception ex) => ex switch
        {
            AiGovernanceDeniedException denied => denied.Reason,
            JsonException => "parse_error",
            HttpRequestException => "http_request",
            _ => "unexpected"
        };

        private static string TruncateForLog(string text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…(截断)";
        }

        /// <summary>
        /// 从 OpenAI 兼容响应解析 tool-use 结果：message.content + message.tool_calls + finish_reason。
        /// </summary>
        private static ChatToolResult ReadToolResult(JsonElement root)
        {
            var result = new ChatToolResult();
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return result;
            }

            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                result.FinishReason = fr.GetString();
            }

            if (!firstChoice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            // 复用公共的 content 读取逻辑，避免与 ReadContent 重复
            result.Content = TryReadMessageContent(message);

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var call = new ToolCall();
                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        call.Id = id.GetString();
                    }
                    if (tc.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                    {
                        // 仅记录，OpenAI 当前仅 function
                        _ = type.GetString();
                    }
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            call.Name = name.GetString();
                        }
                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            call.Arguments = args.GetString();
                        }
                    }

                    result.ToolCalls.Add(call);
                }
            }

            return result;
        }

        /// <summary>
        /// 从 OpenAI 兼容响应解析 message.content 文本（message.content 为字符串时返回，否则空）。
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
        public static string ReadContent(JsonElement root)
        {
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            return TryReadMessageContent(message);
        }

        /// <summary>
        /// 从 message 节点读取 content 文本（message.content 为字符串时返回，否则空）。
        /// 同时被 ReadContent 与 ReadToolResult 复用。
        /// </summary>
        private static string TryReadMessageContent(JsonElement message)
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// 从 OpenAI 兼容响应解析 usage 节点，返回 prompt/completion/total token 数。
        /// </summary>
        /// <param name="statusCode"></param>
        /// <param name="responseText"></param>
        /// <returns></returns>
        public static string BuildAiErrorMessage(int statusCode, string responseText)
        {
            var detail = TryReadProviderErrorMessage(responseText);
            return string.IsNullOrWhiteSpace(detail)
                ? $"AI request failed({statusCode}): {responseText}"
                : $"AI request failed({statusCode}): {detail}";
        }

        private static string TryReadProviderErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorObj))
                {
                    if (errorObj.ValueKind == JsonValueKind.String)
                    {
                        return errorObj.GetString();
                    }

                    if (errorObj.ValueKind == JsonValueKind.Object && errorObj.TryGetProperty("message", out var msg))
                    {
                        return msg.GetString();
                    }
                }

                if (root.TryGetProperty("message", out var message))
                {
                    return message.GetString();
                }
            }
            catch
            {
                // 解析失败时返回原文
            }

            return responseText;
        }

        /// <summary>
        /// 响应若携带 provider 错误（如 OpenAI/千问 error 节点：配额耗尽、鉴权失败等），
        /// 提取 error.message 抛 HttpRequestException，避免被当作空内容静默吞掉难以排查。
        /// 无 error 节点时不做处理。
        /// <paramref name="responseText"/> 原始响应文本，用于日志记录与解析 error.message。
        /// <paramref name="root"/> JsonDocument 根节点，已解析的响应 JSON。
        /// </summary>
        private static void EnsureNoProviderError(string responseText, JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out _))
            {
                return;
            }

            var message = TryReadProviderErrorMessage(responseText);
            Logger.LogError("AI 服务返回错误响应：{Message}", message);
            throw new HttpRequestException("AI 服务调用失败，请稍后重试或检查 AI 配置");
        }

        /// <summary>
        /// 按 provider/model 判断是否写入 enable_thinking。
        /// 仅通义文本混合思考模型需要；看图请求或视觉模型（qwen-vl-* 等）带此字段常直接 400。
        /// </summary>
        private static void ApplyThinkingOptions(Dictionary<string, object> payload, string provider, string model, bool enableThinking, bool skipThinking = false)
        {
            if (skipThinking)
            {
                return;
            }
            if (!string.Equals((provider ?? "").Trim(), "qwen", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (IsVisionModel(model))
            {
                return;
            }
            payload["enable_thinking"] = enableThinking;
        }

        /// <summary>名称含 vl / vision 的视为视觉模型，不写 enable_thinking。</summary>
        private static bool IsVisionModel(string model)
        {
            var m = (model ?? "").Trim().ToLowerInvariant();
            return m.Contains("-vl") || m.Contains("vl-") || m.Contains("vision");
        }

        /// <summary>
        /// 组装 chat/completions 负载。Ollama 等本地兼容端对 OpenAI 扩展字段（stream_options）支持差，
        /// 多带会导致挂起或空响应；tools 为空时也不下发 tool_choice。
        /// </summary>
        private static Dictionary<string, object> BuildChatPayload(
            AiOptions options,
            (string Provider, string BaseUrl, string ChatEndpoint, string Model, string ApiKey) resolved,
            object[] messages,
            object[] tools,
            bool stream)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = resolved.Model,
                ["messages"] = messages,
                ["temperature"] = options.Temperature,
                ["max_tokens"] = options.MaxTokens,
                ["stream"] = stream
            };

            if (tools != null && tools.Length > 0)
            {
                payload["tools"] = tools;
                payload["tool_choice"] = "auto";
            }

            // stream_options.include_usage 仅 OpenAI 系可靠；Ollama 常不认导致长时间无分片
            if (stream && SupportsStreamUsageOptions(resolved.Provider))
            {
                payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
            }

            return payload;
        }

        private static bool SupportsStreamUsageOptions(string provider)
        {
            var p = (provider ?? "").Trim().ToLowerInvariant();
            return p is "openai" or "deepseek" or "qwen" or "bigmodel";
        }

        private static string GetDefaultBaseUrl(string provider)
        {
            return (provider ?? "openai").Trim().ToLowerInvariant() switch
            {
                "deepseek" => "https://api.deepseek.com",
                "qwen" => "https://dashscope.aliyuncs.com/compatible-mode/v1",
                "ollama" => "http://127.0.0.1:11434/v1",
                _ => "https://api.openai.com"
            };
        }

        private static string GetDefaultEndpoint(string provider)
        {
            return (provider ?? "openai").Trim().ToLowerInvariant() switch
            {
                "deepseek" => "/chat/completions",
                "qwen" => "/chat/completions",
                "ollama" => "/chat/completions",
                _ => "/v1/chat/completions"
            };
        }

        private static string GetDefaultModel(string provider)
        {
            return (provider ?? "openai").Trim().ToLowerInvariant() switch
            {
                "deepseek" => "deepseek-chat",
                "qwen" => "qwen-turbo",
                "ollama" => "qwen2.5:7b",
                _ => "gpt-4o-mini"
            };
        }

        /// <summary>Ollama 等本地服务不强制云厂商 ApiKey。</summary>
        public static bool AllowsEmptyApiKey(string provider)
        {
            return string.Equals((provider ?? "").Trim(), "ollama", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 治理预估 token：含 data URI 时按文件体积估算，避免把 Base64 字符数当成 prompt tokens 撑爆额度。
        /// </summary>
        private static int EstimatePromptTokensForGovernance(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return 0;
            // data:image/...;base64,XXXX —— Base64 约 4/3 原文件，视觉模型按图块计费，给固定上限即可
            const string marker = "data:image/";
            var idx = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return Math.Max(1, Math.Min(payload.Length / 4, 200_000));
            }
            var textLen = idx;
            var dataUriApproxBytes = Math.Max(0, (payload.Length - idx) * 3 / 4);
            var imageTokens = Math.Min(8_000, Math.Max(1_000, dataUriApproxBytes / 256));
            var textTokens = Math.Max(1, textLen / 4);
            return Math.Min(textTokens + imageTokens, 50_000);
        }

        /// <summary>分项 TimeoutSeconds 优先，否则用顶层，缺省 60 秒。</summary>
        public static int ResolveTimeoutSeconds(AiOptions options, string provider = null)
        {
            var p = (provider ?? options?.Provider ?? "").Trim();
            var matched = (options?.Providers ?? new List<AiProviderOptions>())
                .FirstOrDefault(x => string.Equals((x.Provider ?? string.Empty).Trim(), p, StringComparison.OrdinalIgnoreCase));
            if (matched?.TimeoutSeconds is int t && t > 0) return t;
            var top = options?.TimeoutSeconds ?? 0;
            return top > 0 ? top : 60;
        }
    }
}
