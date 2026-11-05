using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Raven.Server.Documents.Indexes.Joins;
using Sparrow.Collections;
using AsyncManualResetEvent = Sparrow.Server.AsyncManualResetEvent;

namespace Raven.Server.Documents.Indexes
{
    public sealed class CollectionOfIndexes : IEnumerable<Index>
    {
        private readonly ConcurrentDictionary<string, Index> _indexesByName = new ConcurrentDictionary<string, Index>(IndexNameComparer.Instance);
        private readonly ConcurrentDictionary<string, ConcurrentSet<Index>> _indexesByCollection = 
            new ConcurrentDictionary<string, ConcurrentSet<Index>>(StringComparer.OrdinalIgnoreCase);

        private sealed class IndexNameComparer : IEqualityComparer<string>
        {
            public static readonly IndexNameComparer Instance = new IndexNameComparer();

            public bool Equals(string x, string y)
            {
                if (x.StartsWith("Auto/"))
                {
                    return StringComparer.Ordinal.Equals(x, y);
                }
                return StringComparer.OrdinalIgnoreCase.Equals(x, y);
            }

            public int GetHashCode(string obj)
            {
                if (obj.StartsWith("Auto/"))
                {
                    return StringComparer.Ordinal.GetHashCode(obj);
                }
                return StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
            }
        }

        private readonly AsyncManualResetEvent _registered = new();

        /// <summary>Completes when an index is added or takes the place of another by its name. Taken before the indexes are read, so a registration in between is not missed.</summary>
        public AsyncManualResetEvent.FrozenAwaiter GetRegistrationAwaiter() => _registered.GetFrozenAwaiter();

        private void Registered() => _registered.SetAndResetAtomically();

        private readonly ConcurrentDictionary<string, JoinIndex> _joinsByName = new ConcurrentDictionary<string, JoinIndex>(IndexNameComparer.Instance);

        /// <summary>The join indexes, empty most of the time: a query with joins looks for the ones of its indexes.</summary>
        public IEnumerable<JoinIndex> Joins => _joinsByName.IsEmpty ? Enumerable.Empty<JoinIndex>() : _joinsByName.Values;

        private sealed class OrderedJoinSet(int version, JoinIndex[] joinIndexes)
        {
            public readonly int Version = version;
            public readonly JoinIndex[] Joins = joinIndexes;
        }

        private int _joinsVersion;
        private OrderedJoinSet _orderedJoins;

        /// <summary>
        /// The join indexes in one fixed order: each by name. A query that has several join indexes of the same join tries them in this order.
        /// The array is shared by all queries and rebuilt after the join indexes changed.
        /// </summary>
        public JoinIndex[] OrderedJoins
        {
            get
            {
                int version = Volatile.Read(ref _joinsVersion);
                OrderedJoinSet cached = Volatile.Read(ref _orderedJoins);
                if (cached != null && cached.Version == version)
                    return cached.Joins;

                JoinIndex[] ordered = _joinsByName.Values.ToArray();
                Array.Sort(ordered, static (x, y) => string.CompareOrdinal(x.Name, y.Name));

                // read before the join indexes were listed: a change that happens in between makes the next read build again
                Volatile.Write(ref _orderedJoins, new OrderedJoinSet(version, ordered));
                return ordered;
            }
        }

        private void JoinsChanged() => Interlocked.Increment(ref _joinsVersion);

        public void Add(Index index)
        {
            _indexesByName[index.Name] = index;
            if (index is JoinIndex joinIndex)
            {
                _joinsByName[index.Name] = joinIndex;
                JoinsChanged();
            }

            Registered();

            foreach (var collection in index.Definition.Collections)
            {
                var indexes = _indexesByCollection.GetOrAdd(collection, s => new ConcurrentSet<Index>());
                indexes.Add(index);
            }
        }

        public void ReplaceIndex(string name, Index oldIndex, Index newIndex)
        {
            Debug.Assert(oldIndex == null || IndexNameComparer.Instance.Equals(name, oldIndex.Name));

            _indexesByName.AddOrUpdate(name, newIndex, (key, oldValue) => newIndex);
            if (newIndex is JoinIndex joinIndex)
                _joinsByName[name] = joinIndex;
            else
                _joinsByName.TryRemove(name, out _);

            if (newIndex.Name != name)
            {
                _indexesByName.TryRemove(newIndex.Name, out Index _);
                _joinsByName.TryRemove(newIndex.Name, out _);
            }

            JoinsChanged();

            Registered();

            if (oldIndex == null)
                return;

            foreach (var collection in oldIndex.Definition.Collections)
            {
                if (_indexesByCollection.TryGetValue(collection, out ConcurrentSet<Index> indexes) == false)
                    continue;

                indexes.TryRemove(oldIndex);
            }
        }

        public bool TryRemoveByName(string name, Index existingInstance)
        {
            var result = _indexesByName.TryGetValue(name, out var index);
            if (result == false)
                return false;

            if (index != existingInstance)
                return false;

            if (_indexesByName.TryRemove(name, out index) == false)
                return false; // already removed?

            if (index is JoinIndex)
            {
                _joinsByName.TryRemove(name, out _);
                JoinsChanged();
            }

            if (index != existingInstance)
            {
                // here for correctness only, don't expect this to ever happen
                _indexesByName.TryAdd(name, index);// re-add the removed one
                if (index is JoinIndex readded)
                {
                    _joinsByName[name] = readded;
                    JoinsChanged();
                }

                return false;
            }

            foreach (var collection in index.Definition.Collections)
            {
                if (_indexesByCollection.TryGetValue(collection, out ConcurrentSet<Index> indexes) == false)
                    continue;

                indexes.TryRemove(index);
            }

            return true;
        }


        public bool TryGetByName(string name, out Index index)
        {
            return _indexesByName.TryGetValue(name, out index);
        }

        public IEnumerable<Index> GetForCollection(string collection)
        {

            if (_indexesByCollection.TryGetValue(collection, out ConcurrentSet<Index> indexes) == false)
                return Enumerable.Empty<Index>();

            return indexes;
        }

        public IEnumerator<Index> GetEnumerator()
        {
            // This doesn't happen often enough for this lock to hurt
            return _indexesByName.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// This value should be used sparingly: fetching it locks the cache.
        /// </summary>
        public int Count => _indexesByName.Count;
    }
}
