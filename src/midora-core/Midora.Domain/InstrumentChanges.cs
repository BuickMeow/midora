using System.Collections.Immutable;

namespace Midora.Domain;

/// <summary>
/// Explicit editing association, never an additional MIDI event. BankLsbEventId
/// is absent for SubVoice, whose Bank event already owns both bank components.
/// Tick and musical values are deliberately not duplicated here.
/// </summary>
public readonly record struct InstrumentChange(
    MidoraId Id,
    MidoraId BankEventId,
    MidoraId? BankLsbEventId,
    MidoraId ProgramEventId)
{
    public void ValidateShape(bool directMidi)
    {
        if (Id == default || BankEventId == default || ProgramEventId == default
            || directMidi != BankLsbEventId.HasValue || BankLsbEventId == default(MidoraId)
            || Id == BankEventId || Id == ProgramEventId || Id == BankLsbEventId
            || BankEventId == ProgramEventId || BankEventId == BankLsbEventId
            || ProgramEventId == BankLsbEventId)
            throw new ArgumentException("Invalid Instrument Change member identities.");
    }

    public IEnumerable<MidoraId> MemberIds
    {
        get
        {
            yield return BankEventId;
            if (BankLsbEventId is { } lsb) yield return lsb;
            yield return ProgramEventId;
        }
    }
}

/// <summary>
/// Persistent association root with a reverse member index. A one-point edit
/// copies only tree paths, not all associations or any raw event collection.
/// Identity ordering is for lookup/serialization only, never musical ordering.
/// </summary>
public sealed class InstrumentChangeSet
{
    public static InstrumentChangeSet Empty { get; } = new(
        ImmutableSortedDictionary<MidoraId, InstrumentChange>.Empty,
        ImmutableDictionary<MidoraId, MidoraId>.Empty);
    private readonly ImmutableSortedDictionary<MidoraId, InstrumentChange> _groups;
    private readonly ImmutableDictionary<MidoraId, MidoraId> _members;
    // Detached transactions may temporarily separate members. Keep only their
    // explicit associations until the final raw revision is known; never infer
    // new groups from coincident ticks. This receipt is not serialized.
    private readonly ImmutableSortedDictionary<MidoraId, InstrumentChange> _pending;
    private readonly long? _validatedRevision;

    private InstrumentChangeSet(ImmutableSortedDictionary<MidoraId, InstrumentChange> groups,
        ImmutableDictionary<MidoraId, MidoraId> members,
        ImmutableSortedDictionary<MidoraId, InstrumentChange>? pending = null, long? validatedRevision = null)
    { _groups = groups; _members = members; _pending = pending ?? ImmutableSortedDictionary<MidoraId, InstrumentChange>.Empty;
        _validatedRevision = validatedRevision; }

    public int Count => _groups.Count;
    public IEnumerable<InstrumentChange> Values => _groups.Values;
    internal IEnumerable<InstrumentChange> PendingReconciliation => _pending.Values;
    internal bool IsValidatedFor(long revision) => _validatedRevision == revision;
    internal InstrumentChangeSet ValidatedAt(long revision, bool complete = false) =>
        new(_groups, _members, complete ? null : _pending, revision);
    internal InstrumentChangeSet DeferRemoval(MidoraId id)
    {
        if (!_groups.TryGetValue(id, out var value)) return this;
        var removed = Remove(id);
        return new(removed._groups, removed._members, _pending.SetItem(id, value));
    }
    public bool TryGet(MidoraId id, out InstrumentChange value) => _groups.TryGetValue(id, out value);
    public bool TryGetByMember(MidoraId memberId, out InstrumentChange value)
    {
        value = default;
        return _members.TryGetValue(memberId, out var id) && _groups.TryGetValue(id, out value);
    }

    public InstrumentChangeSet Add(InstrumentChange value, bool directMidi)
    {
        value.ValidateShape(directMidi);
        if (_groups.ContainsKey(value.Id)) throw new ArgumentException("Duplicate Instrument Change identity.");
        var members = _members;
        foreach (var id in value.MemberIds)
        {
            if (members.ContainsKey(id)) throw new ArgumentException("An event already belongs to an Instrument Change.");
            members = members.Add(id, value.Id);
        }
        return new(_groups.Add(value.Id, value), members, _pending.Remove(value.Id));
    }

    public InstrumentChangeSet Remove(MidoraId id)
    {
        if (!_groups.TryGetValue(id, out var value)) return this;
        var members = _members;
        foreach (var member in value.MemberIds) members = members.Remove(member);
        return new(_groups.Remove(id), members, _pending.Remove(id));
    }
}
