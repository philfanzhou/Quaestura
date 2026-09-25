namespace Quaestura.Common.Oss;

internal static class PresignedUrlPathPrefixer
{
    public static string AddPrefix(string presignedUrl, Uri publicBaseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presignedUrl);
        ArgumentNullException.ThrowIfNull(publicBaseUri);

        var signedUri = new Uri(presignedUrl, UriKind.Absolute);
        if (!AuthorityMatches(signedUri, publicBaseUri))
        {
            throw new InvalidOperationException(
                "The presigned URL authority does not match Oss:PublicBaseUrl.");
        }

        var pathPrefix = publicBaseUri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(pathPrefix))
        {
            return presignedUrl;
        }

        var authority = signedUri.GetLeftPart(UriPartial.Authority);
        return authority + pathPrefix + presignedUrl[authority.Length..];
    }

    private static bool AuthorityMatches(Uri signedUri, Uri publicBaseUri)
    {
        return string.Equals(signedUri.Scheme, publicBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(signedUri.Host, publicBaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               signedUri.Port == publicBaseUri.Port;
    }
}
