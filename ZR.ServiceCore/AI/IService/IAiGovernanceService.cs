using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;

namespace ZR.ServiceCore.AI.IService
{
    public interface IAiGovernanceService
    {
        PagedInfo<AiPolicyListDto> GetPolicyList(AiPolicyQueryDto query);
        AiAccessPolicy GetPolicy(long id);
        List<AiPolicySubjectDto> GetPolicySubjects(string scopeType, string keyword);
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
