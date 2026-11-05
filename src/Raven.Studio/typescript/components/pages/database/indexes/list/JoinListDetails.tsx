import React from "react";
import Badge from "react-bootstrap/Badge";
import { IndexSharedInfo } from "components/models/indexes";
import { RichPanelDetailItem } from "components/common/RichPanel";
import { Icon } from "components/common/Icon";
import { useStudioTranslation } from "hooks/useStudioTranslation";
import {
    getJoinListStatus,
    getJoinListStorageModeView,
    JoinListState,
} from "components/pages/database/indexes/join/joinUtils";

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

// the join index specific details of a row of the indexes list: the state and the storage mode that the lists were built as
export function JoinListDetails({ index }: JoinListDetailsProps) {
    const t = useStudioTranslation("joinIndex");
    const status = getJoinListStatus(index);
    const storageMode = getJoinListStorageModeView(index);

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
                <RichPanelDetailItem title={storageMode.notBuiltYet ? t("list.storageModeNotBuiltHelp") : undefined}>
                    <Icon icon="storage" />
                    {t("list.storageMode")}
                    <div className="value" data-testid="join-list-storage-mode">
                        {storageMode.notBuiltYet
                            ? t("list.storageModes.Auto")
                            : storageMode.modes.map((mode) => t(`list.storageModes.${mode}`)).join(" / ")}
                        {storageMode.chosenByAuto && <span className="text-muted"> {t("list.chosenByAuto")}</span>}
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
