namespace TradingFlow.Contracts.Transport;

/// <summary>Validates the operator application's origin before credentials or session cookies can be sent.</summary>
public static class BackendOrigin
{
    /// <summary>Requires HTTPS; a debug host may explicitly allow only loopback and the Android emulator's host bridge over HTTP.</summary>
    public static string Normalize(string? value, bool allowDevelopmentLoopback = false)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var origin) ||
            !String.IsNullOrEmpty(origin.UserInfo) || !String.IsNullOrEmpty(origin.Query) ||
            !String.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/")
            throw new ArgumentException("Enter an absolute HTTPS backend origin without a path, query, fragment or embedded credentials.");
        var developmentHttp = allowDevelopmentLoopback && origin.Scheme == Uri.UriSchemeHttp &&
            (origin.IsLoopback || origin.Host == "10.0.2.2");
        if (origin.Scheme != Uri.UriSchemeHttps && !developmentHttp)
            throw new ArgumentException("Use an HTTPS backend origin, including the current ngrok address. Cleartext LAN sign-in is not supported.");
        return origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
