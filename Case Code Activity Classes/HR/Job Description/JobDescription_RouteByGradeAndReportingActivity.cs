using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    public class JobDescription_RouteByGradeAndReportingActivity : ActivityTemplate
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

        // Routing matrix (grade matching is case-insensitive — "a" == "A"):
        //
        //   gradeLevel ∈ {A,B,C,D}  AND  isDirectReporteeToCEO == false
        //     → CHRO → DMS
        //     → nextApprovalRoute = "CHROOnly"
        //
        //   gradeLevel ∈ {A,B,C,D}  AND  isDirectReporteeToCEO == true
        //     → CHRO → CEO → DMS
        //     → nextApprovalRoute = "CHROAndCEO"
        //
        //   Anything else  (E, F, G, H, any new grade, empty, unknown)
        //     → HR Director / Associate → DMS  (skip CHRO + CEO)
        //     → nextApprovalRoute = "HRDirectorOrAssociate"
        //
        // The "anything else" branch means a future-introduced grade letter
        // (e.g. "I", "J") routes through HR without any code change. An empty
        // grade also takes this path, with a WARN log entry to flag the bad
        // input for audit.
        //
        // Inputs (workflow item properties):
        //   - gradeLevel             : string  e.g. "A".."H" (case-insensitive)
        //   - isDirectReporteeToCEO  : string  "true"/"false" (form checkbox)
        //
        // Output:
        //   - nextApprovalRoute      : "CHROOnly" | "CHROAndCEO" | "HRDirectorOrAssociate"
        //
        // The output drives the gateway's outgoing transitions:
        //   RouteByGradeAndReporting → CHRO   → ArchiveToDMS     (CHROOnly)
        //   RouteByGradeAndReporting → CHRO   → CEO → ArchiveToDMS (CHROAndCEO)
        //   RouteByGradeAndReporting → HRDir  → ArchiveToDMS     (HRDirectorOrAssociate)

        #endregion


        // Senior grades require CHRO endorsement. Anything else (E onwards,
        // or any new / unrecognized grade) falls through to the HR Director
        // route — no need to enumerate juniors explicitly, which means a
        // future grade like "I" or "J" routes correctly without a code change.
        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

        private const string RouteCHROOnly          = "CHROOnly";
        private const string RouteCHROAndCEO        = "CHROAndCEO";
        private const string RouteHRDirectorOrAssoc = "HRDirectorOrAssociate";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- JobDescription_RouteByGradeAndReportingActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string grade            = GetProp(workflowItem, "gradeLevelText");
                bool   isDirectReportee = ParseBool(GetProp(workflowItem, "isDirectReporteeToCEO"));

                LogInfo($"Input: gradeLevel='{grade}', isDirectReporteeToCEO={isDirectReportee}");

                string nextApprovalRoute;
                if (IsSenior(grade))
                {
                    // Grades A-D — always CHRO endorsement; CEO only when the
                    // job reports directly to the CEO.
                    nextApprovalRoute = isDirectReportee ? RouteCHROAndCEO : RouteCHROOnly;
                }
                else
                {
                    // Anything not in A-D — HR Director (or Associate Director)                 
                    nextApprovalRoute = RouteHRDirectorOrAssoc;

                    if (string.IsNullOrWhiteSpace(grade))
                        LogWarn($"gradeLevel is empty — defaulting to {RouteHRDirectorOrAssoc}.");
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- JobDescription_RouteByGradeAndReportingActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- JobDescription_RouteByGradeAndReportingActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"----  JobDescription_RouteByGradeAndReportingActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        // Case-insensitive: "a" / "A" / " a " all match.
        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.Trim().ToUpperInvariant();
            foreach (var s in SeniorGrades) if (s == up) return true;
            return false;
        }

        private static bool ParseBool(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string v = s.Trim().ToLowerInvariant();
            return v == "true" || v == "yes" || v == "1" || v == "on";
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
                    "JobDescription_RouteByGradeAndReportingActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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