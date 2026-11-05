import {
    emptyJoinForm,
    formatJoinFallbackReason,
    indexDefinitionFromForm,
    joinDefinitionFromForm,
    joinFormFromDefinition,
    joinOuterIdKey,
    utf8ByteLength,
} from "./joinUtils";
import { IndexesStubs } from "test/stubs/IndexesStubs";

describe("joinUtils", () => {
    it("round-trips a definition through the form", () => {
        const definition = IndexesStubs.getJoinIndexDefinition();

        const form = joinFormFromDefinition(definition);
        expect(form.outerOnId).toBe(false);
        expect(form.outerField).toBe("Company");
        expect(form.innerField).toBe("");

        expect(joinDefinitionFromForm(form)).toEqual(definition.Join);
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

        it("explains the other reasons", () => {
            expect(formatJoinFallbackReason("Building")).toBe("The join index is being built");
        });

        it("shows a reason that it does not know as it is", () => {
            expect(formatJoinFallbackReason("SomethingNew")).toBe("SomethingNew");
            expect(formatJoinFallbackReason("KindNotServed:Brand")).toContain("Brand");
        });
    });
});
