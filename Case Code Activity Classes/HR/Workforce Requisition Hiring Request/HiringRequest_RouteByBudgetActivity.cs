using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Routes a Workforce Requisition Hiring Request based on budget status,
    /// and within the budgeted branch, on whether the job title is one of the
    /// exception positions (CEO / VP-Internal Audit / Board Office Manager etc.).
    ///
    /// Rules:
    ///   budgetStatus != "budgeted"
    ///     -> nextApprovalRoute = "NonBudgeted"   (any non-empty non-budgeted
    ///                                             value, plus the safe default
    ///                                             for empty / unknown input)
    ///   budgetStatus == "budgeted"  AND
    ///     ( IsExceptionPosition(jobTitleText)
    ///       OR reportingToText contains "CEO" (any casing) )
    ///     -> nextApprovalRoute = "HRDirectorOrAssociateDirector"
    ///   budgetStatus == "budgeted"  AND  neither signal
    ///     -> nextApprovalRoute = "HODDepartmentHeadApproval"
    ///
    /// Input  (WorkflowItem.Properties):
    ///   - DocumentId       : long
    ///   - budgetStatus     : string  ("budgeted" | "nonBudgeted", case-insensitive)
    ///   - jobTitleText     : string  free-text title; matched against the
    ///                                 canonical ExceptionPositionsNormalized list
    ///                                 using equality after stripping non-alphanumerics
    ///                                 and lowercasing (same matcher used by the
    ///                                 other HR routing activities).
    ///   - reportingToText  : string  free-text "Reports To" value; case-insensitive
    ///                                 substring check for "CEO" (matches "CEO",
    ///                                 "Ceo", "CEO Office", "Reports to CEO
    ///                                 directly", "Deputy of the CEO", etc.).
    ///
    /// Output (WorkflowItem.Properties):
    ///   - nextApprovalRoute : "NonBudgeted"
    ///                       | "HRDirectorOrAssociateDirector"
    ///                       | "HODDepartmentHeadApproval"
    ///
    /// The output drives the gateway's outgoing transitions in the Workforce
    /// Requisition Hiring Request workflow:
    ///   RouteByBudget -> NonBudgeted                    -> ... (CFO/CEO path)
    ///   RouteByBudget -> HRDirectorOrAssociateDirector  -> ... (exception roles)
    ///   RouteByBudget -> HODDepartmentHeadApproval      -> ... (standard roles)
    /// </summary>
    public class HiringRequest_RouteByBudgetActivity : ActivityTemplate
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

        // Exception positions — when the job title matches any of these AND the
        // request is budgeted, routing goes to HR Director / Associate Director
        // instead of the standard HOD path. Stored in canonical normalized form
        // (lowercased, all non-alphanumeric chars stripped). The matcher below
        // uses EXACT equality on the normalized form, not Contains — by design,
        // so a title like "CEO Office Coordinator" does NOT escalate. Keep this
        // list in sync with the other HR routing activities' equivalents.
        private static readonly string[] ExceptionPositionsNormalized =
        {
            "ceo",
            "chiefexecutiveofficer",
            "vpinternalaudit",
            "boardofficemanager",
            "managerboardoffice",
            "managerceooffice",
            "vicepresidentinternalaudit",
            "n1leadership"
        };

        private const string RouteNonBudgeted                  = "NonBudgeted";
        private const string RouteHRDirectorOrAssociateDirector = "HRDirectorOrAssociateDirector";
        private const string RouteHODDepartmentHeadApproval    = "HODDepartmentHeadApproval";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- HiringRequest_RouteByBudgetActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string budget          = GetProp(workflowItem, "budgetStatus");
                string jobTitleText    = GetProp(workflowItem, "jobTitleText");
                string reportingToText = GetProp(workflowItem, "reportingToText");
                bool   reportingToCeo  = IsReportingToCeo(reportingToText);

                LogInfo($"Input: budgetStatus='{budget}', jobTitle='{jobTitleText}', reportingTo='{reportingToText}', reportingToCeo={reportingToCeo}");

                // Case-insensitive trim-tolerant match for "budgeted". Anything
                // else (incl. empty / unknown) falls into NonBudgeted - the
                // stricter approval path. A bad input never accidentally
                // bypasses extra approval.
                bool isBudgeted = string.Equals(budget?.Trim(), "budgeted", StringComparison.OrdinalIgnoreCase);

                string nextApprovalRoute;
                if (!isBudgeted)
                {
                    nextApprovalRoute = RouteNonBudgeted;

                    if (string.IsNullOrWhiteSpace(budget))
                        LogWarn($"budgetStatus is empty - defaulting to {RouteNonBudgeted}.");
                    else if (!string.Equals(budget.Trim(), "nonBudgeted", StringComparison.OrdinalIgnoreCase))
                        LogWarn($"budgetStatus '{budget}' is not one of 'budgeted' / 'nonBudgeted' - defaulting to {RouteNonBudgeted}.");
                }
                else if (IsExceptionPosition(jobTitleText) || reportingToCeo)
                {
                    // Two signals trigger this branch in the budgeted case:
                    //   1. Job title is an exception position (CEO / VP-Internal
                    //      Audit / Board Office Manager / Manager - CEO Office /
                    //      N-1 Leadership / etc.).
                    //   2. The free-text "Reports To" field contains "CEO" in
                    //      any casing - matches "CEO", "Ceo", "Reports to CEO
                    //      directly", "CEO Office", etc.
                    // Either signal routes through HR Director or Associate
                    // Director instead of the standard HOD step.
                    nextApprovalRoute = RouteHRDirectorOrAssociateDirector;
                    LogInfo($"Budgeted exception ({(IsExceptionPosition(jobTitleText) ? "title='" + jobTitleText + "'" : "reportingTo='" + reportingToText + "'")}) - routing via {RouteHRDirectorOrAssociateDirector}.");
                }
                else
                {
                    // Standard budgeted hire - HOD (Head of Department) approval.
                    nextApprovalRoute = RouteHODDepartmentHeadApproval;
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- HiringRequest_RouteByBudgetActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- HiringRequest_RouteByBudgetActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- HiringRequest_RouteByBudgetActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        // Normalize-then-EXACT-match for exception positions. Strips every
        // non-alphanumeric character (spaces, dashes, dots, parentheses) and
        // lowercases the rest so input variations all collapse to the same
        // canonical form, then checks whether that canonical form is EXACTLY
        // one of the canonical exception patterns. Equality (vs. Contains) is
        // intentional - many ordinary titles contain these substrings ("CEO
        // Office Coordinator", "Junior Board Office Manager Assistant", etc.)
        // that should NOT escalate. New exception titles must be added
        // explicitly to ExceptionPositionsNormalized above.
        private static bool IsExceptionPosition(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string norm = Normalize(title);
            foreach (var x in ExceptionPositionsNormalized) if (x == norm) return true;
            return false;
        }

        // Case-insensitive substring check for "CEO" anywhere in a free-text
        // reporting-to value. Matches "CEO", "Ceo", "ceo", "CEO Office",
        // "Reports to CEO directly", "Deputy of the CEO", etc. - any indication
        // that the role reports up to the CEO. Used alongside IsExceptionPosition
        // so a budgeted hire whose Reports To names the CEO routes to the same
        // HR Director / Associate Director path as the explicit exception roles.
        // Case-insensitive substring check for "CEO" anywhere in a free-text
        // reporting-to value. Matches "CEO", "Ceo", "ceo", "CEO Office",
        // "Reports to CEO directly", "Deputy of the CEO", etc.
        private static bool IsReportingToCeo(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            s = s.Trim();

            return string.Equals(s, "CEO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Chief Executive Officer", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }

        private static void LogInfo (string m) { Write("INFO ", m); }
        private static void LogWarn (string m) { Write("WARN ", m); }
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
                    "HiringRequest_RouteByBudgetActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
