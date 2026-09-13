using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.IService
{
    public interface IAiGovernanceService
    {
        PagedInfo<AiAccessPolicy> GetPolicyList(AiPolicyQueryDto query);
        AiAccessPolicy GetPolicy(long id);
        long SavePolicy(AiPolicySaveDto input);
        int DeletePolicy(long id);

        PagedInfo<AiModelPrice> GetPriceList(AiModelPriceQueryDto query);
        AiModelPrice GetPrice(long id);
        long SavePrice(AiModelPriceSaveDto input);
        int DeletePrice(long id);

        AiGovernanceCapabilitiesDto GetCapabilities();
        AiModelCatalogDto GetCatalog();
        AiConfigCheckDto CheckConfiguration();
        Task<AiHealthCheckDto> CheckHealthAsync();
    }
}
