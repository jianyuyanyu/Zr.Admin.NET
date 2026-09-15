using Infrastructure;
using Infrastructure.AI;
using Infrastructure.Attribute;
using Infrastructure.Model;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using ZR.Model;
using ZR.Model.AI;
using ZR.Model.AI.Dto;
using ZR.Model.System.Tenant;
using ZR.ServiceCore.AI.IService;
using ZR.ServiceCore.Services;

namespace ZR.ServiceCore.AI.Governance
{
    [AppService(ServiceType = typeof(IAiGovernanceService), ServiceLifetime = LifeTime.Transient)]
    public class AiGovernanceService : BaseService<AiAccessPolicy>, IAiGovernanceService
    {
        private static readonly HashSet<string> ScopeTypes = new(StringComparer.OrdinalIgnoreCase)
            { "global", "tenant", "role", "user" };
        private readonly IAiCallGovernance _callGovernance;
        private readonly ISysRoleService _roleService;
        private readonly AiOptions _options;

        public AiGovernanceService(
            IAiCallGovernance callGovernance,
            ISysRoleService roleService,
            IOptions<OptionsSetting> options)
        {
            _callGovernance = callGovernance;
            _roleService = roleService;
            _options = options.Value?.AiOptions ?? new AiOptions();
        }

        public PagedInfo<AiPolicyListDto> GetPolicyList(AiPolicyQueryDto query)
        {
            EnsureManager();
            query ??= new AiPolicyQueryDto();
            var currentTenant = App.GetCurrentTenantId();
            var platform = IsPlatformAdmin();
            var pageNum = Math.Max(1, query.PageNum);
            var pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, 100);
            var total = 0;
            var list = Queryable()
                .WhereIF(!platform, x => x.TenantId == currentTenant && (x.ScopeType == "role" || x.ScopeType == "user"))
                .WhereIF(platform && !string.IsNullOrWhiteSpace(query.TenantId), x => x.TenantId == query.TenantId)
                .WhereIF(!string.IsNullOrWhiteSpace(query.ScopeType), x => x.ScopeType == query.ScopeType)
                .WhereIF(query.SubjectId.HasValue, x => x.SubjectId == query.SubjectId.Value)
                .WhereIF(!string.IsNullOrWhiteSpace(query.Scene), x => x.Scene == query.Scene)
                .WhereIF(query.Status.HasValue, x => x.Status == query.Status.Value)
                .OrderBy(x => x.Id, global::SqlSugar.OrderByType.Desc)
                .ToPageList(pageNum, pageSize, ref total);
            return new PagedInfo<AiPolicyListDto>
            {
                PageIndex = pageNum,
                PageSize = pageSize,
                TotalNum = total,
                Result = FillPolicyNames(list)
            };
        }

        public AiAccessPolicy GetPolicy(long id)
        {
            EnsureManager();
            var entity = Queryable().First(x => x.Id == id);
            AuthorizePolicy(entity);
            return entity;
        }

        public List<AiPolicySubjectDto> GetPolicySubjects(string scopeType, string keyword)
        {
            EnsureManager();
            var key = (keyword ?? string.Empty).Trim();
            if (string.Equals(scopeType, "role", StringComparison.OrdinalIgnoreCase))
            {
                return _roleService.Queryable()
                    .Where(r => r.DelFlag == 0)
                    .WhereIF(!string.IsNullOrEmpty(key), r => r.RoleName.Contains(key) || r.RoleKey.Contains(key))
                    .OrderBy(r => r.RoleSort)
                    .Take(200)
                    .ToList()
                    .Select(r => new AiPolicySubjectDto { Id = r.RoleId, Name = r.RoleName, Extra = r.RoleKey })
                    .ToList();
            }
            return new List<AiPolicySubjectDto>();
        }

