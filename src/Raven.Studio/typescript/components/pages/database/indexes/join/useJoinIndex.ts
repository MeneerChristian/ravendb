import { useAsync } from "react-async-hook";
import { useServices } from "components/hooks/useServices";
import { useAppSelector } from "components/store";
import { databaseSelectors } from "components/common/shell/databaseSliceSelectors";
import DatabaseUtils from "components/utils/DatabaseUtils";
import IndexUtils from "components/utils/IndexUtils";
import IndexDefinition = Raven.Client.Documents.Indexes.IndexDefinition;
import IndexStats = Raven.Client.Documents.Indexes.IndexStats;

export type JoinLoadResult =
    | { status: "new" }
    | { status: "notFound" }
    | { status: "notJoin" }
    | { status: "loaded"; definition: IndexDefinition; stats: IndexStats | null };

export function useJoinIndex(indexName: string | undefined) {
    const { indexesService } = useServices();
    const activeDatabase = useAppSelector(databaseSelectors.activeDatabase);

    const asyncLoad = useAsync(async (): Promise<JoinLoadResult> => {
        if (!indexName) {
            return { status: "new" };
        }

        const definitions = await indexesService.getDefinitions(activeDatabase.name, { indexNames: [indexName] });
        const definition = definitions?.find((x) => x.Name === indexName);
        if (!definition) {
            return { status: "notFound" };
        }
        if (!IndexUtils.isJoinIndex(definition)) {
            return { status: "notJoin" };
        }

        const location = DatabaseUtils.getLocations(activeDatabase)[0];
        const stats = await indexesService
            .getStats(activeDatabase.name, location)
            .then((all) => all?.find((x) => x.Name === indexName) ?? null)
            // the definition is useful without the statistics
            .catch((): IndexStats | null => null);

        return { status: "loaded", definition, stats };
    }, [indexName, activeDatabase.name]);

    const asyncIndexNames = useAsync(async (): Promise<string[]> => {
        const location = DatabaseUtils.getLocations(activeDatabase)[0];
        const all = await indexesService.getStats(activeDatabase.name, location);

        // a join index joins named indexes: neither a join index nor an auto index can be a side
        return (all ?? [])
            .filter((x) => !IndexUtils.isJoinIndex(x.Type) && !x.Name.startsWith(IndexUtils.AutoIndexPrefix))
            .map((x) => x.Name)
            .sort((a, b) => a.localeCompare(b));
    }, [activeDatabase.name]);

    return { asyncLoad, indexNames: asyncIndexNames.result ?? [], activeDatabase };
}
