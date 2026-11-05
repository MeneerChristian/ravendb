import { IndexNodeJoinInfo, IndexSharedInfo } from "components/models/indexes";
import IndexState = Raven.Client.Documents.Indexes.IndexState;
import IndexDefinition = Raven.Client.Documents.Indexes.IndexDefinition;
import JoinDefinition = Raven.Client.Documents.Indexes.JoinDefinition;
import JoinFieldKind = Raven.Client.Documents.Indexes.JoinFieldKind;
import JoinStorageMode = Raven.Client.Documents.Indexes.JoinStorageMode;

// the limits of Raven.Client.Documents.Indexes.JoinLimits
export const joinLimits = {
    maxFields: 8,
    maxGroupings: 4,
    maxNameLength: 206,
    maxTermLengthInBytes: 128,
} as const;

// the value of the outer field that stands for the id of the outer document
export const joinOuterIdKey = "id()";

export const joinFieldKinds: JoinFieldKind[] = ["Exact", "Long", "Text"];

export const joinStorageModes: JoinStorageMode[] = ["Auto", "Expanded", "Indirect"];

export interface JoinFieldFormData {
    field: string;
    kind: JoinFieldKind;
}

export interface JoinGroupingFormData {
    field: string;
    value: string;
}

export interface JoinFormData {
    name: string;
    outerIndex: string;
    outerField: string;
    outerOnId: boolean;
    innerIndex: string;
    innerField: string;
    storageMode: JoinStorageMode;
    fields: JoinFieldFormData[];
    groupings: JoinGroupingFormData[];
}

export function emptyJoinForm(): JoinFormData {
    return {
        name: "",
        outerIndex: "",
        outerField: "",
        outerOnId: false,
        innerIndex: "",
        innerField: "",
        storageMode: "Auto",
        fields: [],
        groupings: [],
    };
}

export function joinFormFromDefinition(definition: IndexDefinition): JoinFormData {
    const joinIndex = definition.Join;
    const outerOnId = joinIndex.OuterField === joinOuterIdKey;

    return {
        name: definition.Name,
        outerIndex: joinIndex.OuterIndex ?? "",
        outerField: outerOnId ? "" : (joinIndex.OuterField ?? ""),
        outerOnId,
        innerIndex: joinIndex.InnerIndex ?? "",
        innerField: joinIndex.InnerField ?? "",
        storageMode: joinIndex.StorageMode ?? "Auto",
        fields: (joinIndex.Fields ?? []).map((x) => ({ field: x.Field, kind: x.Kind })),
        groupings: (joinIndex.Groupings ?? []).map((x) => ({ field: x.Field, value: x.Value })),
    };
}

export function joinDefinitionFromForm(form: JoinFormData): JoinDefinition {
    return {
        OuterIndex: form.outerIndex.trim(),
        OuterCollection: null,
        OuterField: form.outerOnId ? joinOuterIdKey : form.outerField.trim(),
        InnerIndex: form.innerIndex.trim(),
        InnerCollection: null,
        InnerField: form.innerField.trim() === "" ? null : form.innerField.trim(),
        StorageMode: form.storageMode,
        Fields: form.fields.map((x) => ({ Field: x.field.trim(), Kind: x.kind })),
        Groupings: form.groupings.map((x) => ({ Field: x.field.trim(), Value: x.value })),
    };
}

/**
 * The definition to save: a new join index gets the defaults of the server, an existing one keeps everything that the form does not edit
 * (priority, lock mode, state, configuration).
 */
export function indexDefinitionFromForm(form: JoinFormData, original?: IndexDefinition): IndexDefinition {
    const base: IndexDefinition =
        original ??
        ({
            Type: "Join",
            SourceType: "Documents",
            LockMode: "Unlock",
            Priority: "Normal",
            State: "Normal",
            Maps: [],
            Fields: {},
            Configuration: {},
            AdditionalSources: {},
            AdditionalAssemblies: [],
        } as IndexDefinition);

    return { ...base, Name: form.name.trim(), Join: joinDefinitionFromForm(form) };
}

export function utf8ByteLength(value: string): number {
    let bytes = 0;
    for (const char of value ?? "") {
        const codePoint = char.codePointAt(0);
        bytes += codePoint < 0x80 ? 1 : codePoint < 0x800 ? 2 : codePoint < 0x10000 ? 3 : 4;
    }
    return bytes;
}

export const joinFallbackPrefix = "JoinFallback:";

