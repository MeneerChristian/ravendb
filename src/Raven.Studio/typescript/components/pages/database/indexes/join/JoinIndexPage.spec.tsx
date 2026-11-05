import React from "react";
import { composeStories } from "@storybook/react-webpack5";
import { rtlRender } from "test/rtlTestUtils";
import { waitFor } from "@testing-library/react";
import { mockServices } from "test/mocks/services/MockServices";
import * as stories from "./JoinIndexPage.stories";

const { StaticJoin, IndirectJoin, AutoJoin, NewJoin, MissingJoin, ShardedDatabase } = composeStories(stories);

describe("JoinIndexPage", () => {
    it("shows the definition of a static join index", async () => {
        const { screen } = rtlRender(<StaticJoin />);

        expect(await screen.findByText("Join index")).toBeInTheDocument();
        expect(screen.getByDisplayValue("Orders/WithCompany")).toBeInTheDocument();
        expect(screen.getByDisplayValue("Company")).toBeInTheDocument();
        expect(screen.getByText("Orders/ByCompany")).toBeInTheDocument();
        expect(screen.getByText("Companies/ByName")).toBeInTheDocument();

        expect(screen.getAllByTestId("join-field-row")).toHaveLength(3);
        expect(screen.getByDisplayValue("Country")).toBeInTheDocument();
        expect(screen.getAllByTestId("join-grouping-row")).toHaveLength(1);
        expect(screen.getByDisplayValue("IsActive")).toBeInTheDocument();

        expect(screen.getByRole("button", { name: /Save/ })).toBeDisabled();
    });

    it("shows the statistics of the join index", async () => {
        const { screen } = rtlRender(<StaticJoin />);

        const stats = await screen.findByTestId("join-stats");
        expect(stats).toHaveTextContent("Ready");
        expect(screen.getByTestId("join-served")).toHaveTextContent("240");
        expect(screen.getByTestId("join-not-served")).toHaveTextContent("7");
        expect(screen.getByTestId("join-fallbacks")).toHaveTextContent("OR");
        expect(screen.getByTestId("join-fallbacks")).toHaveTextContent("The join index is being built");
        expect(screen.getByTestId("join-rebuilds")).toHaveTextContent("OuterIndexReset");
    });

    it("shows no resident cache and no refusal for a join index that has none", async () => {
        const { screen } = rtlRender(<StaticJoin />);

        await screen.findByTestId("join-stats");
        expect(screen.queryByTestId("join-resident")).not.toBeInTheDocument();
        expect(screen.queryByTestId("join-refused")).not.toBeInTheDocument();
    });

    it("shows the storage mode, the refusal and the resident cache of an Indirect join index", async () => {
        const { screen } = rtlRender(<IndirectJoin />);

        await screen.findByTestId("join-stats");
        expect(screen.getByText("Indirect")).toBeInTheDocument();
        expect(screen.getByTestId("join-refused")).toHaveTextContent("a join index cannot read");

        const resident = screen.getByTestId("join-resident");
        expect(resident).toHaveTextContent("3 MB");
        expect(resident).toHaveTextContent("1,200");
        expect(screen.getByTestId("join-partial-drops")).toHaveTextContent("2");
    });

    it("refuses the Indirect storage mode for a join on the id of the outer document before it is saved", async () => {
        const { screen, fireClick } = rtlRender(<IndirectJoin />);

        await screen.findByTestId("join-stats");
        await fireClick(screen.getByLabelText("Join on the id of the outer document"));
        await fireClick(screen.getByRole("button", { name: /Save/ }));

        expect(
            await screen.findByText("The Indirect storage mode is not for a join on the id of the outer document")
        ).toBeInTheDocument();
        expect(mockServices.indexesService.getMock("saveDefinitions")).not.toHaveBeenCalled();
    });

    it("shows an auto join index without the means to edit it", async () => {
        const { screen } = rtlRender(<AutoJoin />);

        expect(await screen.findByText("Auto join index")).toBeInTheDocument();
        expect(screen.getByText(/can be viewed, but not edited/)).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: /Save/ })).not.toBeInTheDocument();
        expect(screen.queryByText("Add field")).not.toBeInTheDocument();
        expect(screen.getByTestId("join-stats")).toBeInTheDocument();
    });

    it("starts a new join index empty, without statistics", async () => {
        const { screen } = rtlRender(<NewJoin />);

        expect(await screen.findByText("New join index")).toBeInTheDocument();
        expect(screen.queryByTestId("join-stats")).not.toBeInTheDocument();
        expect(screen.queryAllByTestId("join-field-row")).toHaveLength(0);
    });

    it("validates a new join index before it is saved", async () => {
        const { screen, fireClick, fillInput } = rtlRender(<NewJoin />);

        await screen.findByText("New join index");

        await fireClick(screen.getByText("Add field"));
        await fireClick(screen.getByText("Add grouping"));
        expect(screen.getAllByTestId("join-field-row")).toHaveLength(1);
        expect(screen.getAllByTestId("join-grouping-row")).toHaveLength(1);

        await fillInput(screen.getByPlaceholderText("e.g. Orders/WithCompany"), "Orders/WithCompany");
        await fireClick(screen.getByRole("button", { name: /Save/ }));

        expect(await screen.findByText("The outer index is required")).toBeInTheDocument();
        expect(screen.getByText("The inner index is required")).toBeInTheDocument();
        expect(screen.getByText("The outer field is required")).toBeInTheDocument();
        expect(screen.getByText("A field name is required")).toBeInTheDocument();
        expect(screen.getByText("A grouping needs a field")).toBeInTheDocument();
        expect(mockServices.indexesService.getMock("saveDefinitions")).not.toHaveBeenCalled();
    });

    it("saves the changes of a static join index and keeps the rest of its definition", async () => {
        const { screen, fireClick, fillInput } = rtlRender(<StaticJoin />);

        await screen.findByText("Join index");
        await fillInput(screen.getByDisplayValue("Country"), "Region");
        await fireClick(screen.getByRole("button", { name: /Save/ }));

        const save = mockServices.indexesService.getMock("saveDefinitions");
        await waitFor(() => expect(save).toHaveBeenCalledTimes(1));

        const [definitions, isJavaScript] = save.mock.calls[0];
        expect(isJavaScript).toBe(false);
        expect(definitions).toHaveLength(1);
        expect(definitions[0].Name).toBe("Orders/WithCompany");
        expect(definitions[0].Priority).toBe("Normal");
        expect(definitions[0].Join).toEqual({
            OuterIndex: "Orders/ByCompany",
            OuterCollection: null,
            OuterField: "Company",
            InnerIndex: "Companies/ByName",
            InnerCollection: null,
            InnerField: null,
            StorageMode: "Auto",
            Fields: [
                { Field: "Region", Kind: "Exact" },
                { Field: "Rank", Kind: "Long" },
                { Field: "Title", Kind: "Text" },
            ],
            Groupings: [{ Field: "IsActive", Value: "true" }],
        });
    });

    it("says when the join index does not exist", async () => {
        const { screen } = rtlRender(<MissingJoin />);

        expect(await screen.findByText("The join index 'Does/Not/Exist' does not exist.")).toBeInTheDocument();
    });

    it("does not offer join indexes for a sharded database", async () => {
        const { screen } = rtlRender(<ShardedDatabase />);

        expect(await screen.findByText("Join indexes are not supported for sharded databases.")).toBeInTheDocument();
    });
});
