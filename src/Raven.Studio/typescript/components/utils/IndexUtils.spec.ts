import IndexUtils from "components/utils/IndexUtils";
import { IndexSharedInfo } from "components/models/indexes";

type IndexType = Raven.Client.Documents.Indexes.IndexType;

function index(type: IndexType, name: string): IndexSharedInfo {
    return { name, type } as IndexSharedInfo;
}

describe("IndexUtils", () => {
    describe("join indexes", () => {
        it("tells a static join index from an auto join index", () => {
            const joinIndex = index("Join", "Orders/WithCompany");
            const autoJoin = index("AutoJoin", "Auto/Orders/ByCompany/Join/Companies/ByName/OnCompanyToId");

            expect(IndexUtils.isJoinIndex(joinIndex)).toBe(true);
            expect(IndexUtils.isJoinIndex(autoJoin)).toBe(true);
            expect(IndexUtils.isJoinIndex(index("Map", "Orders/ByCompany"))).toBe(false);

            expect(IndexUtils.isAutoIndex(joinIndex)).toBe(false);
            expect(IndexUtils.isStaticIndex(joinIndex)).toBe(true);
            expect(IndexUtils.isAutoIndex(autoJoin)).toBe(true);
            expect(IndexUtils.isStaticIndex(autoJoin)).toBe(false);
        });

        it("goes by the type, not by the name", () => {
            expect(IndexUtils.isAutoIndex(index("Join", "Auto/Looks/Like/Auto"))).toBe(false);
            expect(IndexUtils.isAutoIndex(index("AutoJoin", "Renamed"))).toBe(true);
        });

        it("has a label and an icon", () => {
            expect(IndexUtils.formatType("Join")).toBe("Join");
            expect(IndexUtils.formatType("AutoJoin")).toBe("Auto Join");
            expect(IndexUtils.indexTypeIcon("Join")).not.toBeNull();
            expect(IndexUtils.indexTypeIcon("AutoJoin")).toBe(IndexUtils.indexTypeIcon("Join"));
        });
    });

    it("keeps the other types as they were", () => {
        expect(IndexUtils.formatType("Map")).toBe("Map");
        expect(IndexUtils.formatType("AutoMapReduce")).toBe("Auto Map-Reduce");
        expect(IndexUtils.indexTypeIcon("MapReduce")).toBe("map-reduce");
        expect(IndexUtils.isAutoIndex(index("AutoMap", "Auto/Orders/ByCompany"))).toBe(true);
        expect(IndexUtils.isAutoIndex(index("Map", "Orders/ByCompany"))).toBe(false);
    });
});
