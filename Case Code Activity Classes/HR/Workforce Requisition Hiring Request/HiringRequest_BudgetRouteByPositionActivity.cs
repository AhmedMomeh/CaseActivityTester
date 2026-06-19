using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Routes a Workforce Requisition Hiring Request to its next approver
    /// purely by position group (job title + reports-to). Replaces the old
    /// HiringRequest_NextApprovalRoleActivity, which also factored in budget
    /// and grade - those concerns now live in HiringRequest_RouteByBudgetActivity.
    ///
    /// Position groups + their downstream route (checked in this order):
    ///
    ///   1.  Reports to CEO  OR  job title in {Board Office Manager / Manager - Board Office}
    ///         -> nextApprovalRoute = "CEO"
    ///   2.  Job title in {VP - Internal Audit / Vice President - Internal Audit}
    ///         -> nextApprovalRoute = "AC"            (Audit Committee)
    ///   3.  Job title in {CEO / Chief Executive Officer}
    ///         -> nextApprovalRoute = "NRC"           (Nomination & Remuneration Committee)
    ///   4.  Anything else
    ///         -> nextApprovalRoute = "ArchiveToDMS"  (no further committee step)
    ///
    /// The first matching rule wins, so a VP Internal Audit who reports to
    /// the CEO routes through CEO (rule 1), not AC (rule 2). That's
    /// intentional: reporting-to-CEO is the stronger signal.
    ///
    /// Title matching uses the same canonical normalization as the other HR
    /// routing activities (lowercase + strip every non-alphanumeric char) so
    /// "VP-Internal Audit", "VP Internal Audit", "vp.internal.audit" all
    /// collapse to "vpinternalaudit" and match. The reporting-to test is a
    /// case-insensitive substring check for "CEO" inside the free-text
    /// reportingToText.
    ///
    /// Input  (WorkflowItem.Properties):
    ///   - DocumentId       : long
    ///   - jobTitleText     : string  free-text job title
    ///   - reportingToText  : string  free-text "Reports To"
    ///
    /// Output (WorkflowItem.Properties):
    ///   - nextApprovalRoute : "CEO" | "AC" | "NRC" | "ArchiveToDMS"
    /// </summary>
    public class HiringRequest_BudgetRouteByPositionActivity : ActivityTemplate
    {
        private static readonly string LogDirectory = CodeActivityConfig.Get("CaseActivities:LogDirectory");

        // Configuration reader - kept INSIDE this activity class so the file is
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

        // Position groups - each one a list of canonical normalized titles
        // (lowercased, every non-alphanumeric char stripped). Matched with EXACT
        // equality on the normalized form, not Contains, so an incidental
        // substring like "CEO" inside "CEO Office Coordinator" does NOT
        // escalate. New title variants must be added to the relevant group.

        // Rule 1 -> CEO route (also triggered by reportingToCeo)
        private static readonly string[] BoardOfficeManagerGroup =
        {
            "boardofficemanager",
            "managerboardoffice",
        };

        // Rule 2 -> AC (Audit Committee) route
        private static readonly string[] VPInternalAuditGroup =
        {
            "vpinternalaudit",
            "vicepresidentinternalaudit",
        };

        // Rule 3 -> NRC (Nomination & Remuneration Committee) route
        private static readonly string[] CEOTitleGroup =
        {
            "ceo",
            "chiefexecutiveofficer",
        };

        private const string RouteCEO          = "CEO";
        private const string RouteAC           = "AC";
        private const string RouteNRC          = "NRC";
        private const string RouteArchiveToDMS = "ArchiveToDMS";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- HiringRequest_BudgetRouteByPositionActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string jobTitleText    = GetProp(workflowItem, "jobTitleText");
                string reportingToText = GetProp(workflowItem, "reportingToText");

                string norm           = Normalize(jobTitleText);
                bool   reportingToCeo = IsReportingToCeo(reportingToText);

                LogInfo($"Input: jobTitle='{jobTitleText}', reportingTo='{reportingToText}', reportingToCeo={reportingToCeo}, normalizedTitle='{norm}'");

                string nextApprovalRoute;
                if (reportingToCeo || IsInGroup(norm, BoardOfficeManagerGroup))
                {
                    // Reports to CEO OR Board Office Manager / Manager - Board Office
                    nextApprovalRoute = RouteCEO;
                    LogInfo($"Matched CEO route ({(reportingToCeo ? "reportingToCeo" : "title='" + jobTitleText + "'")}) -> {RouteCEO}.");
                }
                else if (IsInGroup(norm, VPInternalAuditGroup))
                {
                    // VP - Internal Audit / Vice President - Internal Audit
                    nextApprovalRoute = RouteAC;
                    LogInfo($"Matched AC route (title='{jobTitleText}') -> {RouteAC}.");
                }
                else if (IsInGroup(norm, CEOTitleGroup))
                {
                    // CEO / Chief Executive Officer (the position itself, not the reportee)
                    nextApprovalRoute = RouteNRC;
                    LogInfo($"Matched NRC route (title='{jobTitleText}') -> {RouteNRC}.");
                }
                else
                {
                    // None of the committee/CEO escalation rules apply - terminal.
                    nextApprovalRoute = RouteArchiveToDMS;
                    LogWarn($"No position-group match for title='{jobTitleText}' (normalized='{norm}') - defaulting to {RouteArchiveToDMS}.");
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- HiringRequest_BudgetRouteByPositionActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- HiringRequest_BudgetRouteByPositionActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- HiringRequest_BudgetRouteByPositionActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        // Exact-equality membership test on a normalized title group.
        private static bool IsInGroup(string normalizedTitle, string[] group)
        {
            if (string.IsNullOrEmpty(normalizedTitle)) return false;
            foreach (var x in group) if (x == normalizedTitle) return true;
            return false;
        }

        // Case-insensitive substring check for "CEO" anywhere in a free-text
        // reporting-to value. Matches "CEO", "Ceo", "ceo", "CEO Office",
        // "Reports to CEO directly", "Deputy of the CEO", etc.
        private static bool IsReportingToCeo(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.ToUpperInvariant().Contains("CEO");
        }

        // Normalize a title: lowercase + strip every non-alphanumeric char.
        // "VP - Internal Audit" -> "vpinternalaudit"; "C.E.O." -> "ceo".
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
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
                    "HiringRequest_BudgetRouteByPositionActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
