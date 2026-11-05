import {
    emptyJoinForm,
    formatJoinFallbackReason,
    indexDefinitionFromForm,
    joinDefinitionFromForm,
    joinFormFromDefinition,
    joinOuterIdKey,
    getJoinListStatus,
    getJoinListStorageModeView,
    getJoinStorageModeView,
    utf8ByteLength,
} from "./joinUtils";
import { IndexesStubs } from "test/stubs/IndexesStubs";
import { IndexNodeJoinInfo, IndexSharedInfo } from "components/models/indexes";

function joinIndex(
    ...nodes: { joinIndex?: Partial<IndexNodeJoinInfo>; state?: "Normal" | "Disabled" }[]
): IndexSharedInfo {
    return {
        nodesInfo: nodes.map((node) => ({
            details: {
                state: node.state ?? "Normal",
                joinIndex: node.joinIndex
                    ? ({
                          pendingReason: null,
                          refusedReason: null,
                          generation: 3,
                          isPartial: false,
                          storageMode: "Auto",
                          resolvedStorageMode: null,
                          ...node.joinIndex,
                      } as IndexNodeJoinInfo)
                    : undefined,
            },
        })),
    } as IndexSharedInfo;
}

describe("joinUtils", () => {
    it("round-trips a definition through the form", () => {
        const definition = IndexesStubs.getJoinIndexDefinition();

        const form = joinFormFromDefinition(definition);
        expect(form.outerOnId).toBe(false);
        expect(form.outerField).toBe("Company");
        expect(form.innerField).toBe("");

        expect(joinDefinitionFromForm(form)).toEqual(definition.Join);
    });

    it("keeps the storage mode of the definition and sends Auto for a new form", () => {
        const definition = IndexesStubs.getJoinIndexDefinition();
        definition.Join.StorageMode = "Indirect";

        const form = joinFormFromDefinition(definition);
        expect(form.storageMode).toBe("Indirect");
        expect(joinDefinitionFromForm(form).StorageMode).toBe("Indirect");
        expect(emptyJoinForm().storageMode).toBe("Auto");
    });

    it("maps a join on the id of the outer document", () => {
        const definition = IndexesStubs.getJoinIndexDefinition();
        definition.Join.OuterField = joinOuterIdKey;
        definition.Join.InnerField = "OrderId";

        const form = joinFormFromDefinition(definition);
        expect(form.outerOnId).toBe(true);
        expect(form.outerField).toBe("");
        expect(joinDefinitionFromForm(form)).toEqual(definition.Join);
    });

    it("trims names and sends no inner field for a join on the inner id", () => {
        const form = {
            ...emptyJoinForm(),
            outerIndex: " A ",
            outerField: " F ",
            innerIndex: " B ",
            innerField: "  ",
            fields: [{ field: " X ", kind: "Long" as const }],
        };

        expect(joinDefinitionFromForm(form)).toMatchObject({
            OuterIndex: "A",
            OuterField: "F",
            InnerIndex: "B",
            InnerField: null,
            Fields: [{ Field: "X", Kind: "Long" }],
        });
    });

    it("creates a new join index with the defaults and keeps the settings of an existing one", () => {
        const form = { ...joinFormFromDefinition(IndexesStubs.getJoinIndexDefinition()), name: " New/Name " };

        const created = indexDefinitionFromForm(form);
        expect(created.Name).toBe("New/Name");
        expect(created.Maps).toEqual([]);
        expect(created.Priority).toBe("Normal");

        const original = { ...IndexesStubs.getJoinIndexDefinition(), Priority: "High" as const };
        expect(indexDefinitionFromForm(joinFormFromDefinition(original), original).Priority).toBe("High");
    });

    it("counts the bytes of a term the way the server does", () => {
        expect(utf8ByteLength("abc")).toBe(3);
        expect(utf8ByteLength("\u00e9")).toBe(2);
        expect(utf8ByteLength("\u20ac")).toBe(3);
        expect(utf8ByteLength("\u{1F600}")).toBe(4);
        expect(utf8ByteLength(undefined)).toBe(0);
    });

    describe("formatJoinFallbackReason", () => {
        it("explains the kinds that are not served", () => {
            expect(formatJoinFallbackReason("KindNotServed:Or")).toContain("OR");
            expect(formatJoinFallbackReason("KindNotServed:OrderBy:Title")).toContain("order by Title");
        });

        it("names the undeclared field and the Indirect order", () => {
            expect(formatJoinFallbackReason("KindNotServed:UndeclaredField:Title")).toContain(
                "the field Title is not declared"
            );
            expect(formatJoinFallbackReason("KindNotServed:OrderByIndirect")).toContain("Indirect");
        });

        it("explains the other reasons", () => {
            expect(formatJoinFallbackReason("Building")).toBe("The join index is being built");
        });

        it("shows a reason that it does not know as it is", () => {
            expect(formatJoinFallbackReason("SomethingNew")).toBe("SomethingNew");
            expect(formatJoinFallbackReason("KindNotServed:Brand")).toContain("Brand");
        });
    });

    describe("getJoinListStatus", () => {
        it("is ready when a generation is published and nothing is wrong", () => {
            expect(getJoinListStatus(joinIndex({ joinIndex: {} }))).toEqual({ state: "ready", reason: null });
        });

        it("is building until a generation is published", () => {
            expect(getJoinListStatus(joinIndex({ joinIndex: { generation: 0 } }))?.state).toBe("building");
        });

        it("is partial when the published generation is partial", () => {
            expect(getJoinListStatus(joinIndex({ joinIndex: { isPartial: true } }))?.state).toBe("partial");
        });

        it("carries the pending and the refusal reason", () => {
            expect(getJoinListStatus(joinIndex({ joinIndex: { pendingReason: "waits" } }))).toEqual({
                state: "pending",
                reason: "waits",
            });
            expect(getJoinListStatus(joinIndex({ joinIndex: { refusedReason: "refused" } }))).toEqual({
                state: "refused",
                reason: "refused",
            });
        });

        it("reports the refusal before the pending reason", () => {
            expect(
                getJoinListStatus(joinIndex({ joinIndex: { refusedReason: "refused", pendingReason: "waits" } }))
            ).toEqual({ state: "refused", reason: "refused" });
        });

        it("is disabled when the index is disabled and has no reason", () => {
            expect(getJoinListStatus(joinIndex({ state: "Disabled", joinIndex: {} }))?.state).toBe("disabled");
        });

        it("reports the worst state of the nodes", () => {
            const index = joinIndex({ joinIndex: {} }, { joinIndex: { generation: 0 } }, { joinIndex: { isPartial: true } });
            expect(getJoinListStatus(index)?.state).toBe("building");
        });

        it("is null for a node without statistics and for an index that is not a join index", () => {
            expect(getJoinListStatus(joinIndex({}))).toBeNull();
            expect(getJoinListStatus(joinIndex())).toBeNull();
        });
    });

    describe("storage mode", () => {
        it("shows what the lists were built as, and that Auto chose it", () => {
            expect(getJoinStorageModeView("Auto", ["Indirect"])).toEqual({
                modes: ["Indirect"],
                chosenByAuto: true,
                notBuiltYet: false,
            });
            expect(getJoinStorageModeView("Expanded", ["Expanded"])).toEqual({
                modes: ["Expanded"],
                chosenByAuto: false,
                notBuiltYet: false,
            });
        });

        it("has no mode to show for an Auto join index that is not built, but knows the one of a named mode", () => {
            expect(getJoinStorageModeView("Auto", [null])).toEqual({
                modes: ["Auto"],
                chosenByAuto: false,
                notBuiltYet: true,
            });
            expect(getJoinStorageModeView("Indirect", [null])).toEqual({
                modes: ["Indirect"],
                chosenByAuto: false,
                notBuiltYet: false,
            });
        });

        it("treats a server that reports nothing as Auto that is not built", () => {
            expect(getJoinStorageModeView(undefined, [undefined])).toEqual({
                modes: ["Auto"],
                chosenByAuto: false,
                notBuiltYet: true,
            });
        });

        it("lists each mode that the nodes of a join index were built as, once", () => {
            const view = getJoinListStorageModeView(
                joinIndex(
                    { joinIndex: { resolvedStorageMode: "Indirect" } },
                    { joinIndex: { resolvedStorageMode: "Expanded" } },
                    { joinIndex: { resolvedStorageMode: "Indirect" } },
                    { joinIndex: { resolvedStorageMode: null } }
                )
            );

            expect(view.modes).toEqual(["Indirect", "Expanded"]);
            expect(view.chosenByAuto).toBe(true);
        });

        it("has nothing to show for an index that is not a join index", () => {
            expect(getJoinListStorageModeView(joinIndex({}))).toBeNull();
        });
    });
});
