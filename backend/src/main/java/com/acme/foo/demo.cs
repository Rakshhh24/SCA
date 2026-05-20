using Asp.Versioning;
using FormWorks.Core;
using FormWorks.Navigator.Services.Security;
using FormWorksServices.Configuration;
using FormWorksServices.Core.Database;
using FormWorksServices.ErrorDetails;
using FormWorksServices.Models.WRViewer;
using FormWorksServices.Services.WRViewer;
using FormWorksServices.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Swashbuckle.AspNetCore.Annotations;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;

namespace FormWorksServices.Controllers.V1.WRViewer
{
    /// <summary>
    /// Controller for WR (Work Request) operations
    /// </summary>

    [ApiController]
    [ApiVersion("1")]
    [Route("api/v{version:apiVersion}/[controller]")]
    [SwaggerResponse((int)HttpStatusCode.BadRequest, "Bad request", typeof(ProblemDetails))]
    [SwaggerResponse((int)HttpStatusCode.Unauthorized, "Unauthorized", typeof(ProblemDetails))]
    [SwaggerResponse((int)HttpStatusCode.Forbidden, "Forbidden", typeof(ProblemDetails))]
    [SwaggerResponse((int)HttpStatusCode.NotFound, "Not found", typeof(ProblemDetails))]
    [SwaggerResponse((int)HttpStatusCode.UnsupportedMediaType, "Unsupported media type")]
    [SwaggerResponse((int)HttpStatusCode.TooManyRequests, "Too many requests")]
    [SwaggerResponse((int)HttpStatusCode.InternalServerError, "Internal server error", typeof(ProblemDetails))]
    public class WRViewerController(
        IConfiguration config,
        IFormWorksServicesConfig fwConfig,
        IHttpContextAccessor httpContextAccessor,
        INavigatorSecurityServiceClient navigatorSecurityServiceClient,
        ILogFactory logFactory,
        FWDbContext dbContext) : AbstractController(fwConfig, dbContext, navigatorSecurityServiceClient, logFactory)
    {
        private readonly CapturedRecordsService _capturedRecordsService = new(config, fwConfig, logFactory, httpContextAccessor);

        [Authorize]
        [HttpGet("files")]
        [SwaggerOperation(
            Summary = "Gets file list from WR archive",
            Description = "Validates user access and returns the list of files contained in the specified WR archive. The WR ID is extracted from the .wr file ($Record line).",
            OperationId = nameof(GetWRFiles),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns the file list from the archive with WR ID and archive file name", typeof(WRFileInfoResponse))]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request parameters", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.Forbidden, "User doesn't have access to this WR", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.NotFound, "WR or archive file not found", typeof(ProblemDetails))]
        [Produces("application/json")]
        public IActionResult GetWRFiles(
            [FromQuery, BindRequired, StringLength(256), SwaggerParameter("The archive file name (*.zip or *.wrz)")] string archiveFileName
            )
        {
            var sw = Stopwatch.StartNew();
            var log = CreateLog(nameof(GetWRFiles), "Getting file list for archive");

            var userId = ResolveUserId();

            archiveFileName = ResolveArchiveFileName(archiveFileName);

            if (ValidateArchiveRequest<WRFileInfoResponse>(userId, archiveFileName, nameof(GetWRFiles), log, sw.ElapsedMilliseconds, "Archive fileName parameter is required.")
                is IActionResult invalidRequestResult)
            {
                return invalidRequestResult;
            }

            log.Pedantic($"Processing request for User {userId}, File {archiveFileName}");

            // Validate user access (without WR ID)
            var validationResult = _capturedRecordsService.ValidateUserAccessByArchive(log, userId, archiveFileName);
            if (validationResult.Code != ResultCode.OK)
            {
                return ErrorResultToProblem(validationResult, nameof(GetWRFiles), log, sw.ElapsedMilliseconds);
            }

            // Get file list from archive with download links
            var fileListResult = _capturedRecordsService.GetFileList(log, userId, archiveFileName);

            switch (fileListResult?.Code)
            {
                case ResultCode.OK:
                    return Ok(fileListResult.Value);
                default:
                    return ErrorResultToProblem(fileListResult, nameof(GetWRFiles), log, sw.ElapsedMilliseconds);
            }
        }

        [Authorize]
        [HttpGet("file/{archiveFileName}/{fileName}")]

        [SwaggerOperation(
            Summary = "Gets a file from WR archive",
            Description = "Extracts a specific file from the WR. The WR ID is extracted from the .wr file ($Record line). Content-Type is determined by the file type.",
            OperationId = nameof(GetFileFromArchive),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns a specific file with content-type based on file type")]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request parameters", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.Forbidden, "User doesn't have access to this WR", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.NotFound, "File not found", typeof(ProblemDetails))]
        [Produces("image/png", "text/plain", "text/xml", "application/octet-stream")]
        public IActionResult GetFileFromArchive(
            [FromRoute, BindRequired, StringLength(256), SwaggerParameter("The archive file name (*.zip or *.wrz)")] string archiveFileName,
            [FromRoute, BindRequired, StringLength(256), SwaggerParameter("The name of the file to extract from the archive")] string fileName
            )
        {
            var sw = Stopwatch.StartNew();
            var log = CreateLog(nameof(GetFileFromArchive), "Getting file from archive");

            var userId = ResolveUserId();
            archiveFileName = ResolveArchiveFileName(archiveFileName);
            fileName = fileName?.ToLowerInvariant() ?? string.Empty;

            if (ValidateArchiveFileRequest(userId, archiveFileName, fileName) is IActionResult invalidRequestResult)
            {
                return invalidRequestResult;
            }

            log.Pedantic($"Processing get request for User {userId}, Archive {archiveFileName}, File {fileName}");

            // Validate user access (by archive only)
            var validationResult = _capturedRecordsService.ValidateUserAccessByArchive(log, userId, archiveFileName);
            if (validationResult.Code != ResultCode.OK)
            {
                return ErrorResultToProblem(validationResult, nameof(GetFileFromArchive), log, sw.ElapsedMilliseconds);
            }

            // Extract file from archive
            var extractResult = _capturedRecordsService.ExtractFile(log, userId, archiveFileName, fileName);

            switch (extractResult?.Code)
            {
                case ResultCode.OK:
                    var contentType = extractResult.Value.contentType;

                    return new FileStreamResult(extractResult.Value.stream, contentType)
                    {
                        FileDownloadName = fileName,
                        EnableRangeProcessing = true
                    };
                default:
                    return ErrorResultToProblem(extractResult, nameof(GetFileFromArchive), log, sw.ElapsedMilliseconds);
            }
        }

        [Authorize]
        [HttpGet("filetypes")]
        [SwaggerOperation(
           Summary = "Gets all supported file types",
           Description = "Returns the list of supported WR file type values.",
           OperationId = nameof(GetFileTypes),
           Tags = ["WorkRecord Viewer"]
       )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns all supported WR file types", typeof(List<string>))]
        [Produces("application/json")]
        public IActionResult GetFileTypes()
        {
            return Ok(WRFileTypes.Values);
        }

        [Authorize]
        [HttpGet("filetype/{filetype}/thumbnail")]
        [SwaggerOperation(
            Summary = "Gets thumbnail for a file type",
            Description = "Returns the thumbnail for a specific file type.",
            OperationId = nameof(GetThumbnailByFileType),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns the thumbnail")]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request parameters", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.NotFound, "Thumbnail not found for the specified file type", typeof(ProblemDetails))]
        [Produces("image/png")]
        public IActionResult GetThumbnailByFileType([FromRoute, SwaggerParameter("The file type")] string filetype)
        {
            var log = CreateLog(nameof(GetThumbnailByFileType), "Getting thumbnail for file type");

            var imagePath = _capturedRecordsService.GetImageFilePath(log, filetype);

            if (string.IsNullOrEmpty(imagePath))
            {
                return NotFound(new NotFoundProblem($"Thumbnail not found for file type: {filetype}"));
            }

            var fileBytes = System.IO.File.ReadAllBytes(imagePath);
            return File(fileBytes, "image/png");
        }

        [Authorize]
        [HttpGet("thumbnail-info")]
        [SwaggerOperation(
            Summary = "Gets file types and thumbnail URLs.",
            Description = "Returns a list of all file types with their corresponding thumbnail URLs.",
            OperationId = nameof(GetAllThumbnailInfo),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns all files types and thumbnail URLs", typeof(WRThumbnailInfoResponse[]))]
        [Produces("application/json")]
        public IActionResult GetAllThumbnailInfo()
        {
            CreateLog(nameof(GetAllThumbnailInfo), "Getting all file types and thumbnail URLs");

            var baseUrl = $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}{HttpContext.Request.PathBase}";
            var apiVersion = HttpContext.GetRequestedApiVersion()?.ToString() ?? "1";

            var response = WRFileTypes.Values.Select(fileType => new WRThumbnailInfoResponse
            {
                FileType = fileType,
                ThumbnailUrl = $"{baseUrl}/api/v{apiVersion}/WRViewer/filetype/{fileType}/thumbnail"
            }).ToArray();

            return Ok(response);
        }

        [Authorize]
        [HttpGet("workrecord")]
        [SwaggerOperation(
          Summary = "Gets the WorkRecord as JSON",
          Description = "Extracts the WorkRecord from the WR archive and returns it as a JSON string.",
          OperationId = nameof(GetWorkRecord),
          Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns the WorkRecord as JSON string")]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request parameters", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.Forbidden, "User doesn't have access to this WR", typeof(ProblemDetails))]
        [SwaggerResponse((int)HttpStatusCode.NotFound, "WR or archive file not found", typeof(ProblemDetails))]
        [Produces("application/json")]
        public IActionResult GetWorkRecord(
          [FromQuery, BindRequired, StringLength(256), SwaggerParameter("The archive file name (*.zip or *.wrz)")] string archiveFileName
          )
        {
            var sw = Stopwatch.StartNew();
            var log = CreateLog(nameof(GetWorkRecord), "Getting WorkRecord as JSON");

            var userId = ResolveUserId();
            archiveFileName = ResolveArchiveFileName(archiveFileName);

            if (ValidateArchiveRequest<string>(userId, archiveFileName, nameof(GetWorkRecord), log, sw.ElapsedMilliseconds, "Archive fileName parameter is required.")
                is IActionResult invalidRequestResult)
            {
                return invalidRequestResult;
            }

            log.Pedantic($"Processing request for User {userId}, File {archiveFileName}");

            // Validate user access (without WR ID)
            var validationResult = _capturedRecordsService.ValidateUserAccessByArchive(log, userId, archiveFileName);
            if (validationResult.Code != ResultCode.OK)
            {
                return ErrorResultToProblem(validationResult, nameof(GetWorkRecord), log, sw.ElapsedMilliseconds);
            }

            // Get WorkRecord as JSON
            var workRecordResult = _capturedRecordsService.GetWorkRecordJson(log, userId, archiveFileName);

            switch (workRecordResult?.Code)
            {
                case ResultCode.OK:
                    return Ok(System.Text.Json.JsonDocument.Parse(workRecordResult.Value).RootElement);
                default:
                    return ErrorResultToProblem(workRecordResult, nameof(GetWorkRecord), log, sw.ElapsedMilliseconds);
            }
        }

        [AllowAnonymous]
        [HttpPost("upload")]
        [SwaggerOperation(
            Summary = "Uploads a WR archive file",
            Description = "Accepts a .zip or .wrz file upload, saves it to the user's directory, and updates CapturedRecords.xml. The WR ID is extracted from the .wr file inside the archive.",
            OperationId = nameof(UploadArchive),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "File uploaded successfully", typeof(UploadArchiveResponse))]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request or unsupported file type", typeof(ProblemDetails))]
        [Produces("application/json")]
        public async Task<IActionResult> UploadArchive(
            [BindRequired, SwaggerParameter("The WR archive file to upload (*.zip or *.wrz)")] IFormFile file,
            [FromQuery, StringLength(256), SwaggerParameter("The user ID")] string userId)
        {
            var sw = Stopwatch.StartNew();
            var log = CreateLog(nameof(UploadArchive), "Uploading archive file");

            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = ResolveUserId();
            }

            var fileName = ResolveArchiveFileName(file?.FileName ?? string.Empty);

            if (ValidateUploadRequest(userId, file) is IActionResult invalidRequestResult)
            {
                return invalidRequestResult;
            }

            log.Pedantic($"Processing upload for User {userId}, File {fileName}, Size {file.Length} bytes");

            using (var stream = file.OpenReadStream())
            {
                var uploadResult = await _capturedRecordsService.SaveUploadedArchiveAsync(
                    log, userId, stream, fileName);

                switch (uploadResult?.Code)
                {
                    case ResultCode.OK:
                        return Ok(new UploadArchiveResponse
                        {
                            Message = "File uploaded successfully",
                            FileName = fileName,
                            WrId = uploadResult.Value,
                        });
                    default:
                        return ErrorResultToProblem(uploadResult, nameof(UploadArchive), log, sw.ElapsedMilliseconds);
                }
            }
        }

        [AllowAnonymous]
        [HttpGet("check-archivefile-exists")]
        [SwaggerOperation(
            Summary = "Checks if an archive file exists",
            Description = "Verifies if a specified .zip or .wrz archive file exists in the user's CapturedRecords directory.",
            OperationId = nameof(CheckArchiveFileExists),
            Tags = ["WorkRecord Viewer"]
        )]
        [SwaggerResponse((int)HttpStatusCode.OK, "Returns whether the file exists", typeof(CheckArchiveExistsResponse))]
        [SwaggerResponse((int)HttpStatusCode.BadRequest, "Invalid request parameters", typeof(ProblemDetails))]
        [Produces("application/json")]
        public IActionResult CheckArchiveFileExists(
            [FromQuery, BindRequired, StringLength(256), SwaggerParameter("The archive file name (*.zip or *.wrz)")] string archiveFileName,
             [FromQuery, StringLength(256), SwaggerParameter("The user ID")] string userId)
        {
            var sw = Stopwatch.StartNew();
            var log = CreateLog(nameof(CheckArchiveFileExists), "Checking if archive file exists");

            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = ResolveUserId();
            }

            archiveFileName = ResolveArchiveFileName(archiveFileName);

            if (ValidateArchiveRequest<bool>(userId, archiveFileName, nameof(CheckArchiveFileExists), log, sw.ElapsedMilliseconds, "Archive fileName parameter is required.")
                is IActionResult invalidRequestResult)
            {
                return invalidRequestResult;
            }

            log.Pedantic($"Checking archive existence for User {userId}, File {archiveFileName}");

            var checkResult = _capturedRecordsService.CheckArchiveFileExists(log, userId, archiveFileName);

            switch (checkResult?.Code)
            {
                case ResultCode.OK:
                    return Ok(new CheckArchiveExistsResponse
                    {
                        Exists = checkResult.Value,
                        FileName = archiveFileName
                    });
                default:
                    return ErrorResultToProblem(checkResult, nameof(CheckArchiveFileExists), log, sw.ElapsedMilliseconds);
            }
        }

        private ILog CreateLog(string actionName, string message)
        {
            var log = _logFactory.GetLog($"svr:{HttpContext?.User?.Identity?.Name}");
            log.Pedantic($"{actionName}: {message}");
            return log;
        }

        private string ResolveUserId()
        {
            //var userId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
            var userId = _sessionManager.GetUserIdFromClaims(HttpContext.User?.Claims);
            return userId;
        }

        private static string ResolveArchiveFileName(string archiveFileName)
        {
            return archiveFileName?.ToLowerInvariant() ?? string.Empty;
        }

        private IActionResult? ValidateUserId(string userId)
        {
            return Guid.TryParse(userId, out _)
                ? null
                : BadRequest(new InvalidParameterProblem("UserId must be a valid GUID"));
        }

        private IActionResult? ValidateUserId<T>(string userId, string actionName, ILog log, long elapsedMilliseconds)
        {
            if (Guid.TryParse(userId, out _))
            {
                return null;
            }

            var result = new OperationResult<T>(ResultCode.INVALID_PARAMETER, "userId must be a valid GUID");
            return ErrorResultToProblem(result, actionName, log, elapsedMilliseconds);
        }

        private IActionResult? ValidateArchiveFileName(string archiveFileName, string requiredMessage)
        {
            if (ValidateRequiredParameter(archiveFileName, requiredMessage) is IActionResult missingArchiveResult)
            {
                return missingArchiveResult;
            }

            return archiveFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || archiveFileName.EndsWith(".wrz", StringComparison.OrdinalIgnoreCase)
                ? null
                : BadRequest(new InvalidParameterProblem("Archive fileName must be a .zip or .wrz file."));
        }

        private IActionResult? ValidateRequiredParameter(string value, string message)
        {
            return string.IsNullOrWhiteSpace(value)
                ? BadRequest(new InvalidParameterProblem(message))
                : null;
        }

        private IActionResult? ValidateArchiveRequest<T>(string userId, string archiveFileName, string actionName, ILog log, long elapsedMilliseconds, string requiredMessage)
        {
            if (ValidateUserId<T>(userId, actionName, log, elapsedMilliseconds) is IActionResult invalidUserIdResult)
            {
                return invalidUserIdResult;
            }

            if (ValidateArchiveFileName(archiveFileName, requiredMessage) is IActionResult invalidArchiveResult)
            {
                return invalidArchiveResult;
            }

            return null;
        }

        private IActionResult? ValidateArchiveFileRequest(string userId, string archiveFileName, string fileName)
        {
            if (ValidateUserId(userId) is IActionResult invalidUserIdResult)
            {
                return invalidUserIdResult;
            }

            if (ValidateArchiveFileName(archiveFileName, "Archive file name parameter is required") is IActionResult invalidArchiveResult)
            {
                return invalidArchiveResult;
            }

            if (ValidateRequiredParameter(fileName, "File name parameter is required") is IActionResult invalidFileNameResult)
            {
                return invalidFileNameResult;
            }

            return null;
        }

        private IActionResult? ValidateUploadRequest(string userId, IFormFile file)
        {
            if (ValidateUserId(userId) is IActionResult invalidUserIdResult)
            {
                return invalidUserIdResult;
            }

            return file == null || file.Length == 0
                ? BadRequest(new InvalidParameterProblem("No file uploaded"))
                : null;
        }
    }
}
