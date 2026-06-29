using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    public class Probationevaluation_RouteByPositionAndGradeActivity : ActivityTemplate
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
        /// Routes the Probation Evaluation form by position group and grade
        /// band combined. Three outcomes:
        ///
        ///   1. Exception positions (CEO / Chief Executive Officer /
        ///      VP - Internal Audit / Vice President - Internal Audit /
        ///      Board Office Manager / Manager - Board Office /
        ///      Manager - CEO Office / N-1 Leadership) take the position
        ///      branch:
        ///        -> nextApprovalRoute = "Position"
        ///
        ///   2. Non-exception position AND senior grade (A / B / C / D, incl.
        ///      A1 / A2 / B2 / C2 / D1 / D2 sub-bands):
        ///        -> nextApprovalRoute = "D/AD-HR"     (Director / Associate Director - HR)
        ///
        ///   3. Non-exception position AND non-senior grade (E and below,
        ///      empty, unknown):
        ///        -> nextApprovalRoute = "O-HR"        (Officer - HR)
        ///
        /// Title matching uses the same canonical normalization as the other
        /// HR routing activities (lowercase + strip every non-alphanumeric
        /// char) with EXACT equality on ExceptionPositionsNormalized. Senior
        /// matching is case-insensitive substring on A/B/C/D so all senior
        /// sub-bands count without enumerating each variant.
        ///
        /// Inputs  (WorkflowItem.Properties):
        ///   - DocumentId      : long
        ///   - designation    : free-text job title
        ///   - gradeLevel  : "A".."H" or "A1", "B2" sub-bands etc.
        ///                       (read only when position is non-exception)
        ///
        /// Output (WorkflowItem.Properties):
        ///   - nextApprovalRoute : "Position" | "D/AD-HR" | "O-HR"
        /// </summary>
        #endregion

        // Exception positions — when the job title matches any of these, the
        // request takes the position-first path. Stored normalized
        // (lowercased, all non-alphanumeric chars stripped) so the lookup
        // tolerates spacing / dashes / dots / casing variations:
        //   "CEO" / "C.E.O." / "ceo"                       -> "ceo"
        //   "Chief Executive Officer"                      -> "chiefexecutiveofficer"
        //   "VP-Internal Audit" / "VP Internal Audit"      -> "vpinternalaudit"
        //   "Vice President - Internal Audit"              -> "vicepresidentinternalaudit"
        //   "Board Office Manager" / "BoardOfficeManager"  -> "boardofficemanager"
        //   "Manager - Board Office" / "ManagerBoardOffice"-> "managerboardoffice"
        //   "Manager - CEO Office"                         -> "managerceooffice"
        //   "N-1 Leadership" / "N1Leadership"              -> "n1leadership"
        // Keep this list in sync with the other HR routing activities.
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

        // Senior grades route to D/AD-HR (Director / Associate Director - HR)
        // on the non-exception branch. Matched with Contains, so "A", "A1",
        // "A2", "B", "B2", "C", "C2", "D", "D1", "D2" all count as senior
        // while "E", "E1", "F", "F2", "G", "H" don't. A future grade letter
        // not listed here (e.g. "I", "J") automatically falls through to the
        // O-HR (Officer - HR) route without a code change.
        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

        private const string RoutePosition             = "Position";
        private const string RouteDirectorOrAssociateHR = "HRDirector";
        private const string RouteOfficerHR             = "HROfficer";

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- Probationevaluation_RouteByPositionAndGradeActivity BEGIN  DocumentId={documentIdStr} ----");
            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                string position = GetProp(workflowItem, "designation");

                string nextApprovalRoute;
                if (IsExceptionPosition(position))
                {
                    // CEO / Chief Executive Officer / VP-Internal Audit /
                    // Vice President - Internal Audit / Board Office Manager /
                    // Manager - Board Office / Manager - CEO Office /
                    // N-1 Leadership - take the position-first path.
                    LogInfo($"Input: jobTitle='{position}'");
                    nextApprovalRoute = RoutePosition;
                    LogInfo($"Exception position '{position}' matched -> routing via {RoutePosition}.");
                }
                else
                {
                    // Non-exception position - decide between D/AD-HR and O-HR
                    // by grade band. Senior (A-D incl. sub-bands) -> Director /
                    // Associate Director - HR; everything else -> Officer - HR.
                    string grade   = GetProp(workflowItem, "gradeLevel");
                    bool   isSenior = IsSenior(grade);

                    LogInfo($"Input: jobTitle='{position}', gradeLevel='{grade}', isSenior={isSenior}");

                    nextApprovalRoute = isSenior ? RouteDirectorOrAssociateHR : RouteOfficerHR;
                    LogInfo($"Non-exception position - {(isSenior ? "senior" : "non-senior")} grade -> routing via {nextApprovalRoute}.");

                    if (string.IsNullOrWhiteSpace(grade))
                        LogWarn($"gradeLevel is empty - defaulting to {RouteOfficerHR}.");
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- Probationevaluation_RouteByPositionAndGradeActivity nextApprovalRoute={nextApprovalRoute} ");
                LogInfo($"---- Probationevaluation_RouteByPositionAndGradeActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- Probationevaluation_RouteByPositionAndGradeActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

        // Normalize-then-EXACT-match for exception positions. Strips every
        // non-alphanumeric character (spaces, dashes, dots, parentheses) and
        // lowercases the rest so input variations all collapse to the same
        // canonical form, then checks whether that canonical form is EXACTLY
        // one of the canonical exception patterns.
        //
        // Equality (vs. Contains) is intentional: many ordinary titles contain
        // these substrings ("CEO Office Coordinator", "Acting VP Internal Audit
        // Deputy", "Junior Board Office Manager Assistant", etc.) that should
        // NOT escalate. Matching only on the exact canonical form means new
        // exception titles must be added explicitly to the list - safer than
        // over-broad matching.
        //
        // Variants that DO match the existing list (since normalization collapses
        // spaces/dashes/dots/casing):
        //   "CEO" / "C.E.O." / "ceo"                       -> "ceo"
        //   "VP-Internal Audit" / "VP Internal Audit"      -> "vpinternalaudit"
        //   "Vice President - Internal Audit"              -> "vicepresidentinternalaudit"
        //   "Board Office Manager" / "BoardOfficeManager"  -> "boardofficemanager"
        //   "Manager - Board Office" / "ManagerBoardOffice"-> "managerboardoffice"
        //   "Manager - CEO Office"                         -> "managerceooffice"
        //   "N-1 Leadership" / "N1Leadership"              -> "n1leadership"
        // Add new title variants to ExceptionPositionsNormalized above (in
        // canonical form) when business confirms they belong to the exception
        // route.
        private static bool IsExceptionPosition(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string norm = Normalize(title);
            foreach (var x in ExceptionPositionsNormalized) if (x == norm) return true;
            return false;
        }

        // Case-insensitive, substring-based: "A", "A1", "B2", "C2", "D",
        // "D1", "D2", "Grade-A", "  d  " all match. "E", "F2", "G", "H" don't.
        // Same matcher used by CandidateSelectionForm_RouteByGradeActivity and
        // the rest of the HR routing activities - keep in sync.
        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.ToUpperInvariant();
            foreach (var s in SeniorGrades) if (up.Contains(s)) return true;
            return false;
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
                    "Probationevaluation_RouteByPositionAndGradeActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
