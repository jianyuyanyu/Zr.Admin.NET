using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZR.Workflow")]

namespace Infrastructure.AI
{
    /// <summary>
    /// 提示词（Prompt）加载器：从磁盘目录读取 .md 提示词文件，带内存缓存。
    /// 设计目标：
    /// 1) 路径可配置（AiOptions.PromptDir），默认 AppContext.BaseDirectory/Prompts；
    /// 2) 文件缺失仅返回 null 并记录告警，<b>不抛异常、不阻断编译与启动</b>；
    /// 3) 调用方在真正使用提示词时再决定是否抛友好异常。
    /// </summary>
    public class PromptLoader
    {
        private static readonly ILogger<PromptLoader> Logger =
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<PromptLoader>();

        private static readonly TimeSpan CacheSliding = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 进程内记录的“读取失败（缺失或为空）”提示词文件名，供治理自检
        /// （AiGovernanceService.CheckConfiguration）展示，避免"缺提示词"只留在日志里。
        /// 文件名来自各实际调用点，无需另维护一份场景→提示词映射；某文件后续加载成功会自动移除。
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> MissingFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>读取失败的提示词文件名（升序）。进程内累计；后续加载成功会自动移除。</summary>
        public static IReadOnlyList<string> GetMissingFiles() =>
            MissingFiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        private readonly string _baseDir;
        private readonly Dictionary<string, (string Text, DateTime Expire)> _cache = new();

        public PromptLoader(string promptDir = null)
        {
            var dir = string.IsNullOrWhiteSpace(promptDir)
                ? Path.Combine(AppContext.BaseDirectory, "Prompts")
                : promptDir;
            _baseDir = Path.GetFullPath(dir);
        }

        /// <summary>
        /// 读取提示词文本。文件不存在或为空时返回 null（不抛异常）。
        /// </summary>
        /// <param name="fileName">文件名，如 flow-generate.md</param>
        public string Load(string fileName)
        {
            var key = fileName ?? string.Empty;
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var hit) && hit.Expire > DateTime.Now)
                {
                    return hit.Text;
                }
            }

            var path = Path.Combine(_baseDir, fileName);
            string text = null;
            try
            {
                if (File.Exists(path))
                {
                    text = File.ReadAllText(path);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "读取提示词文件失败：{Path}", path);
            }

            if (text == null)
            {
                Logger.LogWarning("提示词文件不存在或为空：{Path}（该 AI 能力调用时将报错）", path);
                if (!string.IsNullOrWhiteSpace(key)) MissingFiles[key] = 0;
            }
            else if (!string.IsNullOrWhiteSpace(key))
            {
                MissingFiles.TryRemove(key, out _);
            }

            lock (_cache)
            {
                _cache[key] = (text, DateTime.Now.Add(CacheSliding));
            }
            return text;
        }
    }
}