        public long SavePolicy(AiPolicySaveDto input)
        {
            EnsureManager();
            if (input == null) throw new CustomException("策略参数不能为空");
            NormalizePolicy(input);
            ValidatePolicy(input);

            var duplicate = Queryable().Any(x => x.Id != input.Id && x.ScopeType == input.ScopeType
                && x.TenantId == input.TenantId && x.SubjectId == input.SubjectId && x.Scene == input.Scene);
            if (duplicate) throw new CustomException("相同作用域和场景的 AI 策略已存在");

            if (input.Id > 0)
            {
                var entity = GetPolicy(input.Id) ?? throw new CustomException("AI 策略不存在");
                MapPolicy(input, entity);
                entity.Update_by = App.UserName;
                entity.Update_time = DateTime.Now;
                Context.Updateable(entity).ExecuteCommand();
                _callGovernance.InvalidatePolicyCache(entity.TenantId);
                return entity.Id;
            }

            var added = new AiAccessPolicy();
            MapPolicy(input, added);
            added.Create_by = App.UserName;
            added.Create_time = DateTime.Now;
            var id = Context.Insertable(added).ExecuteReturnIdentity();
            _callGovernance.InvalidatePolicyCache(added.TenantId);
            return id;
        }

        public int DeletePolicy(long id)
        {
            var entity = GetPolicy(id) ?? throw new CustomException("AI 策略不存在");
            var result = Context.Deleteable<AiAccessPolicy>().Where(x => x.Id == id).ExecuteCommand();
            _callGovernance.InvalidatePolicyCache(entity.TenantId);
            return result;
        }

        public PagedInfo<AiModelPrice> GetPriceList(AiModelPriceQueryDto query)
        {
            EnsurePlatformAdmin();
            query ??= new AiModelPriceQueryDto();
            var pageNum = Math.Max(1, query.PageNum);
            var pageSize = query.PageSize <= 0 ? 20 : Math.Min(query.PageSize, 100);
            var total = 0;
            var list = Context.Queryable<AiModelPrice>()
                .WhereIF(!string.IsNullOrWhiteSpace(query.Provider), x => x.Provider == query.Provider)
                .WhereIF(!string.IsNullOrWhiteSpace(query.Model), x => x.Model.Contains(query.Model))
                .WhereIF(query.Status.HasValue, x => x.Status == query.Status.Value)
                .OrderBy(x => x.Id, global::SqlSugar.OrderByType.Desc)
                .ToPageList(pageNum, pageSize, ref total);
            return new PagedInfo<AiModelPrice>
            {
                PageIndex = pageNum,
                PageSize = pageSize,
                TotalNum = total,
                Result = list
            };
        }

        public AiModelPrice GetPrice(long id)
        {
            EnsurePlatformAdmin();
            return Context.Queryable<AiModelPrice>().First(x => x.Id == id);
        }

        public long SavePrice(AiModelPriceSaveDto input)
        {
            EnsurePlatformAdmin();
            if (input == null) throw new CustomException("模型价格参数不能为空");
            var provider = (input.Provider ?? string.Empty).Trim().ToLowerInvariant();
            var model = (input.Model ?? string.Empty).Trim();
            var currency = (input.Currency ?? "CNY").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) throw new CustomException("Provider 和模型不能为空");
            if (provider.Length > 32) throw new CustomException("Provider 最多 32 个字符");
            if (model.Length > 100) throw new CustomException("模型名称最多 100 个字符");
            if (input.InputPricePerMillion < 0 || input.OutputPricePerMillion < 0) throw new CustomException("模型单价不能小于 0");
            if (currency != "CNY") throw new CustomException("当前额度金额统一按人民币 CNY 管理");
            if (Context.Queryable<AiModelPrice>().Any(x => x.Id != input.Id && x.Provider == provider && x.Model == model))
                throw new CustomException("该 Provider 和模型的价格已存在");

            if (input.Id > 0)
            {
                var entity = GetPrice(input.Id) ?? throw new CustomException("模型价格不存在");
                entity.Provider = provider;
                entity.Model = model;
                entity.InputPricePerMillion = input.InputPricePerMillion;
                entity.OutputPricePerMillion = input.OutputPricePerMillion;
                entity.Currency = currency;
                entity.Status = input.Status;
                entity.Remark = input.Remark;
                entity.Update_by = App.UserName;
                entity.Update_time = DateTime.Now;
                Context.Updateable(entity).ExecuteCommand();
                return entity.Id;
            }

