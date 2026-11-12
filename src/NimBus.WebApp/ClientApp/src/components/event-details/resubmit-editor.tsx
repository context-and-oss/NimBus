import { useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import { Textarea } from "components/ui/textarea";
import {
  describeCommandError,
  isStaleCommand,
} from "functions/operator-command.functions";

export function prettyPayload(raw: string | undefined): string {
  if (!raw) return "";
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

interface ResubmitEditorProps {
  payload?: string;
  eventTypeId?: string;
  eventTypes: api.EventType[];
  onClose: () => void;
  onSubmit: (body: api.ResubmitWithChanges) => Promise<void>;
}

/** Shared editor for the event details page and the Failed drawer. */
export default function ResubmitEditor({
  payload,
  eventTypeId,
  eventTypes,
  onClose,
  onSubmit,
}: ResubmitEditorProps) {
  const original = useMemo(() => prettyPayload(payload), [payload]);
  const edited = useRef(false);
  const [content, setContent] = useState(original);
  useEffect(() => {
    if (!edited.current) setContent(original);
  }, [original]);
  const [typeId, setTypeId] = useState(eventTypeId ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [stale, setStale] = useState(false);
  const submit = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await onSubmit(
        new api.ResubmitWithChanges({
          eventTypeId: typeId,
          eventContent: content,
        }),
      );
    } catch (caught) {
      setError(describeCommandError(caught, "resubmit"));
      setStale(isStaleCommand(caught));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal isOpen onClose={busy ? () => {} : onClose} size="2xl">
      <ModalHeader onClose={busy ? undefined : onClose}>
        Resubmit with changes
      </ModalHeader>
      <ModalBody>
        <div className="mb-4">
          <div className="mb-2 text-sm font-medium">Original event:</div>
          <pre className="max-h-96 overflow-auto rounded-sm bg-muted p-4 font-mono text-sm">
            {original}
          </pre>
        </div>
        <div className="mb-4">
          <label
            htmlFor="resubmit-event-type"
            className="mb-2 block text-sm font-medium"
          >
            Event type:
          </label>
          <Select
            id="resubmit-event-type"
            value={typeId}
            onChange={(e) => setTypeId(e.target.value)}
            disabled={busy}
          >
            {eventTypeId && !eventTypes.some((t) => t.id === eventTypeId) && (
              <option value={eventTypeId}>{eventTypeId}</option>
            )}
            {eventTypes.map((t) => (
              <option key={t.id} value={t.id}>
                {t.id}
              </option>
            ))}
          </Select>
        </div>
        <label
          htmlFor="resubmit-payload"
          className="mb-2 block text-sm font-medium"
        >
          Modified event:
        </label>
        <Textarea
          id="resubmit-payload"
          value={content}
          onChange={(e) => {
            edited.current = true;
            setContent(e.target.value);
          }}
          className="min-h-[300px] font-mono text-sm"
          spellCheck={false}
          disabled={busy}
        />
        {error && (
          <p role="alert" className="mt-3 text-sm text-destructive">
            {error}
          </p>
        )}
      </ModalBody>
      <ModalFooter>
        <Button variant="outline" onClick={onClose} disabled={busy}>
          Close
        </Button>
        <Button onClick={() => void submit()} disabled={busy || stale}>
          {busy ? "Resubmitting..." : "Resubmit"}
        </Button>
      </ModalFooter>
    </Modal>
  );
}
