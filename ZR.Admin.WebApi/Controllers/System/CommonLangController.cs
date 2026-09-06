using Microsoft.AspNetCore.Mvc;
using MiniExcelLibs;
using ZR.Model;
using ZR.Model.Dto;
using ZR.Model.Models;
using ZR.Model.System.Dto;
using ZR.ServiceCore.AI.IService;

namespace ZR.Admin.WebApi.Controllers
{
    /// <summary>
    /// 多语言配置Controller
    /// </summary>
    [Route("system/CommonLang")]
    [ApiExplorerSettings(GroupName = "sys")]
    public class CommonLangController : BaseController
    {
        /// <summary>
        /// 多语言配置接口
        /// </summary>
        private readonly ICommonLangService _CommonLangService;
        private readonly ISysAiService _sysAiService;

        public CommonLangController(ICommonLangService CommonLangService, ISysAiService sysAiService)
        {
            _CommonLangService = CommonLangService;
            _sysAiService = sysAiService;
        }

        /// <summary>
        /// 查询多语言配置列表
        /// </summary>
        /// <param name="parm"></param>
        /// <returns></returns>
        [HttpGet("list")]
        [ActionPermissionFilter(Permission = "system:lang:list")]
        public IActionResult QueryCommonLang([FromQuery] CommonLangQueryDto parm)
        {
            if (parm.ShowMode == 2)
            {
                PagedInfo<dynamic> pagedInfo = new()
                {
                    Result = _CommonLangService.GetListToPivot(parm)
                };

                return SUCCESS(pagedInfo);
            }

            return SUCCESS(_CommonLangService.GetList(parm));
        }

        /// <summary>
        /// 查询多语言配置列表
        /// </summary>
        /// <returns></returns>
        [HttpGet("list/{lang}")]
        [AllowAnonymous]
        public IActionResult QueryCommonLangs(string lang)
        {
            var msgList = _CommonLangService.GetLangList(new CommonLangQueryDto() { LangCode = lang });

            return SUCCESS(_CommonLangService.SetLang(msgList));
        }

        /// <summary>
        /// 查询多语言配置详情
        /// </summary>
        /// <param name="Id"></param>
        /// <returns></returns>
        [HttpGet("{Id}")]
        [ActionPermissionFilter(Permission = "system:lang:query")]
        public IActionResult GetCommonLang(long Id)
        {
            var response = _CommonLangService.GetFirst(x => x.Id == Id);

            var list = _CommonLangService.GetList(x => x.LangKey == response.LangKey);
            var vo = list.Adapt<List<CommonLangDto>>();
            var modal = new CommonLangDto() { LangKey = response.LangKey, LangList = vo };
            return SUCCESS(modal);
        }

        /// <summary>
        /// 查询多语言配置详情
        /// </summary>
        /// <param name="langKey"></param>
        /// <returns></returns>
        [HttpGet("key/{langKey}")]
        [ActionPermissionFilter(Permission = "system:lang:query")]
        public IActionResult GetCommonLangByKey(string langKey)
        {
            var list = _CommonLangService.GetList(x => x.LangKey == langKey);
            var vo = list.Adapt<List<CommonLangDto>>();
            var modal = new CommonLangDto() { LangKey = langKey, LangList = vo };

            return SUCCESS(modal);
        }

        /// <summary>
        /// 更新多语言配置
        /// </summary>
        /// <returns></returns>
        [HttpPut]
        [ActionPermissionFilter(Permission = "system:lang:edit")]
        [Log(Title = "多语言配置", BusinessType = BusinessType.UPDATE)]
        public IActionResult UpdateCommonLang([FromBody] CommonLangDto parm)
        {
            if (parm == null || parm.LangKey.IsEmpty())
            {
                throw new CustomException("请求实体不能为空");
            }

            _CommonLangService.StorageCommonLang(parm);

            return ToResponse(1);
        }

        /// <summary>
        /// 删除多语言配置
        /// </summary>
        /// <returns></returns>
        [HttpDelete("{ids}")]
        [ActionPermissionFilter(Permission = "system:lang:delete")]
        [Log(Title = "多语言配置", BusinessType = BusinessType.DELETE)]
        public IActionResult DeleteCommonLang(string ids)
        {
            long[] idsArr = Tools.SpitLongArrary(ids);
            if (idsArr.Length <= 0) { return ToResponse(ApiResult.Error($"删除失败Id 不能为空")); }

            var response = _CommonLangService.Delete(idsArr);

            return ToResponse(response);
        }

