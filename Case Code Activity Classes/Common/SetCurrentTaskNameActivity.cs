using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Microsoft.Data.SqlClient;
using System;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
   
    public class SetCurrentTaskNameActivity : ActivityTemplate
    {
        private static string _logDirectory;
        private static string LogDirectory
        {
            get
            {
                if (_logDirectory != null) return _logDirectory;
                try { _logDirectory = CodeActivityConfig.Get("CaseActivities:LogDirectory"); }
                catch { _logDirectory = @"C:\Logs\Case"; }
                return _logDirectory;
            }
        }

        private const string OutCurrentTaskName = "CurrentTaskName";

        private static readonly object LogLock = new object();

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- BEGIN  DocumentId={docIdStr} ----");

            if (!long.TryParse(docIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{docIdStr}'");
                return;
            }

            try
            {
                string taskName = LookupCurrentTaskName(documentId);
                if (string.IsNullOrWhiteSpace(taskName))
                {
                    // Expected at the very start of a document's lifecycle —
                    // the workflow engine hasn't created the first Task row
                    // yet. Treat as "initiation mode": clear the property so
                    // forms reading data.CurrentTaskName see empty and hide
                    // step-specific panels.
                    SetProp(workflowItem, OutCurrentTaskName, "");
                    LogInfo($"No task row yet for DocumentId={documentId} — CurrentTaskName left empty (initiation mode).");
                    LogInfo($"---- END  DocumentId={documentId}  result=success (empty) ----");
                    return;
                }

                SetProp(workflowItem, OutCurrentTaskName, taskName);
                LogInfo($"CurrentTaskName = '{taskName}'");
                LogInfo($"---- END  DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }
        
        private string LookupCurrentTaskName(long documentId)
        {
            // Two-pass lookup so we don't fight the workflow engine's timing:
            //   1. The currently OPEN task (ClosedDate IS NULL). This is the
            //      truth — it's the task the user is about to / currently
            //      working on. Works for every step EXCEPT the very first one
            //      (no row yet) and the very last one (no open row left).
            //   2. Fallback to the most-recently-created task in either state.
            //      Catches the "between Task1 close and Task2 create" window
            //      that the workflow engine briefly sits in.
            // Either pass returning empty means "really no task" (e.g. before
            // initiation) — caller treats that as initiation mode.
            const string sqlOpen =
                "SELECT TOP 1 COALESCE(ad.Name, NULLIF(LTRIM(RTRIM(ad.Title)), '')) " +
                "FROM   dbo.[Task]               t " +
                "INNER  JOIN dbo.ActivityInstances  ai ON ai.ActivityInstanceId = t.ActivityInstanceId " +
                "INNER  JOIN dbo.ActivityDefinition ad ON ad.ActivityId         = ai.ActivityDefinitionId " +
                "WHERE  t.DocumentId = @docId AND t.ClosedDate IS NULL " +
                "ORDER  BY t.CreatedDate DESC";

            const string sqlAny =
                "SELECT TOP 1 COALESCE(ad.Name, NULLIF(LTRIM(RTRIM(ad.Title)), '')) " +
                "FROM   dbo.[Task]               t " +
                "INNER  JOIN dbo.ActivityInstances  ai ON ai.ActivityInstanceId = t.ActivityInstanceId " +
                "INNER  JOIN dbo.ActivityDefinition ad ON ad.ActivityId         = ai.ActivityDefinitionId " +
                "WHERE  t.DocumentId = @docId " +
                "ORDER  BY t.CreatedDate DESC";

            using (var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();

                string result = ExecuteScalarString(conn, sqlOpen, documentId);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    LogInfo("Matched OPEN task (ClosedDate IS NULL).");
                    return result;
                }

                result = ExecuteScalarString(conn, sqlAny, documentId);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    LogInfo("No open task — falling back to most-recently-created.");
                    return result;
                }

                return "";
            }
        }

        private static string ExecuteScalarString(SqlConnection conn, string sql, long documentId)
        {
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@docId", documentId);
                var v = cmd.ExecuteScalar();
                return (v == null || v == DBNull.Value) ? "" : Convert.ToString(v);
            }
        }

        // ----- Workflow property helpers ----------------------------------

        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? "" : Convert.ToString(v); }
            catch { return ""; }
        }

        private static void SetProp(WorkflowItem item, string key, string value)
        {
            if (item == null || item.Properties == null) return;
            var existing = item.Properties[key];
            if (existing != null) existing.Value = value;
            else                  item.Properties.Add(new Property { Name = key, Value = value });
        }

        // ----- Logging (daily-rotated file in LogDirectory) ---------------

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory,
                    $"SetCurrentTaskNameActivity-{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [tid={Thread.CurrentThread.ManagedThreadId}] {message}{Environment.NewLine}";
                lock (LogLock) File.AppendAllText(path, line);
            }
            catch { /* swallow — never fail the activity over a log write */ }
        }
        private static void LogInfo(string m)  => Write("INFO",  m);
        private static void LogError(string m) => Write("ERROR", m);
        private static void LogException(string m, Exception ex) =>
            Write("ERROR", m + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);

        // -------- Self-contained config reader (for Designer single-file paste) --------
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
                        obj.TryGetValue(part, StringComparison.OrdinalIgnoreCase, out var next))
                        node = next;
                    else
                        throw new InvalidOperationException(
                            $"Missing required setting '{keyPath}' in '{_path}'. " +
                            "Add the key under 'CaseActivities' in the host appsettings.json.");
                }
                if (node == null || node.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                    throw new InvalidOperationException($"Setting '{keyPath}' is null in '{_path}'.");
                string value = node.ToString();
                if (!allowEmpty && string.IsNullOrEmpty(value))
                    throw new InvalidOperationException($"Setting '{keyPath}' is empty in '{_path}'.");
                return value;
            }

            private static void Load()
            {
                if (_root != null) return;
                lock (_gate)
                {
                    if (_root != null) return;
                    foreach (var p in new[] {
                        Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
                        Path.Combine(AppContext.BaseDirectory,         "appsettings.json") })
                    {
                        if (!File.Exists(p)) continue;
                        _root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(p));
                        _path = p;
                        return;
                    }
                    throw new InvalidOperationException("appsettings.json not found.");
                }
            }
        }
    }
}
