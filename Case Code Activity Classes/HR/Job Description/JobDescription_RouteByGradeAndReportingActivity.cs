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

        // Business rules (per the Job Description DOA matrix):
        // if gradeLevel A, B, C,d go to chro then archive to dms
        // if mark isDirectReporteeToCEO and if gradeLevel A, B, C,d will go to chro then ceo then archive to dms  

        //if gradeLevel e f g h d  it will not go to chro  go to HR Directory or Associate  then dms

        //   RouteByGradeAndReporting -> NeedCEOApproval          when nextApprovalRoute == "NeedCEOApproval"
        //   RouteByGradeAndReporting -> CHROApproval -> CEO...   when nextApprovalRoute == "CHROAndCEOApproval"
        //   RouteByGradeAndReporting -> HRDirectoryOrAssociate   when nextApprovalRoute == "HRDirectoryOrAssociate"       
        #endregion


        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

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
                string grade = GetProp(workflowItem, "gradeLevel");
                bool isDirectReportee = ParseBool(GetProp(workflowItem, "isDirectReporteeToCEO"));

                string nextApprovalRoute;
                if (isDirectReportee)
                {
                    
                    nextApprovalRoute = "";
                }
                else if (IsSenior(grade))
                {
                    
                    nextApprovalRoute = "";
                }
                else
                {
                  
                    nextApprovalRoute = "";
                }

                SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);

                LogInfo($"---- JobDescription_RouteByGradeAndReportingActivity nextApprovalRole={nextApprovalRoute} ");

                LogInfo($"---- JobDescription_RouteByGradeAndReportingActivity END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"----  JobDescription_RouteByGradeAndReportingActivity END    DocumentId={documentIdStr}  result=FAILED ----");
            }
        }

        public override void Complete(WorkflowItem workflowItem) { }

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