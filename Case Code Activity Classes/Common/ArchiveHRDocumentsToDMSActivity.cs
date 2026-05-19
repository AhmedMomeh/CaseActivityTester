using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Intalio.Core.Utility;     // AsposeLicense
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Activities
{
    /// <summary>
    /// Archives every attachment of a case to the DMS under
    ///
    ///     HRE / {workflowName} / {referenceNumber}
    ///
    /// where the workflow name and reference number are derived from the case
    /// itself (Document.ReferenceNumber + the WorkflowDefinition the case is
    /// running through). Example resulting path:
    ///
    ///     HRE / WorkforceRequisition_HiringRequest / WFR-2026-000012
    ///
    /// Folders are created on the fly if they don't already exist, so the same
    /// activity works for the first case in a new workflow as well as for
    /// subsequent cases that land in the same parent folder.
    /// </summary>
    internal class ArchiveHRDocumentsToDMSActivity : ActivityTemplate
    {
        // -------- STATIC CONFIG --------
        private const string IamBaseUrl       = "http://localhost:11111";
        private const string DmsBaseUrl       = "http://localhost:8080/DMS/";
        private const string StorageBaseUrl   = "http://localhost:44444/";
        private const string DmsClientId      = "398ff3ac-49b6-44fd-a70b-3cd69874c118";
        private const string DmsClientSecret  = "ac63daac-edd5-496a-834f-e14a0e76c5c0";
        private const string DmsUserName      = "admin";
        private const string DmsUserPassword  = "1";
        private const string DmsUserId        = "1"; // numeric DMS user id — required header for IntegrationService/UploadPage
        private const string HrCabinetName    = "HRE";

        // Daily-rotated log: C:\IntalioLogs\ArchiveHRDocumentsToDMSActivity-YYYY-MM-DD.log
        private const string LogDirectory     = @"C:\IntalioLogs";
        private static readonly object LogLock = new object();

        // Aspose.Words license — applied once per process for Word→PDF conversion.
        private static int _licenseApplied;

        private sealed class TokenResponse { public string access_token { get; set; } }
        private sealed class FolderHit
        {
            public string FolderName { get; set; }
            public long   FolderId   { get; set; }
            public string Path       { get; set; }
            public bool   IsCabinet  { get; set; }
        }

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

                string referenceNumber = document.ReferenceNumber;
                if (string.IsNullOrWhiteSpace(referenceNumber))
                {
                    LogError($"Document {documentId} has no ReferenceNumber — aborting.");
                    return;
                }

                string workflowName = ResolveWorkflowName(documentId);
                if (string.IsNullOrWhiteSpace(workflowName))
                {
                    LogError($"Could not resolve workflow name for document {documentId} — aborting.");
                    return;
                }

                string workflowFolder = SanitizeFolderName(workflowName);
                LogInfo($"Target DMS path: {HrCabinetName}/{workflowFolder}/{referenceNumber}");

                System.Threading.Tasks.Task
                    .Run(() => ArchiveToDmsAsync(document, workflowFolder, referenceNumber))
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
        // DMS archive flow
        //
        // We use ListByDocumentIdWithOriginalAsync so the result includes BOTH the
        // template-generated original AND any additional user uploads — the older
        // FindByDocumentId excluded the original, which caused the empty-list bug.
        //
        // For each attachment, if it's the original AND a Word document (.doc/.docx),
        // we convert it to PDF via Aspose.Words before uploading so the DMS always
        // gets a PDF for the case template. Additional attachments are uploaded
        // unchanged (in whatever format the user originally uploaded).
        // ---------------------------------------------------------------------
        private async System.Threading.Tasks.Task ArchiveToDmsAsync(Document document, string workflowFolder, string referenceNumber)
        {
            var attachments = await new Attachment().ListByDocumentIdWithOriginalAsync(document.Id);
            int total = attachments?.Count ?? 0;
            LogInfo($"ListByDocumentIdWithOriginalAsync({document.Id}) returned {total} attachment(s).");
            if (total == 0) return;

            string accessToken = await GetAccessTokenAsync();
            LogInfo($"IAM token acquired (length={accessToken?.Length ?? 0}).");

            using (var http = new HttpClient { BaseAddress = new Uri(DmsBaseUrl) })
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                http.DefaultRequestHeaders.Add("userId", DmsUserId);

                // 1) HRE cabinet
                long hreCabinetId = await ResolveCabinetIdAsync(http, HrCabinetName);
                LogInfo($"HRE cabinet resolved: id={hreCabinetId}");

                // 2) Workflow-named folder under HRE
                long workflowFolderId = await FindOrCreateFolderAsync(
                    http, hreCabinetId, HrCabinetName + "/", workflowFolder);
                LogInfo($"Workflow folder: name='{workflowFolder}' id={workflowFolderId} parent={HrCabinetName}/");

                // 3) Reference-number folder under the workflow folder
                long refFolderId = await FindOrCreateFolderAsync(
                    http, workflowFolderId, HrCabinetName + "/" + workflowFolder + "/", referenceNumber);
                LogInfo($"Reference folder: name='{referenceNumber}' id={refFolderId} parent={HrCabinetName}/{workflowFolder}/");

                // 4) Upload each attachment into the reference-number folder
                int uploaded = 0, skipped = 0, failed = 0;
                foreach (var attachment in attachments)
                {
                    if (attachment == null || string.IsNullOrEmpty(attachment.Name))
                    { LogWarn("Skipping attachment with null/empty name."); skipped++; continue; }
                    if (string.IsNullOrEmpty(attachment.StorageAttachmentId))
                    { LogWarn($"Skipping '{attachment.Name}' (id={attachment.Id}): missing StorageAttachmentId."); skipped++; continue; }

                    // Document.AttachmentId is the FK pointing at the case's main
                    // (template-generated) attachment row. C# lifted equality
                    // (long == long?) returns false when AttachmentId is null,
                    // which is exactly what we want — so no extra null-check
                    // and no spurious negation are needed.
                    bool isOriginal = attachment.Id == document.AttachmentId;
                    LogInfo($"Attachment {attachment.Id} '{attachment.Name}' storageId={attachment.StorageAttachmentId} sizeDb={attachment.Size} isOriginal={isOriginal}");

                    byte[] fileBytes;
                    try { fileBytes = await DownloadFromStorageAsync(attachment.StorageAttachmentId, accessToken); }
                    catch (Exception ex)
                    { LogException($"Download FAILED for '{attachment.Name}' (storageId={attachment.StorageAttachmentId})", ex); failed++; continue; }
                    if (fileBytes == null || fileBytes.Length == 0)
                    { LogWarn($"Skipping '{attachment.Name}': empty bytes from storage."); skipped++; continue; }
                    LogInfo($"Downloaded '{attachment.Name}' ({fileBytes.Length} bytes).");

                    // Determine final filename + bytes — convert Word→PDF only for the original document.
                    string  uploadName  = attachment.Name;
                    byte[]  uploadBytes = fileBytes;
                    string  ext = (Path.GetExtension(attachment.Name) ?? "").ToLowerInvariant();
                    if (isOriginal && (ext == ".doc" || ext == ".docx"))
                    {
                        try
                        {
                            EnsureAsposeLicensed();
                            uploadBytes = ConvertWordToPdf(fileBytes);
                            uploadName  = Path.GetFileNameWithoutExtension(attachment.Name) + ".pdf";
                            LogInfo($"Converted original '{attachment.Name}' -> '{uploadName}' ({uploadBytes.Length} bytes) for DMS.");
                        }
                        catch (Exception ex)
                        {
                            LogException($"Word->PDF conversion FAILED for original '{attachment.Name}' — uploading the .docx as-is", ex);
                            // Fall through with the original Word bytes/name.
                        }
                    }

                    try
                    {
                        await UploadFileAsync(http, refFolderId, uploadName, uploadBytes);
                        LogInfo($"Uploaded '{uploadName}' to DMS folderId={refFolderId}.");
                        uploaded++;
                    }
                    catch (Exception ex)
                    { LogException($"Upload FAILED for '{uploadName}' to folderId={refFolderId}", ex); failed++; }
                }

                LogInfo($"Archive summary: total={total}  uploaded={uploaded}  skipped={skipped}  failed={failed}");
            }
        }

        /// <summary>Convert a Word document (.docx or .doc) to PDF bytes via Aspose.Words.</summary>
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
        /// Applies the Aspose.Words license once per process. Without it, Aspose
        /// produces an evaluation-mode PDF (4-page cap + watermark).
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
        // Workflow name lookup — Document → WorkflowInstance → WorkflowDefinition
        // ---------------------------------------------------------------------
        private static string ResolveWorkflowName(long documentId)
        {
            string conn = Intalio.Case.Core.Configuration.DbConnectionString;
            if (string.IsNullOrEmpty(conn)) { LogWarn("No DB connection string — cannot resolve workflow name."); return null; }

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

        /// <summary>
        /// Strips characters from a workflow name that can't appear in DMS folder
        /// names (slash, backslash, colon, etc.). Spaces are preserved.
        /// Example: "WorkforceRequisition/HiringRequest" -> "WorkforceRequisition_HiringRequest"
        /// </summary>
        private static string SanitizeFolderName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Workflow";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var ch in raw.Trim())
            {
                if (ch == '/' || ch == '\\' || ch == ':' || ch == '*' || ch == '?'
                    || ch == '"' || ch == '<' || ch == '>' || ch == '|')
                    sb.Append('_');
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------
        // Storage IO + IAM
        // ---------------------------------------------------------------------
        private async Task<byte[]> DownloadFromStorageAsync(string storageAttachmentId, string bearerToken)
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

        private async Task<string> GetAccessTokenAsync()
        {
            using (var iamClient = new HttpClient { BaseAddress = new Uri(IamBaseUrl) })
            using (var request   = new HttpRequestMessage(HttpMethod.Post, "/connect/token"))
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type",    "password"),
                    new KeyValuePair<string, string>("client_id",     DmsClientId),
                    new KeyValuePair<string, string>("client_secret", DmsClientSecret),
                    new KeyValuePair<string, string>("scope",         "IdentityServerApi"),
                    new KeyValuePair<string, string>("username",      DmsUserName),
                    new KeyValuePair<string, string>("password",      DmsUserPassword),
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
        // DMS folder helpers
        // ---------------------------------------------------------------------
        private async Task<long> ResolveCabinetIdAsync(HttpClient http, string cabinetName)
        {
            var hits = await SearchFoldersByNameAsync(http, cabinetName);
            var cabinet = hits.FirstOrDefault(h => h.IsCabinet
                                                && string.Equals(h.FolderName, cabinetName, StringComparison.OrdinalIgnoreCase));
            if (cabinet == null)
                throw new InvalidOperationException("Cabinet '" + cabinetName + "' not found in DMS.");
            return cabinet.FolderId;
        }

        private async Task<long> FindOrCreateFolderAsync(HttpClient http, long parentFolderId, string parentPath, string folderName)
        {
            var hits = await SearchFoldersByNameAsync(http, folderName);
            string expectedPath = parentPath.EndsWith("/") ? parentPath : parentPath + "/";

            var exact = hits.FirstOrDefault(h =>
                string.Equals(h.FolderName, folderName, StringComparison.Ordinal) &&
                string.Equals(NormalizePath(h.Path), expectedPath, StringComparison.OrdinalIgnoreCase));

            if (exact != null) return exact.FolderId;
            return await CreateFolderAsync(http, folderName, parentFolderId);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            return path.EndsWith("/") ? path : path + "/";
        }

        private async Task<List<FolderHit>> SearchFoldersByNameAsync(HttpClient http, string folderName)
        {
            string url = "apis/IntegrationService/GetFolderIdsByName?folderName=" + Uri.EscapeDataString(folderName);
            using (var response = await http.GetAsync(url))
            {
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body)) return new List<FolderHit>();

                // The DMS returns ["Couldn't find the specified folder"] when there is no
                // match — a JSON array of strings, not of objects. Parse loosely.
                var token = Newtonsoft.Json.Linq.JToken.Parse(body);
                if (token.Type != Newtonsoft.Json.Linq.JTokenType.Array) return new List<FolderHit>();

                var hits = new List<FolderHit>();
                foreach (var element in (Newtonsoft.Json.Linq.JArray)token)
                {
                    if (element.Type != Newtonsoft.Json.Linq.JTokenType.Object) continue;
                    hits.Add(element.ToObject<FolderHit>());
                }
                return hits;
            }
        }

        private async Task<long> CreateFolderAsync(HttpClient http, string folderName, long parentFolderId)
        {
            string url = "apis/IntegrationService/SaveFolder"
                       + "?folderName="     + Uri.EscapeDataString(folderName)
                       + "&parentFolderId=" + parentFolderId;
            using (var response = await http.GetAsync(url))
            {
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(body);
                return Convert.ToInt64((string)json.d);
            }
        }

        private async System.Threading.Tasks.Task UploadFileAsync(HttpClient http, long folderId, string fileName, byte[] fileBytes)
        {
            string url = "apis/IntegrationService/UploadPage"
                       + "?folderId="       + folderId
                       + "&FileName="       + Uri.EscapeDataString(fileName)
                       + "&NewVersion=false"
                       + "&Overwrite=true"
                       + "&confidential=false"
                       + "&isSingleUpload=true"
                       + "&comment=";
            using (var content = new ByteArrayContent(fileBytes))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using (var response = await http.PostAsync(url, content))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"DMS upload returned {(int)response.StatusCode} {response.ReasonPhrase} for '{fileName}'.\n" +
                            $"URL: {http.BaseAddress}{url}\nBody: {body}");
                    if (!body.Contains("#Success#"))
                        throw new InvalidOperationException("DMS upload failed for '" + fileName + "': " + body);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Helpers + logging
        // ---------------------------------------------------------------------
        private static string GetProp(WorkflowItem item, string key)
        {
            try { var v = item?.Properties?[key]?.Value; return v == null ? string.Empty : Convert.ToString(v); }
            catch { return string.Empty; }
        }

        private static void LogInfo (string m) { Write("INFO ", m); }
        private static void LogWarn (string m) { Write("WARN ", m); }
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
                    "ArchiveHRDocumentsToDMSActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
