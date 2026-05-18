using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Activities
{
    /// <summary>
    /// Sends an HTML email — to a hardcoded recipient list — with every attachment
    /// of the current case attached. Reads case context (reference number + workflow
    /// name) from the WorkflowItem and renders them into a clean HTML body.
    ///
    /// Usage as a Code Activity Template: drop this class into the Designer and
    /// place it after the final approval step (or wherever distribution should
    /// happen) in the workflow.
    /// </summary>
    internal class SendCaseDocumentsEmailActivity : ActivityTemplate
    {
        // ============================================================
        //                 CONFIGURATION — EDIT HERE
        // ============================================================

        // SMTP
        private const string SmtpHost      = "smtp.intalio.com";  // <-- replace with your SMTP host
        private const int    SmtpPort      = 587;                 // 587 = STARTTLS, 25 = plain, 465 = implicit TLS
        private const bool   SmtpUseSsl    = true;
        private const string SmtpUserName  = "no-reply@intalio.com";
        private const string SmtpPassword  = "CHANGE_ME";

        // Email envelope
        private const string FromAddress   = "no-reply@intalio.com";
        private const string FromName      = "Case Notifications";

        // Recipients — separate multiple addresses with ";"
        private const string ToRecipients  = "hr@intalio.com; archive@intalio.com";
        private const string CcRecipients  = "";    // optional, "" to omit
        private const string BccRecipients = "";    // optional, "" to omit

        // IAM + Storage (for downloading attachment bytes)
        private const string IamBaseUrl       = "http://localhost:11111";
        private const string StorageBaseUrl   = "http://localhost:44444/";
        private const string AuthClientId     = "398ff3ac-49b6-44fd-a70b-3cd69874c118";
        private const string AuthClientSecret = "ac63daac-edd5-496a-834f-e14a0e76c5c0";
        private const string AuthUserName     = "admin";
        private const string AuthUserPassword = "1";

        // Daily-rotated log: C:\IntalioLogs\SendCaseDocumentsEmailActivity-YYYY-MM-DD.log
        private const string LogDirectory     = @"C:\IntalioLogs";
        private static readonly object LogLock = new object();

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
                Document document = new Document().Find(documentId);
                if (document == null) { LogError($"Document.Find({documentId}) returned null."); return; }

                string referenceNumber = document.ReferenceNumber ?? "";
                string workflowName    = ResolveWorkflowName(documentId) ?? "Case";
                LogInfo($"Case context: workflow='{workflowName}' refNumber='{referenceNumber}'");

                // Pull all attachments + their bytes
                var attachments = LoadAttachments(documentId);
                LogInfo($"Loaded {attachments.Count} attachment(s).");

                // Compose + send
                System.Threading.Tasks.Task
                    .Run(() => SendEmailAsync(workflowName, referenceNumber, attachments))
                    .GetAwaiter().GetResult();

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
        // Attachment loading (Storage HTTP)
        // ---------------------------------------------------------------------
        private sealed class AttachmentBytes { public string Name; public byte[] Bytes; public string ContentType; }

        private static List<AttachmentBytes> LoadAttachments(long documentId)
        {
            var result = new List<AttachmentBytes>();
            var atts = new Attachment().ListOriginalDocumentByDocumentId(documentId);
            if (atts == null || atts.Count == 0) return result;

            string token = GetAccessTokenAsync().GetAwaiter().GetResult();

            foreach (var a in atts)
            {
                if (a == null || string.IsNullOrEmpty(a.StorageAttachmentId) || string.IsNullOrEmpty(a.Name))
                {
                    LogWarn("Skipping attachment with null/empty name or storage id.");
                    continue;
                }
                try
                {
                    byte[] bytes = DownloadFromStorageAsync(a.StorageAttachmentId, token).GetAwaiter().GetResult();
                    if (bytes == null || bytes.Length == 0)
                    {
                        LogWarn($"Skipping '{a.Name}': empty bytes from storage.");
                        continue;
                    }
                    result.Add(new AttachmentBytes
                    {
                        Name        = a.Name,
                        Bytes       = bytes,
                        ContentType = GuessContentType(a.Name)
                    });
                    LogInfo($"Downloaded '{a.Name}' ({bytes.Length} bytes).");
                }
                catch (Exception ex) { LogException($"Download FAILED for '{a.Name}'", ex); }
            }
            return result;
        }

        private static string GuessContentType(string name)
        {
            string ext = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".pdf":  return "application/pdf";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".doc":  return "application/msword";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls":  return "application/vnd.ms-excel";
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".txt":  return "text/plain";
                default:      return "application/octet-stream";
            }
        }

        // ---------------------------------------------------------------------
        // Email composition + send
        // ---------------------------------------------------------------------
        private static async System.Threading.Tasks.Task SendEmailAsync(
            string workflowName, string referenceNumber, List<AttachmentBytes> attachments)
        {
            string subject = $"[{referenceNumber}] {workflowName} — Case Documents";
            string htmlBody = BuildHtmlBody(workflowName, referenceNumber, attachments);
            string plainBody = BuildPlainBody(workflowName, referenceNumber, attachments);

            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress(FromAddress, FromName);
                AddAddresses(msg.To,  ToRecipients);
                AddAddresses(msg.CC,  CcRecipients);
                AddAddresses(msg.Bcc, BccRecipients);
                msg.Subject = subject;

                var plain = AlternateView.CreateAlternateViewFromString(plainBody, new ContentType("text/plain; charset=utf-8"));
                var html  = AlternateView.CreateAlternateViewFromString(htmlBody,  new ContentType("text/html;  charset=utf-8"));
                msg.AlternateViews.Add(plain);
                msg.AlternateViews.Add(html);

                foreach (var a in attachments)
                {
                    var stream = new MemoryStream(a.Bytes);
                    var att    = new System.Net.Mail.Attachment(stream, a.Name, a.ContentType);
                    msg.Attachments.Add(att);
                }

                using (var smtp = new SmtpClient(SmtpHost, SmtpPort))
                {
                    smtp.EnableSsl   = SmtpUseSsl;
                    smtp.Credentials = new NetworkCredential(SmtpUserName, SmtpPassword);
                    smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                    LogInfo($"Sending: subject='{subject}'  to={ToRecipients}  cc={CcRecipients}  bcc={BccRecipients}  attachments={attachments.Count}");
                    await smtp.SendMailAsync(msg);
                    LogInfo("Email sent successfully.");
                }
            }
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

        /// <summary>HTML body with inline styles for email-client compatibility.</summary>
        private static string BuildHtmlBody(string workflowName, string referenceNumber, List<AttachmentBytes> attachments)
        {
            string today = DateTime.Now.ToString("dd MMM yyyy");
            string attRows = "";
            if (attachments.Count == 0)
            {
                attRows = "<tr><td style='padding:8px;color:#888;border-top:1px solid #eee'>(no attachments)</td><td style='padding:8px;color:#888;border-top:1px solid #eee;text-align:right'></td></tr>";
            }
            else
            {
                int i = 1;
                foreach (var a in attachments)
                {
                    string kb = (a.Bytes.Length / 1024.0).ToString("N0") + " KB";
                    attRows += "<tr>"
                            +  "<td style='padding:8px;border-top:1px solid #eee;font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#333'>"
                            +     i + ". " + WebEncode(a.Name)
                            +  "</td>"
                            +  "<td style='padding:8px;border-top:1px solid #eee;font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#888;text-align:right'>"
                            +     kb
                            +  "</td>"
                            +  "</tr>";
                    i++;
                }
            }

            return
"<!doctype html><html><body style='margin:0;padding:0;background:#f4f6f8;font-family:Segoe UI,Arial,sans-serif;color:#333'>" +
"<table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='background:#f4f6f8;padding:24px 0'>" +
  "<tr><td align='center'>" +
    "<table role='presentation' cellpadding='0' cellspacing='0' border='0' width='600' style='background:#ffffff;border:1px solid #e1e5ea;border-radius:6px;overflow:hidden'>" +

      "<tr><td style='background:#1f3a5f;padding:18px 24px;color:#ffffff'>" +
        "<div style='font-size:18px;font-weight:600'>" + WebEncode(workflowName) + "</div>" +
        "<div style='font-size:13px;opacity:0.85;margin-top:2px'>Reference: <strong>" + WebEncode(referenceNumber) + "</strong></div>" +
      "</td></tr>" +

      "<tr><td style='padding:20px 24px;font-size:14px;line-height:1.5;color:#333'>" +
        "<p style='margin:0 0 12px'>Dear team,</p>" +
        "<p style='margin:0 0 12px'>Please find attached the documents for case <strong>" + WebEncode(referenceNumber) + "</strong> (" + WebEncode(workflowName) + ").</p>" +
        "<p style='margin:0'>Kind regards,<br><span style='color:#777'>" + WebEncode(FromName) + "</span></p>" +
      "</td></tr>" +

      "<tr><td style='padding:0 24px 8px;font-size:13px;color:#555;font-weight:600'>Attached documents</td></tr>" +
      "<tr><td style='padding:0 24px 18px'>" +
        "<table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;border:1px solid #e1e5ea'>" +
          "<tr style='background:#f7f9fc'>" +
            "<th align='left'  style='padding:8px;font-size:12px;color:#666;font-weight:600;border-bottom:1px solid #e1e5ea'>Filename</th>" +
            "<th align='right' style='padding:8px;font-size:12px;color:#666;font-weight:600;border-bottom:1px solid #e1e5ea'>Size</th>" +
          "</tr>" +
          attRows +
        "</table>" +
      "</td></tr>" +

      "<tr><td style='background:#fafbfc;padding:12px 24px;font-size:11px;color:#999;border-top:1px solid #e1e5ea'>" +
        "Generated on " + WebEncode(today) + " — this is an automated message from the Case Portal." +
      "</td></tr>" +

    "</table>" +
  "</td></tr>" +
"</table>" +
"</body></html>";
        }

        private static string BuildPlainBody(string workflowName, string referenceNumber, List<AttachmentBytes> attachments)
        {
            var sb = new StringBuilder();
            sb.AppendLine(workflowName + " — " + referenceNumber);
            sb.AppendLine(new string('-', 60));
            sb.AppendLine();
            sb.AppendLine("Dear team,");
            sb.AppendLine();
            sb.AppendLine("Please find attached the documents for case " + referenceNumber + " (" + workflowName + ").");
            sb.AppendLine();
            sb.AppendLine("Attached documents:");
            if (attachments.Count == 0) sb.AppendLine("  (no attachments)");
            else
            {
                int i = 1;
                foreach (var a in attachments)
                {
                    sb.AppendLine("  " + i + ". " + a.Name + "  (" + (a.Bytes.Length / 1024.0).ToString("N0") + " KB)");
                    i++;
                }
            }
            sb.AppendLine();
            sb.AppendLine("Kind regards,");
            sb.AppendLine(FromName);
            return sb.ToString();
        }

        private static string WebEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        // ---------------------------------------------------------------------
        // Workflow-name SQL lookup (same pattern as ArchiveHRDocumentsToDMSActivity)
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
                    new KeyValuePair<string, string>("client_id",     AuthClientId),
                    new KeyValuePair<string, string>("client_secret", AuthClientSecret),
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
