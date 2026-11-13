import * as api from "api-client";
import React, { useState } from "react";
import { Button } from "components/ui/button";
import MetadataButton from "./information-modal";
import "./metadata.css";

// Icons
const EditIcon = () => (
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
      d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
    />
  </svg>
);

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

interface IMetadataColumnProps {
  metadata: api.Metadata | undefined;
}

const MetadataColumn = (props: IMetadataColumnProps) => {
  const [endpointMetadata, setendpointMetadata] = React.useState<
    api.Metadata | undefined
  >(props.metadata);

  const [hasMetaData, sethasMetaData] = React.useState<boolean>(false);
  const [hasTechnicalContact, sethasTechnicalContact] =
    React.useState<boolean>(false);
  const [feedback, setFeedback] = useState("");
  const timeoutIdRef = React.useRef<NodeJS.Timeout | undefined>(undefined);
  const [feedbackColour, setFBcolour] = useState("text-green-500");

  React.useEffect(() => {
    if (endpointMetadata) {
      sethasMetaData(true);
      if (
        endpointMetadata.technicalContacts &&
        endpointMetadata.technicalContacts.length !== 0
      ) {
        sethasTechnicalContact(true);
      }
    } else {
      sethasMetaData(false);
    }
  }, [endpointMetadata]);

  const clearAlertsFeedback = () => {
    setFeedback("");
  };

  const HasMetaData = () => (
    <div className="p-3">
      <div className="grid grid-cols-3">
        <div className="col-span-3 text-center border-0 text-foreground font-bold py-2">
          Owner Details
        </div>

        <span className="font-bold py-1">Owner team</span>
        <div
          className="col-span-2 py-1 truncate cursor-pointer hover:bg-accent"
          title={endpointMetadata?.endpointOwnerTeam}
          onClick={() => copyToClipboard(endpointMetadata?.endpointOwnerTeam!)}
        >
          {endpointMetadata?.endpointOwnerTeam!}
        </div>

        <span className="font-bold py-1">Owner (PO)</span>
        <div
          className="col-span-2 py-1 truncate cursor-pointer hover:bg-accent"
          title={endpointMetadata?.endpointOwner}
          onClick={() => copyToClipboard(endpointMetadata?.endpointOwner!)}
        >
          {endpointMetadata?.endpointOwner!}
        </div>

        <span className="font-bold py-1">Owner email (PO)</span>
        <div
          className="col-span-2 py-1 truncate cursor-pointer hover:bg-accent"
          title={endpointMetadata?.endpointOwnerEmail}
          onClick={() => copyToClipboard(endpointMetadata?.endpointOwnerEmail!)}
        >
          {endpointMetadata?.endpointOwnerEmail!}
        </div>
        <div className="col-span-3 text-center border-0 text-foreground font-bold py-2 mt-2">
          Code location
        </div>

        <span className="font-bold py-1">Source system</span>
        <div className="col-span-2 py-1 truncate">{endpointMetadata?.sourceSystem || "Not set"}</div>

        <span className="font-bold py-1">Adapter repository</span>
        <div className="col-span-2 py-1 truncate">
          {endpointMetadata?.repositoryUrl ? (
            <a className="text-primary hover:underline" href={endpointMetadata.repositoryUrl} target="_blank" rel="noopener noreferrer">
              {endpointMetadata.repositoryUrl}
            </a>
          ) : (
            "Not set: agents hand adapter faults to a person"
          )}
        </div>

        <span className="font-bold py-1">Ticket link</span>
        <div className="col-span-2 py-1 truncate font-mono text-xs">{endpointMetadata?.ticketLinkTemplate || "Site default"}</div>
      </div>
      <br />
      <hr className="border-border" />
      {hasTechnicalContact ? <TechnicalTable /> : <p>No technical contact</p>}
      <p className={`italic ${feedbackColour}`}>{feedback}</p>
      <br />

      <MetadataButton
        onClose={setendpointMetadata}
        metaData={endpointMetadata}
        icon={<EditIcon />}
        buttonText="Update metadata"
      />
    </div>
  );

  const TechnicalTable = () => (
    <div className="grid grid-cols-2">
      <div className="col-span-2 text-center border-0 text-foreground font-bold py-2">
        Technical Contacts
      </div>

      <span className="font-bold py-1">Name</span>
      <span className="font-bold py-1">Email</span>

      {endpointMetadata?.technicalContacts?.map((technicalContact, idx) => (
        <div className="col-span-2" key={idx + "gridItem"}>
          <div className="grid grid-cols-2" key={idx + "grid"}>
            <span
              className="w-full truncate py-1 cursor-pointer hover:bg-accent"
              title={technicalContact.name}
              key={idx + "namecol"}
              onClick={() => copyToClipboard(technicalContact.name!)}
            >
              {technicalContact.name}
            </span>

            <span
              className="w-full truncate py-1 cursor-pointer hover:bg-accent"
              title={technicalContact.email}
              key={idx + "emailcol"}
              onClick={() => copyToClipboard(technicalContact.email!)}
            >
              {technicalContact.email}
            </span>
          </div>
        </div>
      ))}
    </div>
  );

  function copyToClipboard(textToCopy: string) {
    navigator.clipboard.writeText(textToCopy!);
    setFeedback("Copied " + textToCopy + " to clipboard");
    clearTimeout(timeoutIdRef.current);
    timeoutIdRef.current = setTimeout(() => {
      clearAlertsFeedback();
    }, 4000);
  }

  const NoMetaData = () => (
    <div className="p-3">
      <p>There is no information available click add to metadata endpoint</p>
      <MetadataButton
        onClose={setendpointMetadata}
        metaData={endpointMetadata}
        icon={<AddIcon />}
        buttonText="Add metadata"
      />
    </div>
  );

  return !hasMetaData ? <NoMetaData /> : <HasMetaData />;
};
export default MetadataColumn;
