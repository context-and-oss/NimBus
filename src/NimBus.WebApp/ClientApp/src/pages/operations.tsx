import Operations from "components/admin/operations";

/**
 * Act on endpoints and stored messages: the endpoint kill switch and every bulk
 * operation, grouped by blast radius. Replaces Admin → Operations (Spec 038 §6).
 * Site Owner only, like every /api/admin/* call it makes.
 */
export default function OperationsPage() {
  return <Operations />;
}
