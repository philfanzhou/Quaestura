using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Quaestura.Common.Authentication;

/// <summary>
/// Shared JWT Bearer configuration for all services that trust the configured identity domain.
/// </summary>
public static class RuoyuJwtBearerExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication and authorization using the standard
    /// IdentityService configuration keys (IdentityService:Authority, etc.).
    /// The signing key is resolved automatically via OIDC discovery (JWKS).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing the IdentityService section.</param>
    /// <param name="configureAuthorization">Optional callback to add authorization policies.</param>
    public static IServiceCollection AddRuoyuJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<RuoyuJwtBearerConsumerOptions>? configureConsumer = null,
        Action<AuthorizationOptions>? configureAuthorization = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (environment is null) throw new ArgumentNullException(nameof(environment));

        var identityOptions = configuration
            .GetSection(IdentityAuthenticationOptions.SectionName)
            .Get<IdentityAuthenticationOptions>() ?? new IdentityAuthenticationOptions();
        var validation = new IdentityAuthenticationOptionsValidator()
            .Validate(Options.DefaultName, identityOptions);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                IdentityAuthenticationOptions.SectionName,
                typeof(IdentityAuthenticationOptions),
                validation.Failures);
        }

        services.AddOptions<IdentityAuthenticationOptions>()
            .Bind(configuration.GetSection(IdentityAuthenticationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityAuthenticationOptions>>(
            new IdentityAuthenticationOptionsValidator());

        var consumerOptions = new RuoyuJwtBearerConsumerOptions();
        configureConsumer?.Invoke(consumerOptions);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = identityOptions.Authority.Trim().TrimEnd('/');
                options.Audience = identityOptions.Audience;
                options.RequireHttpsMetadata = identityOptions.RequireHttpsMetadata;
                options.MapInboundClaims = consumerOptions.MapInboundClaims;
                options.TokenValidationParameters = IdentityTokenValidationParametersFactory.Create(
                    identityOptions,
                    nameClaimType: consumerOptions.NameClaimType,
                    roleClaimType: consumerOptions.RoleClaimType);

                if (!string.IsNullOrWhiteSpace(consumerOptions.AccessTokenCookieName))
                {
                    var cookieName = consumerOptions.AccessTokenCookieName;
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var hasAuthorizationHeader = context.Request.Headers
                                .ContainsKey(HeaderNames.Authorization);
                            if (!hasAuthorizationHeader && string.IsNullOrWhiteSpace(context.Token))
                            {
                                context.Token = context.Request.Cookies[cookieName];
                            }

                            return Task.CompletedTask;
                        }
                    };
                }
            });

        services.AddAuthorization(options =>
        {
            // Default fallback policy: any authenticated user.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            configureAuthorization?.Invoke(options);
        });

        return services;
    }
}
