using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// ProfileApiClient — typed client over the profiles.opcfoundation.org REST
// API that backs the React SPA. All reads are anonymous and return a common
// envelope:  { "result": [...], "failed": bool, "errorCode": ..., ... }.
//
// Endpoints (verified live):
//   GET /api/profilegroup                       — all profile groups (spec+version)
//   GET /api/profile?pg={gid}                   — profiles in a group
//   GET /api/category?pg={gid}                  — categories in a group
//   GET /api/profilefolder?pg={gid}             — folders (parentFolderGuid hierarchy)
//   GET /api/conformancegroup?pg={gid}          — conformance groups
//   GET /api/conformanceunit?pg={gid}           — conformance units
//   GET /api/profile/includedprofiles/{id}      — profiles a profile includes
//   GET /api/profile/includedconformanceunits/{id} — CUs in a profile
//   (includingprofiles is derived by inverting includedprofiles.)
//
// Uses the shared HttpClient (never recreate inside loops) and RetryHelper
// for 429/503 backoff. Never swallows HTTP errors silently in batch loops.
// ═══════════════════════════════════════════════════════════════════════

sealed class ProfileApiClient
{
    public const string DefaultBaseUrl = "https://profiles.opcfoundation.org/api";

    readonly HttpClient _http;
    readonly string _base;
    readonly ILogger _log;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public ProfileApiClient(HttpClient http, ILogger log, string? baseUrl = null)
    {
        _http = http;
        _log = log;
        _base = (baseUrl ?? Environment.GetEnvironmentVariable("PROFILES_API_BASE") ?? DefaultBaseUrl)
            .TrimEnd('/');
    }

    public Task<List<ProfileGroupDto>> GetProfileGroupsAsync() =>
        GetListAsync<ProfileGroupDto>("profilegroup");

    public Task<List<ProfileDto>> GetProfilesAsync(int pg) =>
        GetListAsync<ProfileDto>($"profile?pg={pg}");

    public Task<List<CategoryDto>> GetCategoriesAsync(int pg) =>
        GetListAsync<CategoryDto>($"category?pg={pg}");

    public Task<List<ProfileFolderDto>> GetProfileFoldersAsync(int pg) =>
        GetListAsync<ProfileFolderDto>($"profilefolder?pg={pg}");

    public Task<List<ConformanceGroupDto>> GetConformanceGroupsAsync(int pg) =>
        GetListAsync<ConformanceGroupDto>($"conformancegroup?pg={pg}");

    public Task<List<ConformanceUnitDto>> GetConformanceUnitsAsync(int pg) =>
        GetListAsync<ConformanceUnitDto>($"conformanceunit?pg={pg}");

    public Task<List<ProfileDto>> GetIncludedProfilesAsync(int profileId) =>
        GetListAsync<ProfileDto>($"profile/includedprofiles/{profileId}");

    public Task<List<ConformanceUnitDto>> GetIncludedConformanceUnitsAsync(int profileId) =>
        GetListAsync<ConformanceUnitDto>($"profile/includedconformanceunits/{profileId}");

    async Task<List<T>> GetListAsync<T>(string path)
    {
        var url = $"{_base}/{path}";
        using var resp = await RetryHelper.RetryAsync(() => _http.GetAsync(url), _log);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Profiles API GET {path} failed with HTTP {(int)resp.StatusCode}.");

        var stream = await resp.Content.ReadAsStreamAsync();
        var env = await JsonSerializer.DeserializeAsync<Envelope<T>>(stream, JsonOpts);
        if (env == null)
            throw new HttpRequestException($"Profiles API GET {path} returned an unparsable body.");
        if (env.Failed)
            throw new HttpRequestException(
                $"Profiles API GET {path} reported failure: {env.ErrorCode} {env.ErrorText}");
        return env.Result ?? [];
    }

    sealed class Envelope<T>
    {
        [JsonPropertyName("result")] public List<T>? Result { get; set; }
        [JsonPropertyName("failed")] public bool Failed { get; set; }
        [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
        [JsonPropertyName("errorText")] public string? ErrorText { get; set; }
    }
}

// ── DTOs (field names mirror the API; only what we consume is mapped) ──

sealed class ProfileGroupDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? FullName { get; set; }
    public int WorkingGroupId { get; set; }
    public int Sort { get; set; }
    public int? CoreDependencyId { get; set; }
    public int? ReplacedById { get; set; }
    public bool HasTestCases { get; set; }
    public List<int>? RequiredProfileGroups { get; set; }
}

sealed class ProfileDto
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? ProfileUri { get; set; }
    public string? Description { get; set; }
    public int ReleaseStatus { get; set; }
    public int Version { get; set; }
    public int ProfileGroupId { get; set; }
    public int WorkingGroupId { get; set; }
    public int Sort { get; set; }
    public DateTime? LastUpdateTime { get; set; }
    // Present on relationship-edge payloads:
    public bool? IsOptional { get; set; }
    public bool? IncludedComponentOnly { get; set; }
    public int? IncludingProfileId { get; set; }
}

sealed class CategoryDto
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ReleaseStatus { get; set; }
    public int ProfileGroupId { get; set; }
    public int Sort { get; set; }
    public DateTime? LastUpdateTime { get; set; }
}

sealed class ProfileFolderDto
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ReleaseStatus { get; set; }
    public int ProfileGroupId { get; set; }
    public string? CategoryGuid { get; set; }
    public int? CategoryPgId { get; set; }
    public string? ParentFolderGuid { get; set; }
    public int? ParentFolderPgId { get; set; }
    public int Sort { get; set; }
    public DateTime? LastUpdateTime { get; set; }
}

sealed class ConformanceGroupDto
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ReleaseStatus { get; set; }
    public int ProfileGroupId { get; set; }
    public int Sort { get; set; }
    public DateTime? LastUpdateTime { get; set; }
}

sealed class ConformanceUnitDto
{
    public int Id { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ReleaseStatus { get; set; }
    public int ProfileGroupId { get; set; }
    public string? ConformanceGroupGuid { get; set; }
    public int? ConformanceGroupPgId { get; set; }
    public string? CategoryGuid { get; set; }
    public int? CategoryPgId { get; set; }
    public int Sort { get; set; }
    public DateTime? LastUpdateTime { get; set; }
    // Present on relationship-edge payloads:
    public bool? IsOptional { get; set; }
    public bool? IncludedComponentOnly { get; set; }
    public int? IncludingProfileId { get; set; }
}
