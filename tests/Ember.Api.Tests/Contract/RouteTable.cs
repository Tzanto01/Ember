using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ember.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ember.Api.Tests.Contract;

/// <summary>One endpoint of the running app: its <see cref="Key"/> (<c>METHOD pattern</c>) and the reviewable <see cref="Line"/> that goes into routes.txt.</summary>
internal sealed record RouteLine(string Key, string Line);

/// <summary>Reads every endpoint of the app from its <see cref="EndpointDataSource"/>.</summary>
internal static class RouteTable
{
    /// <summary>Endpoints that bind their body as a <see cref="JsonElement"/> and deserialize it themselves: the type and options they use.</summary>
    private static readonly Dictionary<string, (Type Type, JsonSerializerOptions Options)> BodyDocuments = new()
    {
        ["PATCH /api/me"] = (typeof(MeEndpoints.MePatch), JsonSerializerOptions.Web),
        ["PATCH /api/moods/{id:int}"] = (typeof(JournalEndpoints.MoodRequest), JsonSerializerOptions.Web),
        ["POST /api/me/import"] = (typeof(ImportEndpoints.ImportDocument), ExportBuilder.JsonOptions),
    };

    private static readonly NullabilityInfoContext Nullability = new();

    public static List<RouteLine> Read(IServiceProvider services)
    {
        var json = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var lines = new List<RouteLine>();
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
            foreach (var method in methods.Order())
            {
                var pattern = endpoint.RoutePattern.RawText!;
                var key = $"{method} {pattern}";
                lines.Add(new RouteLine(key, $"{key} | {Auth(endpoint)} | {RateLimit(endpoint)} | {Parameters(endpoint)} | {Body(key, endpoint, json)}"));
            }
        }

        return lines.OrderBy(l => l.Key.Split(' ')[1], StringComparer.Ordinal).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
    }

    private static string Auth(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
            ? "signed-in"
            : "anonymous";

    private static string RateLimit(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null ? "-"
            : endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "-";

    private static string Parameters(RouteEndpoint endpoint)
    {
        var routeNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet();
        var bound = endpoint.Metadata.GetOrderedMetadata<IParameterBindingMetadata>()
            .Where(m => m.Name is not null && IsSimple(m.ParameterInfo.ParameterType))
            .GroupBy(m => m.Name).Select(g => g.Last()).ToList();
        string Render(IParameterBindingMetadata m)
            => $"{m.Name}{(IsNullable(m.ParameterInfo) ? "?" : "")}:{ScalarKind(Unwrap(m.ParameterInfo.ParameterType))}";

        var route = endpoint.RoutePattern.Parameters.Select(p =>
            bound.FirstOrDefault(m => m.Name == p.Name) is { } m ? Render(m) : $"{p.Name}:string");
        var query = bound.Where(m => !routeNames.Contains(m.Name!)).Select(Render).Order(StringComparer.Ordinal);
        return $"route[{string.Join(", ", route)}] query[{string.Join(", ", query)}]";
    }

    private static string Body(string key, RouteEndpoint endpoint, JsonSerializerOptions json)
    {
        if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is not { RequestType: { } type } accepts) return "body[-]";
        var options = json;
        if (BodyDocuments.TryGetValue(key, out var document)) (type, options) = document;
        var reflected = new JsonSerializerOptions(options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var optional = accepts.IsOptional ? " optional" : "";
        return $"body[{Kind(type, reflected, 0)}{optional}]";
    }

    private static bool IsSimple(Type type)
    {
        type = Unwrap(type);
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(DateTime) || type == typeof(decimal);
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNullable(ParameterInfo parameter)
        => Nullable.GetUnderlyingType(parameter.ParameterType) is not null || parameter.HasDefaultValue
            || (!parameter.ParameterType.IsValueType && Nullability.Create(parameter).WriteState == NullabilityState.Nullable);

    private static string ScalarKind(Type type)
    {
        if (type == typeof(string) || type.IsEnum) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(Guid)) return "guid";
        if (type == typeof(DateOnly)) return "date";
        if (type == typeof(TimeOnly)) return "time";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type.IsPrimitive) return "integer";
        return "json";
    }

    /// <summary>A body type as its JSON wire form: properties by their serialized names and JSON kinds, <c>?</c> when nullable.</summary>
    private static string Kind(Type type, JsonSerializerOptions options, int depth)
    {
        type = Unwrap(type);
        var info = options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return $"[{Kind(info.ElementType ?? typeof(object), options, depth + 1)}]";
            case JsonTypeInfoKind.Dictionary:
                return $"{{string: {Kind(info.ElementType ?? typeof(object), options, depth + 1)}}}";
            case JsonTypeInfoKind.Object when type != typeof(object) && depth < 6:
                var properties = info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p =>
                    $"{p.Name}{(PropertyIsNullable(p) ? "?" : "")}: {Kind(p.PropertyType, options, depth + 1)}");
                return $"{{{string.Join(", ", properties)}}}";
            default:
                return ScalarKind(type);
        }
    }

    private static bool PropertyIsNullable(JsonPropertyInfo property)
        => Nullable.GetUnderlyingType(property.PropertyType) is not null
            || (!property.PropertyType.IsValueType && property.AttributeProvider is PropertyInfo member
                && Nullability.Create(member).ReadState == NullabilityState.Nullable);
}
