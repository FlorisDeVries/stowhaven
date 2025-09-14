using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace FlorisDeV.Logging.OpenTelemetry;

internal static class ActivityEnrichment
{
    // https://opentelemetry.io/docs/reference/specification/trace/semantic_conventions/span-general/#general-identity-attributes
    private const string AttributeHttpClientIp = "http.client_ip";
    private const string AttributeEndUserId = "enduser.id";
    private const string AttributeEndUserRole = "enduser.role";

    public static void EnrichWithHttpResponse(Activity activity, HttpResponse response)
    {
        if (response.HttpContext.User is { Identity: { } identity })
        {
            activity.AddTag(AttributeEndUserId, identity.Name);

            if (identity is ClaimsIdentity ci && ci.FindFirst(ci.RoleClaimType) is { } role)
            {
                activity.AddTag(AttributeEndUserRole, role.Value);
            }
        }
    }

    public static void EnrichWithHttpRequest(Activity activity, HttpRequest request)
    {
        if (request.HttpContext.Connection is { RemoteIpAddress: { } remoteIp })
        {
            activity.AddTag(AttributeHttpClientIp, remoteIp);
        }
    }
}