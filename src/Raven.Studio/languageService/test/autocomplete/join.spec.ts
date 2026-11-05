import { autocomplete } from "../autocompleteUtils";

describe("can complete join", function () {

    it("suggests join after the from clause", async function () {
        const suggestions = await autocomplete("from index 'Games' as g |");

        expect(suggestions.map(x => x.caption))
            .toIncludeAllMembers(["join", "where"]);
    });

    it("suggests join after another join", async function () {
        const suggestions = await autocomplete("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) |");

        expect(suggestions.map(x => x.caption))
            .toIncludeAllMembers(["join", "where"]);
    });

    it("suggests on after the alias of a join", async function () {
        const suggestions = await autocomplete("from index 'Games' as g join (from index 'Users') as u |");

        expect(suggestions.map(x => x.caption))
            .toContain("on");
    });

    it("suggests the clauses of a query after the join", async function () {
        const suggestions = await autocomplete("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u) where search(g.Name, 'zelda') |");

        const captions = suggestions.map(x => x.caption);
        expect(captions).toIncludeAllMembers(["order by", "select"]);
        expect(captions).not.toContain("join");
    });
});
