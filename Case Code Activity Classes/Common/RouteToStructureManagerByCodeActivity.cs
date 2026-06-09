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
    /// Routes the next approval to the **manager of the structure** identified
    /// by a department/structure CODE read from a workflow property (default
    /// <c>departmentCode</c>). Useful when the form captures a department code
    /// (e.g. "101IT") and the workflow needs to route the next task to the
    /// manager of that department.
    ///
    /// Data flow:
    ///   workflowItem.Properties["departmentCode"]   (e.g. "101IT")
    ///      → IAM /Api/SearchStructures              (returns all structures)
    ///         → filter where structure.code == departmentCode (case-insensitive)
    ///            → structure.managerId
    ///               → workflowItem.Properties["nextApprovalUserId"]
    ///
    /// IAM has no "GetStructureByCode" endpoint — SearchStructures returns
    /// the full list and we filter client-side. The list is small in practice
    /// (hundreds of structures, not millions), so this is fine.
    ///
    /// Inputs (workflow item properties):
    ///   DocumentId       — required, numeric Document.Id
    ///   departmentCode   — required, the structure's code (e.g. "101IT")
    ///
    /// Outputs (workflow item properties set by this activity):
    ///   structureId           — id of the matched structure
    ///   structureManagerId    — manager user id (alias of nextApprovalUserId)
    ///   nextApprovalUserId    — manager user id (the routing key)
    /// </summary>
    public class RouteToStructureManagerByCodeActivity : ActivityTemplate
    {
        // -------- Config (lazy + try/catch so the type loads cleanly even
        //                  when appsettings.json isn't reachable at type-load
        //                  time — otherwise Designer rejects the class) --
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

        // -------- Input / output property keys --------
        // Change these to match your workflow's property names.
        private const string InDepartmentCode      = "departmentCode";
        private const string OutStructureId        = "structureId";
        private const string OutStructureManagerId = "structureManagerId";
        
        private static readonly object LogLock = new object();

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr = GetProp(workflowItem, "DocumentId");
            string code     = GetProp(workflowItem, InDepartmentCode);
            LogInfo($"---- BEGIN  DocumentId={docIdStr}  departmentCode='{code}' ----");

            if (!long.TryParse(docIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{docIdStr}'");
                return;
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                LogError($"Workflow property '{InDepartmentCode}' is empty — cannot look up structure.");
                return;
            }

            try
            {
                var resolved = System.Threading.Tasks.Task
                    .Run(() => ResolveStructureManagerAsync(code.Trim()))
                    .GetAwaiter().GetResult();

                if (!resolved.structureId.HasValue)
                {
                    LogError($"No structure matched code='{code}' in IAM /Api/SearchStructures.");
                    return;
                }
                if (!resolved.managerId.HasValue)
                {
                    LogError($"Structure {resolved.structureId.Value} (code='{code}') has no managerId in IAM.");
                    return;
                }

                LogInfo($"Code '{code}' → Structure {resolved.structureId.Value} → Manager userId {resolved.managerId.Value}");

                SetProp(workflowItem, OutStructureId,        resolved.structureId.Value.ToString());
                SetProp(workflowItem, OutStructureManagerId, resolved.managerId.Value.ToString());                

                LogInfo($"---- END  DocumentId={documentId}  structureManagerId={resolved.managerId.Value}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ----- IAM calls ----------------------------------------------------

        private async Task<(long? structureId, long? managerId)> ResolveStructureManagerAsync(string code)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                string token = await GetTokenAsync(http);
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                // IAM returns ALL structures from this endpoint — we filter client-side.
                string url = $"{IamBaseUrl.TrimEnd('/')}/Api/SearchStructures";
                LogInfo($"GET {url}");
                var resp = await http.GetAsync(url);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"IAM /Api/SearchStructures HTTP {(int)resp.StatusCode}: {body}");

                var arr = JArray.Parse(body);
                LogInfo($"IAM returned {arr.Count} structure(s) — filtering by code='{code}' (case-insensitive).");

                foreach (var t in arr)
                {
                    if (!(t is JObject s)) continue;
                    string sc = (string)(s["code"] ?? s["Code"]);
                    if (string.IsNullOrEmpty(sc)) continue;
                    if (!string.Equals(sc.Trim(), code, StringComparison.OrdinalIgnoreCase)) continue;

                    long? sid = AsLong(s["id"] ?? s["Id"]);
                    long? mid = AsLong(s["managerId"] ?? s["ManagerId"]);
                    return (sid, mid);
                }

                return (null, null);
            }
        }

        // OAuth password grant — same pattern the other activities use.
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
                    $"RouteToStructureManagerByCodeActivity-{DateTime.Now:yyyy-MM-dd}.log");
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
