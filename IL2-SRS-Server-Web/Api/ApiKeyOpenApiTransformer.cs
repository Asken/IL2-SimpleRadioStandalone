using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Api
{
    /// <summary>Describes API key authentication in the OpenAPI document, so the API reference can send keys.</summary>
    internal sealed class ApiKeyOpenApiTransformer : IOpenApiDocumentTransformer
    {
        public const string SchemeName = "ApiKey";

        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
            CancellationToken cancellationToken)
        {
            document.Info.Title = "IL2-SRS Server API";
            document.Info.Description =
                "Create API keys under API Keys in the admin UI. Read keys can call every GET; write keys can call everything.";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "An IL2-SRS API key (srs_...), sent as Authorization: Bearer <key>."
            };

            document.Security ??= new List<OpenApiSecurityRequirement>();
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = new List<string>()
            });

            return Task.CompletedTask;
        }
    }
}
