using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Activities
{
    public class HiringRequest_NextApprovalRoleActivity : ActivityTemplate
    {
        private static readonly string LogDirectory = CodeActivityConfig.Get("CaseActivities:LogDirectory");

        // Configuration reader — kept INSIDE this activity class so the file is
        // self-contained for Case Designer's single-file code-activity editor.
        private static class CodeActivityConfig
        {
            private static Newtonsoft.Json.Linq.JObject _root;
            private static string _path;
            private static readonly object _gate = new object();

            public static string Get(string keyPath)           => Resolve(keyPath, allowEmpty: false);
            public static string GetAllowEmpty(string keyPath) => Resolve(keyPath, allowEmpty: true);

            private static string Resolve(string keyPath, bool allowEmpty)
            {
                Load();
                Newtonsoft.Json.Linq.JToken node = _root;
                foreach (var part in keyPath.Split(':'))
                {
                    if (node is Newtonsoft.Json.Linq.JObject obj &&
                        obj.TryGetValue(part, System.StringComparison.OrdinalIgnoreCase, out var next))
                        node = next;
                    else
                        throw new System.InvalidOperationException(
                            $"Missing required setting '{keyPath}' in '{_path}'. " +
                            $"Add the key under 'CaseActivities' in the host appsettings.json.");
                }
                if (node == null || node.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                    throw new System.InvalidOperationException($"Setting '{keyPath}' is null in '{_path}'.");
                string value = node.ToString();
                if (!allowEmpty && string.IsNullOrEmpty(value))
                    throw new System.InvalidOperationException($"Setting '{keyPath}' is empty in '{_path}'. Set a non-empty value.");
                return value;
            }

            private static void Load()
            {
                if (_root != null) return;
                lock (_gate)
                {
                    if (_root != null) return;
                    foreach (var p in new[] {
                        System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "appsettings.json"),
                        System.IO.Path.Combine(System.AppContext.BaseDirectory,            "appsettings.json") })
                    {
                        if (!System.IO.File.Exists(p)) continue;
                        _root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(p));
                        _path = p;
                        return;
                    }
                    throw new System.InvalidOperationException(
                        "appsettings.json not found in current directory or app base directory.");
                }
            }
        }

        private static readonly object LogLock = new object();

        // Per Delegation of Authority:
        //   Exception positions              -> Board of Directors
        //   Non-Budgeted, Grade A..D         -> CEO
        //   Anything else                    -> RouteArchiveToDMS (no extra approval)

        // Senior grades trigger CEO approval on non-budgeted requests.
        // Matched with Contains, so "A", "A1", "B2", "C", "C2", "D", "D1",
        // "D2", "Grade-A", "  d  " all count as senior — while "E", "F2",
        // "G", "H" don't. A future grade letter not listed here automatically
        // falls through to the RouteArchiveToDMS path with no code change.
        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

        // Exception positions — when the job title matches any of these, the
        // request bypasses budget / grade logic and routes straight to BoD.
        // Stored normalized (lowercased, all non-alphanumeric chars stripped)
        // so the lookup tolerates spacing / dashes / dots / casing variations:
        //   "CEO" / "C.E.O." / "ceo"                       → match
        //   "VP-Internal Audit" / "VP Internal Audit"      → match
        //   "Board Office Manager" / "BoardOfficeManager"  → match
        //   "Manager - Board Office" / "Manager Board Off" → match
        //   "N-1 Leadership" / "N1Leadership"              → match
        private static readonly string[] ExceptionPositionsNormalized =
        {
            "ceo",
            "vpinternalaudit",
            "boardofficemanager",
            "managerboardoffice",
            "managerceooffice",
            "vicepresidentinternalaudit",
            "n1leadership"
        };

        private const string RouteBoardApproval = "BoardApproval";
        private const string RouteCEOApproval   = "CEOApproval";
        private const string RouteArchiveToDMS = "ArchiveToDMS";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- HiringRequest_NextApprovalRoleActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string position   = GetProp(workflowItem, "jobTitleText");
                string gradeLevel = GetProp(workflowItem, "gradeLevelText");
                string budget     = GetProp(workflowItem, "budgetStatus");
                bool   isNonBudgeted = string.Equals(budget?.Trim(), "nonBudgeted", StringComparison.OrdinalIgnoreCase);

                LogInfo($"Input: jobTitle='{position}', gradeLevel='{gradeLevel}', budgetStatus='{budget}'");

                string nextApprovalRoute;
                if (IsExceptionPosition(position))
                {
                    // CEO / VP-Internal Audit / Board Office Manager / N-1
                    // Leadership — these protected roles always escalate to
                    // the Board of Directors regardless of budget or grade.
                    nextApprovalRoute = RouteBoardApproval;
                    LogInfo($"Exception position '{position}' matched → routing via {RouteBoardApproval}.");
                }
                else if (isNonBudgeted && IsSenior(gradeLevel))
                {
                    // Non-budgeted A-D hires need CEO sign-off.
                    nextApprovalRoute = RouteCEOApproval;
                }
                else
                {
                    // Anything not senior (E, F, F2, G, H, empty, unknown)
                    // — CPCO was the final step; archive directly.
                    nextApprovalRoute = RouteArchiveToDMS;

                    if (string.IsNullOrWhiteSpace(gradeLevel))
                        LogWarn($"gradeLevel is empty — defaulting to {RouteArchiveToDMS}.");
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- HiringRequest_NextApprovalRoleActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- HiringRequest_NextApprovalRoleActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"----  HiringRequest_NextApprovalRoleActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem)
        {
            // No-op: this activity is fully automated.
        }

        // Case-insensitive, substring-based: "A", "A1", "B2", "C2", "D",
        // "D1", "D2", "Grade-A", "  d  " all match. "E", "F2", "G", "H" don't.
        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.ToUpperInvariant();
            foreach (var s in SeniorGrades) if (up.Contains(s)) return true;
            return false;
        }

        // Normalize-then-Contains match for exception positions. Strips every
        // non-alphanumeric character (spaces, dashes, dots, parentheses) and
        // lowercases the rest so input variations all collapse to the same
        // canonical form, then checks whether the canonical form CONTAINS any
        // of the canonical exception patterns. Contains (vs. equality) means
        // common prefix/suffix variations also match:
        //   "Vice President - Internal Audit"           -> matches "vicepresidentinternalaudit"
        //   "Acting Vice President - Internal Audit"    -> matches "vicepresidentinternalaudit"  (prefix)
        //   "VP - Internal Audit Department"            -> matches "vpinternalaudit"             (suffix)
        //   "Deputy Board Office Manager"               -> matches "boardofficemanager"          (prefix)
        //   "Senior CEO" / "Acting CEO" / "CEO Office"  -> match  "ceo"                          (any-position)
        // All routes through this list lead to the same downstream route (Board),
        // so an early "ceo" match short-circuiting a more specific entry is
        // harmless.
        private static bool IsExceptionPosition(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string norm = Normalize(title);
            foreach (var x in ExceptionPositionsNormalized) if (norm.Contains(x)) return true;
            return false;
        }

        private static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // ---------- helpers ----------
        private static string GetProp(WorkflowItem item, string key)
        {
            try
            {
                var val = item?.Properties?[key]?.Value;
                return val == null ? string.Empty : Convert.ToString(val);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void SetProp(WorkflowItem item, string key, object value)
        {
            if (item == null || item.Properties == null) return;
            try
            {
                var prop = item.Properties[key];
                if (prop != null) prop.Value = value;
            }
            catch
            {
                // property not declared on this workflow � silently skip
            }
        }

        private static void LogInfo(string m) { Write("INFO ", m); }
        private static void LogWarn(string m) { Write("WARN ", m); }
        private static void LogError(string m) { Write("ERROR", m); }

        private static void LogException(string context, Exception ex)
        {
            var sb = new System.Text.StringBuilder().Append(context).Append(": ");
            int depth = 0;
            for (var e = ex; e != null; e = e.InnerException, depth++)
            {
                if (depth > 0) sb.Append(" --> ");
                sb.Append('[').Append(e.GetType().FullName).Append("] ").Append(e.Message);
            }
            Write("ERROR", sb.ToString());
            Write("ERROR", "STACK: " + (ex.StackTrace ?? "(no stack)"));
        }

        private static void Write(string level, string message)
        {
            try
            {
                string path = Path.Combine(LogDirectory,
                    "HiringRequest_NextApprovalRoleActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + level
                            + "  [tid:" + Thread.CurrentThread.ManagedThreadId + "]  " + message + Environment.NewLine;
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
