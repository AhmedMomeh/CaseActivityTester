using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Routes the Candidate Selection Form to its next approver purely by
    /// grade band. Mirrors HiringRequest_RouteByGradeActivity so candidate
    /// selection and hiring-request flows escalate on identical signals.
    ///
    /// Rules:
    ///   IsSenior(gradeLevelText)  -> nextApprovalRoute = "CPCO"
    ///   else                      -> nextApprovalRoute = "HRDirectorOrAssociateDirector"
    ///
    /// Senior matching is case-insensitive substring-based, so all of these
    /// count as senior and route to CPCO:
    ///   "A", "A1", "A2", "B", "B2", "C", "C2", "D", "D1", "D2", "Grade-A",
    ///   "  d  ".
    /// Anything not containing A/B/C/D - "E", "E1", "F", "F2", "G", "H",
    /// empty, unknown - falls through to the HR Director / Associate Director
    /// route.
    ///
    /// Input  (WorkflowItem.Properties):
    ///   - DocumentId      : long
    ///   - gradeLevelText  : string  ("A".."H" or "A1", "B2" sub-bands etc.)
    ///
    /// Output (WorkflowItem.Properties):
    ///   - nextApprovalRoute : "CPCO" | "HRDirectorOrAssociateDirector"
    ///
    /// The output drives the gateway's outgoing transitions:
    ///   RouteByGrade -> CPCO                          -> CPCO step
    ///   RouteByGrade -> HRDirectorOrAssociateDirector -> HR Director step
    /// </summary>
    public class CandidateSelectionForm_RouteByGradeActivity : ActivityTemplate
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

        // Senior grades route to CPCO. Matched with Contains, so "A", "A1",
        // "A2", "B", "B2", "C", "C2", "D", "D1", "D2" all count as senior
        // while "E", "E1", "F", "F2", "G", "H" don't. A future grade letter
        // not listed here (e.g. "I", "J") automatically falls through to the
        // HR Director / Associate Director route without a code change.
        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

        private const string RouteCPCO                         = "CPCO";
        private const string RouteHRDirectorOrAssociateDirector = "HRDirectorOrAssociateDirector";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- CandidateSelectionForm_RouteByGradeActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string grade = GetProp(workflowItem, "gradeLevelText");

                LogInfo($"Input: gradeLevel='{grade}'");

                string nextApprovalRoute;
                if (IsSenior(grade))
                {
                    // Grades A-D (incl. A1, A2, B2, C2, D1, D2, ...) -> CPCO.
                    nextApprovalRoute = RouteCPCO;
                }
                else
                {
                    // E and below (incl. E1, F, F2, G, H, empty, unknown) ->
                    // HR Director / Associate Director.
                    nextApprovalRoute = RouteHRDirectorOrAssociateDirector;

                    if (string.IsNullOrWhiteSpace(grade))
                        LogWarn($"gradeLevel is empty - defaulting to {RouteHRDirectorOrAssociateDirector}.");
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- CandidateSelectionForm_RouteByGradeActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- CandidateSelectionForm_RouteByGradeActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- CandidateSelectionForm_RouteByGradeActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        // Case-insensitive, substring-based: "A", "A1", "B2", "C2", "D",
        // "D1", "D2", "Grade-A", "  d  " all match. "E", "F2", "G", "H" don't.
        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.ToUpperInvariant();
            foreach (var s in SeniorGrades) if (up.Contains(s)) return true;
            return false;
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
                    "CandidateSelectionForm_RouteByGradeActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
