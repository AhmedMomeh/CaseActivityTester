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
    /// After an approval task closes, resolves the **manager of the case
    /// creator's organizational structure** and writes that user id into a
    /// workflow property so the next Task step routes there.
    ///
    /// User and Structure data live in IAM (not the Case DB), so this activity
    /// calls IAM over HTTP using the same OAuth password-grant pattern the
    /// other activities (BuildApprovalHistoryActivity etc.) already use.
    ///
    /// Data flow:
    ///   Document (the case)
    ///     → CreatedByUserId         (user id of who opened the case)
    ///       → IAM /Api/GetUser?id=… → structureId
    ///         → IAM /Api/GetStructure?id=… → managerUserId
    ///
    /// IAM response shapes vary between versions, so the JSON probing logic
    /// tries the common field-name conventions in order (camelCase, PascalCase,
    /// flat fields, nested objects, arrays). If your IAM build returns a
    /// shape none of those match, add another key to the probe lists in
    /// <see cref="ExtractStructureId"/> / <see cref="ExtractManagerId"/>.
    ///
    /// Inputs (workflow item properties):
    ///   DocumentId           — required, numeric Document.Id
    ///
    /// Outputs (workflow item properties set by this activity):
    ///   creatorUserId        — the user who opened the case
    ///   creatorStructureId   — that user's structure id
    ///   nextApprovalUserId   — manager of that structure (this is the routing key)
    /// </summary>
    public class RouteToCreatorStructureManagerActivity : ActivityTemplate
    {
        // -------- Config (all lazy + try/catch so the type loads even when
        // appsettings.json is unreachable at type-load time — Case Designer
        // otherwise reports "Please choose a class that inherits from
        // ActivityTemplate" because the cctor threw). --------

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

        // Resolved on first call (after the activity actually runs), so a
        // missing key surfaces in the Execute log — not at type load.
        private static string IamBaseUrl       => Cfg("CaseActivities:IAM:IamBaseUrl",      "http://localhost:11111");
        private static string AuthUserName     => Cfg("CaseActivities:IAM:UserName",        "admin");
        private static string AuthUserPassword => Cfg("CaseActivities:IAM:UserPassword",    "1");
        private static string AuthClientId     => Cfg("CaseActivities:CaseAuth:ClientId",     "");
        private static string AuthClientSecret => Cfg("CaseActivities:CaseAuth:ClientSecret", "");

        // -------- Workflow property keys --------
        // Change OutNextApprovalUserId to match whatever property the next Task
        // step reads (common alternatives: "assigneeUserId", "nextApproverUserId").
        private const string OutNextApprovalUserId  = "nextApprovalUserId";
        private const string OutCreatorUserId       = "creatorUserId";
        private const string OutCreatorStructureId  = "creatorStructureId";

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
                    LogError("Document.CreatedByUserId is empty — cannot resolve manager.");
                    return;
                }

                // 2) Resolve structure + manager via IAM (one or two HTTP calls).
                var resolved = System.Threading.Tasks.Task
                    .Run(() => ResolveStructureAndManagerAsync(creatorId))
                    .GetAwaiter().GetResult();

                if (!resolved.structureId.HasValue)
                {
                    LogError($"IAM /Api/GetUser?id={creatorId} returned no structureId.");
                    return;
                }
                if (!resolved.managerId.HasValue)
                {
                    LogError(
                        $"Structure {resolved.structureId.Value} has no manager in IAM — " +
                        "the structure exists but no manager is assigned.");
                    return;
                }

                LogInfo(
                    $"Creator {creatorId} → Structure {resolved.structureId.Value} → " +
                    $"Manager userId {resolved.managerId.Value}");

                // 3) Write the routing properties for the next workflow step.
                SetProp(workflowItem, OutCreatorUserId,      creatorId.ToString());
                SetProp(workflowItem, OutCreatorStructureId, resolved.structureId.Value.ToString());
                SetProp(workflowItem, OutNextApprovalUserId, resolved.managerId.Value.ToString());

                LogInfo($"---- END  DocumentId={documentId}  nextApprovalUserId={resolved.managerId.Value}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ----- IAM HTTP calls ----------------------------------------------

        private async Task<(long? structureId, long? managerId)> ResolveStructureAndManagerAsync(long userId)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                string token = await GetTokenAsync(http);
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                // ---- GetUser ----
                string userUrl = $"{IamBaseUrl.TrimEnd('/')}/Api/GetUser?id={userId}";
                LogInfo($"GET {userUrl}");
                var userResp = await http.GetAsync(userUrl);
                string userBody = await userResp.Content.ReadAsStringAsync();
                if (!userResp.IsSuccessStatusCode)
                    throw new Exception($"IAM /Api/GetUser HTTP {(int)userResp.StatusCode}: {userBody}");

                var userJson = JObject.Parse(userBody);
                long? structureId = ExtractStructureId(userJson);
                if (!structureId.HasValue) return (null, null);

                // ---- Manager: try inline first, then GetStructure ----
                long? managerInline = ExtractManagerFromUserJson(userJson, structureId.Value);
                if (managerInline.HasValue)
                {
                    LogInfo("Manager found inline in /Api/GetUser response.");
                    return (structureId, managerInline);
                }

                string structUrl = $"{IamBaseUrl.TrimEnd('/')}/Api/GetStructure?id={structureId.Value}";
                LogInfo($"GET {structUrl}");
                var structResp = await http.GetAsync(structUrl);
                string structBody = await structResp.Content.ReadAsStringAsync();
                if (!structResp.IsSuccessStatusCode)
                    throw new Exception($"IAM /Api/GetStructure HTTP {(int)structResp.StatusCode}: {structBody}");

                var structJson = JObject.Parse(structBody);
                long? managerId = ExtractManagerId(structJson);
                return (structureId, managerId);
            }
        }

        // OAuth password grant against IAM — same shape every Intalio install uses.
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

        // ----- JSON probing (defensive — IAM field names vary by version) ------

        // The user's structure id may show up under any of these:
        //   userJson.structureId   (or PascalCase)
        //   userJson.structure.id  (single nested object)
        //   userJson.structures[0].id  (array)
        private static long? ExtractStructureId(JObject user)
        {
            foreach (var key in new[] { "structureId", "StructureId", "structure_id" })
            {
                long? v = AsLong(user[key]);
                if (v.HasValue) return v;
            }
            foreach (var key in new[] { "structure", "Structure" })
            {
                if (user[key] is JObject so)
                {
                    long? v = AsLong(so["id"] ?? so["Id"]);
                    if (v.HasValue) return v;
                }
            }
            foreach (var key in new[] { "structures", "Structures" })
            {
                if (user[key] is JArray arr && arr.Count > 0 && arr[0] is JObject so0)
                {
                    long? v = AsLong(so0["id"] ?? so0["Id"]);
                    if (v.HasValue) return v;
                }
            }
            return null;
        }

        // Some IAM builds embed the manager id directly on the structure object
        // inside the user response — avoid a second HTTP call when they do.
        private static long? ExtractManagerFromUserJson(JObject user, long structureId)
        {
            // Single-structure case
            foreach (var key in new[] { "structure", "Structure" })
            {
                if (user[key] is JObject s)
                {
                    long? m = ExtractManagerId(s);
                    if (m.HasValue) return m;
                }
            }
            // Array case — match by id
            foreach (var key in new[] { "structures", "Structures" })
            {
                if (!(user[key] is JArray arr)) continue;
                foreach (var item in arr)
                {
                    if (!(item is JObject so)) continue;
                    long? sid = AsLong(so["id"] ?? so["Id"]);
                    if (!sid.HasValue || sid.Value != structureId) continue;
                    long? m = ExtractManagerId(so);
                    if (m.HasValue) return m;
                }
            }
            return null;
        }

        // Manager id may be a flat field or a nested object (manager: { id: ... }).
        private static long? ExtractManagerId(JObject s)
        {
            foreach (var mk in new[] {
                "managerId", "ManagerId",
                "managerUserId", "ManagerUserId",
                "managerUser_id", "manager_id"
            })
            {
                long? v = AsLong(s[mk]);
                if (v.HasValue) return v;
            }
            foreach (var key in new[] { "manager", "Manager", "managerUser", "ManagerUser" })
            {
                if (s[key] is JObject m)
                {
                    long? v = AsLong(m["id"] ?? m["Id"] ?? m["userId"] ?? m["UserId"]);
                    if (v.HasValue) return v;
                }
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
                    $"RouteToCreatorStructureManagerActivity-{DateTime.Now:yyyy-MM-dd}.log");
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
