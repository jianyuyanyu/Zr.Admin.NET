using Microsoft.AspNetCore.Mvc;
using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.Admin.WebApi.Controllers.AI
{
    /// <summary>
    /// AI 权限额度、模型价格与 Provider 诊断管理控制器。
    /// 提供 AI 能力查询、治理策略管理、模型价格维护、配置检查和健康诊断等功能。
    /// </summary>
    [Route("aiGovernance")]
    [ApiExplorerSettings(GroupName = "ai")]
    public class AiGovernanceController : BaseController
    {
        private readonly IAiGovernanceService _service;

        public AiGovernanceController(IAiGovernanceService service)
        {
            _service = service;
        }

        /// <summary>
        /// 获取 AI 能力列表。
        /// </summary>
        [HttpGet("capabilities")]
        [ActionPermissionFilter(Permission = "ai:governance:list")]
        public IActionResult Capabilities() => SUCCESS(_service.GetCapabilities());

        /// <summary>
        /// 获取 AI 能力目录信息。
        /// </summary>
        [HttpGet("catalog")]
        [ActionPermissionFilter(Permission = "ai:governance:list,ai:price:list,ai:usage:list")]
        public IActionResult Catalog() => SUCCESS(_service.GetCatalog());

        /// <summary>
        /// 查询治理策略列表。
        /// </summary>
        [HttpGet("policy/list")]
        [ActionPermissionFilter(Permission = "ai:governance:list")]
        public IActionResult PolicyList([FromQuery] AiPolicyQueryDto query) => SUCCESS(_service.GetPolicyList(query));

        /// <summary>
        /// 查询治理策略适用主体集合。
        /// <paramref name="keyword"/>
        /// <paramref name="scopeType"/>
        /// </summary>
        [HttpGet("policy/subjects")]
        [ActionPermissionFilter(Permission = "ai:governance:list,ai:governance:add,ai:governance:edit")]
        public IActionResult PolicySubjects(string scopeType, string keyword = null) => SUCCESS(_service.GetPolicySubjects(scopeType, keyword));

        /// <summary>
        /// 根据主键查询治理策略详情
        /// <paramref name="id"/>
        /// </summary>
        [HttpGet("policy/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:governance:query")]
        public IActionResult Policy(long id) => SUCCESS(_service.GetPolicy(id));

        /// <summary>
        /// 新增治理策略。
        /// </summary>
        [HttpPost("policy")]
        [ActionPermissionFilter(Permission = "ai:governance:add")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.INSERT)]
        public IActionResult AddPolicy([FromBody] AiPolicySaveDto input)
        {
            input.Id = 0;
            return SUCCESS(new { id = _service.SavePolicy(input) });
        }

        /// <summary>
        /// 修改治理策略。
        /// </summary>
        [HttpPut("policy")]
        [ActionPermissionFilter(Permission = "ai:governance:edit")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.UPDATE)]
        public IActionResult EditPolicy([FromBody] AiPolicySaveDto input) => SUCCESS(_service.SavePolicy(input));

        /// <summary>
        /// 删除治理策略。
        /// </summary>
        [HttpDelete("policy/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:governance:remove")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.DELETE)]
        public IActionResult DeletePolicy(long id) => SUCCESS(_service.DeletePolicy(id));

        /// <summary>
        /// 查询模型价格列表。
        /// </summary>
        [HttpGet("price/list")]
        [ActionPermissionFilter(Permission = "ai:price:list")]
        public IActionResult PriceList([FromQuery] AiModelPriceQueryDto query) => SUCCESS(_service.GetPriceList(query));

        /// <summary>
        /// 根据主键查询模型价格详情。
        /// </summary>
        [HttpGet("price/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:price:query")]
        public IActionResult Price(long id) => SUCCESS(_service.GetPrice(id));

        /// <summary>
        /// 新增模型价格。
        /// </summary>
        [HttpPost("price")]
        [ActionPermissionFilter(Permission = "ai:price:add")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.INSERT)]
        public IActionResult AddPrice([FromBody] AiModelPriceSaveDto input)
        {
            input.Id = 0;
            return SUCCESS(new { id = _service.SavePrice(input) });
        }

        /// <summary>
        /// 修改模型价格。
        /// </summary>
        [HttpPut("price")]
        [ActionPermissionFilter(Permission = "ai:price:edit")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.UPDATE)]
        public IActionResult EditPrice([FromBody] AiModelPriceSaveDto input) => SUCCESS(_service.SavePrice(input));

        /// <summary>
        /// 删除模型价格。
        /// </summary>
        [HttpDelete("price/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:price:remove")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.DELETE)]
        public IActionResult DeletePrice(long id) => SUCCESS(_service.DeletePrice(id));

        /// <summary>
        /// 检查 AI 配置项是否正确。
        /// </summary>
        [HttpGet("config/check")]
        [ActionPermissionFilter(Permission = "ai:governance:health")]
        public IActionResult ConfigCheck() => SUCCESS(_service.CheckConfiguration());

        /// <summary>
        /// 执行 AI 服务健康检查。
        /// </summary>
        [HttpPost("health")]
        [ActionPermissionFilter(Permission = "ai:governance:health")]
        public async Task<IActionResult> Health() => SUCCESS(await _service.CheckHealthAsync());
    }
}
