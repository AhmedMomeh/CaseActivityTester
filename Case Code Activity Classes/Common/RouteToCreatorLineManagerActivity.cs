using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Activities
{
    /// <summary>
    /// After an approval task closes, resolves the **line manager of the case
    /// creator** (the manager id stored directly on the user record in IAM —
    /// distinct from <see cref="RouteToCreatorLineManagerActivity"/>,
    /// which resolves "manager of the user's structure").
    ///
    /// Data flow:
    ///   Document (the case)
    ///     → CreatedByUserId          (user id of who opened the case)
    ///       → IAM /Api/GetUser?id=…  → user.managerId (or whatever IAM names it)
    ///
    /// Only ONE IAM call — the manager id lives on the user object itself, so
    /// no /Api/GetStructure round trip is needed. Probes a range of common
    /// field-name conventions (managerId / ManagerId / managerUserId /
    /// lineManagerId / nested manager.id / reportsTo / …) so the activity
    /// works regardless of which Intalio IAM build is deployed.
    ///
    /// Inputs (workflow item properties):
    ///   DocumentId           — required, numeric Document.Id
    ///
    /// Outputs (workflow item properties set by this activity):
    ///   creatorUserId        — the user who opened the case
    ///   nextLineManagerApprovalUserId   — line manager of that user (this is the routing key)
    /// </summary>
    public class RouteToCreatorLineManagerActivity : ActivityTemplate
    {
        // -------- Config (all lazy + try/catch so the type loads cleanly even
        //                  when appsettings.json isn't reachable at type-load
        //                  time — otherwise Designer rejects the class) ----

        private static string _logDirectory;
        private static string LogDirectory
        {
            get
            {
                if (_logDirectory != null) return _logDirectory;
                try { _logDirectory = CodeActivityConfig.Get("CaseActivities:LogDirectory"); }
                catch { _logDirectory = @"C:\Logs\Case"; }
                return _logDirectory;
            }
        }

        private static string Cfg(string key, string fallback)
        {
            try { return CodeActivityConfig.Get(key); }
            catch { return fallback; }
        }

        private static string IamBaseUrl       => Cfg("CaseActivities:IAM:IamBaseUrl",      "http://localhost:11111");
        private static string AuthUserName     => Cfg("CaseActivities:IAM:UserName",        "admin");
        private static string AuthUserPassword => Cfg("CaseActivities:IAM:UserPassword",    "1");
        private static string AuthClientId     => Cfg("CaseActivities:CaseAuth:ClientId",     "");
        private static string AuthClientSecret => Cfg("CaseActivities:CaseAuth:ClientSecret", "");

        // -------- Workflow property keys --------
        // Change OutNextLineManagerApprovalUserId to match the property the next Task
        // step reads (common alternatives: "assigneeUserId", "nextApproverUserId").
        private const string OutNextLineManagerApprovalUserId = "nextLineManagerApprovalUserId";
        private const string OutCreatorUserId       = "creatorUserId";

        private static readonly object LogLock = new object();

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- BEGIN  DocumentId={docIdStr} ----");

            if (!long.TryParse(docIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{docIdStr}'");
                return;
            }

            try
            {
                // 1) Find the case so we know who created it.
                Document document = new Document().Find(documentId);
                if (document == null)
                {
                    LogError($"Document.Find({documentId}) returned null.");
                    return;
                }

                long creatorId = document.CreatedByUserId;
                LogInfo($"Case creator userId = {creatorId}");
                if (creatorId <= 0)
                {
                    LogError("Document.CreatedByUserId is empty — cannot resolve line manager.");
                    return;
                }

                // 2) Resolve line manager via IAM /Api/GetUser.
                long? managerId = System.Threading.Tasks.Task
                    .Run(() => ResolveLineManagerAsync(creatorId))
                    .GetAwaiter().GetResult();

                if (!managerId.HasValue)
                {
                    LogError(
                        $"IAM /Api/GetUser?id={creatorId} returned no manager id — " +
                        "the user has no line manager assigned in IAM, or the JSON shape " +
                        "doesn't match any of the probed field names (see ExtractManagerId).");
                    return;
                }

                LogInfo($"Creator {creatorId} → Line manager userId {managerId.Value}");

                // 3) Write the routing properties for the next workflow step.
                SetProp(workflowItem, OutCreatorUserId,      creatorId.ToString());
                SetProp(workflowItem, OutNextLineManagerApprovalUserId, managerId.Value.ToString());

                LogInfo($"---- END  DocumentId={documentId}  nextLineManagerApprovalUserId={managerId.Value}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ----- IAM call ----------------------------------------------------

        private async Task<long?> ResolveLineManagerAsync(long userId)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                string token = await GetTokenAsync(http);
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                string userUrl = $"{IamBaseUrl.TrimEnd('/')}/Api/GetUser?id={userId}";
                LogInfo($"GET {userUrl}");
                var userResp = await http.GetAsync(userUrl);
                string body  = await userResp.Content.ReadAsStringAsync();
                if (!userResp.IsSuccessStatusCode)
                    throw new Exception($"IAM /Api/GetUser HTTP {(int)userResp.StatusCode}: {body}");

                return ExtractManagerId(JObject.Parse(body));
            }
        }

        // OAuth password grant — same pattern as the other activities.
        private async Task<string> GetTokenAsync(HttpClient http)
        {
            if (string.IsNullOrEmpty(AuthClientId) || string.IsNullOrEmpty(AuthClientSecret))
                throw new InvalidOperationException(
                    "CaseActivities:CaseAuth:ClientId / ClientSecret not configured.");

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id",     AuthClientId),
                new KeyValuePair<string, string>("client_secret", AuthClientSecret),
                new KeyValuePair<string, string>("grant_type",    "password"),
                new KeyValuePair<string, string>("username",      AuthUserName),
                new KeyValuePair<string, string>("password",      AuthUserPassword),
            });
            string tokenUrl = $"{IamBaseUrl.TrimEnd('/')}/connect/token";
            var resp = await http.PostAsync(tokenUrl, form);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"IAM /connect/token HTTP {(int)resp.StatusCode}: {body}");
            string accessToken = (string)JObject.Parse(body)["access_token"];
            if (string.IsNullOrEmpty(accessToken))
                throw new Exception("IAM /connect/token returned an empty access_token.");
            return accessToken;
        }

        // ----- JSON probing -------------------------------------------------
        // Tries every common field-name shape for "this user's line manager":
        //   user.managerId         (flat field, camelCase or PascalCase)
        //   user.managerUserId     (alt name some IAM builds use)
        //   user.lineManagerId     (UAE / HR-flavored IAM builds)
        //   user.reportsTo / reportsToId
        //   user.manager.id        (nested object — id / userId)
        //   user.manager.userId
        // If your IAM build uses a different field name, add it to the lists
        // below — the rest of the activity is data-shape-agnostic.
        private static long? ExtractManagerId(JObject user)
        {
            string[] flatKeys =
            {
                "managerId",     "ManagerId",
                "managerUserId", "ManagerUserId",
                "lineManagerId", "LineManagerId",
                "reportsToId",   "ReportsToId",
                "reportsTo",     "ReportsTo",
                "manager_id",    "manager_userid",
            };
            foreach (var k in flatKeys)
            {
                long? v = AsLong(user[k]);
                if (v.HasValue) return v;
            }

            string[] nestedKeys = { "manager", "Manager", "lineManager", "LineManager", "reportsTo" };
            foreach (var k in nestedKeys)
            {
                if (!(user[k] is JObject m)) continue;
                long? v = AsLong(m["id"] ?? m["Id"] ?? m["userId"] ?? m["UserId"]);
                if (v.HasValue) return v;
            }
            return null;
        }

        private static long? AsLong(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.String && long.TryParse((string)t, out var n)) return n;
            return null;
        }

        // ----- Workflow property helpers ----------------------------------

        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? "" : Convert.ToString(v); }
            catch { return ""; }
        }

        private static void SetProp(WorkflowItem item, string key, string value)
        {
            if (item == null || item.Properties == null) return;
            var existing = item.Properties[key];
            if (existing != null) existing.Value = value;
            else                  item.Properties.Add(new Property { Name = key, Value = value });
        }

        // ----- Logging (daily-rotated file in LogDirectory) ---------------

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory,
                    $"RouteToCreatorLineManagerActivity-{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [tid={Thread.CurrentThread.ManagedThreadId}] {message}{Environment.NewLine}";
                lock (LogLock) File.AppendAllText(path, line);
            }
            catch { /* swallow — never fail the activity over a log write */ }
        }
        private static void LogInfo(string m)  => Write("INFO",  m);
        private static void LogError(string m) => Write("ERROR", m);
        private static void LogException(string m, Exception ex) =>
            Write("ERROR", m + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);

        // -------- Self-contained config reader (for Designer single-file paste) --------
        private static class CodeActivityConfig
        {
            private static JObject _root;
            private static string  _path;
            private static readonly object _gate = new object();

            public static string Get(string keyPath)           => Resolve(keyPath, allowEmpty: false);
            public static string GetAllowEmpty(string keyPath) => Resolve(keyPath, allowEmpty: true);

            private static string Resolve(string keyPath, bool allowEmpty)
            {
                Load();
                JToken node = _root;
                foreach (var part in keyPath.Split(':'))
                {
                    if (node is JObject obj &&
                        obj.TryGetValue(part, StringComparison.OrdinalIgnoreCase, out var next))
                        node = next;
                    else
                        throw new InvalidOperationException(
                            $"Missing required setting '{keyPath}' in '{_path}'. " +
                            "Add the key under 'CaseActivities' in the host appsettings.json.");
                }
                if (node == null || node.Type == JTokenType.Null)
                    throw new InvalidOperationException($"Setting '{keyPath}' is null in '{_path}'.");
                string value = node.ToString();
                if (!allowEmpty && string.IsNullOrEmpty(value))
                    throw new InvalidOperationException($"Setting '{keyPath}' is empty in '{_path}'.");
                return value;
            }

            private static void Load()
            {
                if (_root != null) return;
                lock (_gate)
                {
                    if (_root != null) return;
                    foreach (var p in new[] {
                        Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
                        Path.Combine(AppContext.BaseDirectory,         "appsettings.json") })
                    {
                        if (!File.Exists(p)) continue;
                        _root = JObject.Parse(File.ReadAllText(p));
                        _path = p;
                        return;
                    }
                    throw new InvalidOperationException("appsettings.json not found.");
                }
            }
        }
    }
}
