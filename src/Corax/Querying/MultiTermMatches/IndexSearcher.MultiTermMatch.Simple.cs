using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Corax.Mappings;
using Corax.Querying.Matches;
using Corax.Querying.Matches.Meta;
using Corax.Querying.Matches.TermProviders;
using Corax.Utils;
using Voron;
using Voron.Data.Lookups;
using static Voron.Data.CompactTrees.CompactTree;

namespace Corax.Querying;

public partial class IndexSearcher
{
    /// <summary>
    /// Test API only
    /// </summary>
    public MultiTermMatch StartWithQuery(string field, string startWith, bool hasBoost = false, bool forward = true) => StartWithQuery(FieldMetadataBuilder(field, hasBoost: hasBoost), EncodeAndApplyAnalyzer(default, startWith), forward);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch StartWithQuery(in FieldMetadata field, string startWith, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<StartsWithTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, startWith, streamingEnabled, token)
            : MultiTermMatchBuilder<StartsWithTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, startWith, streamingEnabled, token);
    }

    public MultiTermMatch StartWithQuery(in FieldMetadata field, Slice startWith, bool forward = true, bool streamingEnabled = false, bool validatePostfixLen = false,in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<StartsWithTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, startWith, streamingEnabled, validatePostfixLen, token)
            : MultiTermMatchBuilder<StartsWithTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, startWith, streamingEnabled, validatePostfixLen, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch EndsWithQuery(in FieldMetadata field, string endsWith, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<EndsWithTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, endsWith, streamingEnabled, token)
            : MultiTermMatchBuilder<EndsWithTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, endsWith, streamingEnabled, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch EndsWithQuery(in FieldMetadata field, Slice endsWith, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<EndsWithTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, endsWith, streamingEnabled, validatePostfixLen: false, token: token)
            : MultiTermMatchBuilder<EndsWithTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, endsWith, streamingEnabled, validatePostfixLen: false, token: token);
    }
    
    public MultiTermMatch ContainsQuery(in FieldMetadata field, string containsTerm, bool forward = true, in CancellationToken token = default) => ContainsQuery(field, (Slice)EncodeAndApplyAnalyzer(field, containsTerm), forward, token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch ContainsQuery(in FieldMetadata field, Slice containsTerm, bool forward = true, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<ContainsTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, containsTerm, token: token)
            : MultiTermMatchBuilder<ContainsTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, containsTerm, token: token);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch ExistsQuery(in FieldMetadata field, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<ExistsTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, default(Slice), streamingEnabled: streamingEnabled, token: token, scoreAsConstant: true)
            : MultiTermMatchBuilder<ExistsTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, default(Slice), streamingEnabled: streamingEnabled, token: token, scoreAsConstant: true);
    }


    /// <summary>
    /// Whether <see cref="ExistsQuery"/> of the field matches every entry of the index, found from the number of entries under the terms of the field instead of by reading them.
    /// False when that cannot be told: an entry may have several terms in the field (the counts would add up an entry more than once) or the field has more than
    /// <paramref name="maxTerms"/> terms. <paramref name="score"/> is what the match adds to the score of every entry, as <see cref="MultiTermMatch.Score"/> would.
    /// </summary>
    public unsafe bool TryGetExistsOfEveryEntry(in FieldMetadata field, long maxTerms, out float score)
    {
        // the scoring mode of the match is the one MultiTermMatch picks for a boosting field with a provider that fills
        score = field.HasBoost ? Bm25Relevance.ConstantScoreValue : 0f;
        if (HasMultipleTermsInField(field))
            return false;

        long entries = 0;
        if (TryGetPostingListForNull(field, out var nullPostingListId))
            entries += GetPostingList(nullPostingListId)?.State.NumberOfEntries ?? 0;

        var terms = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (terms != null)
        {
            if (terms.NumberOfEntries > maxTerms)
                return false;

            var iterator = terms.Iterate<Lookup<CompactKeyLookup>.ForwardIterator>();
            iterator.Reset();
            long* containers = stackalloc long[64];
            int read;
            while ((read = iterator.Fill(new System.Span<long>(containers, 64))) > 0)
            {
                for (int i = 0; i < read; i++)
                    entries += NumberOfDocumentsUnderSpecificTerm(containers[i]);
            }
        }

        return entries == NumberOfEntries;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MultiTermMatch PatternQuery(in FieldMetadata field, Slice pattern, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        return forward
            ? MultiTermMatchBuilder<PatternTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(field, pattern, streamingEnabled, validatePostfixLen: false, token: token)
            : MultiTermMatchBuilder<PatternTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(field, pattern, streamingEnabled, validatePostfixLen: false, token: token);
    }

    public MultiTermMatch RegexQuery(in FieldMetadata field, Regex regex, bool forward = true, bool streamingEnabled = false, in CancellationToken token = default)
    {
        var terms = _fieldsTree?.CompactTreeFor(field.FieldName);
        if (terms == null)
            return MultiTermMatch.CreateEmpty(_transaction.Allocator);

        return forward
            ? MultiTermMatch.Create(
                new MultiTermMatch<RegexTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>>(this,
                    field, _transaction.Allocator,
                    new RegexTermProvider<Lookup<CompactKeyLookup>.ForwardIterator>(this, terms, field, regex), streamingEnabled, token: token
                ))
            : MultiTermMatch.Create(
                new MultiTermMatch<RegexTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>>(this,
                    field, _transaction.Allocator,
                    new RegexTermProvider<Lookup<CompactKeyLookup>.BackwardIterator>(this, terms, field, regex), streamingEnabled, token: token
                ));
    }
}
