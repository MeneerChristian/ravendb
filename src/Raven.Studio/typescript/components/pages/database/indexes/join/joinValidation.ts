import * as yup from "yup";
import { yupResolver } from "@hookform/resolvers/yup";
import { joinLimits, joinFieldKinds, joinStorageModes, utf8ByteLength } from "./joinUtils";
import { StudioTranslate } from "common/i18n/resources";

type T = StudioTranslate<"joinIndex">;

export function createJoinSchema(t: T) {
    return yup.object({
        name: yup
            .string()
            .trim()
            .required(t("validation.nameRequired"))
            .max(joinLimits.maxNameLength, t("validation.nameTooLong", { max: joinLimits.maxNameLength })),
        outerIndex: yup.string().trim().required(t("validation.outerIndexRequired")),
        innerIndex: yup
            .string()
            .trim()
            .required(t("validation.innerIndexRequired"))
            .test("different", t("validation.sameIndex"), function (value) {
                return !value || value !== this.parent.outerIndex;
            }),
        outerOnId: yup.boolean().required(),
        outerField: yup.string().when("outerOnId", {
            is: false,
            then: (schema) => schema.trim().required(t("validation.outerFieldRequired")),
        }),
        innerField: yup.string().test("needed", t("validation.innerFieldRequiredForOuterId"), function (value) {
            return !this.parent.outerOnId || (value ?? "").trim() !== "";
        }),
        storageMode: yup
            .string()
            .oneOf(joinStorageModes)
            .required()
            .test("indirect", t("validation.indirectOnOuterId"), function (value) {
                return value !== "Indirect" || !this.parent.outerOnId;
            }),
        fields: yup
            .array()
            .of(
                yup.object({
                    field: yup.string().trim().required(t("validation.fieldRequired")),
                    kind: yup.string().oneOf(joinFieldKinds).required(),
                })
            )
            .max(joinLimits.maxFields, t("validation.tooManyFields", { max: joinLimits.maxFields }))
            .test("unique", t("validation.fieldDuplicated"), (fields) => {
                const names = (fields ?? []).map((x) => x.field?.trim()).filter((x) => x);
                return new Set(names).size === names.length;
            }),
        groupings: yup
            .array()
            .of(
                yup.object({
                    field: yup.string().trim().required(t("validation.groupingFieldRequired")),
                    value: yup
                        .string()
                        .defined()
                        .test(
                            "length",
                            t("validation.groupingValueTooLong", { max: joinLimits.maxTermLengthInBytes }),
                            (value) => utf8ByteLength(value) <= joinLimits.maxTermLengthInBytes
                        ),
                })
            )
            .max(joinLimits.maxGroupings, t("validation.tooManyGroupings", { max: joinLimits.maxGroupings }))
            .test("unique", t("validation.groupingDuplicated"), (groupings) => {
                const keys = (groupings ?? []).map((x) => JSON.stringify([x.field?.trim(), x.value]));
                return new Set(keys).size === keys.length;
            }),
    });
}

export function createJoinResolver(t: T) {
    return yupResolver(createJoinSchema(t));
}
