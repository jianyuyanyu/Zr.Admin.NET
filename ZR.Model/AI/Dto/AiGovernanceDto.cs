namespace ZR.Model.AI.Dto
{
    public class AiPolicyQueryDto : PagerInfo
    {
        public string TenantId { get; set; }
        public string ScopeType { get; set; }
        public long? SubjectId { get; set; }
        public string Scene { get; set; }
        public int? Status { get; set; }
    }

    public class AiPolicySaveDto
    {
        public long Id { get; set; }
        public string ScopeType { get; set; }
        public string TenantId { get; set; }
        public long SubjectId { get; set; }
        public string Scene { get; set; } = "*";
        public int? IsEnabled { get; set; }
        public long? DailyTokenLimit { get; set; }
        public long? MonthlyTokenLimit { get; set; }
        public decimal? DailyAmountLimit { get; set; }
        public decimal? MonthlyAmountLimit { get; set; }
        public int? ConcurrentLimit { get; set; }
        public int Status { get; set; }
        public string Remark { get; set; }
    }

    public class AiPolicyListDto
    {
        public long Id { get; set; }
        public string ScopeType { get; set; }
        public string TenantId { get; set; }
        public string TenantName { get; set; }
        public long SubjectId { get; set; }
        public string SubjectName { get; set; }
        public string Scene { get; set; }
        public int? IsEnabled { get; set; }
        public long? DailyTokenLimit { get; set; }
        public long? MonthlyTokenLimit { get; set; }
        public decimal? DailyAmountLimit { get; set; }
        public decimal? MonthlyAmountLimit { get; set; }
        public int? ConcurrentLimit { get; set; }
        public int Status { get; set; }
        public string Remark { get; set; }
    }

    public class AiPolicySubjectDto
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Extra { get; set; }
    }

    public class AiModelPriceQueryDto : PagerInfo
    {
        public string Provider { get; set; }
        public string Model { get; set; }
        public int? Status { get; set; }
    }

    public class AiModelPriceSaveDto
    {
        public long Id { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public decimal InputPricePerMillion { get; set; }
        public decimal OutputPricePerMillion { get; set; }
        public string Currency { get; set; } = "CNY";
        public int Status { get; set; }
        public string Remark { get; set; }
    }

    public class AiConfigCheckDto
    {
        public bool Valid { get; set; }
        public bool Enabled { get; set; }
        public string Provider { get; set; }
        public string BaseUrl { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public bool ApiKeyConfigured { get; set; }
        public bool VisionConfigured { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    public class AiGovernanceCapabilitiesDto
    {
        public string TenantId { get; set; }
        public bool IsPlatformAdmin { get; set; }
        public bool CanManageGlobalPolicy { get; set; }
        public bool CanManageModelPrice { get; set; }
        public bool CanCheckProvider { get; set; }
    }

    public class AiHealthCheckDto
    {
        public bool Healthy { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public long LatencyMs { get; set; }
        public int? HttpStatusCode { get; set; }
        public string ProviderRequestId { get; set; }
        public string Message { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    public class AiModelCatalogDto
    {
        public List<AiProviderOptionDto> Providers { get; set; } = new();
    }

    public class AiProviderOptionDto
    {
        public string Provider { get; set; }
        public string Label { get; set; }
        public List<string> Models { get; set; } = new();
    }
}
