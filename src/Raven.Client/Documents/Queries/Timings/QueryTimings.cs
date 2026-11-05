using System.Collections.Generic;
using Newtonsoft.Json;
using Raven.Client.Documents.Conventions;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Queries.Timings
{
    /// <summary>
    ///     Representation of query timings.
    ///     Consists of total time spent on server-side execution of query, and time spent on each query part.
    /// </summary>
    /// <inheritdoc cref="DocumentationUrls.Session.Querying.QueryTimings"/>
    public sealed class QueryTimings : IFillFromBlittableJson, IDynamicJson
    {
        /// <summary>
        ///     Total time spent on server-side query execution in milliseconds.
        /// </summary>
        public long DurationInMs { get; set; }

        /// <summary>
        ///     Query timings for each part of query.
        /// </summary>
        public IDictionary<string, QueryTimings> Timings { get; set; }

        /// <summary>
        ///     Counters reported by this part of the query, for example how many entries a join scanned or how much memory it allocated.
        /// </summary>
        public IDictionary<string, long> Counters { get; set; }
        
        [JsonIgnore]
        public IDynamicJson QueryPlan { get; set; }

        internal bool ShouldBeIncluded { get; set; }

        public void FillFromBlittableJson(BlittableJsonReaderObject json)
        {
            var timings = DocumentConventions.Default.Serialization.DefaultConverter.FromBlittable<QueryTimings>(json, "query/timings");

            if (json.TryGetMember(nameof(QueryPlan), out var queryPlanObject) && queryPlanObject is BlittableJsonReaderObject queryPlanReader)
            {
                QueryPlan = DocumentConventions.Default.Serialization.DefaultConverter.FromBlittable<QueryInspectionNode>(queryPlanReader, "query/timings/query_plan");
            }
            
            DurationInMs = timings.DurationInMs;
            Timings = timings.Timings;
            Counters = timings.Counters;
        }

        internal QueryTimings Clone()
        {
            SortedDictionary<string, QueryTimings> timings = null;
            if (Timings != null)
            {
                timings = new SortedDictionary<string, QueryTimings>();
                foreach (var kvp in Timings)
                {
                    timings[kvp.Key] = kvp.Value.Clone();
                }
            }

            var queryPlanCloned = default(IDynamicJson);
            if (QueryPlan is QueryInspectionNode qin)
                queryPlanCloned = qin.Clone();
            
            return new QueryTimings
            {
                DurationInMs = DurationInMs,
                Timings = timings,
                Counters = Counters == null ? null : new SortedDictionary<string, long>(Counters),
                QueryPlan = queryPlanCloned
            };
        }

        internal void Update(QueryResult queryResult)
        {
            DurationInMs = 0;
            Timings = null;
            Counters = null;

            if (queryResult.Timings == null)
                return;

            DurationInMs = queryResult.Timings.DurationInMs;
            Timings = queryResult.Timings.Timings;
            Counters = queryResult.Timings.Counters;
            QueryPlan = queryResult.Timings.QueryPlan;
        }

        public DynamicJsonValue ToJson()
        {
            DynamicJsonValue djv = new DynamicJsonValue
            {
                [nameof(DurationInMs)] = DurationInMs,
                [nameof(Timings)] = InnerToJson(Timings)
            };

            if (Counters is { Count: > 0 })
                djv[nameof(Counters)] = CountersToJson(Counters);

            return djv;

            static DynamicJsonValue CountersToJson(IDictionary<string, long> counters)
            {
                var json = new DynamicJsonValue();
                foreach (var kvp in counters)
                    json[kvp.Key] = kvp.Value;

                return json;
            }

            DynamicJsonValue InnerToJson(IDictionary<string, QueryTimings> queryTimings)
            {
                DynamicJsonValue json = new DynamicJsonValue();
                if (queryTimings == null)
                    return null;
                foreach (var kvp in queryTimings)
                {
                    DynamicJsonValue innerJson = new DynamicJsonValue()
                    {
                        [nameof(DurationInMs)] = kvp.Value.DurationInMs
                    };
                    innerJson[nameof(Timings)] = InnerToJson(kvp.Value.Timings);
                    if (kvp.Value.Counters is { Count: > 0 })
                        innerJson[nameof(Counters)] = CountersToJson(kvp.Value.Counters);
                    json[kvp.Key] = innerJson;
                }

                return json;
            }
        }
    }
}
