using Infrastructure;
using Infrastructure.Attribute;
using Infrastructure.Helper;
using Infrastructure.Model;
using System.Text.Json;
using ZR.Model.Models;
using ZR.Model.System.Dto;

namespace ZR.ServiceCore.Services
{
    /// <summary>
    /// 系统模块 AI 能力：多语言批量翻译、自然语言生成 Cron 表达式。
    /// 与工作流 AI 共用同一套基建（AiLlmClient / PromptLoader / AiOptions），
    /// 提示词放在 Prompts/system/ 下，可由运维热改无需重编译。
    /// </summary>
    [AppService(ServiceType = typeof(ISysAiService), ServiceLifetime = LifeTime.Transient)]
    public class SysAiService : ISysAiService
    {
        /// <summary>单次翻译条目上限，防止超长请求撑爆上下文与 token</summary>
        private const int MaxTranslateItems = 50;

        /// <summary>单条源文案长度上限</summary>
        private const int MaxItemTextLength = 1000;

        /// <summary>语言代码长度上限，对齐 sys_common_lang.lang_code 列长</summary>
        private const int LangCodeMaxLength = 10;

        private readonly ICommonLangService _commonLangService;

        private static readonly PromptLoader PromptLoader =
            new(AppSettings.Get<AiOptions>("AiOptions")?.PromptDir);

        public SysAiService(ICommonLangService commonLangService)
        {
            _commonLangService = commonLangService;
        }

        public async Task<SysAiLangTranslateResult> TranslateLangAsync(SysAiLangTranslateInput input)
        {
            if (input == null)
            {
                throw new Exception("请求参数不能为空");
            }

            var sourceLang = NormalizeLangCode(input.SourceLang) ?? "zh-cn";
            var targetLangs = (input.TargetLangs ?? new List<string>())
                .Select(NormalizeLangCode)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targetLangs.Count == 0)
            {
                throw new Exception("请至少选择一个目标语言");
            }

            // 目标语言与源语言相同时没有翻译意义，直接剔除，避免浪费 token
            targetLangs.RemoveAll(x => string.Equals(x, sourceLang, StringComparison.OrdinalIgnoreCase));
            if (targetLangs.Count == 0)
            {
                throw new Exception("目标语言与源语言相同，无需翻译");
            }

            var items = (input.Items ?? new List<SysAiLangItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.LangKey) && !string.IsNullOrWhiteSpace(x.Text))
                .ToList();
            if (items.Count == 0)
            {
                throw new Exception("没有可翻译的文案，请先选择需要翻译的条目");
            }
            if (items.Count > MaxTranslateItems)
            {
                throw new Exception($"单次最多翻译 {MaxTranslateItems} 条文案，当前 {items.Count} 条，请分批操作");
            }

            var user = BuildTranslatePrompt(sourceLang, targetLangs, items);
            var text = await ChatSafeAsync(GetPromptOrThrow("system/lang-translate.md", "多语言翻译"), user).ConfigureAwait(false);

