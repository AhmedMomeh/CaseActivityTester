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
    /// Routes an LPO (Local Purchase Order) approval based on the purchase
    /// order TYPE and the total AMOUNT.
    ///
    /// Rules:
    ///   Contract          (any amount)      -> nextApprovalRoute = "Direct"    (CBSO already approved; archive)
    ///   Non-Contract, >= 25,000             -> nextApprovalRoute = "NeedCEO"   (CEO endorsement required)
    ///   Non-Contract, <  25,000             -> nextApprovalRoute = "Direct"    (CBSO already approved; archive)
    ///
    /// Inputs (workflow item properties):
    ///   - DocumentId         : long
    ///   - purchaseOrderType  : "Contract" | "NonContract" (case-insensitive)
    ///   - totalAmount        : decimal, the LPO total in AED
    ///
    /// Output:
    ///   - nextApprovalRoute  : "NeedCEO" | "Direct"
    /// </summary>
    public class LPO_RouteByAmountAndTypeActivity : ActivityTemplate
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

        // Final approval rules:
        //   Contract         -> CBSO only (already done)           -> "Direct"
        //   Non-Contract, >= 25,000  -> needs CEO                  -> "NeedCEO"
        //   Non-Contract, <  25,000  -> CBSO only (already done)   -> "Direct"
        private const decimal CEO_THRESHOLD = 25000;

        private const string RouteNeedCEO = "NeedCEO";
        private const string RouteDirect  = "Direct";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- LPO_RouteByAmountAndTypeActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string  poType    = GetProp(workflowItem, "purchaseOrderType");
                string  amountStr = GetProp(workflowItem, "totalAmount");
                decimal amount    = ParseDecimal(amountStr);

                bool isNonContract =
                    string.Equals(poType, "NonContract", StringComparison.OrdinalIgnoreCase);

                LogInfo($"Input: purchaseOrderType='{poType}', totalAmount='{amountStr}' parsed={amount}, isNonContract={isNonContract}");

                string nextApprovalRoute;
                if (isNonContract && amount >= CEO_THRESHOLD)
                {
                    // Non-Contract LPO at or above the CEO threshold - CEO
                    // sign-off required before archive.
                    nextApprovalRoute = RouteNeedCEO;
                    LogInfo($"Non-Contract LPO amount={amount} >= threshold={CEO_THRESHOLD} - routing via {RouteNeedCEO}.");
                }
                else
                {
                    // Contract of any amount, OR Non-Contract below the CEO
                    // threshold - CBSO approval is final; archive directly.
                    nextApprovalRoute = RouteDirect;
                    if (isNonContract)
                        LogInfo($"Non-Contract LPO amount={amount} <  threshold={CEO_THRESHOLD} - routing via {RouteDirect}.");
                    else
                        LogInfo($"Contract LPO (purchaseOrderType='{poType}') - routing via {RouteDirect} regardless of amount.");
                }

                if (string.IsNullOrWhiteSpace(poType))
                    LogWarn($"purchaseOrderType is empty - defaulting to {RouteDirect}.");

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- LPO_RouteByAmountAndTypeActivity nextApprovalRoute={nextApprovalRoute} (threshold={CEO_THRESHOLD}) ");
                LogInfo($"---- LPO_RouteByAmountAndTypeActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- LPO_RouteByAmountAndTypeActivity END    DocumentId={documentIdStr}  result=FAILED ----");
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
                    "LPO_RouteByAmountAndTypeActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
