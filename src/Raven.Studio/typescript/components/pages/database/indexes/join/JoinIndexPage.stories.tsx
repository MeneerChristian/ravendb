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

function init(options: { auto?: boolean; sharded?: boolean } = {}) {
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
