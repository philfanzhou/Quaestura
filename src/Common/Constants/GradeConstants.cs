namespace Quaestura.Common.Constants;

public static class GradeConstants
{
    public const int MinValue = 1;
    public const int MaxValue = 12;

    public static readonly Dictionary<int, string> DisplayNames = new()
    {
        { 1, "一年级" }, { 2, "二年级" }, { 3, "三年级" },
        { 4, "四年级" }, { 5, "五年级" }, { 6, "六年级" },
        { 7, "初一" }, { 8, "初二" }, { 9, "初三" },
        { 10, "高一" }, { 11, "高二" }, { 12, "高三" }
    };

    public static readonly Dictionary<int, string> FullDisplayNames = new()
    {
        { 1, "小学一年级" }, { 2, "小学二年级" }, { 3, "小学三年级" },
        { 4, "小学四年级" }, { 5, "小学五年级" }, { 6, "小学六年级" },
        { 7, "初中一年级" }, { 8, "初中二年级" }, { 9, "初中三年级" },
        { 10, "高中一年级" }, { 11, "高中二年级" }, { 12, "高中三年级" }
    };

    public static readonly Dictionary<int, string> EnglishNames = new()
    {
        { 1, "GRADE_PRIMARY_1" }, { 2, "GRADE_PRIMARY_2" }, { 3, "GRADE_PRIMARY_3" },
        { 4, "GRADE_PRIMARY_4" }, { 5, "GRADE_PRIMARY_5" }, { 6, "GRADE_PRIMARY_6" },
        { 7, "GRADE_MIDDLE_1" }, { 8, "GRADE_MIDDLE_2" }, { 9, "GRADE_MIDDLE_3" },
        { 10, "GRADE_HIGH_1" }, { 11, "GRADE_HIGH_2" }, { 12, "GRADE_HIGH_3" }
    };

    public static readonly Dictionary<string, int> ReverseDisplayNames = new()
    {
        ["一年级"] = 1, ["二年级"] = 2, ["三年级"] = 3,
        ["四年级"] = 4, ["五年级"] = 5, ["六年级"] = 6,
        ["初一"] = 7, ["初二"] = 8, ["初三"] = 9,
        ["高一"] = 10, ["高二"] = 11, ["高三"] = 12,
    };

    public static string GetDisplayName(int grade) =>
        DisplayNames.TryGetValue(grade, out var name) ? name : "未知";

    public static string GetFullDisplayName(int grade) =>
        FullDisplayNames.TryGetValue(grade, out var name) ? name : "未知";

    public static string GetEnglishName(int grade) =>
        EnglishNames.TryGetValue(grade, out var name) ? name : "UNKNOWN";

    public static int FromDisplayName(string? name)
    {
        if (name is null) return 0;
        if (ReverseDisplayNames.TryGetValue(name, out var v)) return v;
        var match = FullDisplayNames.FirstOrDefault(kv =>
            string.Equals(kv.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return match.Key;
    }

    public static bool IsValid(int grade) =>
        grade >= MinValue && grade <= MaxValue;
}
