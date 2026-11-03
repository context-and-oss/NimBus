import {
  EndpointStatusCount,
  EndpointSubscription,
  IEndpointStatusCount,
} from "api-client";

export enum EndpointStatus {
  Healthy = "Healthy",
  Impacted = "Impacted",
  Failed = "Failed",
  Pending = "Pending",
  Disabled = "Disabled",
  MissingSubscription = "Subscription Missing",
  StorageUnavailable = "Storage Unavailable",
}

export const getEndpointStatus = (
  props: IEndpointStatusCount,
): EndpointStatus => {
  if (props.storageStatus === "unavailable") {
    return EndpointStatus.StorageUnavailable;
  } else if (props.subscriptionStatus! === "not-found") {
    return EndpointStatus.MissingSubscription;
  } else if (props.subscriptionStatus! === "disabled") {
    return EndpointStatus.Disabled;
  } else if (props.failedCount! > 0) {
    return EndpointStatus.Failed;
  } else if (props.deferredCount! > 0) {
    return EndpointStatus.Impacted;
  } else if (props.pendingCount! > 0) {
    return EndpointStatus.Pending;
  } else return EndpointStatus.Healthy;
};

export const mapStatusToColor = (status: EndpointStatus): string => {
  switch (status) {
    case EndpointStatus.Healthy:
      return "green";
    case EndpointStatus.Failed:
      return "red";
    case EndpointStatus.Impacted:
      return "yellow";
    case EndpointStatus.Pending:
      return "teal";
    case EndpointStatus.Disabled:
      return "purple";
    case EndpointStatus.MissingSubscription:
      return "gray";
    case EndpointStatus.StorageUnavailable:
      return "gray";
  }
};

export const formatMoment = (moment?: moment.Moment, slim = false): string => {
  const formatString = slim ? "DD/MM/YY HH:mm:ss" : "DD/MM/YYYY HH:mm:ss.SSS";
  if (moment === undefined) return "";
  return typeof moment?.format === "function"
    ? moment.clone().local().format(formatString)
    : "Invalid timestamp";
};

// Duplicate delivery is persisted as an ordinary Skipped outcome. Keep the
// wire status untouched and derive the operator-facing distinction solely
// from the stable Resolver reason token.
export const formatResolutionStatus = (
  resolutionStatus: string | undefined,
  reason: string | undefined,
): string | undefined =>
  resolutionStatus === "Skipped" && reason === "DuplicateDetected"
    ? "Skipped (duplicate)"
    : resolutionStatus;

// Spec 006: parse the blocking event GUID out of a StrictMessageHandler-formatted
// deferral error text ("Session {sessionId} is blocked by {eventId}"). Anchored
// on the `is blocked by` phrase (case-insensitive) so wrapping prefixes/suffixes
// in future error-text rewrites still match as long as the canonical substring
// survives. Returns the GUID exactly as it appears in the input (no normalization)
// so the rendered route matches the destination page's expected casing. Never
// throws; returns undefined on null/undefined input or no match.
const BLOCKED_BY_EVENT_ID_RE =
  /is blocked by\s+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/i;

export const parseBlockedByEventId = (
  errorText: string | null | undefined,
): string | undefined => {
  if (!errorText) return undefined;
  const match = BLOCKED_BY_EVENT_ID_RE.exec(errorText);
  return match ? match[1] : undefined;
};
