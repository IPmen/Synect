using System.Net;
using System.Net.Sockets;
using Synect.Domain.Sync;

namespace Synect.Application.OData;

/// <summary>base_url of an OData connection was refused (SSRF protection); the message never repeats a password.</summary>
public sealed class ODataEndpointException(string message) : ArgumentException(message);

/// <summary>
/// Checks and normalizes base_url of a 1C:Fresh OData connection. Rules (server-side request forgery protection):
/// HTTPS only (http://localhost only in Development with OData:AllowHttpLocalhostInDevelopment); the host must be one of
/// OData:AllowedHosts (default clobus.uz) or its subdomain; no user name or password, query or fragment in the URL; no
/// IP literal; the port is the default one. At connection time <see cref="IsInternalAddress"/> refuses loopback,
/// private, link-local and other internal addresses the host name resolves to (except loopback in local development),
/// and redirects are never followed.
/// </summary>
public static class ODataEndpoint
{
    public const string ServiceSuffix = "/odata/standard.odata";

    /// <summary>
    /// The normalized service root (".../odata/standard.odata/", always with the trailing slash). The application URL
    /// (https://clobus.uz/a/sbm/12345) is accepted too: the service suffix is appended.
    /// </summary>
    public static Uri Normalize(string? baseUrl, ODataOptions options)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ODataEndpointException("--url (base_url) is required.");
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ODataEndpointException("base_url is not an absolute URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ODataEndpointException("base_url must not contain a user name or password; they are stored as the connection's credentials.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ODataEndpointException("base_url must not contain a query or a fragment.");

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var local = options.LocalDevelopmentAllowed && IsLocalHostName(host);
        if (uri.Scheme == Uri.UriSchemeHttp && !local)
            throw new ODataEndpointException("base_url must use HTTPS.");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ODataEndpointException("base_url must use HTTPS.");
        if (!local)
        {
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                throw new ODataEndpointException("base_url must name a host, not an IP address.");
            if (!IsAllowedHost(host, options))
                throw new ODataEndpointException(
                    $"The host of base_url is not allowed; allowed hosts (OData:AllowedHosts): {string.Join(", ", options.EffectiveAllowedHosts)}.");
            if (!uri.IsDefaultPort)
                throw new ODataEndpointException("base_url must use the default HTTPS port.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Contains("..", StringComparison.Ordinal)) throw new ODataEndpointException("base_url must not contain '..'.");
        if (!path.EndsWith(ServiceSuffix, StringComparison.OrdinalIgnoreCase)) path += ServiceSuffix;
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path + "/");
        return builder.Uri;
    }

    public static bool IsAllowedHost(string host, ODataOptions options)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return options.EffectiveAllowedHosts.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal));
    }

    /// <summary>desktop / clobus / other for an OData base_url: clobus when the host is clobus.uz or its subdomain.</summary>
    public static string HostLabel(Uri serviceRoot)
    {
        var host = serviceRoot.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host == "clobus.uz" || host.EndsWith(".clobus.uz", StringComparison.Ordinal) ? ConnectionKinds.HostClobus : ConnectionKinds.HostOther;
    }

    public static bool IsLocalHostName(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);

    /// <summary>
    /// Loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10), carrier-grade NAT
    /// (100.64/10), unspecified, multicast, broadcast and IPv4-mapped forms of these.
    /// </summary>
    public static bool IsInternalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                   || b[0] >= 224;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        }
        return true;
    }
}
