using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Copies the Invoices file list that the user attached on the Temporary
    /// Cash Payment task form into the case's <c>Document.Form</c> JSON, so
    /// the uploaded files become part of the Application Metadata visible to
    /// every subsequent reader without having to open the task's own form.
    ///
    /// Drop this activity in the workflow IMMEDIATELY AFTER the form that
    /// hosts the Invoices panel:
    ///
    ///     [Form: Temporary Cash Payment Submission]   ← user uploads invoices
    ///                  │
    ///                  ▼  (on submit)
    ///     [Code: TemporaryCashPayment_CopyInvoicesToMetadataActivity]
    ///                  │
    ///                  ▼
    ///     [next step …]
    ///
    /// The source panel on the form looks like:
    ///   {
    ///     "type": "panel", "title": "Invoices", "key": "panelInvoices",
    ///     "components": [
    ///       { "type": "file", "label": "Invoices", "key": "invoices",
    ///         "multiple": true, "validate": { "required": true } }
    ///     ]
    ///   }
    ///
    /// The file component persists a JSON array of file descriptors on the
    /// task Form, e.g.:
    ///   "invoices": [
    ///     { "name": "inv-001.pdf", "size": 38421, "type": "application/pdf",
    ///       "storage": "url", "url": "...", "data": { ... } },
    ///     ...
    ///   ]
    ///
    /// The array is copied AS-IS into Document.Form["invoices"] — the wire
    /// shape Form.io expects when reading it back at the case level.
    ///
    /// Source for the field:
    ///   1. WorkflowItem.Properties["invoices"] — used if the Designer Form
    ///      Input mapping passes a JSON-string form of the array through.
    ///   2. Latest task's Form JSON column — primary path for file lists,
    ///      since complex JSON values typically aren't round-tripped through
    ///      string properties.
    ///
    /// Destination:
    ///   dbo.Document.Form  (JSON column read by the case-level form for
    ///                       Application Metadata view)
    ///
    /// Inputs:
    ///   DocumentId — required
    /// </summary>
    public class TemporaryCashPayment_CopyInvoicesToMetadataActivity : ActivityTemplate
    {
        // -------- Config (lazy + fallback) --------
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

        // -------- Field list --------
        // Single file-array field. Add more keys here if other complex
        // components (signatures, datagrids of files, etc.) need to be
        // surfaced into Application Metadata alongside the invoices.
        private static readonly string[] FieldsToCopy = new[]
        {
            "invoices",
        };

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
                // 1) Collect the fields from workflow properties + (fallback)
                //    from the latest task's Form JSON.
                JObject taskForm = LookupLatestTaskForm(documentId);
                var collected = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

                foreach (var key in FieldsToCopy)
                {
                    JToken value = ResolveFieldValue(workflowItem, taskForm, key);
                    if (value == null) continue;
                    collected[key] = value;
                }

                if (collected.Count == 0)
                {
                    LogInfo("No invoice file list found in workflow item or task form — nothing to copy.");
                    return;
                }

                // Log shape, not the full array (file URLs can be long).
                foreach (var kv in collected)
                {
                    int count = kv.Value.Type == JTokenType.Array ? ((JArray)kv.Value).Count : 1;
                    LogInfo($"Collected '{kv.Key}' ({kv.Value.Type}, {count} item(s)) to merge into Document.Form.");
                }

                // 2) Read current Document.Form, merge, write back atomically.
                MergeIntoDocumentForm(documentId, collected);

                LogInfo($"---- END  DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ----- Field resolution ---------------------------------------------

        // Tries the workflow item property first (in case Designer is passing
        // the file array through as a JSON string); falls back to the most
        // recent task's Form JSON column — the primary path for complex
        // components like file lists, signatures, datagrids etc.
        private static JToken ResolveFieldValue(WorkflowItem item, JObject taskForm, string key)
        {
            string viaProp = GetProp(item, key);
            if (!string.IsNullOrWhiteSpace(viaProp))
            {
                // File arrays are usually JSON. Try to parse — if it parses
                // to an array/object keep the structured value, otherwise
                // skip the prop path and fall back to task Form.
                string trimmed = viaProp.Trim();
                if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
                {
                    try
                    {
                        JToken parsed = JToken.Parse(trimmed);
                        if (parsed.Type == JTokenType.Array || parsed.Type == JTokenType.Object)
                            return parsed;
                    }
                    catch { /* not JSON — fall through to task Form */ }
                }
            }

            if (taskForm != null && taskForm[key] != null)
            {
                JToken v = taskForm[key];
                if (v.Type == JTokenType.Null) return null;
                if (v.Type == JTokenType.Array && ((JArray)v).Count == 0) return null;
                return v;
            }

            return null;
        }

        // ----- DB lookups ---------------------------------------------------

        // Reads Form column from the most recently created Task for this document.
        // NULL-safe — returns null if no task / no form data.
        private static JObject LookupLatestTaskForm(long documentId)
        {
            const string sql =
                "SELECT TOP 1 Form FROM dbo.[Task] WITH (NOLOCK) " +
                "WHERE DocumentId = @docId ORDER BY CreatedDate DESC";

            using (var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@docId", documentId);
                    var v = cmd.ExecuteScalar();
                    if (v == null || v == DBNull.Value) return null;
                    string s = Convert.ToString(v);
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    try { return JObject.Parse(s); }
                    catch (Exception ex)
                    {
                        LogInfo($"Latest task Form JSON could not be parsed (ignored): {ex.Message}");
                        return null;
                    }
                }
            }
        }

        // Reads Document.Form, merges new fields, writes back. Single
        // transaction with UPDLOCK to avoid lost-update races against
        // anything else editing the same JSON.
        private static void MergeIntoDocumentForm(long documentId, Dictionary<string, JToken> fields)
        {
            using (var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    string current;
                    using (var cmd = new SqlCommand(
                        "SELECT Form FROM dbo.Document WITH (UPDLOCK) WHERE Id = @docId", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@docId", documentId);
                        var v = cmd.ExecuteScalar();
                        current = (v == null || v == DBNull.Value) ? "" : Convert.ToString(v);
                    }

                    JObject doc = string.IsNullOrWhiteSpace(current) ? new JObject() : JObject.Parse(current);
                    foreach (var kv in fields)
                        doc[kv.Key] = kv.Value;
                    string updated = doc.ToString(Newtonsoft.Json.Formatting.None);

                    using (var cmd = new SqlCommand(
                        "UPDATE dbo.Document SET Form = @form WHERE Id = @docId", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@form",  updated);
                        cmd.Parameters.AddWithValue("@docId", documentId);
                        int rows = cmd.ExecuteNonQuery();
                        if (rows == 0)
                            throw new Exception($"UPDATE dbo.Document matched 0 rows for Id={documentId}.");
                    }

                    tx.Commit();
                }
            }
        }

        // ----- Workflow property helpers ----------------------------------

        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? "" : Convert.ToString(v); }
            catch { return ""; }
        }

        // ----- Logging ----------------------------------------------------

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory,
                    $"TemporaryCashPayment_CopyInvoicesToMetadataActivity-{DateTime.Now:yyyy-MM-dd}.log");
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
            private static JObject _root;
            private static string  _path;
            private static readonly object _gate = new object();

            public static string Get(string keyPath)           => Resolve(keyPath, allowEmpty: false);
            public static string GetAllowEmpty(string keyPath) => Resolve(keyPath, allowEmpty: true);

            private static string Resolve(string keyPath, bool allowEmpty)
            {
                Load();
                JToken node = _root;
                foreach (var part in keyPath.Split(':'))
                {
                    if (node is JObject obj &&
                        obj.TryGetValue(part, StringComparison.OrdinalIgnoreCase, out var next))
                        node = next;
                    else
                        throw new InvalidOperationException(
                            $"Missing required setting '{keyPath}' in '{_path}'. " +
                            "Add the key under 'CaseActivities' in the host appsettings.json.");
                }
                if (node == null || node.Type == JTokenType.Null)
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
                        _root = JObject.Parse(File.ReadAllText(p));
                        _path = p;
                        return;
                    }
                    throw new InvalidOperationException("appsettings.json not found.");
                }
            }
        }
    }
}
