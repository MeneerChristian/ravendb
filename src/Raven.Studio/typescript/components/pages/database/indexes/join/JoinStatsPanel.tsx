import React from "react";
import Card from "react-bootstrap/Card";
import Table from "react-bootstrap/Table";
import Badge from "react-bootstrap/Badge";
import Alert from "react-bootstrap/Alert";
import { Icon } from "components/common/Icon";
import { useStudioTranslation } from "hooks/useStudioTranslation";
import { formatJoinFallbackReason } from "./joinUtils";
import IndexStats = Raven.Client.Documents.Indexes.IndexStats;
import JoinIndexStats = Raven.Client.Documents.Indexes.JoinIndexStats;
import genUtils from "common/generalUtils";

interface JoinStatsPanelProps {
    stats: IndexStats | null;
}

export function JoinStatsPanel({ stats }: JoinStatsPanelProps) {
    const t = useStudioTranslation("joinIndex");
    const joinIndex = stats?.Join;

    if (!joinIndex) {
        return (
            <Card className="mt-3" data-testid="join-stats">
                <Card.Body className="text-muted">{t("stats.unavailable")}</Card.Body>
            </Card>
        );
    }

    const format = (value: number) => genUtils.formatAsCommaSeperatedString(value, 0);
    const formatMs = (value: number) => `${genUtils.formatAsCommaSeperatedString(value, 1)} ms`;

    return (
        <Card className="mt-3" data-testid="join-stats">
            <Card.Header>
                <Icon icon="stats" /> {t("stats.header")}
            </Card.Header>
            <Card.Body>
                {joinIndex.RefusedReason && (
                    <Alert variant="danger" data-testid="join-refused">
                        {t("stats.refused", { reason: joinIndex.RefusedReason })}
                    </Alert>
                )}
                <div className="d-flex flex-wrap gap-4 mb-3">
                    <StatItem label={t("stats.state")} testId="join-state">
                        {joinIndex.PendingReason ? (
                            <Badge bg="warning" text="dark">
                                {t("stats.pending", { reason: joinIndex.PendingReason })}
                            </Badge>
                        ) : (
                            <Badge bg="success">{t("stats.ready")}</Badge>
                        )}
                    </StatItem>
                    <StatItem label={t("stats.size")}>{stats.Memory?.DiskSize?.HumaneSize ?? "-"}</StatItem>
                    <StatItem label={t("stats.served")} testId="join-served">
                        {format(joinIndex.Served)}
                    </StatItem>
                    <StatItem label={t("stats.notServed")} testId="join-not-served">
                        {format(joinIndex.FallbackTotal)}
                    </StatItem>
                    <StatItem label={t("stats.builds")}>{format(joinIndex.Builds)}</StatItem>
                    <StatItem label={t("stats.refreshes")}>{format(joinIndex.Refreshes)}</StatItem>
                    <StatItem label={t("stats.noOpRefreshes")}>{format(joinIndex.NoOpRefreshes)}</StatItem>
                </div>

                <div className="d-flex flex-wrap gap-4 mb-3">
                    <StatItem label={t("stats.idsProcessed")}>{format(joinIndex.IdsProcessed)}</StatItem>
                    <StatItem label={t("stats.listUpdates")}>{format(joinIndex.ListUpdates)}</StatItem>
                    <StatItem label={t("stats.outerBatches")}>{format(joinIndex.OuterBatches)}</StatItem>
                    <StatItem label={t("stats.innerBatches")}>{format(joinIndex.InnerBatches)}</StatItem>
                    <StatItem label={t("stats.queryWaits")}>{format(joinIndex.QueryWaits)}</StatItem>
                    <StatItem label={t("stats.queryWaitTotal")}>{formatMs(joinIndex.QueryWaitTotalInMs)}</StatItem>
                    <StatItem label={t("stats.queryWaitMax")}>{formatMs(joinIndex.QueryWaitMaxInMs)}</StatItem>
                </div>

                {hasResidentCache(joinIndex) && (
                    <div className="mb-3" data-testid="join-resident">
                        <div className="small-label">{t("stats.residentCache")}</div>
                        <div className="d-flex flex-wrap gap-4">
                            <StatItem label={t("stats.residentSize")}>
                                {genUtils.formatBytesToSize(joinIndex.ResidentBytes)}
                            </StatItem>
                            <StatItem label={t("stats.residentKeys")}>{format(joinIndex.ResidentKeys)}</StatItem>
                            <StatItem label={t("stats.residentHits")}>{format(joinIndex.ResidentHits)}</StatItem>
                            <StatItem label={t("stats.residentMisses")}>{format(joinIndex.ResidentMisses)}</StatItem>
                            <StatItem label={t("stats.residentEpochMisses")}>
                                {format(joinIndex.ResidentEpochMisses)}
                            </StatItem>
                            <StatItem label={t("stats.residentEvictions")}>{format(joinIndex.ResidentEvictions)}</StatItem>
                            <StatItem label={t("stats.residentWarmUpKeys")}>
                                {format(joinIndex.ResidentWarmUpKeys)}
                            </StatItem>
                            <StatItem label={t("stats.residentPatchesApplied")}>
                                {format(joinIndex.ResidentPatchesApplied)}
                            </StatItem>
                            <StatItem label={t("stats.residentPartialDrops")} testId="join-partial-drops">
                                {format(joinIndex.ResidentPartialDrops)}
                            </StatItem>
                            <StatItem label={t("stats.residentInvalidations")}>
                                {format(joinIndex.ResidentInvalidations)}
                            </StatItem>
                            <StatItem label={t("stats.residentLowMemoryDrops")}>
                                {format(joinIndex.ResidentLowMemoryDrops)}
                            </StatItem>
                        </div>
                    </div>
                )}

                {joinIndex.LastRefreshBreakdown && (
                    <div className="mb-3">
                        <div className="small-label">{t("stats.lastRefresh")}</div>
                        <code>{joinIndex.LastRefreshBreakdown}</code>
                    </div>
                )}

                <div className="d-flex flex-wrap gap-5">
                    <CountTable
                        title={t("stats.notServed")}
                        help={t("stats.notServedHelp")}
                        empty={t("stats.noNotServed")}
                        counts={joinIndex.Fallbacks}
                        label={formatJoinFallbackReason}
                        columns={[t("stats.reason"), t("stats.count")]}
                        testId="join-fallbacks"
                    />
                    <CountTable
                        title={t("stats.rebuildsByReason")}
                        empty={t("stats.noRebuilds")}
                        counts={joinIndex.RebuildsByReason}
                        label={(reason) => reason}
                        columns={[t("stats.reason"), t("stats.count")]}
                        testId="join-rebuilds"
                    />
                </div>
            </Card.Body>
        </Card>
    );
}

