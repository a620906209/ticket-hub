using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ProjectC.WebApi.OpenApi;

// 讓 Swagger UI 出現 Authorize 按鈕，可以直接貼 JWT 測受保護的端點，而不用每次手動加 Header。
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "Bearer";

    private readonly IAuthenticationSchemeProvider _authenticationSchemeProvider;

    public BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider)
    {
        _authenticationSchemeProvider = authenticationSchemeProvider;
    }

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var authenticationSchemes = await _authenticationSchemeProvider.GetAllSchemesAsync();
        if (authenticationSchemes.All(scheme => scheme.Name != SchemeName))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
        {
            [SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
            },
        };

        var securityRequirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, document)] = [],
        };

        // 只在真的需要登入的端點加鎖頭圖示，註冊/登入/換發 Token/密碼重設這類公開端點維持不用帶 Authorization。
        var protectedRoutes = context.DescriptionGroups
            .SelectMany(group => group.Items)
            .Where(description => description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
            .Select(description => (description.RelativePath, description.HttpMethod))
            .ToHashSet();

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (var (method, operation) in pathItem.Operations)
            {
                var httpMethod = method.ToString().ToUpperInvariant();
                var isProtected = protectedRoutes.Any(route =>
                    string.Equals(route.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) &&
                    RouteMatches(route.RelativePath, path));

                if (isProtected)
                {
                    operation.Security ??= [];
                    operation.Security.Add(securityRequirement);
                }
            }
        }
    }

    private static bool RouteMatches(string? relativePath, string openApiPath)
    {
        // ApiDescription 的 RelativePath 不帶開頭斜線，OpenApiDocument 的 path key 帶，去掉前綴比較即可。
        return string.Equals(relativePath, openApiPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }
}