        /// <summary>
        /// 删除多语言配置
        /// </summary>
        /// <returns></returns>
        [HttpDelete("ByKey")]
        [ActionPermissionFilter(Permission = "system:lang:delete")]
        [Log(Title = "多语言配置", BusinessType = BusinessType.DELETE)]
        public IActionResult DeleteCommonLangByKey(string langkey)
        {
            if (langkey.IsEmpty()) { return ToResponse(ApiResult.Error($"删除失败Id 不能为空")); }

            var response = _CommonLangService
                .Deleteable()
                .EnableDiffLogEvent()
                .Where(f => f.LangKey == langkey)
                .ExecuteCommand();
            return ToResponse(response);
        }

        /// <summary>
        /// 导出多语言配置
        /// </summary>
        /// <returns></returns>
        [Log(Title = "多语言配置", BusinessType = BusinessType.EXPORT, IsSaveResponseData = false)]
        [HttpGet("export")]
        [ActionPermissionFilter(Permission = "system:lang:export")]
        public IActionResult Export([FromQuery] CommonLangQueryDto parm)
        {
            parm.PageSize = 10000;
            var list = _CommonLangService.GetListToPivot(parm);

            string sFileName = ExportExcel(list, "CommonLang", "多语言配置");
            return SUCCESS(new { path = "/export/" + sFileName, fileName = sFileName });
        }

        /// <summary>
        /// 导入
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        [HttpPost("importData")]
        [Consumes("multipart/form-data")]
        [Log(Title = "多语言设置导入", BusinessType = BusinessType.IMPORT, IsSaveRequestData = false, IsSaveResponseData = true)]
        [ActionPermissionFilter(Permission = "system:lang:import")]
        public IActionResult ImportData(IFormFile file)
        {
            List<CommonLang> list = [];
            var nowTime = DateTime.Now;
            using (var stream = file.OpenReadStream())
            {
                var rows = stream.Query(startCell: "A2").ToList();

                foreach (var item in rows)
                {
                    list.Add(new CommonLang() { LangCode = "zh-cn", LangKey = item.A, LangName = item.B, Addtime = nowTime });
                    list.Add(new CommonLang() { LangCode = "en", LangKey = item.A, LangName = item.C, Addtime = nowTime });
                    list.Add(new CommonLang() { LangCode = "zh-tw", LangKey = item.A, LangName = item.D, Addtime = nowTime });
                }
            }

            return SUCCESS(_CommonLangService.ImportCommonLang(list));
        }

        /// <summary>
        /// AI 批量翻译界面文案，返回可编辑草稿（不落库）。提示词见 Prompts/system/lang-translate.md。
        /// </summary>
        [HttpPost("ai/translate")]
        [ActionPermissionFilter(Permission = "system:lang:ai")]
        public async Task<IActionResult> AiTranslate([FromBody] SysAiLangTranslateInput parm)
        {
            try
            {
                return SUCCESS(await _sysAiService.TranslateLangAsync(parm));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 应用 AI 译文到多语言配置。默认只补空缺，勾选覆盖时才更新已有译文。
        /// </summary>
        [HttpPost("ai/apply")]
        [ActionPermissionFilter(Permission = "system:lang:ai")]
        [Log(Title = "AI 多语言译文", BusinessType = BusinessType.IMPORT, IsSaveRequestData = false, IsSaveResponseData = true)]
        public IActionResult AiApply([FromBody] SysAiLangApplyInput parm)
        {
            try
            {
                return SUCCESS(_sysAiService.ApplyLangTranslation(parm));
            }
            catch (Exception ex)
            {
                return ToResponse(ResultCode.FAIL, ex.Message);
            }
        }

        /// <summary>
        /// 多语言设置导入模板下载
        /// </summary>
        /// <returns></returns>
        [HttpGet("importTemplate")]
        [Log(Title = "多语言设置模板", BusinessType = BusinessType.EXPORT, IsSaveRequestData = true, IsSaveResponseData = false)]
        [AllowAnonymous]
        public IActionResult ImportTemplateExcel()
        {
            var result = DownloadImportTemplate(new List<CommonLang>() { }, "lang");
            return ExportExcel(result.Item2, result.Item1);
        }
    }
}