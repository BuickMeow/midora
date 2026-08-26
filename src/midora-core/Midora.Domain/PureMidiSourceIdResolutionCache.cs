namespace Midora.Domain;

/// <summary>
/// Shares immutable paged-source ID resolutions between revision snapshots and
/// their live collection. Overlay and exclusion decisions remain revision-local.
/// </summary>
internal sealed class PureMidiSourceIdResolutionCache<TMatch>(
    Func<TMatch, MidoraId> getId,
    Func<TMatch, int> getIndex)
{
    private readonly object _sync = new();
    private readonly Dictionary<MidoraId, TMatch> _matches = [];
    private readonly HashSet<MidoraId> _absent = [];

    public IReadOnlyList<TMatch> Resolve(
        IReadOnlySet<MidoraId> ids,
        Func<IReadOnlySet<MidoraId>, IEnumerable<TMatch>> query)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(query);
        if (ids.Count == 0) return [];

        lock (_sync)
        {
            HashSet<MidoraId> missing = ids
                .Where(id => !_matches.ContainsKey(id) && !_absent.Contains(id))
                .ToHashSet();
            if (missing.Count != 0)
            {
                HashSet<MidoraId> found = [];
                foreach (TMatch match in query(missing))
                {
                    MidoraId id = getId(match);
                    if (!missing.Contains(id)) continue;
                    _matches[id] = match;
                    found.Add(id);
                }
                missing.ExceptWith(found);
                _absent.UnionWith(missing);
            }

            return ids
                .Where(_matches.ContainsKey)
                .Select(id => _matches[id])
                .OrderBy(getIndex)
                .ToArray();
        }
    }
}
