import { useEffect, useRef, useState } from "react";
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
import { emitToast, useToast } from "components/ui/toast";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";

// A composed event is a real event: the producing endpoint publishes it and every
// consumer processes it. Send therefore waits this long, with an Undo in a toast,
// before it publishes anything.
export const UNDO_WINDOW_MS = 10_000;

export type SendOutcome =
  | { kind: "published"; result: api.ComposeNewEventResult }
  | { kind: "failed"; message: string }
  | { kind: "undone" };

// The server's problem detail is far more useful than the bare "Bad Request" reason phrase.
const composeErrorMessage = (err: unknown): string => errorMessage(err);

/**
 * Publishes a composed event once the undo window has passed, unless Undo is
 * clicked in the countdown toast. The timer is not tied to the component, so
 * closing the dialog or leaving the page does not stop the send; reloading the
 * page does, and then nothing is published.
 *
 * `onSettled` returns true when the dialog showed the outcome itself; otherwise
 * the outcome is shown in a toast, wherever the user is by then.
 */
export function publishAfterUndoWindow({
  body,
  eventTypeName,
  consumers,
  onSettled,
}: {
  body: api.ResubmitWithChanges;
  eventTypeName: string;
  consumers: string[];
  onSettled: (outcome: SendOutcome) => boolean;
}): void {
  // Set when the undo window ends: from then on the publish is under way, and Undo,
  // still clickable until the countdown toast is removed, can no longer stop it.
  let publishing = false;
  const timer = setTimeout(async () => {
    publishing = true;
    let outcome: SendOutcome;
    try {
      const result = await new api.Client(api.CookieAuth()).postComposeNewEvent(body);
      outcome = { kind: "published", result };
    } catch (err: unknown) {
      outcome = { kind: "failed", message: composeErrorMessage(err) };
    }

    if (onSettled(outcome)) return;
    if (outcome.kind === "published") {
      emitToast({
        variant: "success",
        title: `Published ${eventTypeName}`,
        description: outcome.result.publishedBy
          ? `Published by ${outcome.result.publishedBy}, message ID ${outcome.result.messageId}.`
          : undefined,
        duration: 8000,
      });
    } else if (outcome.kind === "failed") {
      emitToast({
        variant: "error",
        title: `${eventTypeName} was not published`,
        description: outcome.message,
        // Validation errors are often long; leave them until closed.
        duration: 0,
      });
    }
  }, UNDO_WINDOW_MS);

  emitToast({
    variant: "warning",
    title: `Publishing ${eventTypeName} in ${UNDO_WINDOW_MS / 1000} s`,
    description:
      consumers.length > 0 ? `To ${consumers.join(", ")}.` : undefined,
    duration: UNDO_WINDOW_MS,
    progress: true,
    action: {
      label: "Undo",
      onClick: () => {
        if (publishing) {
          emitToast({
            variant: "warning",
            title: `${eventTypeName} is already being published`,
            description: "The undo window had passed. The result follows when the publish finishes.",
          });
          return;
        }
        clearTimeout(timer);
        emitToast({ variant: "info", title: `${eventTypeName} was not published` });
        onSettled({ kind: "undone" });
      },
    },
  });
}

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
  /** The endpoints that consume the event type, named before a composed event is sent. */
  consumers?: string[];
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
  consumers = [],
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const { addToast } = useToast();
  // A send settles up to 10 s after the click. Its outcome goes into the dialog only
  // when the same opening of the dialog is still on screen; otherwise into a toast.
  const openingRef = useRef(0);
  const shownRef = useRef(false);
  const mountedRef = useRef(false);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      shownRef.current = false;
    };
  }, []);

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
  // The last payload published, which Send needs a change from as well as from the
  // example, unless the data was freshly generated: a stray click cannot publish the
  // example and a second click cannot repeat a send.
  const [baseline, setBaseline] = useState(exampleJson);
  const [generated, setGenerated] = useState(false);
  // True from Send until the send settles. It belongs to the component, not to one
  // opening of the dialog, so reopening Compose cannot queue a second send meanwhile.
  const [sending, setSending] = useState(false);

  const edited = editedJson.trim();
  const changed =
    generated || (edited !== exampleJson.trim() && edited !== baseline.trim());
  const canSend = changed && !sending && editedJson.trim() !== "";

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
    openingRef.current += 1;
    shownRef.current = true;
    setEditedJson(exampleJson);
    setBaseline(exampleJson);
    setGenerated(false);
    setResponseMessage({ hasError: false, text: "" });
    setPublished(undefined);
    setIsOpen(true);
  };

  const handleClose = () => {
    shownRef.current = false;
    setIsOpen(false);
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
        setGenerated(true);
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

  const handleSendEvent = () => {
    if (!canSend) return;
    const body = new api.ResubmitWithChanges();
    body.eventTypeId = eventType.id;
    body.eventContent = editedJson;
    const sentJson = editedJson;
    const opening = openingRef.current;

    setSending(true);
    setResponseMessage({ hasError: false, text: "" });
    setPublished(undefined);
    publishAfterUndoWindow({
      body,
      eventTypeName: eventType.name ?? eventType.id ?? "Event",
      consumers,
      onSettled: (outcome) => {
        // A page left behind has nothing to show the outcome in.
        if (!mountedRef.current) return false;
        setSending(false);
        // Sending the same payload again needs another change first, in whichever
        // opening of the dialog is current when the send settles.
        if (outcome.kind === "published") setBaseline(sentJson);
        // A reopened or closed dialog does not show it either.
        if (opening !== openingRef.current || !shownRef.current) return false;
        if (outcome.kind === "published") {
          setPublished(outcome.result);
          setGenerated(false);
        } else if (outcome.kind === "failed") {
          setResponseMessage({
            hasError: true,
            text: `Failed to compose event: ${outcome.message}`,
          });
        }
        return true;
      },
    });
  };

  const footerNote = sending
    ? "Publishing — undo it from the notification."
    : changed
      ? `Publishes ${UNDO_WINDOW_MS / 1000} s after you click, with an undo.`
      : "Edit the payload or generate fake data to send.";

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

      <Modal isOpen={isOpen} onClose={handleClose} size="2xl" label="Compose New Event">
        <ModalHeader onClose={handleClose}>
          Compose New Event
        </ModalHeader>
        <ModalBody>
          <p className="font-medium mb-2">Event Type: {eventType.name}</p>
          <Textarea
            value={editedJson}
            onChange={(e) => setEditedJson(e.target.value)}
            spellCheck={false}
            className="min-h-[300px] h-[55vh] font-mono text-sm"
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
        <ModalFooter className="flex-wrap">
          <p className="mr-auto min-w-0 flex-1 basis-48 text-xs text-muted-foreground">
            {footerNote}
          </p>
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
            <Button
              variant="ghost"
              onClick={handleGenerateFakeData}
              disabled={isGenerating}
              leftIcon={<SparklesIcon />}
              className="whitespace-nowrap"
            >
              {isGenerating ? "Generating…" : "Generate fake data"}
            </Button>
            <Button variant="outline" onClick={handleClose} className="whitespace-nowrap">
              Close
            </Button>
            <Button
              variant="solid"
              onClick={handleSendEvent}
              disabled={!canSend}
              className="whitespace-nowrap"
            >
              Send Event
            </Button>
          </div>
        </ModalFooter>
      </Modal>
    </>
  );
};

export default EventTypeExamplePayload;
