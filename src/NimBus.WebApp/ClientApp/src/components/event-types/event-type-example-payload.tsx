import { useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "components/ui/button";
import { Textarea } from "components/ui/textarea";
import { CodeBlock } from "components/ui/code-block";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { useToast } from "components/ui/toast";
import * as api from "api-client";

const CopyIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
    />
  </svg>
);

const DownloadIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M4 16v2a2 2 0 002 2h12a2 2 0 002-2v-2M7 10l5 5 5-5M12 15V3"
    />
  </svg>
);

const EditIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
    />
  </svg>
);

const SparklesIcon = () => (
  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={1.6}
      d="M9.5 3l1.4 3.6L14.5 8l-3.6 1.4L9.5 13 8.1 9.4 4.5 8l3.6-1.4L9.5 3zm9 7l.9 2.3 2.3.9-2.3.9-.9 2.3-.9-2.3-2.3-.9 2.3-.9.9-2.3zm-4 6l.7 1.7 1.7.7-1.7.7-.7 1.7-.7-1.7-1.7-.7 1.7-.7.7-1.7z"
    />
  </svg>
);

interface IEventTypeExamplePayloadProps {
  eventType: api.EventType;
}

// What a Send published. Subscribers each record their own event id, but every
// subscriber's Event Request carries the published message id, so filtering
// Messages by it finds the event on each of them.
const PublishedResult = ({ result }: { result: api.ComposeNewEventResult }) => (
  <div
    role="status"
    className="mt-3 rounded-md border border-status-success/40 bg-status-success/5 px-3 py-2 text-sm"
  >
    <p className="font-medium text-status-success">
      Published to {result.publishedBy}
    </p>
    <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
      <dt className="text-muted-foreground">Message ID</dt>
      <dd className="font-mono text-xs break-all">{result.messageId}</dd>
      <dt className="text-muted-foreground">Session ID</dt>
      <dd className="font-mono text-xs break-all">{result.sessionId}</dd>
    </dl>
    <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1">
      <Link
        to={`/Messages?messageId=${encodeURIComponent(result.messageId)}`}
        className="text-primary hover:underline"
      >
        View on each subscriber →
      </Link>
      <Link
        to={`/Messages?sessionId=${encodeURIComponent(result.sessionId)}`}
        className="text-primary hover:underline"
      >
        View session →
      </Link>
    </div>
  </div>
);

