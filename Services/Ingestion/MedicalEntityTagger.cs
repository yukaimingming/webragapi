using System.Text.RegularExpressions;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 医学实体标签 + 同义词扩展。规则词典，不上图数据库。
/// 切块写入 payload.entities；检索时把查询里的别名展开（如 心衰→心力衰竭）。
/// </summary>
internal static class MedicalEntityTagger
{
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["高血压"] = ["高血压病", "原发性高血压"],
        ["高血压急症"] = ["高血压危象", "高血压危症"],
        ["心力衰竭"] = ["心衰", "慢性心衰", "CHF"],
        ["心肌梗死"] = ["心梗", "急性心梗", "AMI", "STEMI"],
        ["脑梗死"] = ["脑梗", "缺血性卒中", "脑梗塞"],
        ["糖尿病"] = ["DM", "2型糖尿病", "T2DM"],
        ["冠心病"] = ["冠状动脉粥样硬化性心脏病", "CHD"],
        ["慢性阻塞性肺疾病"] = ["COPD", "慢阻肺"],
        ["硝苯地平"] = ["硝苯地平控释片", "硝苯啶"],
        ["阿司匹林"] = ["乙酰水杨酸", "ASA"],
        ["氯吡格雷"] = ["波立维"],
        ["呋塞米"] = ["速尿", "呋喃苯胺酸"],
        ["螺内酯"] = ["安体舒通"],
        ["收缩压"] = ["SBP", "高压"],
        ["舒张压"] = ["DBP", "低压"],
    };

    private static readonly string[] Canonical =
    [
        "高血压急症", "高血压", "心力衰竭", "心肌梗死", "脑梗死", "糖尿病", "冠心病",
        "慢性阻塞性肺疾病", "肺炎", "哮喘", "肾功能不全", "高钾血症", "低钾血症",
        "硝苯地平", "阿司匹林", "氯吡格雷", "呋塞米", "螺内酯", "美托洛尔", "氨氯地平",
        "培哚普利", "缬沙坦", "厄贝沙坦", "氢氯噻嗪", "硝酸甘油", "硝普钠",
        "收缩压", "舒张压", "靶器官损害", "静脉降压", "血钾", "肌酐",
        "心电图", "CT", "MRI", "超声心动图",
    ];

    private static readonly Regex ExtraDrug = new(@"[\u4e00-\u9fff]{2,6}(?:片|胶囊|注射液|缓释片|控释片)", RegexOptions.Compiled);

    /// <summary>从文本抽出规范实体名（最长匹配优先）。</summary>
    public static List<string> Tag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Canonical.OrderByDescending(s => s.Length))
        {
            if (text.Contains(name, StringComparison.Ordinal) && seen.Add(name))
                found.Add(name);
        }
        foreach (var (canon, aliases) in Aliases)
        {
            if (seen.Contains(canon))
                continue;
            if (aliases.Any(a => text.Contains(a, StringComparison.Ordinal)) && seen.Add(canon))
                found.Add(canon);
        }
        foreach (Match m in ExtraDrug.Matches(text))
        {
            var t = m.Value;
            if (t.Length is >= 3 and <= 10 && seen.Add(t))
                found.Add(t);
        }
        return found.Take(16).ToList();
    }

    /// <summary>检索查询扩展：原句 + 命中实体的别名，便于 BM25 命中同义写法。</summary>
    public static string ExpandQuery(string query)
    {
        var tags = Tag(query);
        if (tags.Count == 0)
            return query;
        var extra = new List<string>();
        foreach (var tag in tags)
        {
            extra.Add(tag);
            if (Aliases.TryGetValue(tag, out var tagAliases))
                extra.AddRange(tagAliases);
            foreach (var (canon, canonAliases) in Aliases)
            {
                if (canonAliases.Contains(tag, StringComparer.Ordinal))
                {
                    extra.Add(canon);
                    extra.AddRange(canonAliases);
                }
            }
        }
        var add = extra.Distinct(StringComparer.Ordinal)
            .Where(t => t.Length > 0 && !query.Contains(t, StringComparison.Ordinal))
            .Take(8);
        var joined = string.Join(" ", add);
        return string.IsNullOrEmpty(joined) ? query : $"{query} {joined}";
    }
}
