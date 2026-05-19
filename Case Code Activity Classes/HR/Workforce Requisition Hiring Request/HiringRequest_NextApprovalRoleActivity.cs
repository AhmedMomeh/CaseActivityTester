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
        private const string LogDirectory = @"C:\Logs\Case";
        private static readonly object LogLock = new object();

        // Per Delegation of Authority:
        //   Non-Budgeted, Grade D and above  -> CEO      
        //   Exception positions              -> Board of Directors
        //   Budgeted standard                -> Standard (no extra approval)
        private static readonly string[] CeoGrades = { "A", "B", "C", "D" };
        private static readonly string[] ExceptionKeywords =
        {
              "ceo", "vp internal audit", "board office manager", "N-1 Leadership"   // got to ceo then bord  it ceo go to board
        };

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
                string gradeLevel = GetProp(workflowItem, "gradeLevel");
                string budget = GetProp(workflowItem, "budgetStatus");
                string position = GetProp(workflowItem, "requiredPosition").ToLowerInvariant();

                string nextApprovalRoute;

                if (ExceptionKeywords.Any(k => position.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    nextApprovalRoute = "BoardApproval";
                }
                else if (string.Equals(budget, "nonBudgeted", StringComparison.OrdinalIgnoreCase))
                {
                    if (CeoGrades.Contains(gradeLevel, StringComparer.OrdinalIgnoreCase))
                        nextApprovalRoute = "CEOApproval";
                    else
                        nextApprovalRoute = "Standard"; // budgeted -> no extra approval
                }
                else
                {
                    nextApprovalRoute = "Standard"; // budgeted -> no extra approval
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
                // property not declared on this workflow — silently skip
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
