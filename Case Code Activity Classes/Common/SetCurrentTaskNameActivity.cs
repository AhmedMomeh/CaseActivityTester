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

        // -------- Retry tuning --------
        // The workflow engine sometimes hasn't committed the new Task row by
        // the time this activity fires (typical between a Form Activity's
        // SaveAndSendWithRowVersion and the next step's task creation). We
        // poll: query → sleep → query → sleep → … up to MaxRetries times.
        //
        //   MaxRetries = 6   ┐
        //   DelayMs    = 500 ┘ → total worst-case wait ≈ 3 seconds (6 × 500ms)
        //
        // Bump MaxRetries higher if your DB / workflow engine is slower; lower
        // if you want the activity to fail-fast on initiation (no task yet).
        private const int MaxRetries = 10;
        private const int DelayMs    = 300;

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
                string taskName = "";
                int    attempt  = 0;

                // Poll: query → sleep → query → sleep → … until we get a name
                // or exhaust MaxRetries. Same total budget (~3s) for both the
                // "task already committed" and "engine still committing" cases —
                // the loop exits early as soon as the lookup returns a value.
                for (attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    taskName = LookupCurrentTaskName(documentId);
                    if (!string.IsNullOrWhiteSpace(taskName)) break;

                    if (attempt < MaxRetries)
                    {
                        LogInfo($"Attempt {attempt}/{MaxRetries}: no task yet — waiting {DelayMs}ms before retry.");
                        Thread.Sleep(DelayMs);
                    }
                }

                if (string.IsNullOrWhiteSpace(taskName))
                {
                    // After all retries still nothing — genuinely the very
                    // start of a document's lifecycle (initiation mode).
                    // Leave CurrentTaskName empty so forms reading
                    // data.CurrentTaskName hide step-specific panels.
                    SetProp(workflowItem, OutCurrentTaskName, "");
                    LogInfo($"No task row after {MaxRetries} attempts (~{MaxRetries * DelayMs}ms) for DocumentId={documentId} — CurrentTaskName left empty (initiation mode).");
                    LogInfo($"---- END  DocumentId={documentId}  result=success (empty) ----");
                    return;
                }

                SetProp(workflowItem, OutCurrentTaskName, taskName);
                LogInfo($"CurrentTaskName = '{taskName}'  (found on attempt {attempt}/{MaxRetries})");
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
            // WITH (NOLOCK) on every table reads UNCOMMITTED rows — required
            // because the Intalio workflow engine creates the new Task row
            // inside its own transaction and holds that transaction open
            // until ALL pre/post activities finish (including this one).
            // Default READ COMMITTED isolation can't see those in-flight rows
            // at all; NOLOCK bypasses the lock and reads from the buffer pool.
            //
            // Trade-off: we may briefly see a row that ends up being rolled
            // back if the workflow step aborts. For "what's the current task
            // name" that's harmless — at worst we'd have set a property that
            // never had its task survive, and the next activity would
            // overwrite it.
            //
            // Two-pass lookup:
            //   1. OPEN task (ClosedDate IS NULL) — the task the user is
            //      about to / currently working on.
            //   2. Most-recently-created task — fallback while the engine is
            //      between closing one task and creating the next.
            const string sqlOpen =
                "SELECT TOP 1 COALESCE(ad.Name, NULLIF(LTRIM(RTRIM(ad.Title)), '')) " +
                "FROM   dbo.[Task]               t  WITH (NOLOCK) " +
                "INNER  JOIN dbo.ActivityInstances  ai WITH (NOLOCK) ON ai.ActivityInstanceId = t.ActivityInstanceId " +
                "INNER  JOIN dbo.ActivityDefinition ad WITH (NOLOCK) ON ad.ActivityId         = ai.ActivityDefinitionId " +
                "WHERE  t.DocumentId = @docId AND t.ClosedDate IS NULL " +
                "ORDER  BY t.CreatedDate DESC";

            const string sqlAny =
                "SELECT TOP 1 COALESCE(ad.Name, NULLIF(LTRIM(RTRIM(ad.Title)), '')) " +
                "FROM   dbo.[Task]               t  WITH (NOLOCK) " +
                "INNER  JOIN dbo.ActivityInstances  ai WITH (NOLOCK) ON ai.ActivityInstanceId = t.ActivityInstanceId " +
                "INNER  JOIN dbo.ActivityDefinition ad WITH (NOLOCK) ON ad.ActivityId         = ai.ActivityDefinitionId " +
                "WHERE  t.DocumentId = @docId " +
                "ORDER  BY t.CreatedDate DESC";

            // Diagnostic — counts ALL Task rows for this DocumentId, ignoring
            // joins. If this returns 0 the workflow engine truly hasn't
            // created the row yet (no amount of retrying will help — the
            // engine is waiting for us to finish). If it returns N>0 but the
            // join queries above return empty, the issue is on the join
            // (orphan ActivityInstanceId, etc.) rather than visibility.
            const string sqlCount = "SELECT COUNT(1) FROM dbo.[Task] WITH (NOLOCK) WHERE DocumentId = @docId";

            using (var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();

                int rowCount = 0;
                using (var cmd = new SqlCommand(sqlCount, conn))
                {
                    cmd.Parameters.AddWithValue("@docId", documentId);
                    rowCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                }

                string result = ExecuteScalarString(conn, sqlOpen, documentId);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    LogInfo($"Matched OPEN task (ClosedDate IS NULL). [Task rows seen for this doc: {rowCount}]");
                    return result;
                }

                result = ExecuteScalarString(conn, sqlAny, documentId);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    LogInfo($"No open task — fell back to most-recently-created. [Task rows seen for this doc: {rowCount}]");
                    return result;
                }

                LogInfo($"Lookup empty.  dbo.[Task] WITH (NOLOCK) row count for DocumentId={documentId} = {rowCount}.  " +
                        (rowCount == 0
                            ? "Engine hasn't created any task row yet — retry won't help, fall back to JS-side refresh."
                            : "Task rows exist but the join to ActivityInstances/ActivityDefinition matched nothing — check schema."));
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
