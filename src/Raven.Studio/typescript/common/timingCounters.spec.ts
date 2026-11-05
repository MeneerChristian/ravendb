import { formatTimingCounters, summarizeJoinTimings } from "./timingCounters";

describe("formatTimingCounters", () => {
    it("returns nothing when there are no counters", () => {
        expect(formatTimingCounters(null)).toEqual([]);
        expect(formatTimingCounters(undefined)).toEqual([]);
        expect(formatTimingCounters({})).toEqual([]);
    });

    it("shows counters that are not sizes as numbers with a readable name", () => {
        const [matches] = formatTimingCounters({ SourceMatches: 1234567 });

        expect(matches.name).toBe("Source matches");
        expect(matches.value).toBe("1,234,567");
    });

    it("shows counters ending with Bytes as a size", () => {
        const [scan, total] = formatTimingCounters({ ScanAllocatedManagedBytes: 3 * 1024 * 1024, TotalAllocatedManagedBytes: 2048 });

        expect(scan.name).toBe("Scan allocated managed");
        expect(scan.value).toMatch(/^3(\.0+)? MB$/);
        expect(total.name).toBe("Total allocated managed");
        expect(total.value).toMatch(/^2(\.0+)? KB$/);
    });

    it("keeps the order of the counters", () => {
        const names = formatTimingCounters({ SourceMatches: 1, ScanAllocatedManagedBytes: 2, OuterMatchKeys: 3 }).map((x) => x.name);

        expect(names).toEqual(["Source matches", "Scan allocated managed", "Outer match keys"]);
    });
});

describe("join index counters", () => {
    it("names a fallback counter by its reason", () => {
        const [counter] = formatTimingCounters({ "JoinFallback:KindNotServed:OrderBy:Title": 1 });

        expect(counter.name).toContain("Join index not used");
        expect(counter.name).toContain("order by Title");
        expect(counter.value).toBe("1");
    });

    it("shows the other join index counters as numbers", () => {
        const names = formatTimingCounters({ JoinIndexServed: 1, JoinIndexGeneration: 4 }).map((x) => x.name);

        expect(names).toEqual(["Join index served", "Join index generation"]);
    });
});

describe("summarizeJoinTimings", () => {
    type Timings = Raven.Client.Documents.Queries.Timings.QueryTimings;

    const join = (aliases: Record<string, Record<string, number>>): Timings =>
        ({
            DurationInMs: 1,
            Timings: {
                Join: {
                    DurationInMs: 1,
                    Counters: { OuterQueryAllocatedManagedBytes: 10 },
                    Timings: Object.fromEntries(
                        Object.entries(aliases).map(([alias, Counters]) => [alias, { DurationInMs: 0, Timings: null as Timings, Counters }])
                    ),
                },
            },
        }) as unknown as Timings;

    it("has nothing for a query without join index counters", () => {
        expect(summarizeJoinTimings(null)).toEqual([]);
        expect(summarizeJoinTimings(join({ c: { OuterMatchKeys: 3 } }))).toEqual([]);
    });

    it("tells that a join index served a join", () => {
        const [summary] = summarizeJoinTimings(join({ c: { JoinIndexServed: 1, JoinIndexGeneration: 7 } }));

        expect(summary).toMatchObject({ alias: "c", served: true, ordered: false, generation: 7, reason: null });
        expect(summary.text).toContain("served by a join index");
        expect(summary.text).toContain("generation 7");
    });

    it("tells that a join index served a join in its own order", () => {
        const [summary] = summarizeJoinTimings(join({ c: { JoinIndexServed: 1, JoinOrdered: 1 } }));

        expect(summary.ordered).toBe(true);
        expect(summary.text).toContain("in the order of the join index");
    });

    it("gives the reason when the join was read as usual", () => {
        const [summary] = summarizeJoinTimings(join({ c: { "JoinFallback:Building": 1, SourceMatches: 4 } }));

        expect(summary).toMatchObject({ alias: "c", served: false, reason: "Building" });
        expect(summary.text).toContain("The join index is being built");
    });

    it("summarizes every join of the query", () => {
        const summaries = summarizeJoinTimings(
            join({ c: { JoinIndexServed: 1 }, u: { "JoinFallback:KindNotServed:Or": 1 } })
        );

        expect(summaries.map((x) => [x.alias, x.served])).toEqual([
            ["c", true],
            ["u", false],
        ]);
    });
});
