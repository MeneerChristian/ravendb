import React from "react";
import Button from "react-bootstrap/Button";
import Form from "react-bootstrap/Form";
import { Resolver, SubmitHandler, useForm } from "react-hook-form";
import { useAsyncCallback } from "react-async-hook";
import { Icon } from "components/common/Icon";
import { LoadingView } from "components/common/LoadingView";
import { LoadError } from "components/common/LoadError";
import RichAlert from "components/common/RichAlert";
import ButtonWithSpinner from "components/common/ButtonWithSpinner";
import { useAppUrls } from "components/hooks/useAppUrls";
import { useDirtyFlag } from "components/hooks/useDirtyFlag";
import { useServices } from "components/hooks/useServices";
import { useStudioTranslation } from "hooks/useStudioTranslation";
import { tryHandleSubmit } from "components/utils/common";
import { useAppSelector } from "components/store";
import { databaseSelectors } from "components/common/shell/databaseSliceSelectors";
import IndexUtils from "components/utils/IndexUtils";
import router = require("plugins/router");
import { JoinDefinitionForm } from "./JoinDefinitionForm";
import { JoinStatsPanel } from "./JoinStatsPanel";
import { useJoinIndex } from "./useJoinIndex";
import { createJoinResolver } from "./joinValidation";
import { emptyJoinForm, indexDefinitionFromForm, JoinFormData, joinFormFromDefinition } from "./joinUtils";
import IndexDefinition = Raven.Client.Documents.Indexes.IndexDefinition;
import IndexStats = Raven.Client.Documents.Indexes.IndexStats;

export default function JoinIndexPage({ pathParams }: ReactPathParamsProps) {
    const indexName = pathParams?.[0] || undefined;
    const t = useStudioTranslation("joinIndex");
    const { asyncLoad, indexNames, activeDatabase } = useJoinIndex(indexName);

    if (activeDatabase?.isSharded) {
        return (
            <div className="content-margin">
                <RichAlert variant="warning">{t("shardedNote")}</RichAlert>
            </div>
        );
    }

    if (asyncLoad.loading || !asyncLoad.result) {
        return asyncLoad.error ? (
            <div className="content-margin">
                <LoadError error={t("loadError")} refresh={asyncLoad.execute} />
            </div>
        ) : (
            <LoadingView />
        );
    }

    const result = asyncLoad.result;
    if (result.status === "notFound" || result.status === "notJoin") {
        return (
            <div className="content-margin">
                <RichAlert variant="warning">
                    {t(result.status === "notFound" ? "notFound" : "notJoin", { name: indexName })}
                </RichAlert>
                <BackLink />
            </div>
        );
    }

    return (
        <JoinIndexEditor
            key={indexName ?? "new"}
            indexName={indexName}
            definition={result.status === "loaded" ? result.definition : null}
            stats={result.status === "loaded" ? result.stats : null}
            indexNames={indexNames}
            reload={asyncLoad.execute}
        />
    );
}

function BackLink() {
    const t = useStudioTranslation("joinIndex");
    const { forCurrentDatabase: urls } = useAppUrls();

    return (
        <a href={urls.indexes()()} className="d-inline-block mt-3">
            <Icon icon="arrow-left" /> {t("backToIndexes")}
        </a>
    );
}

interface JoinIndexEditorProps {
    indexName: string | undefined;
    definition: IndexDefinition | null;
    stats: IndexStats | null;
    indexNames: string[];
    reload: () => void;
}

function JoinIndexEditor({ indexName, definition, stats, indexNames, reload }: JoinIndexEditorProps) {
    const t = useStudioTranslation("joinIndex");
    const { indexesService } = useServices();
    const { forCurrentDatabase: urls } = useAppUrls();
    const databaseName = useAppSelector(databaseSelectors.activeDatabaseName);

    const isNew = definition == null;
    const isAuto = definition != null && IndexUtils.isAutoIndex(definition);
    const readOnly = isAuto;

    const { control, handleSubmit, formState, reset } = useForm<JoinFormData>({
        defaultValues: definition ? joinFormFromDefinition(definition) : emptyJoinForm(),
        resolver: createJoinResolver(t) as Resolver<JoinFormData>,
        mode: "all",
    });

    const { setIsDirty } = useDirtyFlag(formState.isDirty);

    const save = useAsyncCallback(async (form: JoinFormData) => {
        await indexesService.saveDefinitions(
            [indexDefinitionFromForm(form, definition ?? undefined)],
            false,
            databaseName
        );
    });

    const onSave: SubmitHandler<JoinFormData> = async (form) => {
        return tryHandleSubmit(async () => {
            await save.execute(form);
            reset(form);
            // the page is left before the dirty flag follows the form
            setIsDirty(false, null);

            if (isNew) {
                router.navigate(urls.editJoin(form.name.trim())());
            } else {
                reload();
            }
        });
    };

    return (
        <div className="content-margin" data-testid="join-page">
            <Form onSubmit={handleSubmit(onSave)} autoComplete="off">
                <div className="d-flex align-items-center gap-3 mb-3">
                    <h2 className="mb-0">
                        <Icon icon="link" /> {t(isNew ? "title.new" : isAuto ? "title.auto" : "title.edit")}
                        {indexName && <small className="ms-3 text-muted">{indexName}</small>}
                    </h2>
                    <div className="ms-auto d-flex gap-2">
                        <Button variant="secondary" href={urls.indexes()()}>
                            <Icon icon="arrow-left" /> {t("backToIndexes")}
                        </Button>
                        {!readOnly && (
                            <ButtonWithSpinner
                                type="submit"
                                variant="primary"
                                icon="save"
                                disabled={!formState.isDirty}
                                isSpinning={formState.isSubmitting}
                            >
                                {t("save")}
                            </ButtonWithSpinner>
                        )}
                    </div>
                </div>

                {isAuto && (
                    <RichAlert variant="info" className="mb-3">
                        {t("autoNote")}
                    </RichAlert>
                )}

                <JoinDefinitionForm control={control} indexNames={indexNames} isNew={isNew} readOnly={readOnly} />
            </Form>

            {!isNew && <JoinStatsPanel stats={stats} />}
        </div>
    );
}
