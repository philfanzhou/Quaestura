namespace Quaestura.Common.Constants;

public static class ReviewStatusConstants
{
    public const int Pending = 1;
    public const int Confirmed = 2;
    public const int Rejected = 3;

    public static readonly Dictionary<int, string> DisplayNames = new()
    {
        { Pending, "待审核" }, { Confirmed, "已确认" }, { Rejected, "已退回" }
    };

    public static readonly Dictionary<int, string> EnglishNames = new()
    {
        { Pending, "PENDING_REVIEW" }, { Confirmed, "CONFIRMED" }, { Rejected, "REJECTED" }
    };

    public static string GetDisplayName(int status) =>
        DisplayNames.TryGetValue(status, out var name) ? name : "未知";

    public static string GetEnglishName(int status) =>
        EnglishNames.TryGetValue(status, out var name) ? name : "UNKNOWN";
}
