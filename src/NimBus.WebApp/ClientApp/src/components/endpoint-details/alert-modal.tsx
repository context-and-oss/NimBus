import { useEffect, useState } from "react";
import * as api from "api-client";
import {
  Input,
  Modal,
  ModalBody,
  ModalHeader,
  Radio,
  RadioGroup,
} from "components/ui";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { getEventTypesByEndpoint } from "hooks/event-types";
import { ALERT_FREQUENCIES } from "components/endpoint-list/alert-frequencies";

interface IAlertModalProps {
  endpointId: string;
  isOpen: boolean;
  onClose: () => void;
  editable: boolean;
  subscription: api.EndpointSubscription | undefined;
}

// Read-only view of an endpoint alert subscription (NimBus subscriptions carry
// mail OR a webhook url — no managed destinations like upstream DIS).
export default function AlertModal(props: IAlertModalProps) {
  const [eventTypes, setEventTypes] = useState<ComboboxOption[]>([]);
  const [selectedEventTypes, setSelectedEventTypes] = useState<string[]>([]);

  useEffect(() => {
    const fetchData = async () => {
      const result = await getEventTypesByEndpoint(props.endpointId);

      const consumes =
        result.consumes
          ?.map((event) => event.events ?? [])
          .reduce((pre, cur) => pre.concat(cur), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      const produces =
        result.produces
          ?.map((event) => event.events ?? [])
          .reduce((pre, cur) => pre.concat(cur), [])
          .map((event) => event.name)
          .filter((name): name is string => Boolean(name)) ?? [];

      setEventTypes(
        [...consumes, ...produces].map(
          (x) => ({ label: x, value: x }) as ComboboxOption,
        ),
      );
    };
    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    setSelectedEventTypes(props.subscription?.eventTypes ?? []);
  }, [props.subscription]);

  if (!props.subscription) return null;

  return (
    <Modal isOpen={props.isOpen} onClose={props.onClose} size="lg">
      <ModalHeader onClose={props.onClose}>
        Alert settings on {props.endpointId} for {props.subscription.authorId}
      </ModalHeader>
      <ModalBody>
        {props.subscription.mail ? (
          <>
            <p className="font-bold mb-1">Email</p>
            <Input
              disabled={!props.editable}
              placeholder={props.subscription.mail}
            />
          </>
        ) : (
          <>
            <p className="font-bold mb-1">Webhook</p>
            <Input
              disabled={!props.editable}
              placeholder={props.subscription.url ?? "—"}
            />
          </>
        )}

        <p className="font-bold mt-3 mb-2">Frequency</p>
        <RadioGroup
          name="alert-frequency"
          value={props.subscription.frequency?.toString()}
          disabled={!props.editable}
        >
          {ALERT_FREQUENCIES.map((option) => (
            <Radio key={option.value} value={option.value}>
              {option.label}
            </Radio>
          ))}
        </RadioGroup>

        <p className="font-bold mt-3 mb-2">Type</p>
        <RadioGroup
          name="alert-type"
          value={props.subscription.type}
          disabled={!props.editable}
        >
          <Radio value="mail">Mail</Radio>
          <Radio value="webhook">Webhook</Radio>
        </RadioGroup>

        <p className="font-bold mt-3 mb-2">Event Filtering</p>
        <Combobox
          options={eventTypes}
          value={selectedEventTypes}
          onChange={setSelectedEventTypes}
          placeholder="Type an event"
        />
      </ModalBody>
    </Modal>
  );
}
