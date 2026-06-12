using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Intalio.Core;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Schedules a delayed HR reminder for the "Submit Original Invoices"
    /// task on Temporary Cash Payment cases. Inserts a row into
    /// dbo.CasePendingNotifications with Status='pending' and
    /// FireAt = now + delayDays. A separately registered drainer (Hangfire
    /// recurring job or SQL Agent job) polls the table, sends email to HR
    /// for rows whose task is still open, and marks them sent.
    /// </summary>
    public class ScheduleHrNotificationActivity : ActivityTemplate
    {
        private static string _logDirectory;
        private static int _tableEnsured;
        private static readonly object LogLock = new object();

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo("---- BEGIN  DocumentId=" + docIdStr + " ----");

            long documentId;
            if (!long.TryParse(docIdStr, out documentId) || documentId <= 0)
            {
                LogError("Invalid DocumentId: '" + docIdStr + "'");
                return;
            }

            try
            {
                int delayDays       = ResolveDelayDays(workflowItem);
                string activityName = Cfg("CaseActivities:TemporaryCashPayment:OverdueTaskActivityName",
                                          "Submit Original Invoices");
                DateTime fireAtUtc  = DateTime.UtcNow.AddDays(delayDays);

                EnsureTableExists();
                long rowId = InsertPendingNotification(documentId, activityName, fireAtUtc);

                SetProp(workflowItem, "overdueNotifyRowId",   rowId.ToString());
                SetProp(workflowItem, "overdueNotifyDueDate", fireAtUtc.ToString("o"));

                LogInfo("Queued row #" + rowId + " activity='" + activityName +
                        "' fireAtUtc=" + fireAtUtc.ToString("o") + " (delayDays=" + delayDays + ").");

                // Opportunistic drain — every workflow execution also processes
                // any rows whose FireAt has passed. Doesn't replace a proper
                // recurring drainer but means low-traffic environments still
                // get reminders out without one.
                DrainDueRows();

                LogInfo("---- END    DocumentId=" + documentId + "  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo("---- END    DocumentId=" + docIdStr + "  result=FAILED ----");
            }
        }

        private static int ResolveDelayDays(WorkflowItem workflowItem)
        {
            string fromProp = GetProp(workflowItem, "overdueDelayDays");
            int n;
            if (!string.IsNullOrWhiteSpace(fromProp) && int.TryParse(fromProp, out n) && n > 0) return n;
            string fromCfg = Cfg("CaseActivities:TemporaryCashPayment:OverdueDelayDays", "7");
            int m;
            if (int.TryParse(fromCfg, out m) && m > 0) return m;
            return 7;
        }

        // ---------- Table + insert ----------
        private static void EnsureTableExists()
        {
            if (Interlocked.CompareExchange(ref _tableEnsured, 1, 0) == 1) return;
            try
            {
                string ddl =
                    "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CasePendingNotifications' AND schema_id = SCHEMA_ID('dbo')) " +
                    "BEGIN " +
                    "  CREATE TABLE dbo.CasePendingNotifications ( " +
                    "    Id           BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
                    "    DocumentId   BIGINT       NOT NULL, " +
                    "    ActivityName NVARCHAR(255) NOT NULL, " +
                    "    FireAt       DATETIME2    NOT NULL, " +
                    "    Status       NVARCHAR(20) NOT NULL, " +
                    "    Attempts     INT          NOT NULL CONSTRAINT DF_CPN_Attempts DEFAULT (0), " +
                    "    CreatedAt    DATETIME2    NOT NULL CONSTRAINT DF_CPN_CreatedAt DEFAULT (SYSUTCDATETIME()), " +
                    "    ProcessedAt  DATETIME2    NULL, " +
                    "    LastError    NVARCHAR(MAX) NULL " +
                    "  ); " +
                    "  CREATE INDEX IX_CPN_FireAt_Status ON dbo.CasePendingNotifications (FireAt, Status); " +
                    "END";
                using (SqlConnection conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand(ddl, conn)) cmd.ExecuteNonQuery();
                }
                LogInfo("EnsureTableExists: 'dbo.CasePendingNotifications' ready.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _tableEnsured, 0);
                LogException("EnsureTableExists failed", ex);
                throw;
            }
        }

        private static long InsertPendingNotification(long documentId, string activityName, DateTime fireAtUtc)
        {
            string sql =
                "INSERT INTO dbo.CasePendingNotifications (DocumentId, ActivityName, FireAt, Status) " +
                "OUTPUT inserted.Id VALUES (@docId, @name, @fireAt, 'pending');";
            using (SqlConnection conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@docId",  documentId);
                    cmd.Parameters.AddWithValue("@name",   activityName == null ? "" : activityName);
                    cmd.Parameters.AddWithValue("@fireAt", fireAtUtc);
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        // ---------- Drain (opportunistic — runs from Execute) ----------
        private static void DrainDueRows()
        {
            try
            {
                string claim =
                    "UPDATE TOP (50) dbo.CasePendingNotifications " +
                    "SET    Status='processing', Attempts=Attempts+1, ProcessedAt=SYSUTCDATETIME() " +
                    "OUTPUT inserted.Id, inserted.DocumentId, inserted.ActivityName " +
                    "WHERE  Status='pending' AND FireAt <= SYSUTCDATETIME();";

                List<long>   ids   = new List<long>();
                List<long>   docs  = new List<long>();
                List<string> names = new List<string>();
                using (SqlConnection conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand(claim, conn))
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            ids.Add(Convert.ToInt64(r["Id"]));
                            docs.Add(Convert.ToInt64(r["DocumentId"]));
                            names.Add(Convert.ToString(r["ActivityName"]));
                        }
                    }
                }

                if (ids.Count == 0) return;
                LogInfo("DRAIN  claimed " + ids.Count + " due row(s).");

                for (int i = 0; i < ids.Count; i++)
                {
                    try { ProcessOne(ids[i], docs[i], names[i]); }
                    catch (Exception ex)
                    {
                        LogException("DRAIN row #" + ids[i] + " failed", ex);
                        UpdateRowStatus(ids[i], "failed", ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex) { LogException("DRAIN top-level failed", ex); }
        }

        private static void ProcessOne(long rowId, long documentId, string activityName)
        {
            long taskId; DateTime? closedDate; DateTime createdDate;
            bool found = LookupTaskStatus(documentId, activityName, out taskId, out closedDate, out createdDate);
            if (!found)
            {
                UpdateRowStatus(rowId, "skipped", "no matching task row");
                return;
            }
            if (closedDate.HasValue)
            {
                UpdateRowStatus(rowId, "skipped", "task closed at " + closedDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                return;
            }
            SendOverdueReminder(documentId, activityName, taskId, createdDate);
            UpdateRowStatus(rowId, "sent", null);
        }

        private static void UpdateRowStatus(long rowId, string status, string error)
        {
            string sql =
                "UPDATE dbo.CasePendingNotifications " +
                "SET    Status=@status, ProcessedAt=SYSUTCDATETIME(), LastError=@error " +
                "WHERE  Id=@id;";
            using (SqlConnection conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id",     rowId);
                    cmd.Parameters.AddWithValue("@status", status);
                    cmd.Parameters.AddWithValue("@error",  error == null ? (object)DBNull.Value : (object)error);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ---------- Task lookup ----------
        private static bool LookupTaskStatus(long documentId, string activityName,
                                              out long taskId, out DateTime? closedDate, out DateTime createdDate)
        {
            taskId = 0; closedDate = null; createdDate = DateTime.Now;
            string sql =
                "SELECT TOP 1 t.Id, t.ClosedDate, t.CreatedDate " +
                "FROM   dbo.[Task] t WITH (NOLOCK) " +
                "INNER  JOIN dbo.ActivityInstances  ai WITH (NOLOCK) ON ai.ActivityInstanceId = t.ActivityInstanceId " +
                "INNER  JOIN dbo.ActivityDefinition ad WITH (NOLOCK) ON ad.ActivityId         = ai.ActivityDefinitionId " +
                "WHERE  t.DocumentId = @docId " +
                "  AND  (ad.Name = @name OR ad.Title = @name) " +
                "ORDER  BY t.CreatedDate DESC";
            using (SqlConnection conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@docId", documentId);
                    cmd.Parameters.AddWithValue("@name",  activityName == null ? "" : activityName);
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;
                        taskId      = Convert.ToInt64(r["Id"]);
                        object cl   = r["ClosedDate"];
                        closedDate  = (cl == DBNull.Value) ? (DateTime?)null : Convert.ToDateTime(cl);
                        object cr   = r["CreatedDate"];
                        createdDate = (cr == DBNull.Value) ? DateTime.Now    : Convert.ToDateTime(cr);
                        return true;
                    }
                }
            }
        }

        // ---------- Email ----------
        private static void SendOverdueReminder(long documentId, string activityName, long taskId, DateTime taskCreatedDate)
        {
            Document document = new Document().Find(documentId);
            if (document == null) { LogError("Document.Find(" + documentId + ") null."); return; }

            string referenceNumber = document.ReferenceNumber == null ? "" : document.ReferenceNumber;
            string workflowName    = ResolveWorkflowName(documentId);
            if (string.IsNullOrEmpty(workflowName)) workflowName = "Temporary Cash Payment";
            double ageDays = (DateTime.Now - taskCreatedDate).TotalDays;

            string templateName = Cfg("CaseActivities:TemporaryCashPayment:EmailTemplateName",
                                      "OnTemporaryCashInvoiceOverdue");
            Intalio.Core.DAL.NotificationTemplate template =
                new Intalio.Core.DAL.NotificationTemplate().FindByName(templateName);

            string subject, body;
            if (template != null)
            {
                Dictionary<string, string> b = BuildBookmarks(
                    document, workflowName, referenceNumber, activityName, taskId, taskCreatedDate, ageDays);
                subject = ApplyBookmarks(template.Subject == null ? "" : template.Subject, b);
                body    = ApplyBookmarks(template.Body    == null ? "" : template.Body,    b);
            }
            else
            {
                subject = "[Overdue] Invoices not submitted - " + referenceNumber;
                body    = "<html><body><p>The case <b>" + referenceNumber + "</b> (" + workflowName +
                          ") has not had its original invoices submitted within the deadline. Task '<b>" +
                          activityName + "</b>' has been open since " +
                          taskCreatedDate.ToString("yyyy-MM-dd HH:mm") +
                          " (" + ageDays.ToString("N1") + " day(s) ago).</p>" +
                          "<p>Please take the required action (e.g. salary deduction).</p></body></html>";
            }

            SmtpSettings smtp = Intalio.Core.Configuration.SmtpSettings;
            if (smtp == null) { LogError("SmtpSettings null."); return; }

            SendMail(smtp, subject, body);
            LogInfo("Reminder email sent for DocumentId=" + documentId + ".");
        }

        private static Dictionary<string, string> BuildBookmarks(
            Document document, string workflowName, string referenceNumber, string activityName,
            long taskId, DateTime taskCreatedDate, double ageDays)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d["ReferenceNumber"] = referenceNumber;
            d["WorkflowName"]    = workflowName;
            d["DocumentId"]      = document.Id.ToString();
            d["TaskActivity"]    = activityName == null ? "" : activityName;
            d["TaskId"]          = taskId.ToString();
            d["TaskOpenedDate"]  = taskCreatedDate.ToString("yyyy-MM-dd HH:mm");
            d["TaskAgeDays"]     = ageDays.ToString("N1");
            d["Date"]            = DateTime.Now.ToString("yyyy-MM-dd");
            d["Time"]            = DateTime.Now.ToString("HH:mm");
            d["FromName"]        = Cfg("CaseActivities:TemporaryCashPayment:FromDisplayName", "Case Notifications");
            d["URL"]             = "";
            return d;
        }

        private static string ApplyBookmarks(string template, Dictionary<string, string> bookmarks)
        {
            if (string.IsNullOrEmpty(template) || bookmarks == null) return template == null ? "" : template;
            return Regex.Replace(template, @"\[([A-Za-z0-9_\.]+)\]", delegate(Match m)
            {
                string key = m.Groups[1].Value;
                string v;
                if (bookmarks.TryGetValue(key, out v)) return v == null ? "" : v;
                return m.Value;
            });
        }

        private static void SendMail(SmtpSettings settings, string subject, string body)
        {
            string toCsv  = Cfg     ("CaseActivities:TemporaryCashPayment:HrRecipients",   "");
            string ccCsv  = CfgEmpty("CaseActivities:TemporaryCashPayment:HrCcRecipients");
            string bccCsv = CfgEmpty("CaseActivities:TemporaryCashPayment:HrBccRecipients");
            string fromDn = Cfg     ("CaseActivities:TemporaryCashPayment:FromDisplayName", "Case Notifications");
            if (string.IsNullOrWhiteSpace(toCsv)) { LogError("HrRecipients empty."); return; }

            using (MailMessage msg = new MailMessage())
            {
                string fromAddress = string.IsNullOrWhiteSpace(settings.SystemEmail)
                    ? settings.UserName : settings.SystemEmail;
                msg.From = new MailAddress(fromAddress, fromDn);
                AddAddresses(msg.To,  toCsv);
                AddAddresses(msg.CC,  ccCsv);
                AddAddresses(msg.Bcc, bccCsv);
                msg.Subject         = subject;
                msg.SubjectEncoding = Encoding.UTF8;
                msg.BodyEncoding    = Encoding.UTF8;
                msg.IsBodyHtml      = LooksLikeHtml(body);
                msg.Body            = body;

                using (SmtpClient client = new SmtpClient(settings.SmtpServer, settings.Port))
                {
                    client.EnableSsl = settings.EnableSSL;
                    if (!string.IsNullOrEmpty(settings.UserName))
                        client.Credentials = new NetworkCredential(settings.UserName,
                            settings.Password == null ? "" : settings.Password);
                    client.DeliveryMethod = SmtpDeliveryMethod.Network;
                    client.Send(msg);
                }
            }
        }

        private static bool LooksLikeHtml(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            int i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            return i < body.Length && body[i] == '<';
        }

        private static void AddAddresses(MailAddressCollection coll, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            string[] parts = csv.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string addr = parts[i].Trim();
                if (addr.Length > 0) coll.Add(new MailAddress(addr));
            }
        }

        private static string ResolveWorkflowName(long documentId)
        {
            string conn = Intalio.Case.Core.Configuration.DbConnectionString;
            if (string.IsNullOrEmpty(conn)) return null;
            string sql =
                "SELECT TOP 1 wd.Name " +
                "FROM   Document d " +
                "JOIN   WorkflowInstances    wi ON wi.WorkflowInstanceId  = d.WorkflowInstanceId " +
                "JOIN   WorkflowDefinition   wd ON wd.WorkflowId          = wi.WorkflowDefinitionId " +
                "WHERE  d.Id = @docId;";
            try
            {
                using (SqlConnection c = new SqlConnection(conn))
                {
                    c.Open();
                    using (SqlCommand cmd = new SqlCommand(sql, c))
                    {
                        cmd.Parameters.AddWithValue("@docId", documentId);
                        object v = cmd.ExecuteScalar();
                        if (v == null || v == DBNull.Value) return null;
                        return Convert.ToString(v);
                    }
                }
            }
            catch (Exception ex) { LogException("ResolveWorkflowName failed", ex); return null; }
        }

        // ---------- Helpers + logging ----------
        private static string GetProp(WorkflowItem item, string key)
        {
            try
            {
                if (item == null || item.Properties == null) return "";
                object v = item.Properties[key] == null ? null : item.Properties[key].Value;
                return v == null ? "" : Convert.ToString(v);
            }
            catch { return ""; }
        }

        private static void SetProp(WorkflowItem item, string key, object value)
        {
            if (item == null || item.Properties == null) return;
            try
            {
                var p = item.Properties[key];
                if (p != null) p.Value = value;
            }
            catch { }
        }

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

        private static string Cfg(string key, string fallback)
        {
            try { return CodeActivityConfig.Get(key); }
            catch { return fallback; }
        }

        private static string CfgEmpty(string key)
        {
            try { return CodeActivityConfig.GetAllowEmpty(key); }
            catch { return ""; }
        }

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory,
                    "ScheduleHrNotificationActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] [tid=" +
                              Thread.CurrentThread.ManagedThreadId + "] " + message + Environment.NewLine;
                lock (LogLock) File.AppendAllText(path, line);
            }
            catch { }
        }

        private static void LogInfo (string m) { Write("INFO ", m); }
        private static void LogError(string m) { Write("ERROR", m); }
        private static void LogException(string m, Exception ex)
        {
            Write("ERROR", m + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);
        }

        // -------- Self-contained config reader (same as CopyDepartmentEvaluationToMetadataActivity) --------
        private static class CodeActivityConfig
        {
            private static JObject _root;
            private static string _path;
            private static readonly object _gate = new object();

            public static string Get(string keyPath)           { return Resolve(keyPath, false); }
            public static string GetAllowEmpty(string keyPath) { return Resolve(keyPath, true); }

            private static string Resolve(string keyPath, bool allowEmpty)
            {
                Load();
                JToken node = _root;
                string[] parts = keyPath.Split(':');
                for (int i = 0; i < parts.Length; i++)
                {
                    JObject obj = node as JObject;
                    JToken next;
                    if (obj != null && obj.TryGetValue(parts[i], StringComparison.OrdinalIgnoreCase, out next))
                        node = next;
                    else
                        throw new InvalidOperationException(
                            "Missing required setting '" + keyPath + "' in '" + _path + "'.");
                }
                if (node == null || node.Type == JTokenType.Null)
                    throw new InvalidOperationException("Setting '" + keyPath + "' is null in '" + _path + "'.");
                string value = node.ToString();
                if (!allowEmpty && string.IsNullOrEmpty(value))
                    throw new InvalidOperationException("Setting '" + keyPath + "' is empty in '" + _path + "'.");
                return value;
            }

            private static void Load()
            {
                if (_root != null) return;
                lock (_gate)
                {
                    if (_root != null) return;
                    string[] candidates = new string[] {
                        Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
                        Path.Combine(AppContext.BaseDirectory,         "appsettings.json")
                    };
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (!File.Exists(candidates[i])) continue;
                        _root = JObject.Parse(File.ReadAllText(candidates[i]));
                        _path = candidates[i];
                        return;
                    }
                    throw new InvalidOperationException("appsettings.json not found.");
                }
            }
        }
    }
}
