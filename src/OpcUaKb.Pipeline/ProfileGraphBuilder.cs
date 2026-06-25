using System.Collections.Concurrent;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// ProfileGraphBuilder — crawls every profile group (all versions) via the
// ProfileApiClient and assembles a normalized ProfileGraph: nodes keyed by
// "{guid}|{pg}" plus typed edges (profile→includedProfile,
// profile→conformanceUnit, conformanceUnit→group/category, folder
// hierarchy, group requires/replacedBy).
//
// Per-profile relationship edges (includedprofiles + includedconformance-
// units) are fetched with bounded concurrency. includingProfiles is NOT
// fetched — it is derived by inverting the includes edges at query time.
//
// Failures on a single profile/group are logged and counted (never silently
// swallowed); the build continues so one bad node can't abort the crawl.
// ═══════════════════════════════════════════════════════════════════════

sealed class ProfileGraphBuilder
{
    readonly ProfileApiClient _api;
    readonly ILogger _log;
    readonly int _edgeConcurrency;

    static readonly HtmlParser HtmlParser = new();

    public int Errors { get; private set; }

    public ProfileGraphBuilder(ProfileApiClient api, ILogger log, int edgeConcurrency = 8)
    {
        _api = api;
        _log = log;
        _edgeConcurrency = Math.Max(1, edgeConcurrency);
    }

    public async Task<ProfileGraph> BuildAsync(IEnumerable<int>? onlyGroupIds = null)
    {
        var graph = new ProfileGraph { GeneratedUtc = DateTime.UtcNow.ToString("O") };

        var groups = await _api.GetProfileGroupsAsync();
        if (onlyGroupIds != null)
        {
            var set = onlyGroupIds.ToHashSet();
            groups = groups.Where(g => set.Contains(g.Id)).ToList();
        }
        _log.LogInformation("[PROFILES] Phase=groups Count={N}", groups.Count);

        var groupIndex = 0;
        foreach (var g in groups.OrderBy(g => g.Sort))
        {
            groupIndex++;
            try
            {
                await BuildGroupAsync(graph, g, groupIndex, groups.Count);
            }
            catch (Exception ex)
            {
                Errors++;
                _log.LogWarning("[PROFILES] Phase=group_failed Group={Group} Id={Id} Error={Error}",
                    g.FullName, g.Id, ex.Message);
            }
        }

        _log.LogInformation(
            "[PROFILES] Phase=build_done Groups={G} Categories={Cat} Folders={F} ConformanceGroups={CG} ConformanceUnits={CU} Profiles={P} Errors={E}",
            graph.Groups.Count, graph.Categories.Count, graph.Folders.Count,
            graph.ConformanceGroups.Count, graph.ConformanceUnits.Count, graph.Profiles.Count, Errors);

        return graph;
    }

    async Task BuildGroupAsync(ProfileGraph graph, ProfileGroupDto g, int idx, int total)
    {
        graph.Groups.Add(new GraphGroup
        {
            Id = g.Id,
            Name = g.Name,
            FullName = g.FullName,
            WorkingGroupId = g.WorkingGroupId,
            Sort = g.Sort,
            ReplacedById = g.ReplacedById,
            Requires = g.RequiredProfileGroups ?? [],
        });

        var categories = await _api.GetCategoriesAsync(g.Id);
        foreach (var c in categories)
            graph.Categories.Add(new GraphCategory
            {
                Key = Key(c.Guid, c.ProfileGroupId), Guid = c.Guid, Pg = c.ProfileGroupId,
                Name = c.Name, Description = Strip(c.Description), Status = c.ReleaseStatus,
            });

        var folders = await _api.GetProfileFoldersAsync(g.Id);
        foreach (var f in folders)
            graph.Folders.Add(new GraphFolder
            {
                Key = Key(f.Guid, f.ProfileGroupId), Guid = f.Guid, Pg = f.ProfileGroupId,
                Name = f.Name, Description = Strip(f.Description), Status = f.ReleaseStatus,
                CategoryGuid = f.CategoryGuid, ParentFolderGuid = f.ParentFolderGuid,
            });

        var cgs = await _api.GetConformanceGroupsAsync(g.Id);
        foreach (var cg in cgs)
            graph.ConformanceGroups.Add(new GraphConformanceGroup
            {
                Key = Key(cg.Guid, cg.ProfileGroupId), Guid = cg.Guid, Pg = cg.ProfileGroupId,
                Name = cg.Name, Description = Strip(cg.Description), Status = cg.ReleaseStatus,
            });

        var cus = await _api.GetConformanceUnitsAsync(g.Id);
        // Dedupe CUs by key — the bulk list can repeat a unit per including profile.
        var seenCu = new HashSet<string>();
        foreach (var cu in cus)
        {
            var key = Key(cu.Guid, cu.ProfileGroupId);
            if (!seenCu.Add(key)) continue;
            graph.ConformanceUnits.Add(new GraphConformanceUnit
            {
                Key = key, Guid = cu.Guid, Pg = cu.ProfileGroupId,
                Name = cu.Name, Description = Strip(cu.Description), Status = cu.ReleaseStatus,
                ConformanceGroupGuid = cu.ConformanceGroupGuid, CategoryGuid = cu.CategoryGuid,
            });
        }

        var profiles = await _api.GetProfilesAsync(g.Id);
        _log.LogInformation(
            "[PROFILES] Phase=group Group={Group} Idx={Idx}/{Total} Profiles={P} ConformanceUnits={CU} Categories={Cat}",
            g.FullName, idx, total, profiles.Count, graph.ConformanceUnits.Count(c => c.Pg == g.Id), categories.Count);

        var built = new ConcurrentBag<GraphProfile>();
        using var sem = new SemaphoreSlim(_edgeConcurrency);
        var tasks = profiles.Select(async p =>
        {
            await sem.WaitAsync();
            try
            {
                var node = new GraphProfile
                {
                    Key = Key(p.Guid, p.ProfileGroupId), Guid = p.Guid, Pg = p.ProfileGroupId,
                    GroupName = g.FullName, Name = p.Name, ProfileUri = p.ProfileUri,
                    Description = Strip(p.Description), Status = p.ReleaseStatus, Version = p.Version,
                };

                var incl = await _api.GetIncludedProfilesAsync(p.Id);
                foreach (var i in incl)
                    node.Includes.Add(new GraphInclude
                    {
                        Guid = i.Guid, Pg = i.ProfileGroupId,
                        IsOptional = i.IsOptional ?? false, ComponentOnly = i.IncludedComponentOnly ?? false,
                    });

                var inclCu = await _api.GetIncludedConformanceUnitsAsync(p.Id);
                foreach (var cu in inclCu)
                    node.ConformanceUnits.Add(new GraphCuRef
                    {
                        Guid = cu.Guid, Pg = cu.ProfileGroupId, IsOptional = cu.IsOptional ?? false,
                    });

                built.Add(node);
            }
            catch (Exception ex)
            {
                Errors++;
                _log.LogWarning("[PROFILES] Phase=profile_failed Profile={Name} Id={Id} Error={Error}",
                    p.Name, p.Id, ex.Message);
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        graph.Profiles.AddRange(built);
    }

    static string Key(string? guid, int pg) => $"{guid}|{pg}";

    static string? Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        try
        {
            var doc = HtmlParser.ParseDocument($"<body>{html}</body>");
            var text = doc.Body?.TextContent ?? html;
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
        catch
        {
            return html;
        }
    }
}
