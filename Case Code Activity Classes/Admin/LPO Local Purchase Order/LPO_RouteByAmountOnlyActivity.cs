using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Routes an LPO (Local Purchase Order) approval based purely on the
    /// total AMOUNT. Companion to LPO_RouteByAmountAndTypeActivity for
    /// workflows that don't care about contract vs. non-contract split.
    ///
    /// Rules:
    ///   amount >= 25,000   -> nextApprovalRoute = "NeedCBSO"       (CBSO endorsement required)
    ///   amount <  25,000   -> nextApprovalRoute = "Archive"   (archive directly, no further approval)
    ///
    /// Inputs (workflow item properties):
    ///   - DocumentId   : long
    ///   - totalAmount  : decimal, the LPO total in AED
    ///
    /// Output:
    ///   - nextApprovalRoute  : "NeedCBSO" | "Archive"
    /// </summary>
    public class LPO_RouteByAmountOnlyActivity : ActivityTemplate
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

        // CBSO endorsement is required on LPOs of AED 25,000 and above;
        // everything below is archived directly with no further approval.
        // Kept identical to LPO_RouteByAmountAndTypeActivity's CEO_THRESHOLD
        // so the two activities pivot on the same numeric boundary.
        private const decimal CBSO_THRESHOLD = 25000;

        private const string RouteNeedCBSO     = "NeedCBSO";
        private const string RouteArchive = "Archive";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- LPO_RouteByAmountOnlyActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string  amountStr = GetProp(workflowItem, "totalAmount");
                decimal amount    = ParseDecimal(amountStr);

                LogInfo($"Input: totalAmount='{amountStr}' parsed={amount}");

                string nextApprovalRoute;
                if (amount >= CBSO_THRESHOLD)
                {
                    // LPO at or above the CBSO threshold - CBSO sign-off required.
                    nextApprovalRoute = RouteNeedCBSO;
                    LogInfo($"LPO amount={amount} >= threshold={CBSO_THRESHOLD} - routing via {RouteNeedCBSO}.");
                }
                else
                {
                    // Below threshold - archive directly, no further approval.
                    nextApprovalRoute = RouteArchive;
                    LogInfo($"LPO amount={amount} <  threshold={CBSO_THRESHOLD} - routing via {RouteArchive}.");
                }

                if (string.IsNullOrWhiteSpace(amountStr))
                    LogWarn($"totalAmount is empty - parsed as 0, defaulting to {RouteArchive}.");

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- LPO_RouteByAmountOnlyActivity nextApprovalRoute={nextApprovalRoute} (threshold={CBSO_THRESHOLD}) ");
                LogInfo($"---- LPO_RouteByAmountOnlyActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- LPO_RouteByAmountOnlyActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }

        private static decimal ParseDecimal(string s)
        { decimal d; return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0m; }

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
                    "LPO_RouteByAmountOnlyActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
