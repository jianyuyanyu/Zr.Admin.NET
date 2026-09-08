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

        /// <summary>
        /// 执行工具：
        /// 1) 非本提供者负责的工具名必须返回 null；
        /// 2) 本提供者负责的工具名必须返回非 null 的 AiToolExecResult；
        /// 3) 可预期业务失败（权限不足/参数非法/无数据）应使用 AiToolExecResult.Error 返回。
        /// </summary>
        Task<AiToolExecResult> ExecuteAsync(string toolName, string argsJson, long userId);
    }
}
