import { parseRql } from "../../src/parser";

describe("JOIN statement", function () {

    const errorsOf = (query: string) => parseRql(query).parser.numberOfSyntaxErrors;

    it("accepts a join with a sub-query", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users' where search(Name, 'chris')) as u on g.OwnerId = id(u)")).toEqual(0);
    });

    it("accepts the rest of the query after the join", function () {
        const query = `from index 'Games' as g
join (from index 'Users' where search(Name, 'chris bob')) as u on g.OwnerId = id(u)
where search(g.Name, 'zelda')
order by score()
select g.Name, u.Name as Owner
include timings()`;

        expect(errorsOf(query)).toEqual(0);
    });

    it("accepts several joins of the main document", function () {
        const query = `from index 'Games' as g
join (from index 'Users' where search(Name, 'a')) as u on g.OwnerId = id(u)
join (from index 'Publishers' where search(Name, 'b')) as p on g.PublisherId = id(p)
where search(g.Name, 'zelda')`;

        expect(errorsOf(query)).toEqual(0);
    });

    it("accepts a join on a joined document (a chain)", function () {
        const query = `from index 'Games' as g
join (from index 'Users' where search(Name, 'a')) as u on g.OwnerId = id(u)
join (from index 'Companies' where search(Name, 'b')) as c on u.CompanyId = id(c)`;

        expect(errorsOf(query)).toEqual(0);
    });

    it("accepts a sub-query that has joins of its own", function () {
        const query = `from index 'Games' as g
join (from index 'Users' as u join (from index 'Companies') as c on u.CompanyId = id(c) where search(u.Name, 'a')) as u on g.OwnerId = id(u)`;

        expect(errorsOf(query)).toEqual(0);
    });

    it("accepts a join on a field of the joined documents", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users') as u on g.Country = u.Country")).toEqual(0);
    });

    it("accepts the field of the joined documents first, and paths of fields", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users') as u on u.Address.City = g.City")).toEqual(0);
    });

    it("accepts a join on a field in a chain", function () {
        const query = `from index 'Games' as g
join (from index 'Users') as u on g.OwnerId = id(u)
join (from index 'Companies') as c on u.CompanyCode = c.Code`;

        expect(errorsOf(query)).toEqual(0);
    });

    it("accepts a join without a where clause in the sub-query", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users') as u on g.OwnerId = id(u)")).toEqual(0);
    });

    it("still accepts fields and aliases that are named like the join keywords", function () {
        expect(errorsOf("from Users where join = 1 and on = 2")).toEqual(0);
    });

    it("reports a join without an alias", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users') on g.OwnerId = id(u)")).toBeGreaterThan(0);
    });

    it("reports a join without an ON clause", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users') as u")).toBeGreaterThan(0);
    });

    it("reports a join sub-query that is not closed", function () {
        expect(errorsOf("from index 'Games' as g join (from index 'Users' as u on g.OwnerId = id(u)")).toBeGreaterThan(0);
    });

    it("does not take the join keyword for the alias of the main document", function () {
        const { parseTree, parser } = parseRql("from index 'Games' join (from index 'Users') as u on OwnerId = id(u)");

        expect(parser.numberOfSyntaxErrors).toEqual(0);
        expect(parseTree.joinStatement()).toHaveLength(1);
    });
});
