using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Routes a Workforce Requisition Hiring Request based purely on whether
    /// the position is budgeted or not.
    ///
    /// Rules:
    ///   budgetStatus = "budgeted"     -> nextApprovalRoute = "Budgeted"
    ///   budgetStatus = "nonBudgeted"  -> nextApprovalRoute = "NonBudgeted"
    ///   any other value (incl. empty) -> nextApprovalRoute = "NonBudgeted"  (safer default,
    ///                                                                       same as missing budget)
    ///
    /// Input  (WorkflowItem.Properties):
    ///   - DocumentId    : long
    ///   - budgetStatus  : string  ("budgeted" | "nonBudgeted", case-insensitive)
    ///
    /// Output (WorkflowItem.Properties):
    ///   - nextApprovalRoute : "Budgeted" | "NonBudgeted"
    ///
    /// The output drives the gateway's outgoing transitions in the Workforce
    /// Requisition Hiring Request workflow:
    ///   RouteByBudget -> Budgeted     -> ... (continues without extra approval)
    ///   RouteByBudget -> NonBudgeted  -> ... (requires extra approval, e.g. CFO/CEO)
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

        private const string RouteBudgeted    = "Budgeted";
        private const string RouteNonBudgeted = "NonBudgeted";

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
                string budget = GetProp(workflowItem, "budgetStatus");

                LogInfo($"Input: budgetStatus='{budget}'");

                // Case-insensitive trim-tolerant match. "budgeted" (any casing /
                // padding) -> Budgeted route; everything else -> NonBudgeted.
                // We default to NonBudgeted (the stricter approval path) when
                // the value is missing or unrecognised so a bad input never
                // bypasses extra approval by accident.
                bool isBudgeted = string.Equals(budget?.Trim(), "budgeted", StringComparison.OrdinalIgnoreCase);
                string nextApprovalRoute = isBudgeted ? RouteBudgeted : RouteNonBudgeted;

                if (string.IsNullOrWhiteSpace(budget))
                    LogWarn($"budgetStatus is empty - defaulting to {RouteNonBudgeted}.");
                else if (!isBudgeted &&
                         !string.Equals(budget.Trim(), "nonBudgeted", StringComparison.OrdinalIgnoreCase))
                    LogWarn($"budgetStatus '{budget}' is not one of 'budgeted' / 'nonBudgeted' - defaulting to {RouteNonBudgeted}.");

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
