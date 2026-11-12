import { useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import ResubmitEditor from "./resubmit-editor";
import { getResubmitPayload } from "functions/resubmit.functions";
import { isFailedStatus } from "functions/event-details.functions";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import {
  describeCommandError,
  describeDeleteError,
  isStaleCommand,
} from "functions/operator-command.functions";

export interface EventActionsProps {
  eventDetails: api.Event | undefined;
  /** Event history, used to prefill Resubmit with changes with the request payload. */
  messages?: api.Message[];
  eventTypes: api.EventType[];
  skipEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEventWithChanges: (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) => Promise<void>;
  deleteEvent?: () => Promise<void>;
  reprocessDeferred?: () => Promise<void>;
  /**
   * Message id of the attempt an accepted resubmit or skip acted on. While it
   * is still the event's latest message, every action stays closed: the server
   * would refuse a second command on that attempt, and Delete (by event and
   * session) could remove a newer attempt the page has not shown yet.
   */
  commandAcceptedFor?: string;
}

interface ButtonState {
  isDisabled: boolean;
  text: string;
}

const RESUBMIT_IDLE: ButtonState = { isDisabled: false, text: "Resubmit" };
const RESUBMIT_WITH_CHANGES_IDLE: ButtonState = {
  isDisabled: false,
  text: "Resubmit with changes",
};
const SKIP_IDLE: ButtonState = { isDisabled: false, text: "Skip" };
const DELETE_IDLE: ButtonState = { isDisabled: false, text: "Delete" };
const REPROCESS_IDLE: ButtonState = { isDisabled: false, text: "Reprocess" };

const ResubmitIcon = () => (
  <svg aria-hidden="true" width="14" height="14" viewBox="0 0 16 16" fill="none">
    <path
      d="M13 8a5 5 0 1 1-1.5-3.5M13 2.5v3h-3"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  </svg>
);

/**
 * The operator commands for one event, shown in the event details page header:
 * Resubmit, Resubmit with changes, Skip and Delete for a failed event, and
 * Reprocess for a deferred one. Pending handoffs are settled from the handoff
 * card instead, so they get no actions here.
 */
export default function EventActions(props: EventActionsProps) {
  const [resubmitButton, setResubmitButton] = useState(RESUBMIT_IDLE);
  const [resubmitWithChangesButton, setResubmitWithChangesButton] = useState(
    RESUBMIT_WITH_CHANGES_IDLE,
  );
  const [skipButton, setSkipButton] = useState(SKIP_IDLE);
  const [deleteButton, setDeleteButton] = useState(DELETE_IDLE);
  const [reprocessButton, setReprocessButton] = useState(REPROCESS_IDLE);
  const [editorOpen, setEditorOpen] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

  const event = props.eventDetails;
  const status = event?.resolutionStatus?.toLowerCase();

  // The event's next attempt (it failed again, or someone else acted on it) has
  // its own message id, and the commands apply to it afresh.
  useEffect(() => {
    setResubmitWithChangesButton(RESUBMIT_WITH_CHANGES_IDLE);
    setResubmitButton(RESUBMIT_IDLE);
    setSkipButton(SKIP_IDLE);
  }, [event?.lastMessageId]);

  // The original event payload: the latest payload-carrying request message in
  // the history, falling back to the event's own payload. For a failed hand-off
  // the event's own content is the empty terminal ErrorResponse.
  const resubmitPayload = useMemo(
    () =>
      getResubmitPayload(props.messages) ??
      event?.messageContent?.eventContent?.eventJson,
    [props.messages, event],
  );

  const attemptActedOn =
    !!props.commandAcceptedFor &&
    props.commandAcceptedFor === event?.lastMessageId;

  const showFailedActions = isFailedStatus(event?.resolutionStatus);
  // A Failed event blocks its session, and the server refuses to delete it
  // (#288): Skip or Resubmit is the way out for it.
  const canDelete = showFailedActions && status !== "failed";
  const showReprocess = status === "deferred" && !!props.reprocessDeferred;

  if (!showFailedActions && !showReprocess) return null;

  const runCommand = async (
    kind: "resubmit" | "skip",
    setButton: (state: ButtonState) => void,
    busy: string,
    done: string,
    idleLabel: string,
    send: () => Promise<void>,
  ) => {
    setButton({ text: busy, isDisabled: true });
    try {
      await send();
      setButton({ text: done, isDisabled: true });
      notifySuccess(`${done}.`);
    } catch (caught) {
      notifyError(describeCommandError(caught, kind));
      setButton(
        isStaleCommand(caught)
          ? { text: "Changed - refresh", isDisabled: true }
          : { text: `${idleLabel} failed`, isDisabled: false },
      );
    }
  };

  const resubmitClick = () =>
    runCommand(
      "resubmit",
      setResubmitButton,
      "Resubmitting...",
      "Resubmitted",
      "Resubmit",
      () => props.resubmitEvent(event?.eventId!, event?.lastMessageId!),
    );

  const skipClick = () =>
    runCommand("skip", setSkipButton, "Skipping...", "Skipped", "Skip", () =>
      props.skipEvent(event?.eventId!, event?.lastMessageId!),
    );

  const resubmitWithChangesClick = async (body: api.ResubmitWithChanges) => {
    setResubmitWithChangesButton({ text: "Resubmitting...", isDisabled: true });
    try {
      await props.resubmitEventWithChanges(
        event?.eventId!,
        event?.lastMessageId!,
        body,
      );
      setResubmitWithChangesButton({ text: "Resubmitted", isDisabled: true });
      notifySuccess("Resubmitted.");
      setEditorOpen(false);
    } catch (caught) {
      notifyError(describeCommandError(caught, "resubmit"));
      setResubmitWithChangesButton(
        isStaleCommand(caught)
          ? { text: "Changed - refresh", isDisabled: true }
          : { text: "Resubmit failed", isDisabled: false },
      );
      throw caught;
    }
  };

  const deleteClick = async () => {
    setShowDeleteConfirm(false);
    setDeleteButton({ text: "Deleting...", isDisabled: true });
    try {
      await props.deleteEvent?.();
      setDeleteButton({ text: "Deleted", isDisabled: true });
    } catch (caught) {
      notifyError(describeDeleteError(caught));
      setDeleteButton({ text: "Delete failed", isDisabled: false });
    }
  };

  const reprocessClick = async () => {
    setReprocessButton({ text: "Triggering...", isDisabled: true });
    try {
      await props.reprocessDeferred?.();
      setReprocessButton({ text: "Triggered", isDisabled: true });
    } catch {
      setReprocessButton({ text: "Failed", isDisabled: false });
    }
  };

  return (
    <div className="flex flex-wrap items-center gap-2">
      {showReprocess && (
        <Button
          disabled={reprocessButton.isDisabled}
          onClick={reprocessClick}
          leftIcon={<ResubmitIcon />}
        >
          {reprocessButton.text}
        </Button>
      )}
      {showFailedActions && (
        <>
          {props.deleteEvent && canDelete && (
            <Button
              variant="ghost"
              colorScheme="red"
              disabled={deleteButton.isDisabled || attemptActedOn}
              onClick={() => setShowDeleteConfirm(true)}
            >
              {deleteButton.text}
            </Button>
          )}
          <Button
            variant="outline"
            colorScheme="red"
            disabled={skipButton.isDisabled || attemptActedOn}
            onClick={skipClick}
          >
            {skipButton.text}
          </Button>
          <Button
            variant="outline"
            colorScheme="gray"
            disabled={resubmitWithChangesButton.isDisabled || attemptActedOn}
            onClick={() => setEditorOpen(true)}
          >
            {resubmitWithChangesButton.text}
          </Button>
          <Button
            disabled={resubmitButton.isDisabled || attemptActedOn}
            onClick={resubmitClick}
            leftIcon={<ResubmitIcon />}
          >
            {resubmitButton.text}
          </Button>
        </>
      )}

      {editorOpen && (
        <ResubmitEditor
          key={`${event?.eventId}:${event?.lastMessageId}`}
          payload={resubmitPayload}
          eventTypeId={event?.eventTypeId}
          eventTypes={props.eventTypes}
          onClose={() => setEditorOpen(false)}
          onSubmit={resubmitWithChangesClick}
        />
      )}

      <Modal
        isOpen={showDeleteConfirm}
        onClose={() => setShowDeleteConfirm(false)}
      >
        <ModalHeader>Delete Event</ModalHeader>
        <ModalBody>
          <p className="text-sm">
            This will permanently delete the event from storage.{" "}
            <span className="font-semibold text-red-600 dark:text-red-400">
              This action cannot be undone.
            </span>
          </p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setShowDeleteConfirm(false)}>
            Cancel
          </Button>
          <Button colorScheme="red" onClick={deleteClick}>
            Delete
          </Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}
