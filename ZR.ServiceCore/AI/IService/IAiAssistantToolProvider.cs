using System.Collections.Generic;
using System.Threading.Tasks;
using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.IService
{
    /// <summary>
    /// AI 助手扩展工具提供者。
    /// 各模块（如工作流）可通过实现本接口 + [AppService] 向全局 AI 助手注册领域工具，
    /// 工具内部一律调用本模块既有 Service，天然继承权限 / 数据范围 / 租户隔离。
    /// </summary>
    public interface IAiAssistantToolProvider
    {
        /// <summary>提供者名称（用于日志定位），如 workflow / order</summary>
        string ProviderName { get; }

        /// <summary>本提供者注册的工具定义集合</summary>
        List<AiToolDef> GetToolDefs();

        /// <summary>执行工具。非本提供者的工具名应返回 Ok=false。</summary>
        Task<AiToolExecResult> ExecuteAsync(string toolName, string argsJson, long userId);
    }
}
