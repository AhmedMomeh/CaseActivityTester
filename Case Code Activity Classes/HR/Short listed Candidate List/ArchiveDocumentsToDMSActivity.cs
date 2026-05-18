using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.API;
using Intalio.Case.Portal.Core.DAL;
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
    internal class ArchiveDocumentsToDMSActivity : ActivityTemplate
    {
        // -------- STATIC CONFIG (replace placeholders with real values) --------
        private const string IamBaseUrl     = "http://localhost:11111";
        private const string DmsBaseUrl     = "http://localhost:8080/DMS/";
        private const string StorageBaseUrl = "http://localhost:44444/";
        private const string DmsClientId     = "398ff3ac-49b6-44fd-a70b-3cd69874c118";
        private const string DmsClientSecret = "ac63daac-edd5-496a-834f-e14a0e76c5c0";
        private const string DmsUserName     = "admin";
        private const string DmsUserPassword = "1";
        private const string DmsUserId       = "1"; // numeric DMS user id — required header for IntegrationService/UploadPage
        private const string HrCabinetName   = "HRE";

        // Daily-rotated log file: C:\IntalioLogs\ArchiveEmployeeDocumentActivity-YYYY-MM-DD.log
        private const string LogDirectory    = @"C:\IntalioLogs";
        private static readonly object LogLock = new object();

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
            string documentId = GetProp(workflowItem, "DocumentId");
            string employeeId = GetProp(workflowItem, "employeeId");
            string category = "Case Files"; //GetProp(workflowItem, "DocumentCategory");

            LogInfo($"---- BEGIN Archive Case DocumentId={documentId}  EmployeeId={employeeId}  Category={category} ----");

            try
            {
                Document document = new Document().Find(Convert.ToInt64(documentId));
                if (document == null)
                {
                    LogError($"Document.Find({documentId}) returned null. Aborting.");
                    return;
                }
                LogInfo($"Document loaded: Id={document.Id}  Status={document.StatusId}  CreatedByUserId={document.CreatedByUserId}");

                System.Threading.Tasks.Task.Run(() => ArchiveToDmsAsync(document, employeeId, category)).GetAwaiter().GetResult();

                document.StatusId = 14; // Approved
                document.Update();
                LogInfo($"Document StatusId updated to 14 (Approved). Persisted via document.Update().");
                LogInfo($"---- END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END    DocumentId={documentId}  result=FAILED ----");
                throw;
            }
        }

        private async System.Threading.Tasks.Task ArchiveToDmsAsync(Document document, string employeeId, string category)
        {
            var attachments = new Attachment().FindByDocumentId(document.Id);
            int total = attachments?.Count ?? 0;
            LogInfo($"FindByDocumentId({document.Id}) returned {total} attachment(s).");
            if (total == 0) return;

            string accessToken = await GetAccessTokenAsync();
            LogInfo($"IAM token acquired (length={accessToken?.Length ?? 0}).");

            using (var http = new HttpClient { BaseAddress = new Uri(DmsBaseUrl) })
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                http.DefaultRequestHeaders.Add("userId", DmsUserId);

                long hreCabinetId = await ResolveCabinetIdAsync(http, HrCabinetName);
                LogInfo($"HRE cabinet resolved: id={hreCabinetId}");

                long employeeFolderId = await FindOrCreateFolderAsync(http, hreCabinetId, HrCabinetName + "/", employeeId);
                LogInfo($"Employee folder resolved: name={employeeId} id={employeeFolderId} parent=HRE/");

                long categoryFolderId = await FindOrCreateFolderAsync(http, employeeFolderId, HrCabinetName + "/" + employeeId + "/", category);
                LogInfo($"Category folder resolved: name={category} id={categoryFolderId} parent=HRE/{employeeId}/");

                int uploaded = 0, skipped = 0, failed = 0;
                foreach (var attachment in attachments)
                {
                    if (attachment == null || string.IsNullOrEmpty(attachment.Name))
                    { LogWarn("Skipping attachment with null/empty name."); skipped++; continue; }
                    if (string.IsNullOrEmpty(attachment.StorageAttachmentId))
                    { LogWarn($"Skipping '{attachment.Name}' (id={attachment.Id}): missing StorageAttachmentId."); skipped++; continue; }

                    LogInfo($"Attachment {attachment.Id} '{attachment.Name}' storageId={attachment.StorageAttachmentId} sizeDb={attachment.Size}");

                    byte[] fileBytes;
                    try
                    {
                        fileBytes = await DownloadFromStorageAsync(attachment.StorageAttachmentId, accessToken);
                    }
                    catch (Exception ex)
                    {
                        LogException($"Download FAILED for '{attachment.Name}' (storageId={attachment.StorageAttachmentId})", ex);
                        failed++;
                        continue;
                    }

                    if (fileBytes == null || fileBytes.Length == 0)
                    { LogWarn($"Skipping '{attachment.Name}': empty bytes from storage."); skipped++; continue; }
                    LogInfo($"Downloaded '{attachment.Name}' ({fileBytes.Length} bytes).");

                    try
                    {
                        await UploadFileAsync(http, categoryFolderId, attachment.Name, fileBytes);
                        LogInfo($"Uploaded '{attachment.Name}' to DMS folderId={categoryFolderId}.");
                        uploaded++;
                    }
                    catch (Exception ex)
                    {
                        LogException($"Upload FAILED for '{attachment.Name}' to folderId={categoryFolderId}", ex);
                        failed++;
                    }
                }

                LogInfo($"Archive summary: total={total}  uploaded={uploaded}  skipped={skipped}  failed={failed}");
            }
        }

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
                // match — a JSON array of strings, not of objects. Parse loosely and only
                // keep object-shaped entries.
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
                    {
                        throw new InvalidOperationException(
                            $"DMS upload returned {(int)response.StatusCode} {response.ReasonPhrase} for '{fileName}'.\n" +
                            $"URL: {http.BaseAddress}{url}\n" +
                            $"Body: {body}");
                    }
                    if (!body.Contains("#Success#"))
                        throw new InvalidOperationException("DMS upload failed for '" + fileName + "': " + body);
                }
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

        // -------- daily-rotated logging --------

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
                    "ArchiveEmployeeDocumentActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

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
            catch
            {
                // logging must never throw and break the activity
            }
        }
    }
}