// only an Indirect join index has a resident cache of its key lists; an Expanded one reads zeros
function hasResidentCache(joinIndex: JoinIndexStats) {
    return (
        joinIndex.ResidentBytes > 0 ||
        joinIndex.ResidentKeys > 0 ||
        joinIndex.ResidentHits > 0 ||
        joinIndex.ResidentMisses > 0 ||
        joinIndex.ResidentEpochMisses > 0 ||
        joinIndex.ResidentInvalidations > 0 ||
        joinIndex.ResidentPartialDrops > 0
    );
}

function StatItem(props: { label: string; testId?: string; children: React.ReactNode }) {
    return (
        <div>
            <div className="small-label">{props.label}</div>
            <div className="fs-5" data-testid={props.testId}>
                {props.children}
            </div>
        </div>
    );
}

interface CountTableProps {
    title: string;
    help?: string;
    empty: string;
    counts: Record<string, number>;
    label: (reason: string) => string;
    columns: [string, string];
    testId: string;
}

function CountTable({ title, help, empty, counts, label, columns, testId }: CountTableProps) {
    const rows = Object.entries(counts ?? {}).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));

    return (
        <div data-testid={testId}>
            <div className="small-label" title={help}>
                {title}
            </div>
            {rows.length === 0 ? (
                <div className="text-muted">{empty}</div>
            ) : (
                <Table size="sm" className="mb-0">
                    <thead>
                        <tr>
                            <th>{columns[0]}</th>
                            <th className="text-end">{columns[1]}</th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map(([reason, count]) => (
                            <tr key={reason}>
                                <td title={reason}>{label(reason)}</td>
                                <td className="text-end">{genUtils.formatAsCommaSeperatedString(count, 0)}</td>
                            </tr>
                        ))}
                    </tbody>
                </Table>
            )}
        </div>
    );
}
