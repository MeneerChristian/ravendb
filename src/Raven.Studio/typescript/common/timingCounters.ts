import genUtils = require("common/generalUtils");
import { formatJoinFallbackReason } from "components/pages/database/indexes/join/joinUtils";

export interface TimingCounter {
    name: string;
    value: string;
}

const joinFallbackPrefix = "JoinFallback:";

// "ScanAllocatedManagedBytes" -> "Scan allocated managed"
function humanize(name: string): string {
    if (name.startsWith(joinFallbackPrefix)) {
        return "Join index not used: " + formatJoinFallbackReason(name.slice(joinFallbackPrefix.length));
    }

    const words = name.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
}

/**
 * The counters reported next to the timings of a part of the query, for example how many entries a join scanned or how much
 * memory it allocated. Names ending with "Bytes" are shown as a size.
 */
export function formatTimingCounters(counters: { [name: string]: number }): TimingCounter[] {
    return Object.entries(counters ?? {}).map(([name, value]) => {
        if (name.endsWith("Bytes")) {
            return { name: humanize(name.slice(0, -"Bytes".length)), value: genUtils.formatBytesToSize(value) };
        }

        return { name: humanize(name), value: genUtils.formatAsCommaSeperatedString(value, 0) };
    });
}

export interface JoinTimingSummary {
    /** The alias of the join in the query. */
    alias: string;
    served: boolean;
    ordered: boolean;
    generation: number | null;
    /** Why the server read the join as usual; null when a join index served it. */
    reason: string | null;
    text: string;
}

type TimingsNode = Raven.Client.Documents.Queries.Timings.QueryTimings;

/**
 * What the join indexes did for the joins of a query, from the counters of the timings: the join is served by a join index
 * ("JoinIndexServed") or is read as usual for a reason ("JoinFallback:&lt;reason&gt;"). A join that no join index was asked about has no summary.
 */
export function summarizeJoinTimings(timings: TimingsNode): JoinTimingSummary[] {
    const summaries: JoinTimingSummary[] = [];

    const visit = (alias: string, node: TimingsNode) => {
        if (!node) {
            return;
        }

        const counters = node.Counters ?? {};
        const fallbackKey = Object.keys(counters).find((x) => x.startsWith(joinFallbackPrefix));
        if (counters["JoinIndexServed"]) {
            const ordered = !!counters["JoinOrdered"];
            const generation = counters["JoinIndexGeneration"] ?? null;
            summaries.push({
                alias,
                served: true,
                ordered,
                generation,
                reason: null,
                text:
                    `The join of '${alias}' was served by a join index` +
                    (ordered ? ", in the order of the join index" : "") +
                    (generation != null ? ` (generation ${generation})` : ""),
            });
        } else if (fallbackKey) {
            const reason = fallbackKey.slice(joinFallbackPrefix.length);
            summaries.push({
                alias,
                served: false,
                ordered: false,
                generation: null,
                reason,
                text: `The join of '${alias}' was read as usual, not from a join index: ${formatJoinFallbackReason(reason)}`,
            });
        }

        Object.entries(node.Timings ?? {}).forEach(([name, child]) => visit(name, child));
    };

    visit("", timings);

    return summaries;
}
