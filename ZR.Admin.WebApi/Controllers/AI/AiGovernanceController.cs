using Microsoft.AspNetCore.Mvc;
using ZR.Model.AI.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.Admin.WebApi.Controllers.AI
{
    /// <summary>AI 权限额度、模型价格与 Provider 诊断。</summary>
    [Route("aiGovernance")]
    [ApiExplorerSettings(GroupName = "ai")]
    public class AiGovernanceController : BaseController
    {
        private readonly IAiGovernanceService _service;

        public AiGovernanceController(IAiGovernanceService service)
        {
            _service = service;
        }

        [HttpGet("capabilities")]
        [ActionPermissionFilter(Permission = "ai:governance:list")]
        public IActionResult Capabilities() => SUCCESS(_service.GetCapabilities());

        [HttpGet("catalog")]
        [ActionPermissionFilter(Permission = "ai:governance:list,ai:price:list,ai:usage:list")]
        public IActionResult Catalog() => SUCCESS(_service.GetCatalog());

        [HttpGet("policy/list")]
        [ActionPermissionFilter(Permission = "ai:governance:list")]
        public IActionResult PolicyList([FromQuery] AiPolicyQueryDto query) => SUCCESS(_service.GetPolicyList(query));

        [HttpGet("policy/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:governance:query")]
        public IActionResult Policy(long id) => SUCCESS(_service.GetPolicy(id));

        [HttpPost("policy")]
        [ActionPermissionFilter(Permission = "ai:governance:add")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.INSERT)]
        public IActionResult AddPolicy([FromBody] AiPolicySaveDto input)
        {
            input.Id = 0;
            return SUCCESS(new { id = _service.SavePolicy(input) });
        }

        [HttpPut("policy")]
        [ActionPermissionFilter(Permission = "ai:governance:edit")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.UPDATE)]
        public IActionResult EditPolicy([FromBody] AiPolicySaveDto input) => SUCCESS(_service.SavePolicy(input));

        [HttpDelete("policy/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:governance:remove")]
        [Log(Title = "AI治理策略", BusinessType = BusinessType.DELETE)]
        public IActionResult DeletePolicy(long id) => SUCCESS(_service.DeletePolicy(id));

        [HttpGet("price/list")]
        [ActionPermissionFilter(Permission = "ai:price:list")]
        public IActionResult PriceList([FromQuery] AiModelPriceQueryDto query) => SUCCESS(_service.GetPriceList(query));

        [HttpGet("price/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:price:query")]
        public IActionResult Price(long id) => SUCCESS(_service.GetPrice(id));

        [HttpPost("price")]
        [ActionPermissionFilter(Permission = "ai:price:add")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.INSERT)]
        public IActionResult AddPrice([FromBody] AiModelPriceSaveDto input)
        {
            input.Id = 0;
            return SUCCESS(new { id = _service.SavePrice(input) });
        }

        [HttpPut("price")]
        [ActionPermissionFilter(Permission = "ai:price:edit")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.UPDATE)]
        public IActionResult EditPrice([FromBody] AiModelPriceSaveDto input) => SUCCESS(_service.SavePrice(input));

        [HttpDelete("price/{id:long}")]
        [ActionPermissionFilter(Permission = "ai:price:remove")]
        [Log(Title = "AI模型价格", BusinessType = BusinessType.DELETE)]
        public IActionResult DeletePrice(long id) => SUCCESS(_service.DeletePrice(id));

        [HttpGet("config/check")]
        [ActionPermissionFilter(Permission = "ai:governance:health")]
        public IActionResult ConfigCheck() => SUCCESS(_service.CheckConfiguration());

        [HttpPost("health")]
        [ActionPermissionFilter(Permission = "ai:governance:health")]
        public async Task<IActionResult> Health() => SUCCESS(await _service.CheckHealthAsync());
    }
}
