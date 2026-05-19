using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Intalio.Core;                 // SmtpSettings
using Intalio.Core.API;             // ManageNotificationTemplate
using Intalio.Core.Utility;         // AsposeLicense
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// Sends an HTML email to a hardcoded recipient list, attaching every document
    /// of the current case.
    ///
    /// Subject + body come from a NotificationTemplate row (the same templates Case
    /// Designer exposes under "Email Templates"), looked up by name.  Bookmarks
    /// inside the template ([ReferenceNumber], [WorkflowName], ...) are substituted
    /// from the case context before sending.
    ///
    /// SMTP settings come from the Portal's configured Intalio.Core.Configuration.SmtpSettings
    /// (the same connection the Portal itself uses for notifications) — no SMTP
    /// credentials live in this file.
    ///
    /// To use a different template, either:
    ///   - change the DefaultTemplateName constant below, OR
    ///   - set a workflow property named "emailTemplateName" on the case to override
    ///     it without touching this code.
    /// </summary>
    internal class SendCaseDocumentsEmailActivity : ActivityTemplate
    {
        // ============================================================
        //                 CONFIGURATION
        // ============================================================

        #region Deveploment
        private const string IamBaseUrl       = "http://localhost:11111";
        private const string StorageBaseUrl   = "http://localhost:44444/";
        private const string AuthCaseClientId     = "398ff3ac-49b6-44fd-a70b-3cd69874c118";
        private const string AuthCaseClientSecret = "ac63daac-edd5-496a-834f-e14a0e76c5c0";
        private const string AuthUserName     = "admin";
        private const string AuthUserPassword = "1";
        private const string LogDirectory = @"C:\Logs\Case";

        private const string FromDisplayName = "Case Notifications";
        private const string ToRecipients = "hr@intalio.com; archive@intalio.com; ahmed.abdelghany@intalio.com";
        private const string CcRecipients = "";
        private const string BccRecipients = "";
        private const string DefaultTemplateName = "OnCaseDocumentsForAction";
        #endregion

        #region Staging
        //private const string IamBaseUrl = "http://uciamdev.unioncoop.ae";        
        //private const string StorageBaseUrl = "http://ucstoragedev.unioncoop.ae/";
        //private const string AuthCaseClientId = "ffcd9846-0390-4792-94f5-43eefb2c0eae";
        //private const string AuthCaseClientSecret = "ebc9af9d-4b49-4e0d-a50b-c792b53e63b8";
        //private const string AuthUserName = "admin";
        //private const string AuthUserPassword = "1";   
        //private const string LogDirectory = @"C:\Logs\Case";

        //private const string FromDisplayName = "Case Notifications";
        //private const string ToRecipients = "hr@intalio.com; archive@intalio.com; ahmed.abdelghany@intalio.com";
        //private const string CcRecipients = "";
        //private const string BccRecipients = "";
        //private const string DefaultTemplateName = "OnCaseDocumentsForAction";
        #endregion

        #region Production
        //private const string IamBaseUrl = "https://uciam.unioncoop.ae";        
        //private const string StorageBaseUrl = "https://ucstorage.unioncoop.ae/";
        //private const string AuthCaseClientId = "ffcd9846-0390-4792-94f5-43eefb2c0eae";
        //private const string AuthCaseClientSecret = "ebc9af9d-4b49-4e0d-a50b-c792b53e63b8";
        //private const string AuthUserName = "admin";
        //private const string AuthUserPassword = "1";
        //private const string LogDirectory = @"C:\Logs\Case";

        //private const string FromDisplayName = "Case Notifications";
        //private const string ToRecipients = "hr@intalio.com; archive@intalio.com; ahmed.abdelghany@intalio.com";
        //private const string CcRecipients = "";
        //private const string BccRecipients = "";
        //private const string DefaultTemplateName = "OnCaseDocumentsForAction";
        #endregion

        private static readonly object LogLock = new object();

        // Aspose license — applied once per process, used for Word→PDF conversion.
        private static int _licenseApplied;

        private sealed class TokenResponse { public string access_token { get; set; } }

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string documentIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- BEGIN  DocumentId={documentIdStr} ----");

            if (!long.TryParse(documentIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{documentIdStr}'");
                return;
            }

            try
            {
                // 1) Case context.
                Document document = new Document().Find(documentId);
                if (document == null) { LogError($"Document.Find({documentId}) returned null."); return; }

                string referenceNumber = document.ReferenceNumber ?? "";
                string workflowName    = ResolveWorkflowName(documentId) ?? "Case";
                LogInfo($"Case: workflow='{workflowName}' ref='{referenceNumber}'");

                // 2) Email template (optionally overridden per case via workflow property).
                string templateName = GetProp(workflowItem, "emailTemplateName");
                if (string.IsNullOrWhiteSpace(templateName)) templateName = DefaultTemplateName;

                var template = new Intalio.Core.DAL.NotificationTemplate().FindByName(templateName);
                if (template == null)
                {
                    LogError($"NotificationTemplate '{templateName}' not found. Aborting.");
                    return;
                }
                LogInfo($"Template: '{template.Name}' (id={template.Id})");

                // 3) Bookmark substitutions.
                var bookmarks = BuildBookmarks(workflowItem, document, workflowName, referenceNumber);
                string subject = ApplyBookmarks(template.Subject ?? "", bookmarks);
                string body    = ApplyBookmarks(template.Body    ?? "", bookmarks);

                // 4) Pull attachments.
                var attachments = LoadAttachments(documentId);
                LogInfo($"Attachments: {attachments.Count}");

                // 5) SMTP settings — pulled from the Portal-managed Intalio.Core.Configuration.
                var smtp = Intalio.Core.Configuration.SmtpSettings;
                if (smtp == null)
                {
                    LogError("Intalio.Core.Configuration.SmtpSettings is null — the Portal hasn't loaded SMTP configuration. Aborting.");
                    return;
                }
                LogInfo($"SMTP: {smtp.SmtpServer}:{smtp.Port} ssl={smtp.EnableSSL} from={smtp.SystemEmail}");

                // 6) Compose + send.
                SendMail(smtp, subject, body, attachments);
                LogInfo("Email sent.");
                LogInfo($"---- END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END    DocumentId={documentIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Bookmarks
        // ---------------------------------------------------------------------
        /// <summary>
        /// Builds the bookmark dictionary for template substitution.
        ///
        /// Always-provided keys (case-insensitive):
        ///   ReferenceNumber, WorkflowName, DocumentId, Date, Time, FromName
        ///
        /// Plus every value in workflowItem.Properties (so any form field is also
        /// available as a bookmark by its key, e.g. [candidateName], [jobTitle]).
        /// </summary>
        private static Dictionary<string, string> BuildBookmarks(
            WorkflowItem item, Document document, string workflowName, string referenceNumber)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ReferenceNumber", referenceNumber },
                { "WorkflowName",    workflowName },
                { "DocumentId",      document.Id.ToString() },
                { "Date",            DateTime.Now.ToString("yyyy-MM-dd") },
                { "Time",            DateTime.Now.ToString("HH:mm") },
                { "FromName",        FromDisplayName },
                { "URL",             "" }
            };
            if (item?.Properties != null)
            {
                foreach (var p in item.Properties)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    string v = p.Value == null ? "" : Convert.ToString(p.Value);
                    dict[p.Name] = v;
                }
            }
            return dict;
        }

        /// <summary>Replaces [BookmarkName] tokens in template text with values from the dictionary.</summary>
        private static string ApplyBookmarks(string template, Dictionary<string, string> bookmarks)
        {
            if (string.IsNullOrEmpty(template) || bookmarks == null || bookmarks.Count == 0) return template ?? "";
            return Regex.Replace(template, @"\[([A-Za-z0-9_\.]+)\]", m =>
            {
                string key = m.Groups[1].Value;
                return bookmarks.TryGetValue(key, out var v) ? (v ?? "") : m.Value;
            });
        }

        // ---------------------------------------------------------------------
        // Mail send
        // ---------------------------------------------------------------------
        private sealed class AttachmentBytes { public string Name; public byte[] Bytes; public string ContentType; }

        private static void SendMail(SmtpSettings settings, string subject, string body, List<AttachmentBytes> attachments)
        {
            using (var msg = new MailMessage())
            {
                string fromAddress = string.IsNullOrWhiteSpace(settings.SystemEmail) ? settings.UserName : settings.SystemEmail;
                msg.From = new MailAddress(fromAddress, FromDisplayName);
                AddAddresses(msg.To,  ToRecipients);
                AddAddresses(msg.CC,  CcRecipients);
                AddAddresses(msg.Bcc, BccRecipients);
                msg.Subject = subject;
                msg.SubjectEncoding = Encoding.UTF8;
                msg.BodyEncoding    = Encoding.UTF8;
                msg.IsBodyHtml      = LooksLikeHtml(body);
                msg.Body            = body;

                foreach (var a in attachments)
                {
                    var att = new System.Net.Mail.Attachment(new MemoryStream(a.Bytes), a.Name, a.ContentType);
                    msg.Attachments.Add(att);
                }

                using (var smtp = new SmtpClient(settings.SmtpServer, settings.Port))
                {
                    smtp.EnableSsl = settings.EnableSSL;
                    if (!string.IsNullOrEmpty(settings.UserName))
                        smtp.Credentials = new NetworkCredential(settings.UserName, settings.Password ?? "");
                    smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                    smtp.Send(msg);
                }
            }
        }

        private static bool LooksLikeHtml(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            // Cheap check — every NotificationTemplate body the Portal ships starts with <meta> or <html>.
            int i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            return i < body.Length && body[i] == '<';
        }

        private static void AddAddresses(MailAddressCollection coll, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            foreach (var raw in csv.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var addr = raw.Trim();
                if (addr.Length > 0) coll.Add(new MailAddress(addr));
            }
        }

        // ---------------------------------------------------------------------
        // Attachment loading (Storage) — every Word document is converted to PDF
        // before being added, so the outgoing email only ever carries PDF files.
        // ---------------------------------------------------------------------
        private static List<AttachmentBytes> LoadAttachments(long documentId)
        {
            var result = new List<AttachmentBytes>();
            var atts = new Intalio.Case.Portal.Core.DAL.Attachment().ListOriginalDocumentByDocumentId(documentId);
            if (atts == null || atts.Count == 0) return result;

            string token = GetAccessTokenAsync().GetAwaiter().GetResult();
            bool licenseEnsured = false;

            foreach (var a in atts)
            {
                if (a == null || string.IsNullOrEmpty(a.StorageAttachmentId) || string.IsNullOrEmpty(a.Name)) continue;
                try
                {
                    byte[] bytes = DownloadFromStorageAsync(a.StorageAttachmentId, token).GetAwaiter().GetResult();
                    if (bytes == null || bytes.Length == 0) continue;
                    LogInfo($"Downloaded '{a.Name}' ({bytes.Length} bytes).");

                    string ext = (Path.GetExtension(a.Name) ?? "").ToLowerInvariant();

                    // Already a PDF — attach as-is.
                    if (ext == ".pdf")
                    {
                        result.Add(new AttachmentBytes
                        {
                            Name        = a.Name,
                            Bytes       = bytes,
                            ContentType = "application/pdf"
                        });
                        continue;
                    }

                    // Word document — convert to PDF on the fly with Aspose.Words.
                    if (ext == ".docx" || ext == ".doc")
                    {
                        if (!licenseEnsured) { EnsureAsposeLicensed(); licenseEnsured = true; }

                        byte[] pdfBytes;
                        try { pdfBytes = ConvertWordToPdf(bytes); }
                        catch (Exception ex)
                        {
                            LogException($"Word->PDF conversion failed for '{a.Name}' — skipping", ex);
                            continue;
                        }

                        string pdfName = Path.GetFileNameWithoutExtension(a.Name) + ".pdf";
                        LogInfo($"Converted '{a.Name}' -> '{pdfName}' ({pdfBytes.Length} bytes).");
                        result.Add(new AttachmentBytes
                        {
                            Name        = pdfName,
                            Bytes       = pdfBytes,
                            ContentType = "application/pdf"
                        });
                        continue;
                    }

                    // Any other file type — skip, per the "PDF only" rule.
                    LogInfo($"Skipping '{a.Name}': extension '{ext}' is not PDF/Word.");
                }
                catch (Exception ex) { LogException($"Failed processing '{a.Name}'", ex); }
            }
            return result;
        }

        /// <summary>Converts a Word document (.docx or .doc) to PDF bytes using Aspose.Words.</summary>
        private static byte[] ConvertWordToPdf(byte[] wordBytes)
        {
            using (var inMs  = new MemoryStream(wordBytes))
            using (var outMs = new MemoryStream())
            {
                var doc = new Aspose.Words.Document(inMs);
                doc.Save(outMs, Aspose.Words.SaveFormat.Pdf);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Applies the Aspose license once per process. Without it, Aspose.Words
        /// produces a watermarked PDF capped at 4 pages.
        /// </summary>
        private static void EnsureAsposeLicensed()
        {
            if (Interlocked.Exchange(ref _licenseApplied, 1) == 1) return;
            try
            {
                using (var s = new AsposeLicense().Get())
                    if (s != null) { s.Position = 0; new Aspose.Words.License().SetLicense(s); }
                LogInfo("Aspose.Words license applied.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _licenseApplied, 0);
                LogException("Aspose license could not be applied — Word->PDF output will be evaluation-limited", ex);
            }
        }

        // ---------------------------------------------------------------------
        // Workflow-name SQL lookup
        // ---------------------------------------------------------------------
        private static string ResolveWorkflowName(long documentId)
        {
            string conn = Intalio.Case.Core.Configuration.DbConnectionString;
            if (string.IsNullOrEmpty(conn)) return null;

            const string sql = @"
SELECT TOP 1 wd.Name
FROM   Document d
JOIN   WorkflowInstances    wi ON wi.WorkflowInstanceId  = d.WorkflowInstanceId
JOIN   WorkflowDefinition   wd ON wd.WorkflowId          = wi.WorkflowDefinitionId
WHERE  d.Id = @docId;";

            try
            {
                using (var c = new SqlConnection(conn))
                {
                    c.Open();
                    using (var cmd = new SqlCommand(sql, c))
                    {
                        cmd.Parameters.AddWithValue("@docId", documentId);
                        var v = cmd.ExecuteScalar();
                        return v == null || v == DBNull.Value ? null : Convert.ToString(v);
                    }
                }
            }
            catch (Exception ex) { LogException($"ResolveWorkflowName({documentId}) failed", ex); return null; }
        }

        // ---------------------------------------------------------------------
        // Storage IO + IAM
        // ---------------------------------------------------------------------
        private static async System.Threading.Tasks.Task<byte[]> DownloadFromStorageAsync(string storageId, string bearerToken)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                using (var resp = await client.GetAsync(
                    StorageBaseUrl + "Storage/Download?fileId=" + Uri.EscapeDataString(storageId)))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync();
                }
            }
        }

        private static async System.Threading.Tasks.Task<string> GetAccessTokenAsync()
        {
            using (var iam = new HttpClient { BaseAddress = new Uri(IamBaseUrl) })
            using (var req = new HttpRequestMessage(HttpMethod.Post, "/connect/token"))
            {
                req.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type",    "password"),
                    new KeyValuePair<string, string>("client_id",     AuthCaseClientId),
                    new KeyValuePair<string, string>("client_secret", AuthCaseClientSecret),
                    new KeyValuePair<string, string>("scope",         "IdentityServerApi"),
                    new KeyValuePair<string, string>("username",      AuthUserName),
                    new KeyValuePair<string, string>("password",      AuthUserPassword),
                });
                using (var resp = await iam.SendAsync(req))
                {
                    resp.EnsureSuccessStatusCode();
                    string body = await resp.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<TokenResponse>(body).access_token;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Helpers + logging
        // ---------------------------------------------------------------------
        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? "" : Convert.ToString(v); }
            catch { return ""; }
        }

        private static void LogInfo (string m) { Write("INFO ", m); }
        private static void LogWarn (string m) { Write("WARN ", m); }
        private static void LogError(string m) { Write("ERROR", m); }

        private static void LogException(string context, Exception ex)
        {
            var sb = new StringBuilder().Append(context).Append(": ");
            for (var e = ex; e != null; e = e.InnerException)
                sb.Append('[').Append(e.GetType().FullName).Append("] ").Append(e.Message).Append(" --> ");
            Write("ERROR", sb.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                string path = Path.Combine(LogDirectory,
                    "SendCaseDocumentsEmailActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
