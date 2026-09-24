using System;

namespace Infrastructure.Attribute
{
    /// <summary>
    /// 标记服务，由 AppServiceExtensions.AddAppService 扫描注册进 DI 容器。
    /// 如何使用？
    /// 1、类名与接口名对应（SysConfigService : ISysConfigService）：直接裸写 [AppService] 或 [AppService(LifeTime.Transient)]，
    ///    自动按约定注册对应接口；
    /// 2、显式指定服务类型：[AppService(typeof(IXxx))] 或 [AppService(typeof(IXxx), LifeTime.Scoped)]；
    /// 3、旧具名参数写法继续有效：[AppService(ServiceType = typeof(IXxx), ServiceLifetime = LifeTime.Transient)]；
    /// 4、无接口的服务：裸写 [AppService] 注册为自身；
    /// 5、类名与接口名不对应的实现（如 DefaultSmsSender : ISmsSender）：用 2 的显式写法。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class AppServiceAttribute : System.Attribute
    {
        /// <summary>裸写 [AppService]：按约定推断服务类型（优先 I+类名 接口，否则注册为自身）。</summary>
        public AppServiceAttribute()
        {
        }

        /// <summary>裸写但显式指定生命周期：按约定推断服务类型（优先 I+类名 接口，否则注册为自身）。</summary>
        public AppServiceAttribute(LifeTime serviceLifetime)
        {
            ServiceLifetime = serviceLifetime;
        }

        /// <summary>显式指定服务类型（生命周期默认 Scoped）。</summary>
        public AppServiceAttribute(Type serviceType)
        {
            ServiceType = serviceType;
        }

        /// <summary>显式指定服务类型与生命周期。</summary>
        public AppServiceAttribute(Type serviceType, LifeTime serviceLifetime)
        {
            ServiceType = serviceType;
            ServiceLifetime = serviceLifetime;
        }

        /// <summary>
        /// 服务生命周期。
        /// 默认 Scoped —— 存量站点（省略 ServiceLifetime 的 20 处）依赖此默认值，请勿随意改动。
        /// </summary>
        public LifeTime ServiceLifetime { get; set; } = LifeTime.Scoped;

        /// <summary>
        /// 指定服务类型；为空时按约定推断（优先 I+类名 接口，否则注册为自身）。
        /// </summary>
        public Type ServiceType { get; set; }

        /// <summary>
        /// 是否可以从第一个接口获取服务类型（约定推断未命中时的回退开关）。
        /// </summary>
        public bool InterfaceServiceType { get; set; }
    }

    public enum LifeTime
    {
        Transient, Scoped, Singleton
    }
}
