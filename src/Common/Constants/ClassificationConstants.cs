namespace Quaestura.Common.Constants;

public static class ClassificationConstants
{
    public const int Mistake = 1;
    public const int Homework = 2;

    public static readonly Dictionary<int, string> DisplayNames = new()
    {
        { Mistake, "错题" }, { Homework, "作业" }
    };

    public static readonly Dictionary<int, string> EnglishNames = new()
    {
        { Mistake, "MISTAKE" }, { Homework, "HOMEWORK" }
    };

    public static string GetDisplayName(int classification) =>
        DisplayNames.TryGetValue(classification, out var name) ? name : "未知";

    public static string GetEnglishName(int classification) =>
        EnglishNames.TryGetValue(classification, out var name) ? name : "UNKNOWN";
}