const EventTypeExamplePayload: React.FC<IEventTypeExamplePayloadProps> = ({
  eventType,
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const { addToast } = useToast();

  // Shape-only placeholder, used only when the event class authors no Example.
  const generatePlaceholderJson = (): string => {
    const obj: Record<string, string> = {};
    eventType.properties?.forEach((prop) => {
      if (prop.name && prop.name !== "MessageMetadata") {
        obj[prop.name] = prop.typeName || "unknown";
      }
    });
    return JSON.stringify(obj, null, 2);
  };

  // The authored static Example, serialized server-side, is the real example payload.
  const exampleJson = eventType.examplePayload || generatePlaceholderJson();
  const [editedJson, setEditedJson] = useState(exampleJson);
  const [responseMessage, setResponseMessage] = useState<{
    hasError: boolean;
    text: string;
  }>({ hasError: false, text: "" });
  const [isGenerating, setIsGenerating] = useState(false);
  // The last successful Send; stays until the next Send or until Compose reopens.
  const [published, setPublished] = useState<api.ComposeNewEventResult>();

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(exampleJson);
      addToast({
        title: "Copied to clipboard",
        variant: "success",
        duration: 2000,
      });
    } catch {
      addToast({
        title: "Failed to copy",
        variant: "error",
        duration: 2000,
      });
    }
  };

  const handleDownload = () => {
    const blob = new Blob([exampleJson], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${eventType.name || "payload"}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const handleComposeOpen = () => {
    setEditedJson(exampleJson);
    setResponseMessage({ hasError: false, text: "" });
    setPublished(undefined);
    setIsOpen(true);
  };

  const handleGenerateFakeData = async () => {
    if (isGenerating) return;
    if (!eventType.id) {
      addToast({
        title: "Could not generate fake data.",
        variant: "error",
        duration: 4000,
      });
      return;
    }

    setIsGenerating(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const response = await client.getEventtypesEventtypeidFake(eventType.id);
      if (response?.payload) {
        setEditedJson(response.payload);
      } else {
        // 200 with payload === null: the type could not be constructed
        // server-side (abstract / no parameterless ctor). Non-blocking
        // toast; leave the textarea untouched.
        addToast({
          title: "Fake data could not be generated for this event type.",
          variant: "info",
          duration: 4000,
        });
      }
    } catch (err: unknown) {
      // 404 surfaces with status 404; everything else is treated as a
      // generic network/server failure. No fallback to the old client-side
      // heuristic — the spec explicitly disallows that (FR-022).
      const status = api.SwaggerException.isSwaggerException(err)
        ? err.status
        : 0;
      if (status === 404) {
        addToast({
          title: "Event type not found.",
          variant: "error",
          duration: 4000,
        });
      } else {
        addToast({
          title: "Could not generate fake data.",
          variant: "error",
          duration: 4000,
        });
      }
    } finally {
      setIsGenerating(false);
    }
  };

  const handleSendEvent = async () => {
    const client = new api.Client(api.CookieAuth());
    const body = new api.ResubmitWithChanges();
    body.eventTypeId = eventType.id;
    body.eventContent = editedJson;

    try {
      const result = await client.postComposeNewEvent(body);
      setResponseMessage({ hasError: false, text: "" });
      setPublished(result);
    } catch (err: unknown) {
      setPublished(undefined);
      // SwaggerException carries the server's response body on .response,
      // which is far more useful than the bare "Bad Request" reason phrase.
      const swaggerResponse =
        api.SwaggerException.isSwaggerException(err) && err.response
          ? err.response
          : null;
      const errorMessage =
        swaggerResponse ?? (err instanceof Error ? err.message : String(err));
      setResponseMessage({
        hasError: true,
        text: `Failed to compose event: ${errorMessage}`,
      });
      setTimeout(() => {
        setResponseMessage({ hasError: false, text: "" });
      }, 4000);
    }
  };

  return (
    <>
      <CodeBlock
        title="Example Payload"
        subtitle="application/json"
        actions={
          <>
            <Button variant="outline" size="sm" onClick={handleCopy} leftIcon={<CopyIcon />}>
              Copy
            </Button>
            <Button variant="ghost" size="sm" onClick={handleDownload} leftIcon={<DownloadIcon />}>
              Download
            </Button>
            <Button variant="solid" size="sm" onClick={handleComposeOpen} leftIcon={<EditIcon />}>
              Compose Event
            </Button>
          </>
        }
      >
        {exampleJson}
      </CodeBlock>

      <Modal isOpen={isOpen} onClose={() => setIsOpen(false)} size="xl">
        <ModalHeader onClose={() => setIsOpen(false)}>
          Compose New Event
        </ModalHeader>
        <ModalBody className="min-h-[350px]">
          <p className="font-medium mb-2">Event Type: {eventType.name}</p>
          <Textarea
            value={editedJson}
            onChange={(e) => setEditedJson(e.target.value)}
            className="min-h-[300px] font-mono text-sm"
          />
          {responseMessage.text && (
            <p
              className={`mt-2 ${responseMessage.hasError ? "text-status-danger" : "text-status-success"}`}
            >
              {responseMessage.text}
            </p>
          )}
          {published && <PublishedResult result={published} />}
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setIsOpen(false)}>
            Close
          </Button>
          <Button
            variant="ghost"
            onClick={handleGenerateFakeData}
            disabled={isGenerating}
            leftIcon={<SparklesIcon />}
          >
            {isGenerating ? "Generating…" : "Generate fake data"}
          </Button>
          <Button
            variant="solid"
            onClick={handleSendEvent}
            disabled={responseMessage.hasError}
          >
            Send Event
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
};

export default EventTypeExamplePayload;
