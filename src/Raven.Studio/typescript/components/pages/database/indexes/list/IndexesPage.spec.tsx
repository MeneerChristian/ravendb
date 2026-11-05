import { rtlRender } from "test/rtlTestUtils";
import React from "react";
import { within } from "@testing-library/react";
import { composeStories } from "@storybook/react-webpack5";

import * as stories from "./IndexesPage.stories";

const {
    EmptyView,
    SampleDataCluster,
    FaultyIndexSharded,
    FaultyIndexSingleNode,
    LicenseLimits: CommunityLimits,
    JoinIndexes,
    JoinIndexStates,
} = composeStories(stories);

describe("IndexesPage", function () {
    it("can render empty view", async () => {
        const { screen } = rtlRender(<EmptyView />);

        await screen.findByText(/No indexes have been created for this database/i);
    });

    it("can render", async () => {
        const { screen } = rtlRender(<SampleDataCluster />);

        await screen.findByText("Orders/ByCompany");
        await screen.findByText("ReplacementOf/Orders/ByCompany");
        const deleteButtons = await screen.findAllByTitle(/Delete the index/i);
        expect(deleteButtons.length).toBeGreaterThanOrEqual(1);
    });

    it("can show the type and the actions of join indexes", async () => {
        const { screen } = rtlRender(<JoinIndexes />);

        await screen.findByText("Orders/WithCompany");
        await screen.findByText("Auto/Orders/ByCompany/Join/Companies/ByName/OnCompanyToId");

        expect(screen.getByText("Join")).toBeInTheDocument();
        expect(screen.getByText("Auto Join")).toBeInTheDocument();

        // a join index is answered through a JOIN, it is not queried directly
        expect(screen.queryByText("Query")).not.toBeInTheDocument();

        // the static join index is edited in the join index page, the auto one is only viewed
        const editButtons = screen.getAllByTitle("Edit index");
        expect(editButtons).toHaveLength(1);
        expect(editButtons[0]).toHaveAttribute(
            "href",
            expect.stringContaining("#databases/indexes/join/Orders%2FWithCompany")
        );
        const viewButtons = screen.getAllByTitle("View index");
        expect(viewButtons).toHaveLength(1);
        expect(viewButtons[0]).toHaveAttribute("href", expect.stringContaining("#databases/indexes/join/Auto%2F"));

        // an auto index has no lock mode
        expect(screen.getAllByText("Locked (Error)")).toHaveLength(1);
    });

    describe("join index rows", () => {
        async function findRow(screen: ReturnType<typeof rtlRender>["screen"], name: string) {
            const title = await screen.findByText(name);
            return title.closest(".rich-panel-item") as HTMLElement;
        }

        it("shows the state of a join index that is ready, and says nothing about a reason", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const row = await findRow(screen, "Joins/Ready");
            expect(within(row).getByTestId("join-list-state")).toHaveTextContent("Ready");
            expect(within(row).queryByTestId("join-list-reason")).not.toBeInTheDocument();
        });

        it("shows the refusal reason of a join index that went to the error state", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const row = await findRow(screen, "Joins/Refused");
            expect(within(row).getByTestId("join-list-state")).toHaveTextContent("Refused");
            expect(within(row).getByTestId("join-list-reason")).toHaveTextContent(
                "The join index is in the error state: The outer index 'Orders/ByCompany' has a definition a join index cannot read"
            );
        });

        it("shows the pending reason of a join index that waits for an index it joins", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const row = await findRow(screen, "Joins/Pending");
            expect(within(row).getByTestId("join-list-state")).toHaveTextContent("Pending");
            expect(within(row).getByTestId("join-list-reason")).toHaveTextContent(
                "The join index waits: The inner index 'Companies/ByName' is disabled"
            );
        });

        it("shows that the lists of a join index are being built when no generation is published", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const row = await findRow(screen, "Joins/Building");
            expect(within(row).getByTestId("join-list-state")).toHaveTextContent("Building");
        });

        it("shows that the published generation is partial", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const row = await findRow(screen, "Joins/Partial");
            expect(within(row).getByTestId("join-list-state")).toHaveTextContent("Partial");
        });

        it("shows the storage mode that the lists were built as, and says when Auto chose it", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const ready = await findRow(screen, "Joins/Ready");
            const refused = await findRow(screen, "Joins/Refused");
            const pending = await findRow(screen, "Joins/Pending");
            const partial = await findRow(screen, "Joins/Partial");

            expect(within(ready).getByTestId("join-list-storage-mode")).toHaveTextContent(
                "Indirect (chosen by Auto)"
            );
            expect(within(refused).getByTestId("join-list-storage-mode")).toHaveTextContent(/^Expanded$/);
            expect(within(pending).getByTestId("join-list-storage-mode")).toHaveTextContent(/^Indirect$/);
            expect(within(partial).getByTestId("join-list-storage-mode")).toHaveTextContent(
                "Expanded (chosen by Auto)"
            );
        });

        it("shows Auto with an explanation for a join index that is not built yet, and does not read definitions", async () => {
            const { screen } = rtlRender(<JoinIndexStates />);

            const building = await findRow(screen, "Joins/Building");
            const mode = within(building).getByTestId("join-list-storage-mode");
            expect(mode).toHaveTextContent(/^Auto$/);
            expect(mode.closest("[title]")).toHaveAttribute("title", expect.stringContaining("not built yet"));

            const ready = await findRow(screen, "Joins/Ready");
            expect(within(ready).getByTestId("join-list-storage-mode").closest("[title]")).toBeNull();
        });

        it("shows no join index details on a row of another index type", async () => {
            const { screen } = rtlRender(<SampleDataCluster />);

            const row = await findRow(screen, "Orders/ByCompany");
            expect(within(row).queryByTestId("join-list-state")).not.toBeInTheDocument();
            expect(within(row).queryByTestId("join-list-storage-mode")).not.toBeInTheDocument();
        });
    });

    it("can show search engine - corax", async () => {
        const { screen, getQueriesForElement } = rtlRender(<SampleDataCluster />);

        const orderTotals = await screen.findByText("Orders/ByCompany");
        const indexItem = orderTotals.closest(".rich-panel-item");
        const indexItemSelectors = getQueriesForElement(indexItem);

        expect(await indexItemSelectors.findByText(/Corax/)).toBeInTheDocument();
    });

    it("can open faulty index - sharded", async () => {
        const { screen } = rtlRender(<FaultyIndexSharded />);

        const openFaultyButtons = await screen.findAllByText(/Open faulty index/);
        expect(openFaultyButtons.length).toBeGreaterThan(0);

        expect(screen.queryByText(/Set State/)).not.toBeInTheDocument();
    });

    it("can open faulty index - single node", async () => {
        const { screen } = rtlRender(<FaultyIndexSingleNode />);

        const openFaultyButtons = await screen.findAllByText(/Open faulty index/);
        expect(openFaultyButtons).toHaveLength(2);

        expect(screen.queryByText(/Set State/)).not.toBeInTheDocument();
    });

    it("can show community limits", async () => {
        const { screen } = rtlRender(<CommunityLimits />);

        expect(await screen.findByText(/Cluster is reaching/)).toBeInTheDocument();
        expect(screen.getByText(/Database is reaching/)).toBeInTheDocument();
    });
});
