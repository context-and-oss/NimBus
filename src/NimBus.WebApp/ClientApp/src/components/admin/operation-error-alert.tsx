/** A failed Operations request, shown inside its card (#285). Renders nothing without a message. */
export default function OperationErrorAlert({ message }: { message: string | null }) {
  if (!message) return null;
  return (
    <div
      role="alert"
      className="bg-red-50 border border-red-200 dark:bg-red-950/30 dark:border-red-900/60 rounded-md p-3 text-red-800 dark:text-red-200 text-sm"
    >
      {message}
    </div>
  );
}

/** Says that a Cosmos-only operation can't run on the active storage provider (#285). */
export function CosmosOnlyNotice({ provider }: { provider: string }) {
  return (
    <div className="bg-yellow-50 border border-yellow-200 dark:bg-yellow-950/30 dark:border-yellow-900/60 rounded-md p-3 text-sm">
      This operation is available only with the Cosmos DB storage provider. This WebApp uses {provider}.
    </div>
  );
}
