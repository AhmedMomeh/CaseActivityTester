using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    public class ShortlistedCandidateList_RouteByPositionAndGradeAActivity : ActivityTemplate
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

        #region Business rules 

        /// <summary>
        /// Routes the Candidate Selection Form after CPCO approval.      
        ///
        ///   1. Exception positions go to the Board of Directors regardless of grade:
        ///        - CEO
        ///        - VP - Internal Audit
        ///        - Board Office Manager
        ///        - N-1 Leadership
        ///        -> nextApprovalRoute = "BoDApproval"
        ///
        ///   2. Grade A, B, C, or D (non-exception positions) require CEO approval:
        ///        -> nextApprovalRoute = "CEOApproval"
        ///
        ///   3. Grade E or F (non-exception positions) are final after CPCO:
        ///        -> nextApprovalRoute = "Direct"
        ///
        /// Inputs  (form fields persisted as WorkflowItem.Properties):
        ///   - positionCategory : "Standard" | "CEO" | "VPInternalAudit" |
        ///                        "BoardOfficeManager" | "N1Leadership"
        ///   - gradeLevel       : "A" | "B" | "C" | "D" | "E" | "F"
        ///
        /// Output (written back to WorkflowItem.Properties):
        ///   - nextApprovalRoute : "BoDApproval" | "CEOApproval" | "Direct"
        /// </summary>       
        #endregion


        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };
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

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- ShortlistedCandidateList_RouteByPositionAndGradeAActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {

                string position = GetProp(workflowItem, "positionCategory");
                string grade = GetProp(workflowItem, "gradeLevel");

                string nextApprovalRoute;
                if (IsException(position))
                {
                    // Rule 1: any of the four protected roles -> Board of Directors approval.
                    nextApprovalRoute = "BoDApproval";
                }
                else if (IsSenior(grade))
                {
                    // Rule 2: Grade A-D non-exception -> CEO approval.
                    nextApprovalRoute = "CEOApproval";
                }
                else
                {
                    // Rule 3: Grade E/F (or unknown) non-exception -> CPCO was final.
                    nextApprovalRoute = "Direct";
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- ShortlistedCandidateList_RouteByPositionAndGradeAActivity nextApprovalRoute={nextApprovalRoute} ");

                LogInfo($"---- ShortlistedCandidateList_RouteByPositionAndGradeAActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"----  ShortlistedCandidateList_RouteByPositionAndGradeAActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static bool IsException(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            string norm = p.Trim();
            foreach (var x in ExceptionPositionsNormalized)
                if (string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.Trim().ToUpperInvariant();
            foreach (var s in SeniorGrades) if (s == up) return true;
            return false;
        }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }

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
                    "ShortlistedCandidateList_RouteByPositionAndGradeAActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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