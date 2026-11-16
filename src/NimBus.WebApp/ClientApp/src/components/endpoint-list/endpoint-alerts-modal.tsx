import { useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";
import {
  Button,
  Input,
  Modal,
  ModalBody,
  ModalFooter,
  ModalHeader,
  Radio,
  RadioGroup,
  useToast,
} from "components/ui";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { getEventTypesByEndpoint } from "hooks/event-types";
import { copyToClipboard } from "lib/clipboard";
import { ALERT_FREQUENCIES, DEFAULT_ALERT_FREQUENCY } from "./alert-frequencies";

interface IEndpointAlertsModalProps {
  endpointId: string;
  isOpen: boolean;
  onClose: () => void;
}

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

// The server has the last word (it also accepts http where a local deployment
// allows it); this only keeps an obviously wrong URL from being sent.
const isHttpsUrl = (value: string): boolean => {
  try {
    return new URL(value).protocol === "https:";
  } catch {
    return false;
  }
};

// Create an alert subscription on an endpoint. NimBus subscriptions carry a
// mail address OR a webhook url — there are no managed destinations like
// upstream DIS, so the type radio picks between those two. The author is
// recorded server-side from the authenticated principal; POST requires Owner
// on the endpoint, which is why the caller only offers this to managers.
//
// A webhook's requests are signed. The server answers the subscribe call with
// the signing secret and never returns it again, so the modal stays open on
// the secret until the user dismisses it.
export default function EndpointAlertsModal(props: IEndpointAlertsModalProps) {
  const { addToast } = useToast();
  const [client] = useState(() => new api.Client(api.CookieAuth()));

  const [notificationType, setNotificationType] = useState("mail");
  const [mail, setMail] = useState("");
  const [url, setUrl] = useState("");
  const [frequency, setFrequency] = useState(DEFAULT_ALERT_FREQUENCY);
  const [signingSecret, setSigningSecret] = useState<string>();
  const [eventTypes, setEventTypes] = useState<ComboboxOption[]>([]);
  const [selectedEventTypes, setSelectedEventTypes] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const eventTypesLoaded = useRef(false);

  useEffect(() => {
    if (!props.isOpen || eventTypesLoaded.current) return;
    eventTypesLoaded.current = true;

    const load = async () => {
      const result = await getEventTypesByEndpoint(props.endpointId);
      const names = (groups: typeof result.consumes) =>
        groups
          ?.map((group) => group.events ?? [])
          .reduce((all, events) => all.concat(events), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      setEventTypes(
        [...names(result.consumes), ...names(result.produces)].map((name) => ({
          label: name,
          value: name,
        })),
      );
    };

    load().catch(() => {
      // Event-type filtering is optional refinement — a failed catalog lookup
      // must not block subscribing to everything on the endpoint.
      setEventTypes([]);
    });
  }, [props.isOpen, props.endpointId]);

  const recipient = notificationType === "mail" ? mail : url;
  const recipientValid =
    notificationType === "mail"
      ? EMAIL_PATTERN.test(mail.trim())
      : isHttpsUrl(url.trim());

  const reset = () => {
    setMail("");
    setUrl("");
    setSelectedEventTypes([]);
    setSigningSecret(undefined);
  };

  const close = () => {
    reset();
    props.onClose();
  };

  const copySecret = () => {
    if (!signingSecret) return;
    copyToClipboard(signingSecret)
      .then(() => addToast({ variant: "success", title: "Secret copied.", duration: 3000 }))
      .catch(() =>
        addToast({
          variant: "error",
          title: "Could not copy the secret.",
          description: "Select it and copy it by hand.",
          duration: 6000,
        }),
      );
  };

  const subscribe = () => {
    setSaving(true);
    const body = new api.EndpointSubscription({
      type: notificationType,
      mail: notificationType === "mail" ? mail.trim() : undefined,
      url: notificationType === "webhook" ? url.trim() : undefined,
      eventTypes: selectedEventTypes,
      frequency: parseInt(frequency, 10),
    });

    client
      .postEndpointSubscribe(props.endpointId, body)
      .then((created) => {
        addToast({
          variant: "success",
          title: `Subscribed to alerts on ${props.endpointId}.`,
          description: recipient.trim(),
          duration: 4000,
        });
        if (created?.signingSecret) {
          setSigningSecret(created.signingSecret);
          return;
        }
        close();
      })
      .catch((error: unknown) => {
        addToast({
          variant: "error",
          title: `Could not subscribe to alerts on ${props.endpointId}.`,
          description: errorMessage(error) || "The request was rejected.",
          duration: 6000,
        });
      })
      .finally(() => setSaving(false));
  };

  if (signingSecret) {
    return (
      <Modal
        isOpen={props.isOpen}
        onClose={close}
        size="lg"
        closeOnOverlayClick={false}
        label="Webhook signing secret"
      >
        <ModalHeader>Copy the signing secret</ModalHeader>
        <ModalBody className="space-y-3">
          <p className="text-sm m-0">
            This secret won't be shown again. Give it to the receiver at{" "}
            <span className="font-mono break-all">{url.trim()}</span> so it can
            check that a request comes from NimBus.
          </p>
          <pre className="overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-xs">
            {signingSecret}
          </pre>
          <p className="text-sm text-muted-foreground m-0">
            Each request carries <code className="font-mono">X-NimBus-Timestamp</code>{" "}
            and <code className="font-mono">X-NimBus-Signature</code>: <code className="font-mono">v1=</code>{" "}
            followed by the HMAC-SHA256 of the timestamp, a dot and the request
            body, keyed with this secret. To replace a secret, delete the
            subscription and create it again.
          </p>
        </ModalBody>
        <ModalFooter>
          <Button variant="ghost" colorScheme="gray" onClick={copySecret}>
            Copy secret
          </Button>
          <Button colorScheme="primary" onClick={close}>
            Done
          </Button>
        </ModalFooter>
      </Modal>
    );
  }

  return (
    <Modal isOpen={props.isOpen} onClose={props.onClose} size="lg">
      <ModalHeader onClose={props.onClose}>
        Subscribe to alerts from {props.endpointId}
      </ModalHeader>
      <ModalBody>
        <p className="text-sm text-muted-foreground m-0">
          Get notified when an event on {props.endpointId} fails or is
          dead-lettered. Alerts go to an email address or a webhook, no more
          often than the frequency you pick.
        </p>

        <p className="font-bold text-sm mt-4 mb-2">Type</p>
        <RadioGroup
          name="alert-type"
          value={notificationType}
          onChange={setNotificationType}
        >
          <Radio value="mail">Mail</Radio>
          <Radio value="webhook">Webhook</Radio>
        </RadioGroup>

        {notificationType === "mail" ? (
          <div className="mt-4">
            <p className="font-bold text-sm mb-1">Email</p>
            <Input
              type="email"
              autoComplete="off"
              placeholder="example@email.com"
              value={mail}
              error={mail.length > 0 && !recipientValid}
              onChange={(e) => setMail(e.currentTarget.value)}
            />
          </div>
        ) : (
          <div className="mt-4">
            <p className="font-bold text-sm mb-1">Webhook</p>
            <Input
              type="url"
              autoComplete="off"
              placeholder="https://example.com/hooks/nimbus"
              value={url}
              error={url.length > 0 && !recipientValid}
              onChange={(e) => setUrl(e.currentTarget.value)}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              An https URL. It receives a signed POST with the ids of the failed
              event, never its payload. You get the signing secret once, after
              subscribing.
            </p>
          </div>
        )}

        <p className="font-bold text-sm mt-4 mb-2">Frequency</p>
        <RadioGroup
          name="alert-frequency"
          value={frequency}
          onChange={setFrequency}
        >
          {ALERT_FREQUENCIES.map((option) => (
            <Radio key={option.value} value={option.value}>
              {option.label}
            </Radio>
          ))}
        </RadioGroup>

        <p className="font-bold text-sm mt-4 mb-2">Event filtering</p>
        <Combobox
          options={eventTypes}
          value={selectedEventTypes}
          onChange={setSelectedEventTypes}
          placeholder="Type an event"
        />
        <p className="mt-1 text-xs text-muted-foreground">
          Leave empty to be alerted about every event type on this endpoint.
        </p>
      </ModalBody>
      <ModalFooter>
        <Button variant="ghost" colorScheme="gray" onClick={props.onClose}>
          Cancel
        </Button>
        <Button
          colorScheme="primary"
          disabled={!recipientValid || saving}
          onClick={subscribe}
        >
          Subscribe
        </Button>
      </ModalFooter>
    </Modal>
  );
}
