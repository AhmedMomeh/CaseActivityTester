using ActivityTester.JdeMock.Authentication;
using ActivityTester.JdeMock.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace ActivityTester.JdeMock
{
    /// <summary>
    /// Starts the mock JDE Orchestrator HTTP server. Invoked from <c>Program.Main</c>
    /// when the user runs <c>ActivityTester.exe mock-api</c>. The activity-testing
    /// path (the default mode) is untouched.
    ///
    /// Configuration (under "JdeMock" in appsettings.json):
    ///   - Username        : Basic Auth username  (default: JDEORCH)
    ///   - Password        : Basic Auth password  (no default — must be set)
    ///   - EnableMock      : if false, the server exits at startup
    ///   - ListenPort      : HTTPS port           (default: 7042)
    /// </summary>
    internal static class JdeMockServer
    {
        public static int Run(string[] args)
        {
            // WebApplication.CreateBuilder defaults ContentRootPath to CWD, which
            // for ActivityTester is whatever the .exe is launched from. Pin it to
            // AppContext.BaseDirectory so the bundled appsettings.json is found.
            var opts = new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                Args            = args,
            };
            var builder = WebApplication.CreateBuilder(opts);

            // Use the same appsettings.json the rest of the harness uses — it
            // already lives next to the .exe and has the JdeMock section.
            builder.Configuration
                   .SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   .AddEnvironmentVariables();

            bool enabled  = builder.Configuration.GetValue("JdeMock:EnableMock", true);
            int  port     = builder.Configuration.GetValue("JdeMock:ListenPort", 7042);
            string user   = builder.Configuration["JdeMock:Username"];
            string pass   = builder.Configuration["JdeMock:Password"];

            if (!enabled)
            {
                Console.WriteLine("JDE Mock is DISABLED (JdeMock:EnableMock=false). Exiting.");
                return 0;
            }
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                Console.Error.WriteLine(
                    "ERROR: JdeMock:Username and JdeMock:Password must be set in appsettings.json " +
                    "before the mock can start.");
                return 1;
            }

            // Bind ONLY to localhost (HTTPS) so the mock isn't exposed to the
            // network. Case Portal integration runs on the same box, so this
            // is sufficient. Uses the .NET dev cert — run
            //     dotnet dev-certs https --trust
            // once per machine if you haven't already (browsers + curl will
            // otherwise reject the certificate as untrusted).
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenLocalhost(port, lo => lo.UseHttps());
            });

            // ----- Auth + MVC + DI -----
            builder.Services
                .AddAuthentication(BasicAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                    BasicAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();

            builder.Services.AddControllers()
                .AddJsonOptions(json =>
                {
                    // Property names on the wire are controlled by
                    // [JsonPropertyName] attributes (e.g. "jde__status"). Disable
                    // the camelCase default so PascalCase fields without an
                    // explicit attribute serialize as-is, matching real JDE.
                    json.JsonSerializerOptions.PropertyNamingPolicy = null;
                    // Real JDE responses are formatted with newlines + indent;
                    // mimic that to keep "diff against curl" comparisons clean.
                    json.JsonSerializerOptions.WriteIndented = true;
                });

            builder.Services.AddSingleton<MockDataService>();

            // CORS — Form.io selects in the Case Portal UI render in the browser
            // and fetch from this mock cross-origin (Portal at http://localhost:22222
            // → mock at https://localhost:7042). Without an Access-Control-Allow-Origin
            // response header the browser blocks the request and the dropdown stays
            // empty. We allow any localhost / 127.0.0.1 origin on any port — safe
            // for a dev-only mock, would NOT be appropriate in production.
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.SetIsOriginAllowed(origin =>
                 {
                     if (string.IsNullOrEmpty(origin)) return false;
                     try
                     {
                         var u = new System.Uri(origin);
                         return u.Host == "localhost" || u.Host == "127.0.0.1";
                     }
                     catch { return false; }
                 })
                 .AllowAnyHeader()
                 .AllowAnyMethod()
                 .AllowCredentials()));

            // ----- Logging — keep things readable in the console -----
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(c =>
            {
                c.SingleLine     = true;
                c.TimestampFormat = "HH:mm:ss  ";
            });
            // appsettings.json sets "Logging.LogLevel.Default" to "Warning" so the
            // Portal's noisy startup doesn't spam the activity-test console. For
            // the mock-API mode we want to SEE incoming requests, so override
            // the filter for our namespace specifically.
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter("ActivityTester.JdeMock", LogLevel.Information);

            var app = builder.Build();
            // CORS must run BEFORE authentication so browser preflight OPTIONS
            // requests (which carry no Authorization header) get a 200, not 401.
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();

            // ----- Friendly startup banner -----
            string baseUrl = $"https://localhost:{port}/jderest/orchestrator/";
            Console.WriteLine();
            Console.WriteLine("===========================================================");
            Console.WriteLine("  JDE Orchestrator MOCK is running");
            Console.WriteLine("  Base URL : " + baseUrl);
            Console.WriteLine($"  Auth     : Basic  (user='{user}', password=<from appsettings>)");
            Console.WriteLine("  Endpoints:");
            Console.WriteLine("    POST  " + baseUrl + "GetDepartmentList");
            Console.WriteLine("    POST  " + baseUrl + "GetEmployeeInfoByEmail");
            Console.WriteLine("    POST  " + baseUrl + "GetJobsList");
            Console.WriteLine();
            Console.WriteLine("  Press Ctrl+C to stop.");
            Console.WriteLine("===========================================================");
            Console.WriteLine();

            app.Run();
            return 0;
        }
    }
}