            var added = new AiModelPrice
            {
                Provider = provider,
                Model = model,
                InputPricePerMillion = input.InputPricePerMillion,
                OutputPricePerMillion = input.OutputPricePerMillion,
                Currency = currency,
                Status = input.Status,
                Remark = input.Remark,
                Create_by = App.UserName,
                Create_time = DateTime.Now
            };
            return Context.Insertable(added).ExecuteReturnIdentity();
        }

        public int DeletePrice(long id)
        {
            EnsurePlatformAdmin();
            return Context.Deleteable<AiModelPrice>().Where(x => x.Id == id).ExecuteCommand();
        }

        public AiGovernanceCapabilitiesDto GetCapabilities()
        {
            EnsureManager();
            var platform = IsPlatformAdmin();
            return new AiGovernanceCapabilitiesDto
            {
                TenantId = App.GetCurrentTenantId(),
                IsPlatformAdmin = platform,
                CanManageGlobalPolicy = platform,
                CanManageModelPrice = platform,
                CanCheckProvider = platform
            };
        }

        public AiModelCatalogDto GetCatalog()
        {
            EnsureCatalogAccess();
            var map = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string provider, string model, string label = null)
            {
                provider = (provider ?? string.Empty).Trim();
                model = (model ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(provider)) return;
                if (!map.TryGetValue(provider, out var models))
                {
                    models = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[provider] = models;
                }
                if (!string.IsNullOrEmpty(model)) models.Add(model);
                if (!string.IsNullOrWhiteSpace(label) && !labels.ContainsKey(provider))
                    labels[provider] = label.Trim();
            }

            Add(_options.Provider, _options.Model);
            Add(_options.VisionProvider, _options.VisionModel);
            if (_options.Providers != null)
            {
                foreach (var item in _options.Providers)
                {
                    Add(item?.Provider, item?.Model, item?.Label);
                    if (item?.Models == null) continue;
                    foreach (var model in item.Models) Add(item.Provider, model, item.Label);
                }
            }

            foreach (var row in Context.Queryable<AiModelPrice>()
                .GroupBy(x => new { x.Provider, x.Model })
                .Select(x => new { x.Provider, x.Model })
                .ToList())
            {
                Add(row.Provider, row.Model);
            }

            return new AiModelCatalogDto
            {
                Providers = map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new AiProviderOptionDto
                    {
                        Provider = x.Key.ToLowerInvariant(),
                        Label = labels.TryGetValue(x.Key, out var label) ? $"{label}（{x.Key}）" : x.Key,
                        Models = x.Value.ToList()
                    })
                    .ToList()
            };
        }

        public AiConfigCheckDto CheckConfiguration()
        {
            EnsurePlatformAdmin();
            var resolved = AiLlmClient.ResolveProvider(_options);
            var key = resolved.ApiKey ?? string.Empty;
            var result = new AiConfigCheckDto
            {
                Enabled = _options.Enable,
                Provider = resolved.Provider,
                BaseUrl = MaskUrl(resolved.BaseUrl),
                Endpoint = resolved.ChatEndpoint,
                Model = resolved.Model,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(key) && !key.Contains("xxx", StringComparison.OrdinalIgnoreCase),
                VisionConfigured = !string.IsNullOrWhiteSpace(_options.VisionModel)
            };
            if (!_options.Enable) result.Warnings.Add("AI 总开关未启用");
            if (!Uri.TryCreate(resolved.BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                result.Warnings.Add("Provider BaseUrl 不是有效的 HTTP(S) 地址");
            else if (uri.Scheme != Uri.UriSchemeHttps)
                result.Warnings.Add("Provider BaseUrl 未使用 HTTPS");
            if (!result.ApiKeyConfigured) result.Warnings.Add("API Key 缺失或仍为占位值");
            if (string.IsNullOrWhiteSpace(resolved.Model)) result.Warnings.Add("模型名称未配置");
            if (_options.TimeoutSeconds <= 0) result.Warnings.Add("请求超时时间必须大于 0");
            var promptDir = string.IsNullOrWhiteSpace(_options.PromptDir)
                ? Path.Combine(AppContext.BaseDirectory, "Prompts")
                : _options.PromptDir;
            if (!Directory.Exists(promptDir)) result.Warnings.Add("Prompt 目录不存在");
            result.Valid = result.Warnings.Count == 0;
            return result;
        }

        public async Task<AiHealthCheckDto> CheckHealthAsync()
        {
            EnsurePlatformAdmin();
            var resolved = AiLlmClient.ResolveProvider(_options);
            var watch = Stopwatch.StartNew();
            try
            {
                var text = await AiLlmClient.ChatAsync(_options, "仅回复 OK", "ping", "health_check");
                var log = GetLatestHealthLog();
                return new AiHealthCheckDto
                {
                    Healthy = !string.IsNullOrWhiteSpace(text),
                    Provider = resolved.Provider,
                    Model = resolved.Model,
                    LatencyMs = watch.ElapsedMilliseconds,
                    HttpStatusCode = log?.HttpStatusCode,
                    ProviderRequestId = log?.ProviderRequestId,
                    Message = string.IsNullOrWhiteSpace(text) ? "Provider 返回空响应" : "连接正常",
                    CheckedAt = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                var log = GetLatestHealthLog();
                return new AiHealthCheckDto
                {
                    Healthy = false,
                    Provider = resolved.Provider,
                    Model = resolved.Model,
                    LatencyMs = watch.ElapsedMilliseconds,
                    HttpStatusCode = log?.HttpStatusCode,
                    ProviderRequestId = log?.ProviderRequestId,
                    Message = SafeError(ex.Message),
                    CheckedAt = DateTime.Now
                };
            }
        }

        private AiCallLog GetLatestHealthLog()
        {
            var traceId = App.HttpContext?.TraceIdentifier;
            return Context.Queryable<AiCallLog>()
                .Where(x => x.Scene == "health_check" && x.TraceId == traceId)
                .OrderBy(x => x.CreateTime, global::SqlSugar.OrderByType.Desc)
                .First();
        }

        private void NormalizePolicy(AiPolicySaveDto input)
        {
            input.ScopeType = (input.ScopeType ?? string.Empty).Trim().ToLowerInvariant();
            input.Scene = string.IsNullOrWhiteSpace(input.Scene) ? "*" : input.Scene.Trim().ToLowerInvariant();
            input.TenantId = (input.TenantId ?? string.Empty).Trim();
            if (input.ScopeType == "global")
            {
                input.TenantId = string.Empty;
                input.SubjectId = 0;
            }
            else if (input.ScopeType == "tenant")
            {
                input.SubjectId = 0;
            }
        }

        private void ValidatePolicy(AiPolicySaveDto input)
        {
            if (!ScopeTypes.Contains(input.ScopeType)) throw new CustomException("无效的策略作用域");
            if (!AiSceneCatalog.All.Contains(input.Scene)) throw new CustomException("无效的 AI 场景");
            if (input.IsEnabled is < 0 or > 1) throw new CustomException("启用状态只能为 0、1 或空");
            if (input.Status is < 0 or > 1) throw new CustomException("策略状态只能为 0 或 1");
            ValidatePositive(input.DailyTokenLimit, "日 Token");
            ValidatePositive(input.MonthlyTokenLimit, "月 Token");
            ValidatePositive(input.DailyAmountLimit, "日金额");
            ValidatePositive(input.MonthlyAmountLimit, "月金额");
            ValidatePositive(input.ConcurrentLimit, "并发");

            var currentTenant = App.GetCurrentTenantId();
            if (!IsPlatformAdmin())
            {
                if (input.ScopeType is "global" or "tenant") throw new CustomException("只有平台管理员可维护全局和租户策略");
                input.TenantId = currentTenant;
            }
            else if ((input.ScopeType is "role" or "user") && input.TenantId != currentTenant)
            {
                throw new CustomException("角色和用户策略须由目标租户管理员在对应租户上下文中维护");
            }
            if (input.ScopeType != "global" && string.IsNullOrWhiteSpace(input.TenantId))
                throw new CustomException("非全局策略必须指定租户");
            if ((input.ScopeType == "role" || input.ScopeType == "user") && input.SubjectId <= 0)
                throw new CustomException("角色或用户策略必须指定主体 ID");

            if (input.ScopeType == "role" && _roleService.SelectRoleById(input.SubjectId) == null)
                throw new CustomException("目标角色不存在");
        }

        private void AuthorizePolicy(AiAccessPolicy entity)
        {
            if (entity == null || IsPlatformAdmin()) return;
            EnsureManager();
            if (entity.TenantId != App.GetCurrentTenantId() || entity.ScopeType is "global" or "tenant")
                throw new CustomException("无权访问该 AI 策略");
        }

        private List<AiPolicyListDto> FillPolicyNames(List<AiAccessPolicy> list)
        {
            var dtos = (list ?? new List<AiAccessPolicy>()).Select(ToListDto).ToList();
            if (dtos.Count == 0) return dtos;

            var tenantIds = dtos.Select(x => x.TenantId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var tenantMap = tenantIds.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : Context.Queryable<SysTenant>()
                    .Where(t => tenantIds.Contains(t.TenantId))
                    .ToList()
                    .GroupBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().TenantName, StringComparer.OrdinalIgnoreCase);

            var roleIds = dtos.Where(x => x.ScopeType == "role" && x.SubjectId > 0).Select(x => x.SubjectId).Distinct().ToList();
            var roleMap = roleIds.Count == 0
                ? new Dictionary<long, string>()
                : _roleService.Queryable().Where(r => roleIds.Contains(r.RoleId)).ToList()
                    .GroupBy(r => r.RoleId).ToDictionary(g => g.Key, g => g.First().RoleName);

            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.TenantId)) dto.TenantName = "全局";
                else if (tenantMap.TryGetValue(dto.TenantId, out var tenantName) && !string.IsNullOrWhiteSpace(tenantName))
                    dto.TenantName = tenantName;

                if (dto.ScopeType == "role" && roleMap.TryGetValue(dto.SubjectId, out var roleName))
                    dto.SubjectName = roleName;
            }
            return dtos;
        }

        private static AiPolicyListDto ToListDto(AiAccessPolicy source) => new()
        {
            Id = source.Id,
            ScopeType = source.ScopeType,
            TenantId = source.TenantId,
            SubjectId = source.SubjectId,
            Scene = source.Scene,
            IsEnabled = source.IsEnabled,
            DailyTokenLimit = source.DailyTokenLimit,
            MonthlyTokenLimit = source.MonthlyTokenLimit,
            DailyAmountLimit = source.DailyAmountLimit,
            MonthlyAmountLimit = source.MonthlyAmountLimit,
            ConcurrentLimit = source.ConcurrentLimit,
            Status = source.Status,
            Remark = source.Remark
        };

        private static void MapPolicy(AiPolicySaveDto source, AiAccessPolicy target)
        {
            target.ScopeType = source.ScopeType;
            target.TenantId = source.TenantId;
            target.SubjectId = source.SubjectId;
            target.Scene = source.Scene;
            target.IsEnabled = source.IsEnabled;
            target.DailyTokenLimit = source.DailyTokenLimit;
            target.MonthlyTokenLimit = source.MonthlyTokenLimit;
            target.DailyAmountLimit = source.DailyAmountLimit;
            target.MonthlyAmountLimit = source.MonthlyAmountLimit;
            target.ConcurrentLimit = source.ConcurrentLimit;
            target.Status = source.Status;
            target.Remark = source.Remark;
        }

        private static void ValidatePositive<T>(T? value, string name) where T : struct, IComparable<T>
        {
            if (value.HasValue && value.Value.CompareTo(default) < 0) throw new CustomException($"{name}额度不能小于 0");
        }

        private static bool IsPlatformAdmin()
        {
            var user = App.HttpContext?.GetCurrentUser();
            return user?.IsAdmin() == true
                && string.Equals(App.GetCurrentTenantId(), App.MainDbConfigId, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsurePlatformAdmin()
        {
            if (!IsPlatformAdmin()) throw new CustomException("只有平台管理员可执行该操作");
        }

        private static void EnsureManager()
        {
            var user = App.HttpContext?.GetCurrentUser();
            var delegated = user?.Permissions?.Any(x => x.StartsWith("ai:governance:", StringComparison.OrdinalIgnoreCase)) == true;
            if (user == null || (user.IsAdmin() != true && !delegated))
                throw new CustomException("当前账号没有 AI 治理管理权限");
        }

        private static void EnsureCatalogAccess()
        {
            var user = App.HttpContext?.GetCurrentUser();
            if (user == null || user.UserId <= 0)
                throw new CustomException("当前账号没有查看 AI 模型目录的权限");
        }

        private static string MaskUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}";
        }

        private static string SafeError(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "健康检查失败";
            var result = value.Replace("\r", " ").Replace("\n", " ");
            return result.Length <= 300 ? result : result[..300];
        }
    }
}
