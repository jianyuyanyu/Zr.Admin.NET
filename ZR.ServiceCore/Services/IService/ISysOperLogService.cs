using ZR.Model;
using ZR.Model.System;
using ZR.Model.System.Dto;

namespace ZR.ServiceCore.Services
{
    public interface ISysOperLogService
    {
        public void InsertOperlog(SysOperLog operLog);

        /// <summary>
        /// 查询系统操作日志集合
        /// </summary>
        /// <param name="operLog">操作日志对象</param>
        /// <returns>操作日志集合</returns>
        public PagedInfo<SysOperLog> SelectOperLogList(SysOperLogQueryDto operLog);

        /// <summary>
        /// 清空操作日志
        /// </summary>
        public void CleanOperLog();

        /// <summary>
        /// 批量删除系统操作日志
        /// </summary>
        /// <param name="operIds">需要删除的操作日志ID</param>
        /// <returns>结果</returns>
        public int DeleteOperLogByIds(long[] operIds);

        /// <summary>
        /// 查询操作日志详细
        /// </summary>
        /// <param name="operId">操作ID</param>
        /// <returns>操作日志对象</returns>
        public SysOperLog SelectOperLogById(long operId);

        /// <summary>
        /// 聚合操作日志健康指标（错误已聚类，供 AI 健康分析解读）
        /// </summary>
        /// <param name="input">时间范围参数</param>
        /// <param name="userId">限定操作人，非空时只统计该用户的日志（非管理员口径）</param>
        /// <returns>聚合指标</returns>
        OperHealthMetricsDto GetOperHealthMetrics(LogAiAnalysisInput input, long? userId = null);

        /// <summary>
        /// 按维度聚合操作日志（供 AI 图表问答，不返回原始日志）。
        /// 支持模块/操作类型/操作人/风险等级四种切片，风险等级由 BusinessType 推导，
        /// 与 <see cref="GetOperHealthMetrics"/> 同口径：userId 非空时只统计该用户。
        /// </summary>
        /// <param name="input">时间范围参数</param>
        /// <param name="dimension">维度 Key，取值见 OperDimensionKinds，非法值降级为 module</param>
        /// <param name="userId">限定操作人，非空时只统计该用户（非管理员口径）</param>
        /// <param name="topN">返回条数上限，超出部分合并为"其他"（风险等级维度固定 3 档不合并）</param>
        /// <returns>该维度的聚合项</returns>
        List<OperDimensionStat> GetOperDimensionStats(LogAiAnalysisInput input, string dimension, long? userId = null, int topN = 12);
    }
}
