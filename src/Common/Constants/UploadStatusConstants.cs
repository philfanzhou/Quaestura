namespace Quaestura.Common.Constants;

public static class UploadStatusConstants
{
    public const int Pending = 1;
    public const int Processing = 2;
    public const int UnderReview = 3;
    public const int Failed = 4;
    public const int Returned = 5;

    public static readonly Dictionary<int, string> DisplayNames = new()
    {
        { Pending, "待处理" }, { Processing, "处理中" }, { UnderReview, "审核中" },
        { Failed, "处理失败" }, { Returned, "已退回" }
    };

    public static readonly Dictionary<int, string> EnglishNames = new()
    {
        { Pending, "PENDING" }, { Processing, "PROCESSING" }, { UnderReview, "UNDER_REVIEW" },
        { Failed, "FAILED" }, { Returned, "RETURNED" }
    };

    public static string GetDisplayName(int status) =>
        DisplayNames.TryGetValue(status, out var name) ? name : "未知";

    public static string GetEnglishName(int status) =>
        EnglishNames.TryGetValue(status, out var name) ? name : "UNKNOWN";
}