            return ParseTranslateResult(text, sourceLang, targetLangs, items);
        }

        public (string, object, object) ApplyLangTranslation(SysAiLangApplyInput input)
        {
            var entries = input?.Entries ?? new List<SysAiLangEntry>();
            var list = new List<CommonLang>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.LangKey)) continue;
                var langKey = Clip(entry.LangKey.Trim(), 100);

                foreach (var t in entry.Translations ?? new List<SysAiLangTranslation>())
                {
                    if (t == null) continue;
                    var langCode = NormalizeLangCode(t.LangCode);
                    if (string.IsNullOrEmpty(langCode) || string.IsNullOrWhiteSpace(t.LangName)) continue;
                    // 同一文案键 + 语言只保留首个，避免重复条目让 Storageable 走批量更新产生歧义
                    if (!seen.Add($"{langKey}|{langCode}")) continue;

                    list.Add(new CommonLang
                    {
                        LangKey = langKey,
                        LangCode = langCode,
                        LangName = Clip(t.LangName.Trim(), 2000),
                        Addtime = now
                    });
                }
            }

            if (list.Count == 0)
            {
                throw new Exception("没有可应用的译文，请检查译文内容是否为空");
            }

            if (input != null && !input.Cover)
            {
                list = FilterExisting(list);
                if (list.Count == 0)
                {
                    throw new Exception("所选文案的目标语言译文均已存在（未勾选覆盖），没有需要新增的内容");
                }
            }

            return _commonLangService.ImportCommonLang(list);
        }

        public async Task<SysAiCronParseResult> ParseCronAsync(SysAiCronParseInput input)
        {
            var text = input?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception("请填写调度描述，例如「每天凌晨2点执行」");
            }
            if (text.Length > 200)
            {
                throw new Exception("调度描述过长，请精简到 200 字以内");
            }

            var reply = await ChatSafeAsync(GetPromptOrThrow("system/cron-parse.md", "Cron 表达式生成"), $"调度描述：{text}").ConfigureAwait(false);
            return ParseCronResult(reply);
        }

        /// <summary>
        /// 组装翻译请求正文：用 JSON 承载条目，避免多行文本在换行/引号上与提示词混淆。
        /// </summary>
        private static string BuildTranslatePrompt(string sourceLang, List<string> targetLangs, List<SysAiLangItem> items)
        {
            var payload = new
            {
                sourceLang,
                targetLangs,
                items = items.Select(x => new
                {
                    langKey = Clip(x.LangKey.Trim(), 100),
                    text = Clip(x.Text.Trim(), MaxItemTextLength),
                    context = string.IsNullOrWhiteSpace(x.Context) ? string.Empty : Clip(x.Context.Trim(), 100)
                }).ToList()
            };
            return System.Text.Json.JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// 解析翻译结果。按 langKey 与 AI 输出对齐，缺失的条目译文留空由前端提示，绝不串行错位。
        /// </summary>
        private static SysAiLangTranslateResult ParseTranslateResult(string raw, string sourceLang, List<string> targetLangs, List<SysAiLangItem> items)
        {
            var result = new SysAiLangTranslateResult { SourceLang = sourceLang, TargetLangs = targetLangs };

            var byKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var parsed = false;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var json = JsonHelper.StripMarkdown(raw);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("entries", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (!el.TryGetProperty("langKey", out var keyEl)) continue;
                            var key = keyEl.GetString();
                            if (string.IsNullOrWhiteSpace(key)) continue;

                            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            if (el.TryGetProperty("translations", out var ts) && ts.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var t in ts.EnumerateArray())
                                {
                                    var code = NormalizeLangCode(ReadString(t, "langCode"));
                                    var name = ReadString(t, "langName");
                                    if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(name)) continue;
                                    map[code] = name.Trim();
                                }
                            }
                            byKey[key.Trim()] = map;
                        }
                        parsed = true;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // AI 返回非结构化文本时按原文兜底，交由前端展示并提示重试
                }
            }

            if (!parsed)
            {
                result.Raw = raw?.Trim();
                return result;
            }

            foreach (var item in items)
            {
                var key = item.LangKey.Trim();
                var entry = new SysAiLangEntry { LangKey = key, Text = item.Text.Trim() };
                byKey.TryGetValue(key, out var map);

                foreach (var lang in targetLangs)
                {
                    var name = map != null && map.TryGetValue(lang, out var v) ? v : string.Empty;
                    entry.Translations.Add(new SysAiLangTranslation { LangCode = lang, LangName = name });
                }
                result.Entries.Add(entry);
            }
            return result;
        }

        private static SysAiCronParseResult ParseCronResult(string raw)
        {
            var result = new SysAiCronParseResult();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var json = JsonHelper.StripMarkdown(raw);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    result.Raw = raw.Trim();
                    return result;
                }
                result.Cron = (ReadString(root, "cron") ?? string.Empty).Trim();
                result.Description = (ReadString(root, "description") ?? string.Empty).Trim();
                result.Warnings = (ReadString(root, "warnings") ?? string.Empty).Trim();
            }
            catch (System.Text.Json.JsonException)
            {
                result.Raw = raw.Trim();
            }
            return result;
        }

        /// <summary>
        /// 剔除库中已存在（langKey + langCode）的译文，使不覆盖模式下只补空缺。
        /// </summary>
        private List<CommonLang> FilterExisting(List<CommonLang> list)
        {
            var keys = list.Select(x => x.LangKey).Distinct().ToList();
            var existed = _commonLangService.Queryable()
                .Where(x => keys.Contains(x.LangKey))
                .Select(x => new { x.LangKey, x.LangCode })
                .ToList()
                .Select(x => $"{x.LangKey}|{x.LangCode}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return list.Where(x => !existed.Contains($"{x.LangKey}|{x.LangCode}")).ToList();
        }

        private static string ReadString(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }

        /// <summary>
        /// 规范化语言代码：去空格转小写，仅保留字母数字与 - _，并截断到列长。
        /// 非法输入返回 null 由调用方跳过，避免把脏数据写进 lang_code。
        /// </summary>
        private static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var chars = code.Trim().ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray();
            if (chars.Length == 0) return null;

            return chars.Length <= LangCodeMaxLength
                ? new string(chars)
                : new string(chars, 0, LangCodeMaxLength);
        }

        private static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string GetPromptOrThrow(string fileName, string capability)
        {
            var text = PromptLoader.Load(fileName);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception($"AI 能力「{capability}」所需提示词文件缺失：{fileName}（请检查 AiOptions:PromptDir 指向的 Prompts 目录）");
            }
            return text;
        }

        /// <summary>
        /// 校验 AI 开关与 ApiKey，未启用时抛友好异常（与工作流 AI 同一口径）。
        /// </summary>
        private static AiOptions EnsureAiEnabled()
        {
            var options = AppSettings.Get<AiOptions>("AiOptions");
            if (options == null || !options.Enable)
            {
                throw new Exception("AI 功能未启用，请在 appsettings.json 配置 AiOptions");
            }
            var resolved = AiLlmClient.ResolveProvider(options);
            if (string.IsNullOrWhiteSpace(resolved.ApiKey))
            {
                throw new Exception("AI 功能未配置 ApiKey，请在 appsettings.json 的 AiOptions 或 Providers 中配置");
            }
            return options;
        }

        /// <summary>
        /// 调用大模型并把网络类异常转成用户可读提示。
        /// </summary>
        private static async Task<string> ChatSafeAsync(string system, string user)
        {
            var options = EnsureAiEnabled();
            try
            {
                return await AiLlmClient.ChatAsync(options, system, user).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception("调用 AI 服务失败：" + ex.Message);
            }
            catch (TaskCanceledException)
            {
                throw new Exception("调用 AI 服务超时，请稍后重试");
            }
        }
    }
}
