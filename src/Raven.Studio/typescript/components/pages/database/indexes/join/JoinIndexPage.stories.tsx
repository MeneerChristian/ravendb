import React from "react";
import { Meta, StoryFn } from "@storybook/react-webpack5";
import { withBootstrap5, withStorybookContexts } from "test/storybookTestUtils";
import { mockStore } from "test/mocks/store/MockStore";
import { mockServices } from "test/mocks/services/MockServices";
import { IndexesStubs } from "test/stubs/IndexesStubs";
import JoinIndexPage from "./JoinIndexPage";

export default {
    title: "Pages/Indexes/Join Index",
    decorators: [withStorybookContexts, withBootstrap5],
} satisfies Meta;

function init(
    options: { auto?: boolean; sharded?: boolean; indirect?: boolean; partial?: boolean; notBuilt?: boolean } = {}
) {
    const { databases } = mockStore;
    const { indexesService } = mockServices;

    if (options.sharded) {
        databases.withActiveDatabase_Sharded();
    } else {
        databases.withActiveDatabase_NonSharded_SingleNode();
    }

    const definition = IndexesStubs.getJoinIndexDefinition();
    const stats = IndexesStubs.getJoinStats(options.auto ? "AutoJoin" : "Join");
    if (options.auto) {
        definition.Name = stats.Name;
        definition.Type = "AutoJoin";
    }

    if (options.partial) {
        stats.Join.PartialRefreshes = 4;
        stats.Join.Generation = 17;
        stats.Join.IsPartial = true;
    }

    if (options.notBuilt) {
        stats.Join.Generation = 0;
        stats.Join.ResolvedStorageMode = null;
        stats.Join.RowsPerKey = null;
        stats.Join.PendingReason = "The inner index 'Companies/ByName' is disabled";
    }

    if (options.indirect) {
        definition.Join.StorageMode = "Indirect";
        stats.Join.StorageMode = "Indirect";
        stats.Join.ResolvedStorageMode = "Indirect";
        stats.Join.RowsPerKey = null;
        stats.Join.RefusedReason = "The outer index 'Orders/ByCompany' has a definition that a join index cannot read";
        stats.Join.ResidentBytes = 3 * 1024 * 1024;
        stats.Join.ResidentKeys = 1200;
        stats.Join.ResidentHits = 5000;
        stats.Join.ResidentMisses = 40;
        stats.Join.ResidentPartialDrops = 2;
    }

    const plainIndexes = IndexesStubs.getSampleStats().filter((x) => x.Type === "Map");
    indexesService.withGetStats([...plainIndexes, stats]);
    indexesService.withGetDefinitions([definition]);
    indexesService.withSaveDefinitions();

    return definition.Name;
}

export const StaticJoin: StoryFn = () => {
    const name = init();
    return <JoinIndexPage pathParams={[name]} />;
};

export const IndirectJoin: StoryFn = () => {
    const name = init({ indirect: true });
    return <JoinIndexPage pathParams={[name]} />;
};

export const PartialJoin: StoryFn = () => {
    const name = init({ partial: true });
    return <JoinIndexPage pathParams={[name]} />;
};

export const NotBuiltJoin: StoryFn = () => {
    const name = init({ notBuilt: true });
    return <JoinIndexPage pathParams={[name]} />;
};

export const AutoJoin: StoryFn = () => {
    const name = init({ auto: true });
    return <JoinIndexPage pathParams={[name]} />;
};

export const NewJoin: StoryFn = () => {
    init();
    return <JoinIndexPage pathParams={[]} />;
};

export const MissingJoin: StoryFn = () => {
    init();
    return <JoinIndexPage pathParams={["Does/Not/Exist"]} />;
};

export const ShardedDatabase: StoryFn = () => {
    init({ sharded: true });
    return <JoinIndexPage pathParams={[]} />;
};
