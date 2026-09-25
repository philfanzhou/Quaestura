namespace Quaestura.Common.Constants;

public static class SubjectConstants
{
    public const int MinValue = 1;
    public const int MaxValue = 9;

    public const string DateFormat = "yyyy-MM-dd";

    public static readonly Dictionary<int, string> DisplayNames = new()
    {
        { 1, "语文" }, { 2, "数学" }, { 3, "英语" },
        { 4, "物理" }, { 5, "化学" }, { 6, "生物" },
        { 7, "历史" }, { 8, "地理" }, { 9, "政治" }
    };

    public static readonly Dictionary<int, string> EnglishNames = new()
    {
        { 1, "CHINESE" }, { 2, "MATHEMATICS" }, { 3, "ENGLISH" },
        { 4, "PHYSICS" }, { 5, "CHEMISTRY" }, { 6, "BIOLOGY" },
        { 7, "HISTORY" }, { 8, "GEOGRAPHY" }, { 9, "POLITICS" }
    };

    public static readonly Dictionary<string, int> ReverseDisplayNames = new()
    {
        ["语文"] = 1, ["数学"] = 2, ["英语"] = 3, ["物理"] = 4,
        ["化学"] = 5, ["生物"] = 6, ["历史"] = 7, ["地理"] = 8, ["政治"] = 9,
    };

    public static string GetDisplayName(int subject) =>
        DisplayNames.TryGetValue(subject, out var name) ? name : "未知";

    public static string GetEnglishName(int subject) =>
        EnglishNames.TryGetValue(subject, out var name) ? name : "UNKNOWN";

    public static int FromDisplayName(string? name) =>
        name is not null && ReverseDisplayNames.TryGetValue(name, out var v) ? v : 0;

    public static bool IsValid(int subject) =>
        subject >= MinValue && subject <= MaxValue;
}
