import { describe, it, expect, afterEach } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import * as api from "api-client";
import EventTypePropertiesTable from "./event-type-properties-table";

function property(name: string): api.EventTypeProperty {
  return Object.assign(new api.EventTypeProperty(), {
    name,
    typeName: "String",
    typeFullName: "System.String",
    isRequired: true,
  });
}

function row(name: string): HTMLElement {
  return screen.getByText(name).closest("tr") as HTMLElement;
}

afterEach(() => cleanup());

describe("EventTypePropertiesTable", () => {
  it("marks the session-key and routing-key properties", () => {
    render(
      <EventTypePropertiesTable
        properties={[property("AccountId"), property("LegalName"), property("CountryCode")]}
        sessionKeyProperty="AccountId"
        routingKeyProperty="CountryCode"
      />,
    );

    expect(within(row("AccountId")).getByText("Session key")).toBeTruthy();
    expect(within(row("AccountId")).queryByText("Routing key")).toBeNull();
    expect(within(row("CountryCode")).getByText("Routing key")).toBeTruthy();
    expect(within(row("CountryCode")).queryByText("Session key")).toBeNull();
    expect(within(row("LegalName")).queryByText(/key$/)).toBeNull();
  });

  it("marks one property with both roles", () => {
    render(
      <EventTypePropertiesTable
        properties={[property("ErpInstance")]}
        sessionKeyProperty="ErpInstance"
        routingKeyProperty="ErpInstance"
      />,
    );

    expect(within(row("ErpInstance")).getByText("Session key")).toBeTruthy();
    expect(within(row("ErpInstance")).getByText("Routing key")).toBeTruthy();
  });

  it("marks nothing for an event type without keys", () => {
    render(<EventTypePropertiesTable properties={[property("OrderId")]} />);

    expect(screen.queryByText("Session key")).toBeNull();
    expect(screen.queryByText("Routing key")).toBeNull();
  });
});
