import * as api from "api-client";
import React, { Dispatch, SetStateAction, useState } from "react";
import { useParams } from "react-router-dom";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { validateRepositoryUrl, validateSourceSystem, validateTicketLinkTemplate } from "./code-location";

// Icons
const AddIcon = () => (
  <svg
    className="w-4 h-4"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M12 4v16m8-8H4"
    />
  </svg>
);

const DeleteIcon = () => (
  <svg
    className="w-4 h-4"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
    />
  </svg>
);

interface IInformationButtonProps {
  metaData: api.Metadata | undefined;
  icon:
    | React.ReactElement<any, string | React.JSXElementConstructor<any>>
    | undefined;
  onClose: Dispatch<SetStateAction<api.Metadata | undefined>>;
  buttonText: string;
}

export default function MetadataButton(props: IInformationButtonProps) {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const [isOpen, setIsOpen] = useState(false);
  const [endpointOwnerTeam, setendpointOwnerTeam] = useState(
    props.metaData && props.metaData.endpointOwnerTeam
      ? props.metaData.endpointOwnerTeam
      : "",
  );
  const [endpointOwner, setEndpointOwner] = useState(
    props.metaData && props.metaData.endpointOwner
      ? props.metaData.endpointOwner
      : "",
  );
  const [endpointOwnerEmail, setEndpointOwnerEmail] = useState(
    props.metaData && props.metaData.endpointOwnerEmail
      ? props.metaData.endpointOwnerEmail
      : "",
  );

  // Spec 044 Phase 3: where agents file adapter defects and how tickets link.
  const [sourceSystem, setSourceSystem] = useState(props.metaData?.sourceSystem ?? "");
  const [repositoryUrl, setRepositoryUrl] = useState(props.metaData?.repositoryUrl ?? "");
  const [ticketLinkTemplate, setTicketLinkTemplate] = useState(props.metaData?.ticketLinkTemplate ?? "");
  const codeLocationError =
    validateSourceSystem(sourceSystem) ?? validateRepositoryUrl(repositoryUrl) ?? validateTicketLinkTemplate(ticketLinkTemplate);

  const [technicalContacts, setTechnicalContacts] = useState<
    api.TechnicalContact[]
  >(
    props.metaData && props.metaData.technicalContacts
      ? props.metaData.technicalContacts
      : [],
  );

  const [newTechnicalContactName, setNewTechnicalContactName] = useState("");
  const [newTechnicalContactEmail, setNewTechnicalContactEmail] = useState("");

  const [feedback, setFeedback] = useState("");
  const [feedbackColour, setFBcolour] = useState("text-green-500");
  const [validation, setValidation] = useState(true);

  React.useEffect(() => {
    validate();
  }, [endpointOwner, endpointOwnerTeam, technicalContacts, endpointOwnerEmail, codeLocationError]);

  const handleOwnerInputChange = (e: React.FormEvent<HTMLInputElement>) => {
    setEndpointOwner(e.currentTarget.value);
  };

  const handleOwnerTeamInputChange = (e: React.FormEvent<HTMLInputElement>) => {
    setendpointOwnerTeam(e.currentTarget.value);
  };

  const handleOwnerEmailInputChange = (
    e: React.FormEvent<HTMLInputElement>,
  ) => {
    setEndpointOwnerEmail(e.currentTarget.value);
  };

  const handlenewTechnicalContactNameInputChange = (
    e: React.FormEvent<HTMLInputElement>,
  ) => {
    setNewTechnicalContactName(e.currentTarget.value);
  };

  const handlenewTechnicalContactEmailInputChange = (
    e: React.FormEvent<HTMLInputElement>,
  ) => {
    setNewTechnicalContactEmail(e.currentTarget.value);
  };

  const handleTechnicalContactNameChange = (
    e: React.FormEvent<HTMLInputElement>,
    index: number,
  ) => {
    if (index !== -1) {
      const nextTechnicalContacts = [...technicalContacts];
      const contact = nextTechnicalContacts[index];
      contact.name = e.currentTarget.value;
      setTechnicalContacts(nextTechnicalContacts);
    } else {
      const contact = new api.TechnicalContact();
      contact.name = e.currentTarget.value;
      setTechnicalContacts([...technicalContacts, contact]);
    }
  };

  const handleTechnicalContactEmailChange = (
    e: React.FormEvent<HTMLInputElement>,
    index: number,
  ) => {
    if (index !== -1) {
      const nextTechnicalContacts = [...technicalContacts];
      const contact = nextTechnicalContacts[index];
      contact.email = e.currentTarget.value;
      setTechnicalContacts(nextTechnicalContacts);
    } else {
      const contact = new api.TechnicalContact();
      contact.email = e.currentTarget.value;
      setTechnicalContacts([...technicalContacts, contact]);
    }
  };

  function validate() {
    const pattern = new RegExp(
      /^(("[\w-\s]+")|([\w-]+(?:\.[\w-]+)*)|("[\w-\s]+")([\w-]+(?:\.[\w-]+)*))(@((?:[\w-]+\.)*\w[\w-]{0,66})\.([a-z]{2,6}(?:\.[a-z]{2})?)$)|(@\[?((25[0-5]\.|2[0-4][0-9]\.|1[0-9]{2}\.|[0-9]{1,2}\.))((25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\.){2}(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\]?$)/i,
    );
    let technicalContactValidation = false;
    if (technicalContacts.length !== 0) {
      technicalContacts.forEach((technicalContact) => {
        technicalContactValidation = validateTechnicalContact(
          technicalContact.name,
          technicalContact.email,
        );
        if (!technicalContactValidation) {
          return;
        }
      });
    } else {
      technicalContactValidation = true;
    }

    if (
      endpointOwner === "" ||
      endpointOwnerTeam === "" ||
      !pattern.test(endpointOwnerEmail) ||
      !technicalContactValidation ||
      codeLocationError !== undefined
    ) {
      setValidation(false);
    } else {
      setValidation(true);
    }
  }

  function validateTechnicalContact(
    name: string | undefined,
    email: string | undefined,
  ) {
    const pattern = new RegExp(
      /^(("[\w-\s]+")|([\w-]+(?:\.[\w-]+)*)|("[\w-\s]+")([\w-]+(?:\.[\w-]+)*))(@((?:[\w-]+\.)*\w[\w-]{0,66})\.([a-z]{2,6}(?:\.[a-z]{2})?)$)|(@\[?((25[0-5]\.|2[0-4][0-9]\.|1[0-9]{2}\.|[0-9]{1,2}\.))((25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\.){2}(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\]?$)/i,
    );

    if (
      !name ||
      !email ||
      name === "" ||
      email === "" ||
      !pattern.test(email)
    ) {
      return false;
    } else {
      return true;
    }
  }

  const clearAlertsFeedback = () => {
    setFeedback("");
  };

  const editEndpointMetadata = () => {
    const body = new api.Metadata({
      id: params.id!,
      endpointOwner: endpointOwner,
      endpointOwnerTeam: endpointOwnerTeam,
      endpointOwnerEmail: endpointOwnerEmail,
      technicalContacts: technicalContacts,
      sourceSystem: sourceSystem.trim() || undefined,
      repositoryUrl: repositoryUrl.trim() || undefined,
      ticketLinkTemplate: ticketLinkTemplate.trim() || undefined,
    });
    client
      .postMetadataEndpoint(params.id!, body)
      .then((r) => {
        props.onClose(body);
        setFeedback("Successfully added metadata to " + params.id!);
      })
      .catch((r) => {
        setFeedback("Unable to add metadata. " + r);
        setFBcolour("text-red-500");
      })
      .finally(() => {
        setTimeout(clearAlertsFeedback, 4000);
      });
  };

  function addTechnicalContact() {
    if (newTechnicalContactName === "") {
      setFeedback("New contact name cannot be empty");
      setFBcolour("text-red-500");
      setTimeout(clearAlertsFeedback, 4000);
      return;
    }
    const pattern = new RegExp(
      /^(("[\w-\s]+")|([\w-]+(?:\.[\w-]+)*)|("[\w-\s]+")([\w-]+(?:\.[\w-]+)*))(@((?:[\w-]+\.)*\w[\w-]{0,66})\.([a-z]{2,6}(?:\.[a-z]{2})?)$)|(@\[?((25[0-5]\.|2[0-4][0-9]\.|1[0-9]{2}\.|[0-9]{1,2}\.))((25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\.){2}(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[0-9]{1,2})\]?$)/i,
    );

    if (!pattern.test(newTechnicalContactEmail)) {
      setFeedback("New contact email is not valid");
      setFBcolour("text-red-500");
      setTimeout(clearAlertsFeedback, 4000);
      return;
    }

    const contact = new api.TechnicalContact();
    contact.name = newTechnicalContactName;
    contact.email = newTechnicalContactEmail;
    setTechnicalContacts([...technicalContacts, contact]);
    setNewTechnicalContactName("");
    setNewTechnicalContactEmail("");
  }

  return (
    <>
      <Button
        aria-label="Subscribe to alerts on endpoint"
        leftIcon={props.icon}
        colorScheme="primary"
        onClick={(event) => {
          event.preventDefault();
          event.stopPropagation();
          setIsOpen(true);
        }}
      >
        {props.buttonText}
      </Button>
      <Modal isOpen={isOpen} onClose={() => setIsOpen(false)} size="xl">
        <ModalHeader onClose={() => setIsOpen(false)}>
          {props.metaData ? "Update metadata on" : "Add metadata to"}{" "}
          {params.id!}
        </ModalHeader>
        <ModalBody>
          <p>Fill out endpoint owner, Technical contacts name and email.</p>
          <br />
          <p className="font-bold">Owner team</p>
          <Input
            placeholder="John Smith"
            value={endpointOwnerTeam}
            onChange={handleOwnerTeamInputChange}
          />
          <br />
          <p className="font-bold mt-2">Owner (PO)</p>
          <Input
            placeholder="John Smith"
            value={endpointOwner}
            onChange={handleOwnerInputChange}
          />
          <br />
          <p className="font-bold mt-2">Owner email (PO)</p>
          <Input
            placeholder="john@smith.com"
            value={endpointOwnerEmail}
            onChange={handleOwnerEmailInputChange}
          />
          <br />
          <p className="font-bold mt-2">Source system</p>
          <Input
            aria-label="Source system"
            placeholder="Dynamics 365 Sales"
            value={sourceSystem}
            onChange={(e) => setSourceSystem(e.currentTarget.value)}
          />
          <p className="font-bold mt-2">Adapter repository</p>
          <p className="text-xs text-muted-foreground">
            Where agents open issues and draft pull requests for this adapter's defects. Leave empty to hand adapter faults to a person.
          </p>
          <Input
            aria-label="Adapter repository"
            placeholder="https://github.com/your-org/your-adapter"
            value={repositoryUrl}
            onChange={(e) => setRepositoryUrl(e.currentTarget.value)}
          />
          <p className="font-bold mt-2">Ticket link</p>
          <p className="text-xs text-muted-foreground">
            The issue tracker, with {"{ticketId}"} where the ticket id goes. Overrides the site's ticket link for this endpoint.
          </p>
          <Input
            aria-label="Ticket link"
            placeholder="https://github.com/your-org/your-adapter/issues/{ticketId}"
            value={ticketLinkTemplate}
            onChange={(e) => setTicketLinkTemplate(e.currentTarget.value)}
          />
          {codeLocationError && (
            <p role="alert" className="mt-1 text-sm text-status-danger">{codeLocationError}</p>
          )}
          <br />

          <div className="overflow-x-auto mt-4">
            <table className="w-full text-sm">
              <thead>
                <tr>
                  <th
                    colSpan={3}
                    className="text-center border-0 text-foreground py-2"
                  >
                    Technical Contacts
                  </th>
                </tr>
                <tr className="border-b border-border">
                  <th className="text-left py-2 font-semibold">Name</th>
                  <th className="text-left py-2 font-semibold">Email</th>
                  <th className="text-left py-2 font-semibold">Add/Delete</th>
                </tr>
              </thead>
              <tbody>
                {technicalContacts?.map((technicalContact, idx) => (
                  <tr key={idx} className="border-b border-border">
                    <td className="py-2" key={idx + "namecolmodal"}>
                      <Input
                        key={idx + "name"}
                        value={technicalContact.name}
                        onChange={(e) =>
                          handleTechnicalContactNameChange(e, idx)
                        }
                      />
                    </td>
                    <td className="py-2" key={idx + "emailcolmodal"}>
                      <Input
                        key={idx + "email"}
                        value={technicalContact.email}
                        onChange={(e) =>
                          handleTechnicalContactEmailChange(e, idx)
                        }
                      />
                    </td>
                    <td className="py-2">
                      <Button
                        variant="ghost"
                        size="sm"
                        colorScheme="red"
                        aria-label="Delete element from list"
                        onClick={() => {
                          setTechnicalContacts(
                            technicalContacts.filter(
                              (c) => c !== technicalContact,
                            ),
                          );
                        }}
                      >
                        <DeleteIcon />
                      </Button>
                    </td>
                  </tr>
                ))}
                <tr className="border-b border-border">
                  <td className="py-2">
                    <Input
                      placeholder="Name"
                      value={newTechnicalContactName}
                      onChange={handlenewTechnicalContactNameInputChange}
                    />
                  </td>
                  <td className="py-2">
                    <Input
                      placeholder="Email"
                      value={newTechnicalContactEmail}
                      onChange={handlenewTechnicalContactEmailInputChange}
                    />
                  </td>
                  <td className="py-2">
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label="Add new technical contact"
                      onClick={addTechnicalContact}
                    >
                      <AddIcon />
                    </Button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <br />
          <p className={`italic ${feedbackColour}`}>{feedback}</p>
        </ModalBody>
        <ModalFooter>
          <Button
            colorScheme="primary"
            disabled={!validation}
            onClick={(event) => {
              editEndpointMetadata();
            }}
          >
            {props.metaData ? "Update metadata" : "Add metadata"}
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
}
