import { Navigate, useLocation } from "react-router-dom";

const VIEW_BY_GROUPING: Record<string, string> = {
  cause: "cause",
  guidance: "guidance",
  origin: "origin",
};

/**
 * The Failed page URL for an old Insights link. The grouping, endpoint and range carry over;
 * an opened group does not, because its key named failed attempts rather than open failures.
 */
export function failedUrlForInsights(search: string): string {
  const old = new URLSearchParams(search);
  const next = new URLSearchParams();
  next.set("view", VIEW_BY_GROUPING[old.get("by") ?? ""] ?? "error");
  const [from, to] = [old.get("from"), old.get("to")];
  if (from && to) {
    next.set("rangeStart", from);
    next.set("rangeEnd", to);
  } else if (old.get("period")) {
    next.set("period", old.get("period")!);
  }
  if (old.get("endpoint")) next.set("endpointId", old.get("endpoint")!);
  return `/Failed?${next.toString()}`;
}

/** Insights was folded into the Failed page; bookmarks and shared links land on its groupings. */
export default function InsightsRedirect() {
  const { search } = useLocation();
  return <Navigate to={failedUrlForInsights(search)} replace />;
}
