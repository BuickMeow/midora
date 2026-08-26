namespace Midora.Domain;

/// <summary>
/// Immutable, structurally shared formal-order sequence used by large editable
/// timeline collections. Leaves are intentionally small: one sparse batch
/// copies only the leaves containing edited values, while snapshots retain the
/// unchanged majority by reference.
/// </summary>
internal sealed class PersistentTimelineSequence<TValue>
{
    internal const int LeafCapacity = 128;

    private readonly Node? _root;
    private readonly Func<TValue, ulong> _getFingerprint;

    private PersistentTimelineSequence(Node? root, Func<TValue, ulong> getFingerprint)
    {
        _root = root;
        _getFingerprint = getFingerprint;
    }

    public int Count => _root?.Count ?? 0;
    public ulong ContentFingerprint => (_root?.Aggregate ?? default).ToFingerprint();

    public static PersistentTimelineSequence<TValue> Empty(
        Func<TValue, ulong> getFingerprint) => new(null, getFingerprint);

    public static PersistentTimelineSequence<TValue> Create(
        IReadOnlyList<TValue> values,
        Func<TValue, ulong> getFingerprint)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(getFingerprint);
        if (values.Count == 0) return Empty(getFingerprint);
        List<Leaf> leaves = new((values.Count + LeafCapacity - 1) / LeafCapacity);
        for (int first = 0; first < values.Count; first += LeafCapacity)
        {
            int count = Math.Min(LeafCapacity, values.Count - first);
            TValue[] leafValues = new TValue[count];
            for (int offset = 0; offset < count; offset++)
                leafValues[offset] = values[first + offset];
            leaves.Add(new(leafValues, getFingerprint));
        }
        return new(BuildBalanced(leaves, 0, leaves.Count), getFingerprint);
    }

    public static PersistentTimelineSequence<TValue> Create(
        IEnumerable<TValue> values,
        Func<TValue, ulong> getFingerprint)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(getFingerprint);
        List<Leaf> leaves = [];
        TValue[] buffer = new TValue[LeafCapacity];
        int count = 0;
        foreach (TValue value in values)
        {
            buffer[count++] = value;
            if (count != buffer.Length) continue;
            leaves.Add(new(buffer, getFingerprint));
            buffer = new TValue[LeafCapacity];
            count = 0;
        }
        if (count != 0)
        {
            TValue[] tail = new TValue[count];
            Array.Copy(buffer, tail, count);
            leaves.Add(new(tail, getFingerprint));
        }
        return new(BuildBalanced(leaves, 0, leaves.Count), getFingerprint);
    }

    public TValue this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            Node node = _root!;
            int remaining = index;
            while (node is Branch branch)
            {
                int leftCount = branch.Left.Count;
                if (remaining < leftCount) node = branch.Left;
                else
                {
                    remaining -= leftCount;
                    node = branch.Right;
                }
            }
            return ((Leaf)node).GetValue(remaining);
        }
    }

    public IEnumerable<TValue> Enumerate()
    {
        if (_root is null) yield break;
        Stack<Node> stack = new();
        Node? current = _root;
        while (current is not null || stack.Count != 0)
        {
            while (current is Branch branch)
            {
                stack.Push(branch.Right);
                current = branch.Left;
            }
            if (current is Leaf leaf)
            {
                foreach (TValue value in leaf.EnumerateValues()) yield return value;
            }
            current = stack.Count == 0 ? null : stack.Pop();
        }
    }

    public IEnumerable<Leaf> EnumerateLeaves()
    {
        if (_root is null) yield break;
        Stack<Node> stack = new();
        Node? current = _root;
        while (current is not null || stack.Count != 0)
        {
            while (current is Branch branch)
            {
                stack.Push(branch.Right);
                current = branch.Left;
            }
            if (current is Leaf leaf) yield return leaf;
            current = stack.Count == 0 ? null : stack.Pop();
        }
    }

    public Mutation ReplaceBatch(IReadOnlyDictionary<int, TValue> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return new(this, [], [], []);
        IndexedReplacement[] ordered = replacements
            .Select(static pair => (pair.Key, pair.Value))
            .OrderBy(static value => value.Key)
            .Select(static value => new IndexedReplacement(value.Key, value.Value))
            .ToArray();
        return ReplaceBatchSorted(ordered);
    }

    /// <summary>
    /// Replaces values whose formal indices are already sorted. Timeline
    /// collections resolve live objects to formal indices in one pass; keeping
    /// that ordering avoids constructing a dictionary and sorting it again at
    /// the persistent sequence boundary.
    /// </summary>
    public Mutation ReplaceBatchSorted(IndexedReplacement[] ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Length == 0) return new(this, [], [], []);
        for (int index = 0; index < ordered.Length; index++)
        {
            if ((uint)ordered[index].Index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(ordered));
            if (index != 0 && ordered[index - 1].Index == ordered[index].Index)
                throw new ArgumentException("A persistent sequence replacement index is duplicated.", nameof(ordered));
            if (index != 0 && ordered[index - 1].Index > ordered[index].Index)
                throw new ArgumentException("Persistent sequence replacements must be ordered.", nameof(ordered));
        }
        List<Leaf> removed = [];
        List<Leaf> added = [];
        List<ValueReplacement> valueReplacements = new(ordered.Length);
        Node root = Replace(
            _root!,
            0,
            ordered,
            0,
            ordered.Length,
            removed,
            added,
            valueReplacements);
        return new(new(root, _getFingerprint), removed, added, valueReplacements);
    }

    public Mutation InsertRange(int index, IReadOnlyList<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (values.Count == 0) return new(this, [], [], []);

        List<Leaf> removed = [];
        List<Leaf> added = [];
        Split(_root, index, _getFingerprint, removed, added, out Node? left, out Node? right);
        PersistentTimelineSequence<TValue> inserted = Create(values, _getFingerprint);
        added.AddRange(inserted.EnumerateLeaves());
        Node? root = Concat(Concat(left, inserted._root), right);
        return new(new(root, _getFingerprint), removed, added, []);
    }

    public Mutation RemoveIndices(IReadOnlyCollection<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count == 0) return new(this, [], [], []);
        int[] ordered = indices.Distinct().Order().ToArray();
        if (ordered.Length != indices.Count)
            throw new ArgumentException("A persistent sequence removal index is duplicated.", nameof(indices));
        if (ordered[0] < 0 || ordered[^1] >= Count)
            throw new ArgumentOutOfRangeException(nameof(indices));
        List<Leaf> removed = [];
        List<Leaf> added = [];
        Node? root = Remove(_root!, 0, ordered, 0, ordered.Length, removed, added);
        return new(new(root, _getFingerprint), removed, added, []);
    }

    private Node Replace(
        Node node,
        int nodeStart,
        IndexedReplacement[] replacements,
        int first,
        int count,
        List<Leaf> removed,
        List<Leaf> added,
        List<ValueReplacement> valueReplacements)
    {
        if (count == 0) return node;
        if (node is Leaf leaf)
        {
            PagedTimelineSequenceFingerprintAggregate aggregate = leaf.Aggregate;
            LeafReplacement[] leafReplacements =
                new LeafReplacement[checked(leaf.Replacements.Count + count)];
            int replacementWriteIndex = 0;
            int existingIndex = 0;
            int changeIndex = first;
            int changeEnd = checked(first + count);
            while (existingIndex < leaf.Replacements.Count || changeIndex < changeEnd)
            {
                int existingOffset = existingIndex < leaf.Replacements.Count
                    ? leaf.Replacements[existingIndex].Index
                    : int.MaxValue;
                int changeOffset = changeIndex < changeEnd
                    ? replacements[changeIndex].Index - nodeStart
                    : int.MaxValue;
                if (existingOffset < changeOffset)
                {
                    leafReplacements[replacementWriteIndex++] =
                        leaf.Replacements[existingIndex++];
                    continue;
                }

                int localIndex = changeOffset;
                TValue currentValue = existingOffset == changeOffset
                    ? leaf.Replacements[existingIndex].Value
                    : leaf.BaseValues[localIndex];
                TValue replacementValue = replacements[changeIndex].Value;
                aggregate.ReplaceAt(
                    localIndex,
                    leaf.Count,
                    _getFingerprint(currentValue),
                    _getFingerprint(replacementValue));
                valueReplacements.Add(new(currentValue, replacementValue));
                if (!EqualityComparer<TValue>.Default.Equals(
                    leaf.BaseValues[localIndex],
                    replacementValue))
                {
                    leafReplacements[replacementWriteIndex++] =
                        new(localIndex, replacementValue);
                }
                if (existingOffset == changeOffset) existingIndex++;
                changeIndex++;
            }
            if (replacementWriteIndex != leafReplacements.Length)
                Array.Resize(ref leafReplacements, replacementWriteIndex);
            Leaf replacement = new(
                leaf.BaseValues,
                leafReplacements,
                aggregate);
            removed.Add(leaf);
            added.Add(replacement);
            return replacement;
        }
        Branch branch = (Branch)node;
        int boundary = checked(nodeStart + branch.Left.Count);
        int middle = LowerBound(replacements, first, count, boundary);
        Node left = Replace(
            branch.Left,
            nodeStart,
            replacements,
            first,
            middle - first,
            removed,
            added,
            valueReplacements);
        Node right = Replace(
            branch.Right,
            boundary,
            replacements,
            middle,
            first + count - middle,
            removed,
            added,
            valueReplacements);
        return ReferenceEquals(left, branch.Left) && ReferenceEquals(right, branch.Right)
            ? branch
            : Balance(new Branch(left, right));
    }

    private Node? Remove(
        Node node,
        int nodeStart,
        int[] indices,
        int first,
        int count,
        List<Leaf> removed,
        List<Leaf> added)
    {
        if (count == 0) return node;
        if (node is Leaf leaf)
        {
            bool[] discard = new bool[leaf.Count];
            for (int index = first; index < first + count; index++)
                discard[indices[index] - nodeStart] = true;
            TValue[] values = new TValue[leaf.Count - count];
            int write = 0;
            for (int index = 0; index < leaf.Count; index++)
                if (!discard[index]) values[write++] = leaf.Values[index];
            removed.Add(leaf);
            if (values.Length == 0) return null;
            Leaf replacement = new(values, _getFingerprint);
            added.Add(replacement);
            return replacement;
        }
        Branch branch = (Branch)node;
        int boundary = checked(nodeStart + branch.Left.Count);
        int middle = LowerBound(indices, first, count, boundary);
        Node? left = Remove(
            branch.Left,
            nodeStart,
            indices,
            first,
            middle - first,
            removed,
            added);
        Node? right = Remove(
            branch.Right,
            boundary,
            indices,
            middle,
            first + count - middle,
            removed,
            added);
        return Concat(left, right);
    }

    private static void Split(
        Node? node,
        int index,
        Func<TValue, ulong> getFingerprint,
        List<Leaf> removed,
        List<Leaf> added,
        out Node? left,
        out Node? right)
    {
        if (node is null)
        {
            left = null;
            right = null;
            return;
        }
        if (index == 0)
        {
            left = null;
            right = node;
            return;
        }
        if (index == node.Count)
        {
            left = node;
            right = null;
            return;
        }
        if (node is Leaf leaf)
        {
            Leaf leftLeaf = new(leaf.Values[..index], getFingerprint);
            Leaf rightLeaf = new(leaf.Values[index..], getFingerprint);
            removed.Add(leaf);
            added.Add(leftLeaf);
            added.Add(rightLeaf);
            left = leftLeaf;
            right = rightLeaf;
            return;
        }
        Branch branch = (Branch)node;
        if (index < branch.Left.Count)
        {
            Split(branch.Left, index, getFingerprint, removed, added, out Node? ll, out Node? lr);
            left = ll;
            right = Concat(lr, branch.Right);
        }
        else
        {
            Split(branch.Right, index - branch.Left.Count, getFingerprint, removed, added, out Node? rl, out Node? rr);
            left = Concat(branch.Left, rl);
            right = rr;
        }
    }

    private static Node? Concat(Node? left, Node? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        if (left is Leaf leftLeaf
            && right is Leaf rightLeaf
            && leftLeaf.Count + rightLeaf.Count <= LeafCapacity)
        {
            // Concat is only called by operations that already register their
            // boundary leaf replacements. Keeping leaves separate here avoids
            // silently losing that precise mutation set.
            return new Branch(left, right);
        }
        if (left.Height > right.Height + 1)
        {
            Branch branch = (Branch)left;
            return Balance(new Branch(branch.Left, Concat(branch.Right, right)!));
        }
        if (right.Height > left.Height + 1)
        {
            Branch branch = (Branch)right;
            return Balance(new Branch(Concat(left, branch.Left)!, branch.Right));
        }
        return new Branch(left, right);
    }

    private static Node Balance(Branch branch)
    {
        int balance = branch.Left.Height - branch.Right.Height;
        if (balance > 1)
        {
            Branch left = (Branch)branch.Left;
            if (left.Left.Height < left.Right.Height)
                left = RotateLeft(left);
            return RotateRight(new Branch(left, branch.Right));
        }
        if (balance < -1)
        {
            Branch right = (Branch)branch.Right;
            if (right.Right.Height < right.Left.Height)
                right = RotateRight(right);
            return RotateLeft(new Branch(branch.Left, right));
        }
        return branch;
    }

    private static Branch RotateLeft(Branch node)
    {
        Branch pivot = (Branch)node.Right;
        return new(new Branch(node.Left, pivot.Left), pivot.Right);
    }

    private static Branch RotateRight(Branch node)
    {
        Branch pivot = (Branch)node.Left;
        return new(pivot.Left, new Branch(pivot.Right, node.Right));
    }

    private static Node? BuildBalanced(IReadOnlyList<Leaf> leaves, int first, int count)
    {
        if (count == 0) return null;
        if (count == 1) return leaves[first];
        int leftCount = count / 2;
        return new Branch(
            BuildBalanced(leaves, first, leftCount)!,
            BuildBalanced(leaves, first + leftCount, count - leftCount)!);
    }

    private static int LowerBound(
        IndexedReplacement[] values,
        int first,
        int count,
        int target)
    {
        int low = first;
        int high = first + count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle].Index < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int LowerBound(int[] values, int first, int count, int target)
    {
        int low = first;
        int high = first + count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal abstract class Node
    {
        protected Node(int count, int height, PagedTimelineSequenceFingerprintAggregate aggregate)
        {
            Count = count;
            Height = height;
            Aggregate = aggregate;
        }

        public int Count { get; }
        public int Height { get; }
        public PagedTimelineSequenceFingerprintAggregate Aggregate { get; }
    }

    internal sealed class Leaf : Node
    {
        public Leaf(TValue[] values, Func<TValue, ulong> getFingerprint)
            : base(values.Length, 1, AggregateValues(values, getFingerprint))
        {
            BaseValues = values;
            Replacements = [];
        }

        internal Leaf(
            TValue[] baseValues,
            LeafReplacement[] replacements,
            PagedTimelineSequenceFingerprintAggregate aggregate)
            : base(baseValues.Length, 1, aggregate)
        {
            BaseValues = baseValues;
            Replacements = replacements;
        }

        public TValue[] BaseValues { get; }
        public IReadOnlyList<LeafReplacement> Replacements { get; }
        public TValue[] Values
        {
            get
            {
                if (Replacements.Count == 0) return BaseValues;
                TValue[] result = [.. BaseValues];
                foreach (LeafReplacement replacement in Replacements)
                    result[replacement.Index] = replacement.Value;
                return result;
            }
        }

        public TValue GetValue(int index)
        {
            int low = 0;
            int high = Replacements.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (Replacements[middle].Index < index) low = middle + 1;
                else high = middle;
            }
            return low < Replacements.Count && Replacements[low].Index == index
                ? Replacements[low].Value
                : BaseValues[index];
        }

        public IEnumerable<TValue> EnumerateValues()
        {
            int replacementIndex = 0;
            for (int index = 0; index < BaseValues.Length; index++)
            {
                if (replacementIndex < Replacements.Count
                    && Replacements[replacementIndex].Index == index)
                {
                    yield return Replacements[replacementIndex++].Value;
                }
                else
                {
                    yield return BaseValues[index];
                }
            }
        }

        private static PagedTimelineSequenceFingerprintAggregate AggregateValues(
            TValue[] values,
            Func<TValue, ulong> getFingerprint)
        {
            PagedTimelineSequenceFingerprintAggregate aggregate = default;
            foreach (TValue value in values) aggregate.Add(getFingerprint(value));
            return aggregate;
        }
    }

    internal readonly record struct LeafReplacement(int Index, TValue Value);

    internal readonly record struct IndexedReplacement(int Index, TValue Value);

    private sealed class Branch : Node
    {
        public Branch(Node left, Node right)
            : base(
                checked(left.Count + right.Count),
                checked(Math.Max(left.Height, right.Height) + 1),
                Combine(left.Aggregate, right.Aggregate))
        {
            Left = left;
            Right = right;
        }

        public Node Left { get; }
        public Node Right { get; }

        private static PagedTimelineSequenceFingerprintAggregate Combine(
            PagedTimelineSequenceFingerprintAggregate left,
            PagedTimelineSequenceFingerprintAggregate right)
        {
            left.Combine(right);
            return left;
        }
    }

    internal readonly record struct Mutation(
        PersistentTimelineSequence<TValue> Sequence,
        IReadOnlyList<Leaf> RemovedLeaves,
        IReadOnlyList<Leaf> AddedLeaves,
        IReadOnlyList<ValueReplacement> ValueReplacements);

    internal readonly record struct ValueReplacement(TValue Expected, TValue Replacement);
}
