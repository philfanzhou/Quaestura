namespace Quaestura.Common.Oss;

internal static class ObjectPath
{
    internal static string Prefix(OssBucket bucket, string? folder = null)
    {
        var prefix = bucket switch
        {
            OssBucket.Uploads => "uploads",
            OssBucket.Mistakes => "mistakes",
            OssBucket.Questions => "questions",
            OssBucket.Documents => "documents",
            _ => "others"
        };
        return string.IsNullOrWhiteSpace(folder) ? prefix : $"{prefix}/{folder}";
    }

    internal static string Create(string fileName, OssBucket bucket, string? folder)
    {
        var prefix = Prefix(bucket, folder);
        if (!string.IsNullOrWhiteSpace(folder))
            return $"{prefix}/{fileName}";

        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}