/** "KindNotServed:OrderBy:Title" -> "Order by Title" and so on; the reasons that the Studio does not know are shown as the server names them. */
export function formatJoinFallbackReason(reason: string): string {
    const kindNotServed = "KindNotServed:";
    if (reason.startsWith(kindNotServed)) {
        const detail = reason.slice(kindNotServed.length);
        return `The query is not of a kind that the join index serves (${formatKindDetail(detail)})`;
    }

    switch (reason) {
        case "Disabled":
            return "The join index is disabled";
        case "Building":
            return "The join index is being built";
        case "IndexMissing":
            return "One of the joined indexes is missing";
        case "Faulted":
            return "The join index is faulted";
        case "SnapshotUnavailable":
            return "No snapshot of the join index is available";
        case "EnvironmentChanged":
            return "The environment of an index changed";
        case "BehindTimeout":
            return "The join index was behind the indexes for longer than the query waited";
        case "Ahead":
            return "The join index is ahead of the index of the query";
        case "Failed":
            return "Reading from the join index failed";
        default:
            return reason;
    }
}

function formatKindDetail(detail: string): string {
    const undeclaredField = "UndeclaredField:";
    if (detail.startsWith(undeclaredField)) {
        return `the field ${detail.slice(undeclaredField.length)} is not declared by the join index`;
    }

    if (detail.startsWith("OrderBy:")) {
        return `order by ${detail.slice("OrderBy:".length)}`;
    }

    switch (detail) {
        case "Or":
            return "OR";
        case "Where":
            return "where clause";
        case "Text":
            return "full-text search";
        case "OrderedWhere":
            return "ordered where clause";
        case "OrderByMissingValues":
            return "order by a field with missing values";
        case "OrderByIndirect":
            return "order by a field of the joined documents of an Indirect join index";
        case "OrderByMultiKey":
            return "order by a field of an outer entry that has several joined documents";
        case "ScoresDiffer":
            return "scores differ";
        case "ScoresMultiKey":
            return "score of an outer entry that has several joined documents";
        case "GroupResidual":
            return "too many outer entries to check";
        default:
            return detail;
    }
}

export interface JoinStorageModeView {
    // the modes to show: what the lists were built as, one per mode when the nodes differ; the mode of the definition while nothing is built
    modes: JoinStorageMode[];
    // the lists are built and the definition left the mode to the server
    chosenByAuto: boolean;
    // the definition leaves the mode to the server and the lists are not built yet, so the server has not chosen
    notBuiltYet: boolean;
}

export function getJoinStorageModeView(
    configured: JoinStorageMode | null | undefined,
    resolved: (JoinStorageMode | null | undefined)[]
): JoinStorageModeView {
    const definitionMode = configured ?? "Auto";
    const built = [...new Set(resolved.filter((x) => !!x && x !== "Auto"))];

    return {
        modes: built.length > 0 ? built : [definitionMode],
        chosenByAuto: built.length > 0 && definitionMode === "Auto",
        notBuiltYet: built.length === 0 && definitionMode === "Auto",
    };
}

export function getJoinListStorageModeView(index: IndexSharedInfo): JoinStorageModeView | null {
    const joinIndexes = index.nodesInfo.map((node) => node.details?.joinIndex).filter((x) => !!x);
    if (joinIndexes.length === 0) {
        return null;
    }

    return getJoinStorageModeView(
        joinIndexes[0].storageMode,
        joinIndexes.map((x) => x.resolvedStorageMode)
    );
}

export type JoinListState = "refused" | "pending" | "disabled" | "building" | "partial" | "ready";

export interface JoinListStatus {
    state: JoinListState;
    // the refusal or the pending reason of the node that is in the worst state; null when it has none
    reason: string | null;
}

// the worst state first: that is the one a user has to act on
const joinListStateOrder: JoinListState[] = ["refused", "pending", "disabled", "building", "partial", "ready"];

export function getJoinListStatus(index: IndexSharedInfo): JoinListStatus | null {
    let worst: JoinListStatus = null;

    for (const node of index.nodesInfo) {
        const details = node.details;
        if (!details?.joinIndex) {
            continue;
        }

        const status = getJoinNodeStatus(details.joinIndex, details.state);
        if (!worst || joinListStateOrder.indexOf(status.state) < joinListStateOrder.indexOf(worst.state)) {
            worst = status;
        }
    }

    return worst;
}

function getJoinNodeStatus(joinIndex: IndexNodeJoinInfo, indexState: IndexState): JoinListStatus {
    if (joinIndex.refusedReason) {
        return { state: "refused", reason: joinIndex.refusedReason };
    }
    if (joinIndex.pendingReason) {
        return { state: "pending", reason: joinIndex.pendingReason };
    }
    if (indexState === "Disabled") {
        return { state: "disabled", reason: null };
    }
    if (joinIndex.generation <= 0) {
        return { state: "building", reason: null };
    }
    if (joinIndex.isPartial) {
        return { state: "partial", reason: null };
    }

    return { state: "ready", reason: null };
}
