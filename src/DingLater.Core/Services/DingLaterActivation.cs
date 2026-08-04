namespace DingLater.Core.Services;

public static class DingLaterActivation
{
    public static bool TryParseMessageId(string? value, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "dinglater", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "inbox", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Length != 37
            || uri.AbsolutePath[0] != '/')
        {
            return false;
        }

        return Guid.TryParseExact(uri.AbsolutePath.AsSpan(1), "D", out id);
    }
}
