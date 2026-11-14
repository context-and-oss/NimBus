import { useState } from "react";
import { Button } from "components/ui/button";

type AsyncApiFormat = "yaml" | "json";

// The server also sets Content-Disposition, but because we download via a raw
// fetch + blob (not a plain <a href>), we set the anchor's download name here so
// the saved file is always correct regardless of how the browser reads headers.
const FILENAME: Record<AsyncApiFormat, string> = {
  yaml: "nimbus-asyncapi.yaml",
  json: "nimbus-asyncapi.json",
};

export default function AsyncApiExport() {
  const [downloading, setDownloading] = useState<AsyncApiFormat | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function handleDownload(format: AsyncApiFormat) {
    setDownloading(format);
    setError(null);
    let objectUrl: string | null = null;
    try {
      // Raw fetch rather than the generated client so the exact document bytes
      // and the server's Content-Type/Content-Disposition headers are preserved
      // (the generated client would parse the response into a typed object).
      const response = await fetch(`/api/v1/admin/asyncapi?format=${format}`, {
        method: "GET",
        credentials: "include",
        headers: { "api-version": "2" },
      });
      if (!response.ok) {
        throw new Error(
          response.status === 403
            ? "You do not have permission to export the AsyncAPI document."
            : `Export failed (HTTP ${response.status}).`,
        );
      }

      const blob = await response.blob();
      objectUrl = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = FILENAME[format];
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : "Failed to export the AsyncAPI document.",
      );
    } finally {
      // Always release the blob URL, even on the error path, to avoid a leak.
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      setDownloading(null);
    }
  }

  // Rendered as the Topology page's header action, so this component is just
  // the action row (plus an error, when a download fails).
  return (
    <div className="space-y-3 w-full">
      <div className="flex gap-3">
        <Button
          onClick={() => handleDownload("yaml")}
          isLoading={downloading === "yaml"}
          disabled={downloading !== null}
          colorScheme="primary"
        >
          Download YAML
        </Button>
        <Button
          onClick={() => handleDownload("json")}
          isLoading={downloading === "json"}
          disabled={downloading !== null}
          variant="outline"
          colorScheme="primary"
        >
          Download JSON
        </Button>
      </div>
      {error && (
        <div
          role="alert"
          className="bg-red-50 border border-red-200 dark:bg-red-950/30 dark:border-red-900/60 rounded-md p-4 text-red-800 dark:text-red-200"
        >
          {error}
        </div>
      )}
    </div>
  );
}
