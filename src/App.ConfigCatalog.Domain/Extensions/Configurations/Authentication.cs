using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using App.ConfigCatalog.Infrastructure.Token;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace App.ConfigCatalog.Domain.Extensions.Configurations;

public static class Authentication
{
    public static void Configure(this WebApplicationBuilder builder)
    {
        var azureAd = builder.Configuration.GetSection("AzureAd");

        var instance = azureAd["Instance"] ?? "https://login.microsoftonline.com/";
        var tenantId = azureAd["TenantId"] ?? "common";
        var authority = $"{instance.TrimEnd('/')}/{tenantId}/v2.0";

        var audience =
            azureAd["Audience"] ??
            throw new InvalidOperationException("AzureAd:Audience is required (e.g., api://{API_CLIENT_ID}).");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = true;

                var hardening =
                    builder.Configuration.GetSection("TokenHardening").Get<TokenHardeningOptions>() ?? new();

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    ValidateIssuer = true,
                    IssuerValidator = BuildIssuerValidator(builder.Configuration),
                    ValidateAudience = true,
                    ValidAudiences = new[] { audience, azureAd["ClientId"] }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToArray()!,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(Math.Clamp(hardening.ClockSkewSeconds, 0, 120)),
                    ValidAlgorithms = new[]
                    {
                        SecurityAlgorithms.RsaSha256,
                        SecurityAlgorithms.EcdsaSha256
                    },
                    NameClaimType = "name",
                    RoleClaimType = "roles"
                };

                options.RefreshOnIssuerKeyNotFound = true;
                options.SaveToken = false;

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        if (ctx.SecurityToken is JwtSecurityToken jwt)
                        {
                            var alg = jwt.Header.Alg;

                            if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
                            {
                                ctx.Fail("Rejected unsigned JWT (alg=none).");
                                return;
                            }

                            if (alg is null ||
                                !(alg.Equals("RS256", StringComparison.OrdinalIgnoreCase) ||
                                  alg.Equals("ES256", StringComparison.OrdinalIgnoreCase)))
                            {
                                ctx.Fail($"Rejected JWT with unsupported alg='{alg}'.");
                                return;
                            }
                        }

                        var revocation = ctx.HttpContext.RequestServices.GetRequiredService<ITokenRevocationStore>();
                        var hard = ctx.HttpContext.RequestServices
                            .GetRequiredService<IOptions<TokenHardeningOptions>>()
                            .Value;

                        if (hard.EnableJtiReplayProtection)
                        {
                            var jti = ctx.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);

                            if (!string.IsNullOrWhiteSpace(jti))
                            {
                                if (await revocation.IsRevokedAsync(jti, ctx.HttpContext.RequestAborted))
                                {
                                    ctx.Fail("Token has been revoked.");
                                    return;
                                }

                                var replayOk = await revocation.TryMarkSeenAsync(
                                    jti,
                                    TimeSpan.FromMinutes(hard.JtiCacheMinutes),
                                    ctx.HttpContext.RequestAborted);

                                if (!replayOk)
                                {
                                    ctx.Fail("Token replay detected (jti reused).");
                                    return;
                                }
                            }
                        }
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        var reason =
                            ctx.Exception is SecurityTokenExpiredException ? "token_expired" :
                            ctx.Exception is SecurityTokenInvalidSignatureException ? "invalid_signature" :
                            ctx.Exception is SecurityTokenInvalidAudienceException ? "invalid_audience" :
                            ctx.Exception is SecurityTokenInvalidIssuerException ? "invalid_issuer" :
                            ctx.Exception is SecurityTokenException ? "token_invalid" :
                            "auth_failed";

                        ctx.HttpContext.Items["auth_fail_reason"] = reason;
                        ctx.HttpContext.Items["auth_failed"] = true;

                        return Task.CompletedTask;
                    },
                    OnChallenge = ctx =>
                    {
                        ctx.HttpContext.Items["auth_fail_reason"] ??= "challenge";
                        return Task.CompletedTask;
                    },
                    OnForbidden = ctx =>
                    {
                        ctx.HttpContext.Items["auth_fail_reason"] ??= "forbidden_policy";
                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static IssuerValidator BuildIssuerValidator(IConfiguration config)
    {
        var allowAny = config.GetValue<bool>("MultiTenant:AllowAnyTenant");

        var allowed = (config.GetSection("MultiTenant:AllowedTenants").Get<string[]>() ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (issuer, token, _) =>
        {
            var tid = TryGetTenantId(token);
            if (string.IsNullOrWhiteSpace(tid))
            {
                throw new SecurityTokenInvalidIssuerException("Missing tenant id (tid) claim.");
            }

            if (!allowAny && !allowed.Contains(tid))
            {
                throw new SecurityTokenInvalidIssuerException($"Tenant '{tid}' is not allowed.");
            }

            if (!string.IsNullOrWhiteSpace(issuer) && !issuer.Contains(tid, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityTokenInvalidIssuerException("Issuer does not match tenant id.");
            }

            return issuer;
        };
    }

    private static string? TryGetTenantId(SecurityToken token)
    {
        if (token is JsonWebToken jwt2)
        {
            return jwt2.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
        }

        if (token is JwtSecurityToken jwt)
        {
            return jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
        }

        return null;
    }
}
