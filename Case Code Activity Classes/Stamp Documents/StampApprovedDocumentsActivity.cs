using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Intalio.Core.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Activities
{
    /// <summary>
    /// Stamps an approval image into the footer of every PDF / DOCX attachment   
    /// </summary>
    internal class StampApprovedDocumentsActivity : ActivityTemplate
    {
        // -------- STATIC CONFIG --------
        private const string StampImagePath = @"C:\stamps\approved.png";
        private const float  StampWidthPt   = 120f;
        private const float  StampHeightPt  = 60f;

        // The Storage server exposes /Storage/Download + /Storage/ReplaceNoVersioning,
        // both protected by an IAM bearer
        // token. These are NOT DMS constants — they're the credentials this code
        // uses to authenticate against the Case Portal's storage service.
        private const string IamBaseUrl       = "http://localhost:11111";
        private const string StorageBaseUrl   = "http://localhost:44444/";
        private const string AuthCaseClientId     = "42faa0d5-9856-4639-9d73-36a5fb6bb561";
        private const string AuthCaseClientSecret = "5b83f635-35d6-4d87-bda4-bbd471ce26c2";
        private const string AuthUserName     = "admin";
        private const string AuthUserPassword = "1";

        // Daily-rotated log: C:\IntalioLogs\StampApprovedDocumentsActivity-YYYY-MM-DD.log
        private const string LogDirectory = @"C:\Logs\Case";
        private static readonly object LogLock = new object();

        // Aspose license is applied once per process. Unlicensed Aspose stamps
        // only the first 4 pages of a PDF and adds an "Evaluation Only" header.
        private static int _licenseApplied; // 0 = not yet, 1 = done

        private sealed class TokenResponse { public string access_token { get; set; } }

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr   = GetProp(workflowItem, "DocumentId");
            long.TryParse(docIdStr, out long earlyDocId);
            string approver   = ResolveCurrentApprover(workflowItem, earlyDocId);
            if (string.IsNullOrWhiteSpace(approver))
                approver = GetProp(workflowItem, "requesterName"); // optional manual override
            if (string.IsNullOrWhiteSpace(approver)) approver = "Approver";
            string approvedOn = DateTime.Now.ToString("yyyy-MMMM-dd");

            LogInfo($"---- BEGIN  DocumentId={docIdStr}  approver={approver}  on={approvedOn} ----");

            if (!File.Exists(StampImagePath))
            {
                LogError($"Stamp image not found at '{StampImagePath}'. Aborting.");
                return;
            }
            if (!long.TryParse(docIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"DocumentId property is missing or invalid: '{docIdStr}'. Aborting.");
                return;
            }

            try
            {
                EnsureAsposeLicensed();

                Document document = new Document().Find(documentId);
                if (document == null) { LogError($"Document.Find({documentId}) returned null."); return; }
                LogInfo($"Document loaded: Id={document.Id}  StatusId={document.StatusId}");

                byte[] stampBytes = File.ReadAllBytes(StampImagePath);
                LogInfo($"Stamp image loaded ({stampBytes.Length} bytes).");

                System.Threading.Tasks.Task
                    .Run(() => StampAllAsync(document, stampBytes, approver, approvedOn))
                    .GetAwaiter().GetResult();

                LogInfo($"---- END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END    DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        private async System.Threading.Tasks.Task StampAllAsync(Document document, byte[] stampBytes, string approver, string when)
        {
            var attachments = new Attachment().FindByDocumentId(document.Id);
            int total = attachments?.Count ?? 0;
            LogInfo($"FindByDocumentId({document.Id}) returned {total} attachment(s).");
            if (total == 0) return;

            string accessToken = await GetAccessTokenAsync();
            LogInfo($"Auth token acquired (length={accessToken?.Length ?? 0}).");

            int stamped = 0, skipped = 0, failed = 0;
            foreach (var attachment in attachments)
            {
                if (attachment == null || string.IsNullOrEmpty(attachment.Name))
                { LogWarn("Skipping attachment with null/empty name."); skipped++; continue; }
                if (string.IsNullOrEmpty(attachment.StorageAttachmentId))
                { LogWarn($"Skipping '{attachment.Name}' (id={attachment.Id}): missing StorageAttachmentId."); skipped++; continue; }

                string ext = (Path.GetExtension(attachment.Name) ?? "").ToLowerInvariant();
                if (ext != ".pdf" && ext != ".docx")
                { LogInfo($"Skipping '{attachment.Name}': unsupported ext '{ext}'."); skipped++; continue; }

                LogInfo($"Attachment {attachment.Id} '{attachment.Name}' sizeDb={attachment.Size}");

                byte[] inputBytes;
                try { inputBytes = await StorageDownloadAsync(attachment.StorageAttachmentId, accessToken); }
                catch (Exception ex) { LogException($"Download failed for '{attachment.Name}'", ex); failed++; continue; }
                if (inputBytes == null || inputBytes.Length == 0)
                { LogWarn($"Empty bytes for '{attachment.Name}'."); skipped++; continue; }
                LogInfo($"Read '{attachment.Name}' ({inputBytes.Length} bytes).");

                byte[] outBytes;
                try
                {
                    outBytes = (ext == ".pdf")
                        ? StampPdf(inputBytes, stampBytes, approver, when)
                        : StampDocx(inputBytes, stampBytes, approver, when);
                }
                catch (Exception ex) { LogException($"Stamp failed for '{attachment.Name}'", ex); failed++; continue; }
                LogInfo($"Stamped '{attachment.Name}' -> {outBytes.Length} bytes.");

                // Replace in-place in Storage (creates no new version — overwrites current)
                try
                {
                    await StorageReplaceNoVersioningAsync(
                        storageAttachmentId: attachment.StorageAttachmentId,
                        fileName: attachment.Name,
                        contentType: (ext == ".pdf") ? "application/pdf"
                                   : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        bytes: outBytes,
                        bearerToken: accessToken);
                    LogInfo($"Replaced '{attachment.Name}' in storage (id={attachment.StorageAttachmentId}).");
                    stamped++;
                }
                catch (Exception ex)
                {
                    LogException($"Replace failed for '{attachment.Name}' (id={attachment.StorageAttachmentId})", ex);
                    failed++;
                }
            }

            LogInfo($"Summary: total={total}  stamped={stamped}  skipped={skipped}  failed={failed}");
        }

        // ---------------------------------------------------------------------
        // Aspose license — must run before any Aspose.* call so we stamp every
        // page and the "Evaluation Only" header is suppressed.
        // ---------------------------------------------------------------------
        private static void EnsureAsposeLicensed()
        {
            if (System.Threading.Interlocked.Exchange(ref _licenseApplied, 1) == 1) return;

            try
            {
                using (var s = new AsposeLicense().Get())
                {
                    if (s != null) { s.Position = 0; new Aspose.Pdf.License().SetLicense(s); }
                }
                using (var s = new AsposeLicense().Get())
                {
                    if (s != null) { s.Position = 0; new Aspose.Words.License().SetLicense(s); }
                }
                LogInfo("Aspose license applied (Pdf + Words).");
            }
            catch (Exception ex)
            {
                // Don't leave the flag set if the license failed to apply — try again next run.
                System.Threading.Interlocked.Exchange(ref _licenseApplied, 0);
                LogException("Aspose license could not be applied — output will be evaluation-limited", ex);
            }
        }

        // ---------------------------------------------------------------------
        // Stamping (Aspose, in-process)
        // ---------------------------------------------------------------------
        private static byte[] StampPdf(byte[] pdfBytes, byte[] imageBytes, string approver, string when)
        {
            using (var inMs  = new MemoryStream(pdfBytes))
            using (var imgMs = new MemoryStream(imageBytes))
            using (var outMs = new MemoryStream())
            using (var doc   = new Aspose.Pdf.Document(inMs))
            {
                foreach (Aspose.Pdf.Page page in doc.Pages)
                {
                    imgMs.Position = 0;
                    var img = new Aspose.Pdf.ImageStamp(imgMs)
                    {
                        HorizontalAlignment = Aspose.Pdf.HorizontalAlignment.Right,
                        VerticalAlignment   = Aspose.Pdf.VerticalAlignment.Bottom,
                        RightMargin         = 24,
                        BottomMargin        = 24,
                        Width  = StampWidthPt,
                        Height = StampHeightPt,
                        Opacity = 0.92
                    };
                    page.AddStamp(img);

                    var txt = new Aspose.Pdf.TextStamp("Approved by " + approver + " - " + when)
                    {
                        HorizontalAlignment = Aspose.Pdf.HorizontalAlignment.Right,
                        VerticalAlignment   = Aspose.Pdf.VerticalAlignment.Bottom,
                        RightMargin         = 24,
                        BottomMargin        = 8,
                        TextState = { FontSize = 8, ForegroundColor = Aspose.Pdf.Color.DarkGray }
                    };
                    page.AddStamp(txt);
                }
                doc.Save(outMs);
                return outMs.ToArray();
            }
        }

        private static byte[] StampDocx(byte[] docxBytes, byte[] imageBytes, string approver, string when)
        {
            using (var inMs  = new MemoryStream(docxBytes))
            using (var outMs = new MemoryStream())
            {
                var doc = new Aspose.Words.Document(inMs);

                foreach (Aspose.Words.Section section in doc.Sections)
                {
                    var footer = section.HeadersFooters[Aspose.Words.HeaderFooterType.FooterPrimary];
                    if (footer == null)
                    {
                        footer = new Aspose.Words.HeaderFooter(doc, Aspose.Words.HeaderFooterType.FooterPrimary);
                        section.HeadersFooters.Add(footer);
                    }

                    var para = new Aspose.Words.Paragraph(doc);
                    para.ParagraphFormat.Alignment = Aspose.Words.ParagraphAlignment.Right;

                    var shape = new Aspose.Words.Drawing.Shape(doc, Aspose.Words.Drawing.ShapeType.Image);
                    using (var imgMs = new MemoryStream(imageBytes))
                    {
                        // Skia-backed Aspose.Words has no SetImage(byte[]); stream overload is portable.
                        shape.ImageData.SetImage(imgMs);
                    }
                    shape.Width    = StampWidthPt;
                    shape.Height   = StampHeightPt;
                    shape.WrapType = Aspose.Words.Drawing.WrapType.Inline;
                    para.AppendChild(shape);

                    var caption = new Aspose.Words.Paragraph(doc);
                    caption.ParagraphFormat.Alignment = Aspose.Words.ParagraphAlignment.Right;
                    var run = new Aspose.Words.Run(doc, "Approved by " + approver + " - " + when);
                    run.Font.Size  = 8;
                    run.Font.Color = System.Drawing.Color.DarkGray;
                    caption.AppendChild(run);

                    footer.AppendChild(para);
                    footer.AppendChild(caption);
                }

                doc.Save(outMs, Aspose.Words.SaveFormat.Docx);
                return outMs.ToArray();
            }
        }

        // ---------------------------------------------------------------------
        // Storage IO (read + replace-in-place)
        // ---------------------------------------------------------------------
        private async System.Threading.Tasks.Task<byte[]> StorageDownloadAsync(string storageAttachmentId, string bearerToken)
        {
            string url = StorageBaseUrl + "Storage/Download?fileId=" + Uri.EscapeDataString(storageAttachmentId);
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                using (var response = await client.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
        }

        /// <summary>
        /// POSTs the stamped bytes to /Storage/ReplaceNoVersioning?fileId={id}.
        /// The endpoint expects an AttachmentModel as multipart/form-data:
        ///   Name, Extension, ContentType, FileSize, Data (the file part)
        /// </summary>
        private async System.Threading.Tasks.Task StorageReplaceNoVersioningAsync(
            string storageAttachmentId, string fileName, string contentType, byte[] bytes, string bearerToken)
        {
            string url = StorageBaseUrl + "Storage/ReplaceNoVersioning?fileId=" + Uri.EscapeDataString(storageAttachmentId);
            string ext = (Path.GetExtension(fileName) ?? "").TrimStart('.');

            using (var client = new HttpClient())
            using (var form = new MultipartFormDataContent())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                form.Add(new StringContent(fileName),              "Name");
                form.Add(new StringContent(ext),                   "Extension");
                form.Add(new StringContent(contentType),           "ContentType");
                form.Add(new StringContent(bytes.Length.ToString()), "FileSize");

                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(fileContent, "Data", fileName);

                using (var response = await client.PostAsync(url, form))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"ReplaceNoVersioning {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task<string> GetAccessTokenAsync()
        {
            using (var iamClient = new HttpClient { BaseAddress = new Uri(IamBaseUrl) })
            using (var request   = new HttpRequestMessage(HttpMethod.Post, "/connect/token"))
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type",    "password"),
                    new KeyValuePair<string, string>("client_id",     AuthCaseClientId),
                    new KeyValuePair<string, string>("client_secret", AuthCaseClientSecret),
                    new KeyValuePair<string, string>("scope",         "IdentityServerApi"),
                    new KeyValuePair<string, string>("username",      AuthUserName),
                    new KeyValuePair<string, string>("password",      AuthUserPassword),
                };
                request.Content = new FormUrlEncodedContent(form);

                using (var response = await iamClient.SendAsync(request))
                {
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<TokenResponse>(body).access_token;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------
        /// <summary>
        /// Resolves the user who is taking the current action.
        ///
        /// Path 1: WorkflowItem.ActivityInstance.ActivityInstanceId  ->  Task.FindTaskByActivityInstanceId  ->  OwnerUser
        ///   This is the most precise, but the Portal does not always populate
        ///   ActivityInstance on WorkflowItem before calling Execute().
        ///
        /// Path 2: Task.ListByDocumentId(documentId)  ->  most recently closed/modified task with OwnerUserId
        ///   Reliable fallback that works whenever an approval task has just
        ///   completed on the document.
        /// </summary>
        private static string ResolveCurrentApprover(WorkflowItem item, long documentId)
        {
            // --- Path 1 -----------------------------------------------------
            try
            {
                var ai = item?.ActivityInstance;
                long aiId = ai?.ActivityInstanceId ?? 0;
                LogInfo($"Approver path-1: WorkflowItem.ActivityInstance {(ai == null ? "is null" : "id=" + aiId)}");

                if (aiId > 0)
                {
                    var stub = new Intalio.Case.Portal.Core.DAL.Task().FindTaskByActivityInstanceId(aiId);
                    LogInfo($"Approver path-1: FindTaskByActivityInstanceId({aiId}) -> {(stub == null ? "null" : "taskId=" + stub.Id)}");
                    if (stub != null)
                    {
                        string n = LoadOwnerName(stub.Id);
                        if (!string.IsNullOrWhiteSpace(n)) return n;
                    }
                }
            }
            catch (Exception ex) { LogException("Approver path-1 failed", ex); }

            // --- Path 2 -----------------------------------------------------
            if (documentId <= 0) return null;
            try
            {
                var tasks = new Intalio.Case.Portal.Core.DAL.Task().ListByDocumentId(documentId);
                LogInfo($"Approver path-2: Task.ListByDocumentId({documentId}) -> {(tasks == null ? 0 : tasks.Count)} task(s).");
                if (tasks == null || tasks.Count == 0) return null;

                // Pick the most recently touched task that has an owner. ClosedDate
                // wins over ModifiedDate wins over CreatedDate so we land on the
                // approval that just fired (which is what the user wants on the stamp).
                Intalio.Case.Portal.Core.DAL.Task best = null;
                DateTime bestKey = DateTime.MinValue;
                foreach (var t in tasks)
                {
                    if (t == null || !t.OwnerUserId.HasValue) continue;
                    DateTime k = t.ClosedDate ?? t.ModifiedDate ?? t.CreatedDate;
                    if (k > bestKey) { bestKey = k; best = t; }
                }
                if (best == null)
                {
                    LogWarn("Approver path-2: no task on this document had OwnerUserId set.");
                    return null;
                }

                LogInfo($"Approver path-2: picked taskId={best.Id} ownerUserId={best.OwnerUserId} closedDate={best.ClosedDate}");
                return LoadOwnerName(best.Id);
            }
            catch (Exception ex) { LogException("Approver path-2 failed", ex); }

            return null;
        }

        private static string LoadOwnerName(long taskId)
        {
            try
            {
                var task = new Intalio.Case.Portal.Core.DAL.Task().FindIncludeUserAndActivityAndDocument(taskId);
                var owner = task?.OwnerUser;
                if (owner == null)
                {
                    LogWarn($"LoadOwnerName(taskId={taskId}): OwnerUser navigation was null.");
                    return null;
                }
                string first = owner.Firstname ?? "";
                string last  = owner.Lastname  ?? "";
                string full  = (first + " " + last).Trim();
                LogInfo($"LoadOwnerName(taskId={taskId}): userId={task.OwnerUserId} name='{full}'");
                return full;
            }
            catch (Exception ex)
            {
                LogException($"LoadOwnerName(taskId={taskId}) failed", ex);
                return null;
            }
        }

        private static string GetProp(WorkflowItem item, string key)
        {
            try
            {
                var val = item?.Properties?[key]?.Value;
                return val == null ? string.Empty : Convert.ToString(val);
            }
            catch { return string.Empty; }
        }

        private static void LogInfo(string message)  { Write("INFO ", message); }
        private static void LogWarn(string message)  { Write("WARN ", message); }
        private static void LogError(string message) { Write("ERROR", message); }

        private static void LogException(string context, Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(context).Append(": ");
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
                string path = Path.Combine(
                    LogDirectory,
                    "StampApprovedDocumentsActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

                string line = string.Concat(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    "  ", level,
                    "  [tid:", Thread.CurrentThread.ManagedThreadId.ToString(), "]  ",
                    message,
                    Environment.NewLine);

                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
            }
            catch { /* logging must never throw and break the activity */ }
        }
    }
}
