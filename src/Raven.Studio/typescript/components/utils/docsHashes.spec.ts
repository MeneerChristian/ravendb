import { getDocsHash } from "components/utils/docsHashes";

describe("getDocsHash", () => {
    it("returns 'MISSING_DOCS' when can't find route", () => {
        expect(getDocsHash("non-existing-route")).toBe("MISSING_DOCS");
    });

    it("returns hash for existing route", () => {
        expect(getDocsHash("databases/documents")).toBe("H6XJDZ");
    });

    it("does not leave the join index page without documentation", () => {
        expect(getDocsHash("databases/indexes/join(/:indexName)")).not.toBe("MISSING_DOCS");
        expect(getDocsHash("databases/indexes/join(/:indexName)")).toBe(
            getDocsHash("databases/indexes/edit(/:indexName)")
        );
    });
});
