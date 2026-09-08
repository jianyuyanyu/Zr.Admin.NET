namespace ZR.ServiceCore.AI.Charts
{
    /// <summary>图表时间分桶（周一为一周起始）。</summary>
    public static class AiChartTimeBuckets
    {
        public static string Key(DateTime dt, string grain)
        {
            grain = (grain ?? "day").ToLowerInvariant();
            if (grain == "month")
            {
                return dt.ToString("yyyy-MM");
            }
            if (grain == "week")
            {
                return WeekStart(dt).ToString("yyyy-MM-dd");
            }
            return dt.ToString("yyyy-MM-dd");
        }

        public static DateTime WeekStart(DateTime dt)
        {
            var offset = ((int)dt.DayOfWeek + 6) % 7;
            return dt.Date.AddDays(-offset);
        }

        /// <summary>把按天序列滚成周/月。grain=day 时原样返回。</summary>
        public static List<Dictionary<string, object>> RollupDaily(
            IEnumerable<(string Date, long Value)> daily, string grain, string valueField)
        {
            grain = (grain ?? "day").ToLowerInvariant();
            valueField ??= "value";
            var list = daily?.ToList() ?? [];
            if (list.Count == 0)
            {
                return [];
            }
            if (grain == "day")
            {
                return list.Select(d => Row(d.Date, valueField, d.Value)).ToList();
            }

            var grouped = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in list)
            {
                if (!DateTime.TryParse(d.Date, out var dt))
                {
                    continue;
                }
                var key = Key(dt, grain);
                grouped.TryGetValue(key, out var acc);
                grouped[key] = acc + d.Value;
            }
            return grouped.Select(kv => Row(kv.Key, valueField, kv.Value)).ToList();
        }

        public static Dictionary<string, object> Row(string date, string valueField, long value) => new()
        {
            ["date"] = date,
            [valueField] = value
        };
    }
}
