namespace ZR.Model.AI
{
    public static class AiSceneCatalog
    {
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "*",
            "ai_chat",
            "lang_translate",
            "cron_parse",
            "schedule_parse",
            "weekly_report",
            "gen_columns",
            "login_security",
            "oper_health",
            "wf_generate",
            "wf_approval_suggest",
            "wf_flow_optimize",
            "wf_intent_match",
            "wf_instance_summary",
            "wf_risk_check",
            "wf_approval_summary",
            "health_check"
        };
    }
}
