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
    /// Mirror of <c>CopyDepartmentEvaluationToMetadataActivity</c>: reads the
    /// previously-saved department evaluation from <c>Document.Form</c> and
    /// writes each field back to the workflow item as a property so the
    /// upcoming Manager Form Activity renders pre-filled with the manager's
    /// last submission.
    ///
    /// Use when the workflow loops the case back to the Manager (e.g. after
    /// a "Not Recommended" → Initiator → Resubmit → Manager again cycle):
    ///
    ///     [Initiator Resubmit Form]
    ///                  │
    ///                  ▼  (on submit)
    ///     [Code: LoadDepartmentEvaluationFromMetadataActivity]
    ///                  │
    ///                  ▼
    ///     [Form: Department Manager Approval]   ← opens with previous values
    ///
    /// For this to work, each evaluation field on the Manager form should be
    /// driven by a customDefaultValue that reads from data:
    ///
    ///     "customDefaultValue": "value = data.departmentPersonalityScore || '';"
    ///
    /// Intalio populates the form's data from workflow properties on render,
    /// so setting the properties here is enough.
    ///
    /// Inputs:
    ///   DocumentId — required
    ///
    /// Output:
    ///   One workflow property per FieldsToLoad entry, populated from
    ///   Document.Form (skipped if the field isn't present in the JSON).
    /// </summary>
    public class LoadDepartmentEvaluationFromMetadataActivity : ActivityTemplate
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

        // Must match the CopyDepartmentEvaluationToMetadataActivity field list
        // so write+read use the same keys.
        private static readonly string[] FieldsToLoad = new[]
        {
            "departmentPersonalityScore",
            "departmentFlexibilityScore",
            "departmentCommunicationScore",
            "departmentTeamPlayerScore",
            "departmentJobKnowledgeScore",
            "departmentProblemSolvingScore",
            "departmentManagementSkillScore",
            "departmentLeadershipSkillScore",
            "departmentTotalScore",
            "departmentTotalScoreDisplay",
            "departmentRecommendation",
            "departmentComments",
            "departmentRecruitmentName",
            "departmentRecruitmentJobTitle",
            "departmentRecruitmentDate",
        };

        // Properties declared as Number in Designer must receive an int value.
        // If we pass them as strings ("4"), the engine coerces silently and the
        // form ends up reading 0 instead of the saved score.
        private static readonly HashSet<string> NumericFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "departmentPersonalityScore",
            "departmentFlexibilityScore",
            "departmentCommunicationScore",
            "departmentTeamPlayerScore",
            "departmentJobKnowledgeScore",
            "departmentProblemSolvingScore",
            "departmentManagementSkillScore",
            "departmentLeadershipSkillScore",
            "departmentTotalScore",
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
                JObject docForm = LookupDocumentForm(documentId);
                if (docForm == null)
                {
                    LogInfo($"Document.Form is empty for DocumentId={documentId} — nothing to load (first pass through the manager step).");
                    LogInfo($"---- END  DocumentId={documentId}  result=success (no data) ----");
                    return;
                }

                int copied = 0;
                foreach (var key in FieldsToLoad)
                {
                    JToken value = docForm[key];
                    if (value == null || value.Type == JTokenType.Null) continue;

                    object propValue;
                    if (NumericFields.Contains(key))
                    {
                        // Numeric properties must be assigned as int, not string —
                        // otherwise the workflow engine coerces "4" → 0.
                        if (!TryReadInt(value, out int n)) continue;
                        propValue = n;
                    }
                    else
                    {
                        string asString = value.Type == JTokenType.String
                            ? (string)value
                            : value.ToString(Newtonsoft.Json.Formatting.None);
                        if (string.IsNullOrEmpty(asString)) continue;
                        propValue = asString;
                    }

                    SetProp(workflowItem, key, propValue);
                    copied++;
                }

                LogInfo($"Loaded {copied} field(s) from Document.Form into workflow properties.");
                LogInfo($"---- END  DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END  DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ----- DB lookup ---------------------------------------------------

        private static JObject LookupDocumentForm(long documentId)
        {
            using (var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(
                    "SELECT Form FROM dbo.Document WITH (NOLOCK) WHERE Id = @docId", conn))
                {
                    cmd.Parameters.AddWithValue("@docId", documentId);
                    var v = cmd.ExecuteScalar();
                    if (v == null || v == DBNull.Value) return null;
                    string s = Convert.ToString(v);
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    try { return JObject.Parse(s); }
                    catch (Exception ex)
                    {
                        LogInfo($"Document.Form could not be parsed (ignored): {ex.Message}");
                        return null;
                    }
                }
            }
        }

        // ----- Workflow property helpers ----------------------------------

        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? "" : Convert.ToString(v); }
            catch { return ""; }
        }

        // Takes object so numeric properties can be assigned as int and string
        // properties as string — the workflow engine matches on the declared
        // type, and a mismatch silently coerces (numbers to 0).
        private static void SetProp(WorkflowItem item, string key, object value)
        {
            if (item == null || item.Properties == null) return;
            var existing = item.Properties[key];
            if (existing != null) existing.Value = value;
            else                  item.Properties.Add(new Property { Name = key, Value = value });
        }

        private static bool TryReadInt(JToken t, out int n)
        {
            n = 0;
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Integer) { n = (int)t; return true; }
            if (t.Type == JTokenType.Float)   { n = (int)Math.Round((double)t); return true; }
            if (t.Type == JTokenType.String && int.TryParse((string)t, out n)) return true;
            return false;
        }

        // ----- Logging ----------------------------------------------------

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory,
                    $"LoadDepartmentEvaluationFromMetadataActivity-{DateTime.Now:yyyy-MM-dd}.log");
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
