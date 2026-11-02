namespace Bim.Core;

// Composes a requested set of skills from a catalog: dependency closure, semver range checks, declared conflicts and
// cycles, then deterministic default resolution. For a dotted key set by several skills the highest priority wins;
// equal priorities with equal values agree; equal priorities with different values are a default_conflict unless the
// caller's precedence order (earlier wins) ranks every tied skill. The result never depends on request or catalog order.
public static class SkillResolver
{
    const string RequestLabel = "request";

    public static SkillResolution Resolve(SkillCatalog catalog, IReadOnlyList<SkillRequest> requests, IReadOnlyList<string>? precedence = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requests);
        // RED STUB: resolve nothing.
        if (requests.Count >= 0) return new SkillResolution(Array.Empty<SkillManifest>(), Array.Empty<SkillSetting>(), Array.Empty<SkillEffectiveParameter>(), Array.Empty<SkillEffectiveRule>(), Array.Empty<SkillIssue>());
        var issues = new List<SkillIssue>();
        var constraints = new SortedDictionary<string, List<(SkillVersionRange Range, string By)>>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests.OrderBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Range, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(request);
            // Callers outside nullable analysis may pass null members; they are refused like any other bad request.
            var requestId = request.Id ?? "";
            if (!SkillParser.IsId(requestId)) { issues.Add(new(SkillCodes.RequestInvalid, requestId, "requested id is not a skill id")); continue; }
            if (!SkillParser.TryParseRange(request.Range, out var range)) { issues.Add(new(SkillCodes.RequestInvalid, requestId, "requested range " + request.Range + " is not a semver range")); continue; }
            requested.Add(requestId);
            Constrain(constraints, requestId, range, RequestLabel);
        }

        // Dependency closure over the catalog.
        var found = new SortedDictionary<string, SkillManifest>(StringComparer.Ordinal);
        var pending = new SortedSet<string>(requested, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var id = pending.Min!;
            pending.Remove(id);
            if (!visited.Add(id)) continue;
            var manifest = catalog.Find(id);
            if (manifest is null) continue;
            found[id] = manifest;
            foreach (var requirement in manifest.Requires)
            {
                Constrain(constraints, requirement.Id, requirement.Range, id);
                if (!visited.Contains(requirement.Id)) pending.Add(requirement.Id);
            }
        }

        foreach (var (id, list) in constraints)
        {
            var by = string.Join(", ", list.Select(item => item.By).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal));
            if (!found.TryGetValue(id, out var manifest))
            {
                issues.Add(requested.Contains(id)
                    ? new SkillIssue(SkillCodes.SkillMissing, id, "requested skill " + id + " is not in the catalog")
                    : new SkillIssue(SkillCodes.DependencyMissing, id, id + " is not in the catalog; required by " + by));
                continue;
            }
            foreach (var (range, requirer) in list.OrderBy(item => item.By, StringComparer.Ordinal).ThenBy(item => item.Range.Text, StringComparer.Ordinal))
                if (!range.Satisfies(manifest.Version))
                    issues.Add(new(SkillCodes.VersionMismatch, id, id + " " + manifest.Version + " does not satisfy " + range.Text + " required by " + requirer));
        }

        foreach (var cycle in Cycles(found)) issues.Add(new(SkillCodes.DependencyCycle, cycle[0], string.Join(" -> ", cycle)));

        var conflictPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, manifest) in found)
        {
            foreach (var other in manifest.ConflictsWith.OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!found.ContainsKey(other)) continue;
                var pair = string.CompareOrdinal(id, other) < 0 ? id + "|" + other : other + "|" + id;
                if (conflictPairs.Add(pair)) issues.Add(new(SkillCodes.SkillConflict, id, id + " declares a conflict with " + other));
            }
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        if (precedence is not null)
        {
            for (var index = 0; index < precedence.Count; index++)
            {
                var entry = precedence[index] ?? "";
                if (!found.ContainsKey(entry)) issues.Add(new(SkillCodes.PrecedenceUnknown, entry, "precedence entry is not a resolved skill"));
                else if (!rank.TryAdd(entry, index)) issues.Add(new(SkillCodes.PrecedenceDuplicate, entry, "precedence lists a skill once"));
            }
        }
        if (issues.Count > 0) return Failure(issues);

        var ordered = TopologicalOrder(found);
        var settings = Settings(ordered, rank, issues);
        if (issues.Count > 0) return Failure(issues);

        var parameters = new List<SkillEffectiveParameter>();
        var rules = new List<SkillEffectiveRule>();
        foreach (var skill in ordered)
        {
            foreach (var parameter in skill.Parameters)
                parameters.Add(new(skill.Id, parameter.Name, parameter.Default, new SkillTrace(skill.Id, SkillTrace.ParameterOrigin, parameter.Name)));
            foreach (var rule in skill.Rules)
            {
                SkillValue? threshold = rule.Check.Value;
                SkillTrace? thresholdSource = null;
                if (rule.Check.Parameter is { } name)
                {
                    threshold = skill.Parameters.First(parameter => parameter.Name == name).Default;
                    thresholdSource = new SkillTrace(skill.Id, SkillTrace.ParameterOrigin, name);
                }
                rules.Add(new(skill.Id, rule, threshold, new SkillTrace(skill.Id, SkillTrace.RuleOrigin, rule.Id), thresholdSource));
            }
        }
        return new SkillResolution(ordered, settings, parameters.ToArray(), rules.ToArray(), Array.Empty<SkillIssue>());
    }

    static void Constrain(SortedDictionary<string, List<(SkillVersionRange Range, string By)>> constraints, string id, SkillVersionRange range, string by)
    {
        if (!constraints.TryGetValue(id, out var list)) constraints[id] = list = new List<(SkillVersionRange Range, string By)>();
        list.Add((range, by));
    }

    static SkillResolution Failure(List<SkillIssue> issues) =>
        new(Array.Empty<SkillManifest>(), Array.Empty<SkillSetting>(), Array.Empty<SkillEffectiveParameter>(), Array.Empty<SkillEffectiveRule>(), issues.ToArray());

    // Every distinct elementary cycle reachable by depth-first search in ordinal order, each rotated to start at its
    // smallest id and closed by repeating it.
    static List<string[]> Cycles(SortedDictionary<string, SkillManifest> found)
    {
        var cycles = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        void Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var next in found[id].Requires.Select(requirement => requirement.Id).Where(found.ContainsKey).OrderBy(item => item, StringComparer.Ordinal))
            {
                var mark = state.GetValueOrDefault(next);
                if (mark == 0) Visit(next);
                else if (mark == 1)
                {
                    var members = stack.Skip(stack.IndexOf(next)).ToList();
                    var start = members.IndexOf(members.Min(StringComparer.Ordinal)!);
                    var rotated = members.Skip(start).Concat(members.Take(start)).ToList();
                    rotated.Add(rotated[0]);
                    if (seen.Add(string.Join("|", rotated))) cycles.Add(rotated.ToArray());
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
        foreach (var id in found.Keys) if (!state.ContainsKey(id)) Visit(id);
        return cycles;
    }

    // Kahn's algorithm: requirements first, ties by ordinal id. Called only when the graph has no cycle.
    static SkillManifest[] TopologicalOrder(SortedDictionary<string, SkillManifest> found)
    {
        var remaining = found.ToDictionary(pair => pair.Key, pair => pair.Value.Requires.Count(requirement => found.ContainsKey(requirement.Id)), StringComparer.Ordinal);
        var ready = new SortedSet<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var order = new List<SkillManifest>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(found[id]);
            foreach (var (otherId, other) in found)
            {
                if (!other.Requires.Any(requirement => requirement.Id == id)) continue;
                if (--remaining[otherId] == 0) ready.Add(otherId);
            }
        }
        return order.ToArray();
    }

    static SkillSetting[] Settings(SkillManifest[] skills, Dictionary<string, int> rank, List<SkillIssue> issues)
    {
        var byKey = new SortedDictionary<string, List<(SkillManifest Skill, SkillValue Value)>>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            foreach (var entry in skill.Defaults)
            {
                if (!byKey.TryGetValue(entry.Key, out var list)) byKey[entry.Key] = list = new List<(SkillManifest Skill, SkillValue Value)>();
                list.Add((skill, entry.Value));
            }
        }
        var settings = new List<SkillSetting>();
        foreach (var (key, contributions) in byKey)
        {
            // Strongest first: priority, then precedence rank (unranked last), then ordinal id.
            var sorted = contributions
                .OrderByDescending(item => item.Skill.Priority)
                .ThenBy(item => rank.TryGetValue(item.Skill.Id, out var position) ? position : int.MaxValue)
                .ThenBy(item => item.Skill.Id, StringComparer.Ordinal)
                .ToList();
            var top = sorted.Where(item => item.Skill.Priority == sorted[0].Skill.Priority).ToList();
            SkillDecision decision;
            if (sorted.Count == 1) decision = SkillDecision.Only;
            else if (top.Count == 1) decision = SkillDecision.Priority;
            else if (top.All(item => item.Value == top[0].Value)) decision = SkillDecision.Agreement;
            else if (top.All(item => rank.ContainsKey(item.Skill.Id))) decision = SkillDecision.Precedence;
            else
            {
                issues.Add(new(SkillCodes.DefaultConflict, key, "equal priority " + top[0].Skill.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture) + " with different values: "
                    + string.Join(", ", top.Select(item => item.Skill.Id + "=" + item.Value)) + "; pass a precedence order"));
                continue;
            }
            var winner = sorted[0];
            settings.Add(new SkillSetting(key, winner.Value, Trace(winner.Skill, key), decision, sorted.Skip(1).Select(item => Trace(item.Skill, key)).ToArray()));
        }
        return settings.ToArray();
    }

    static SkillTrace Trace(SkillManifest skill, string key) => new(skill.Id, SkillTrace.DefaultOrigin, key);
}
