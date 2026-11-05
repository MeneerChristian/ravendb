import React from "react";
import { useAsync } from "react-async-hook";
import Badge from "react-bootstrap/Badge";
import { IndexSharedInfo } from "components/models/indexes";
import { RichPanelDetailItem } from "components/common/RichPanel";
import { Icon } from "components/common/Icon";
import { useServices } from "components/hooks/useServices";
import { useStudioTranslation } from "hooks/useStudioTranslation";
import { useAppSelector } from "components/store";
import { databaseSelectors } from "components/common/shell/databaseSliceSelectors";
import { getJoinListStatus, JoinListState } from "components/pages/database/indexes/join/joinUtils";
import JoinStorageMode = Raven.Client.Documents.Indexes.JoinStorageMode;

const stateBadges: Record<JoinListState, { bg: string; text?: string }> = {
    refused: { bg: "danger" },
    pending: { bg: "warning", text: "dark" },
    disabled: { bg: "secondary" },
    building: { bg: "info" },
    partial: { bg: "warning", text: "dark" },
    ready: { bg: "success" },
};

interface JoinListDetailsProps {
    index: IndexSharedInfo;
}

// the join index specific details of a row of the indexes list: the state and the storage mode
export function JoinListDetails({ index }: JoinListDetailsProps) {
    const t = useStudioTranslation("joinIndex");
    const status = getJoinListStatus(index);
    const storageMode = useJoinStorageMode(index.name);

    return (
        <>
            {status && (
                <RichPanelDetailItem>
                    <Icon icon="stats" />
                    {t("list.state")}
                    <div className="value" data-testid="join-list-state">
                        <Badge bg={stateBadges[status.state].bg} text={stateBadges[status.state].text}>
                            {t(`list.states.${status.state}`)}
                        </Badge>
                    </div>
                </RichPanelDetailItem>
            )}
            {storageMode && (
                <RichPanelDetailItem title={t("list.storageModeHelp")}>
                    <Icon icon="storage" />
                    {t("list.storageMode")}
                    <div className="value" data-testid="join-list-storage-mode">
                        {t(`list.storageModes.${storageMode}`)}
                    </div>
                </RichPanelDetailItem>
            )}
        </>
    );
}

// why the join index is refused or pending, as long as it is
export function JoinListReason({ index }: JoinListDetailsProps) {
    const t = useStudioTranslation("joinIndex");
    const status = getJoinListStatus(index);

    if (!status?.reason) {
        return null;
    }

    return (
        <div
            className={status.state === "refused" ? "px-3 pb-2 text-danger" : "px-3 pb-2 text-warning"}
            data-testid="join-list-reason"
        >
            {status.state === "refused"
                ? t("list.refused", { reason: status.reason })
                : t("list.pending", { reason: status.reason })}
        </div>
    );
}

// the list has no definitions, so the storage mode is read once per join index row
function useJoinStorageMode(indexName: string): JoinStorageMode | null {
    const { indexesService } = useServices();
    const activeDatabase = useAppSelector(databaseSelectors.activeDatabase);

    const asyncMode = useAsync(async (): Promise<JoinStorageMode | null> => {
        const definitions = await indexesService.getDefinitions(activeDatabase.name, { indexNames: [indexName] });
        const definition = definitions?.find((x) => x.Name === indexName);

        if (!definition?.Join) {
            return null;
        }

        // the server leaves the storage mode out of the definition of a join index when it is Auto
        return definition.Join.StorageMode ?? "Auto";
    }, [indexName, activeDatabase.name]);

    return asyncMode.result ?? null;
}
