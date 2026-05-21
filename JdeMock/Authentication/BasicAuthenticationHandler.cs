using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace ActivityTester.JdeMock.Authentication
{
    /// <summary>
    /// HTTP Basic Authentication handler. Validates the Authorization header
    /// against <c>JdeMock:Username</c> and <c>JdeMock:Password</c> from
    /// appsettings.json.
    ///
    /// Behavior:
    ///   - No Authorization header  → 401 with WWW-Authenticate challenge
    ///   - Wrong scheme (not Basic) → 401
    ///   - Bad Base64               → 401
    ///   - Credentials mismatch     → 401 + warning log (with remote IP)
    ///   - Match                    → success, user identity attached
    ///
    /// The scheme name is "Basic" — wired up in JdeMockServer.cs.
    /// </summary>
    internal sealed class BasicAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Basic";

        private readonly IConfiguration _config;

        public BasicAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration config)
            : base(options, logger, encoder)
        {
            _config = config;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // No header → NoResult, NOT Fail. Lets [AllowAnonymous] endpoints
            // still work and lets the challenge step return a clean 401.
            if (!Request.Headers.TryGetValue("Authorization", out var headerVals))
                return Task.FromResult(AuthenticateResult.NoResult());

            string header = headerVals.ToString();
            if (string.IsNullOrEmpty(header) ||
                !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.Fail("Unsupported auth scheme"));

            string user, pass;
            try
            {
                string encoded = header.Substring("Basic ".Length).Trim();
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int colon = decoded.IndexOf(':');
                if (colon < 0)
                    return Task.FromResult(AuthenticateResult.Fail("Malformed credentials"));
                user = decoded.Substring(0, colon);
                pass = decoded.Substring(colon + 1);
            }
            catch (FormatException)
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid Base64 in Authorization header"));
            }

            string expectedUser = _config["JdeMock:Username"];
            string expectedPass = _config["JdeMock:Password"];

            if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPass))
            {
                Logger.LogError(
                    "JdeMock:Username or JdeMock:Password is not configured in appsettings.json. " +
                    "All requests will fail until both are set.");
                return Task.FromResult(AuthenticateResult.Fail("Server credentials not configured"));
            }

            if (!string.Equals(user, expectedUser, StringComparison.Ordinal) ||
                !string.Equals(pass, expectedPass, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "Basic auth FAILED for user '{User}' from {RemoteIp} on {Path}",
                    user, Context.Connection.RemoteIpAddress, Request.Path);
                return Task.FromResult(AuthenticateResult.Fail("Invalid username or password"));
            }

            var claims   = new[] { new Claim(ClaimTypes.Name, user) };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket   = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            // Tells curl / Postman / browsers that Basic auth is required.
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"JDE Orchestrator Mock\", charset=\"UTF-8\"";
            Response.StatusCode = 401;
            return Task.CompletedTask;
        }
    }
}
