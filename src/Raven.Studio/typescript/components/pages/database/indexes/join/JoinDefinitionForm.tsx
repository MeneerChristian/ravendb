import React from "react";
import Card from "react-bootstrap/Card";
import Button from "react-bootstrap/Button";
import Col from "react-bootstrap/Col";
import Row from "react-bootstrap/Row";
import { Control, useFieldArray, useWatch } from "react-hook-form";
import { FormGroup, FormInput, FormLabel, FormSelect, FormSwitch } from "components/common/Form";
import { Icon } from "components/common/Icon";
import { useStudioTranslation } from "hooks/useStudioTranslation";
import { joinFieldKinds, joinLimits, joinStorageModes, JoinFormData } from "./joinUtils";

interface JoinDefinitionFormProps {
    control: Control<JoinFormData>;
    indexNames: string[];
    isNew: boolean;
    readOnly: boolean;
}

interface Option {
    value: string;
    label: string;
}

export function JoinDefinitionForm({ control, indexNames, isNew, readOnly }: JoinDefinitionFormProps) {
    const t = useStudioTranslation("joinIndex");
    const outerOnId = useWatch({ control, name: "outerOnId" });
    const innerField = useWatch({ control, name: "innerField" });
    const outerIndex = useWatch({ control, name: "outerIndex" });
    const innerIndex = useWatch({ control, name: "innerIndex" });

    const fields = useFieldArray({ control, name: "fields" });
    const groupings = useFieldArray({ control, name: "groupings" });

    const options = (current: string): Option[] => {
        // an index that the join index already uses stays selectable, even when it is not a named index of the database any more
        const names = current && !indexNames.includes(current) ? [current, ...indexNames] : indexNames;
        return names.map((x) => ({ value: x, label: x }));
    };

    const kindOptions: Option[] = joinFieldKinds.map((x) => ({ value: x, label: x }));

    const storageModeLabels: Record<(typeof joinStorageModes)[number], string> = {
        Auto: t("definition.storageModeAuto"),
        Expanded: t("definition.storageModeExpanded"),
        Indirect: t("definition.storageModeIndirect"),
    };
    const storageModeOptions: Option[] = joinStorageModes.map((x) => ({ value: x, label: storageModeLabels[x] }));

    return (
        <Card data-testid="join-definition">
            <Card.Header>
                <Icon icon="link" /> {t("definition.header")}
            </Card.Header>
            <Card.Body>
                <FormGroup>
                    <FormLabel>{t("definition.name")}</FormLabel>
                    <FormInput
                        type="text"
                        name="name"
                        control={control}
                        placeholder={t("definition.namePlaceholder")}
                        disabled={readOnly || !isNew}
                    />
                </FormGroup>

                <Row>
                    <Col md={6}>
                        <FormGroup>
                            <FormLabel>{t("definition.outerIndex")}</FormLabel>
                            <FormSelect
                                control={control}
                                name="outerIndex"
                                options={options(outerIndex)}
                                placeholder={t("definition.selectIndex")}
                                isDisabled={readOnly}
                                inputId="join-outer-index"
                            />
                            <div className="small text-muted mt-1">{t("definition.outerIndexHelp")}</div>
                        </FormGroup>
                    </Col>
                    <Col md={6}>
                        <FormGroup>
                            <FormLabel>{t("definition.innerIndex")}</FormLabel>
                            <FormSelect
                                control={control}
                                name="innerIndex"
                                options={options(innerIndex)}
                                placeholder={t("definition.selectIndex")}
                                isDisabled={readOnly}
                                inputId="join-inner-index"
                            />
                            <div className="small text-muted mt-1">{t("definition.innerIndexHelp")}</div>
                        </FormGroup>
                    </Col>
                </Row>

                <Row>
                    <Col md={6}>
                        <FormGroup>
                            <FormLabel>{t("definition.outerField")}</FormLabel>
                            <FormInput
                                type="text"
                                name="outerField"
                                control={control}
                                disabled={readOnly || outerOnId}
                                placeholder={outerOnId ? t("definition.joinOnOuterId") : ""}
                            />
                            <div className="small text-muted mt-1">{t("definition.outerFieldHelp")}</div>
                            <FormSwitch control={control} name="outerOnId" disabled={readOnly} className="mt-2">
                                {t("definition.outerOnId")}
                            </FormSwitch>
                        </FormGroup>
                    </Col>
                    <Col md={6}>
                        <FormGroup>
                            <FormLabel>{t("definition.innerField")}</FormLabel>
                            <FormInput
                                type="text"
                                name="innerField"
                                control={control}
                                disabled={readOnly}
                                placeholder={innerField ? "" : t("definition.joinOnInnerId")}
                            />
                            <div className="small text-muted mt-1">{t("definition.innerFieldHelp")}</div>
                        </FormGroup>
                    </Col>
                </Row>

                <Row>
                    <Col md={6}>
                        <FormGroup>
                            <FormLabel>{t("definition.storageMode")}</FormLabel>
                            <FormSelect
                                control={control}
                                name="storageMode"
                                options={storageModeOptions}
                                isDisabled={readOnly}
                                isSearchable={false}
                                inputId="join-storage-mode"
                            />
                            <div className="small text-muted mt-1">{t("definition.storageModeHelp")}</div>
                        </FormGroup>
                    </Col>
                </Row>

                <h5 className="mt-3">{t("definition.fields")}</h5>
                <div className="small text-muted mb-2">
                    {t("definition.fieldsHelp", { max: joinLimits.maxFields })}
                </div>
                {fields.fields.map((item, index) => (
                    <Row key={item.id} className="mb-2 align-items-start" data-testid="join-field-row">
                        <Col md={6}>
                            <FormInput
                                type="text"
                                name={`fields.${index}.field`}
                                control={control}
                                disabled={readOnly}
                                placeholder={t("definition.fieldName")}
                            />
                        </Col>
                        <Col md={4}>
                            <FormSelect
                                control={control}
                                name={`fields.${index}.kind`}
                                options={kindOptions}
                                isDisabled={readOnly}
                                isSearchable={false}
                            />
                        </Col>
                        {!readOnly && (
                            <Col md={2}>
                                <Button
                                    variant="danger"
                                    title={t("definition.remove")}
                                    onClick={() => fields.remove(index)}
                                >
                                    <Icon icon="trash" margin="m-0" />
                                </Button>
                            </Col>
                        )}
                    </Row>
                ))}
                {readOnly && fields.fields.length === 0 && <div className="text-muted">{t("definition.none")}</div>}
                {!readOnly && (
                    <Button
                        variant="secondary"
                        size="sm"
                        disabled={fields.fields.length >= joinLimits.maxFields}
                        onClick={() => fields.append({ field: "", kind: "Exact" })}
                    >
                        <Icon icon="plus" /> {t("definition.addField")}
                    </Button>
                )}

                <h5 className="mt-4">{t("definition.groupings")}</h5>
                <div className="small text-muted mb-2">
                    {t("definition.groupingsHelp", { max: joinLimits.maxGroupings })}
                </div>
                {groupings.fields.map((item, index) => (
                    <Row key={item.id} className="mb-2 align-items-start" data-testid="join-grouping-row">
                        <Col md={5}>
                            <FormInput
                                type="text"
                                name={`groupings.${index}.field`}
                                control={control}
                                disabled={readOnly}
                                placeholder={t("definition.groupingField")}
                            />
                        </Col>
                        <Col md={5}>
                            <FormInput
                                type="text"
                                name={`groupings.${index}.value`}
                                control={control}
                                disabled={readOnly}
                                placeholder={t("definition.groupingValue")}
                            />
                        </Col>
                        {!readOnly && (
                            <Col md={2}>
                                <Button
                                    variant="danger"
                                    title={t("definition.remove")}
                                    onClick={() => groupings.remove(index)}
                                >
                                    <Icon icon="trash" margin="m-0" />
                                </Button>
                            </Col>
                        )}
                    </Row>
                ))}
                {readOnly && groupings.fields.length === 0 && <div className="text-muted">{t("definition.none")}</div>}
                {!readOnly && (
                    <Button
                        variant="secondary"
                        size="sm"
                        disabled={groupings.fields.length >= joinLimits.maxGroupings}
                        onClick={() => groupings.append({ field: "", value: "" })}
                    >
                        <Icon icon="plus" /> {t("definition.addGrouping")}
                    </Button>
                )}
            </Card.Body>
        </Card>
    );
}
