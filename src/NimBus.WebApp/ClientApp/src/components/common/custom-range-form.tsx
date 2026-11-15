import * as React from "react";
import moment from "moment";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { cn } from "lib/utils";
import {
  customRangeError,
  type TimeRange,
} from "functions/time-range.functions";

// The value format of a datetime-local input, read and written in local time.
const LOCAL_INPUT = "YYYY-MM-DDTHH:mm";

export interface CustomRangeFormProps {
  /** The range the start and end fields begin from. */
  initial: TimeRange;
  /** Called with a valid range on Apply. */
  onApply: (range: TimeRange) => void;
  onCancel: () => void;
  /** Accessible name of the form. */
  label?: string;
  className?: string;
  /** Why a range is refused, or undefined when it is valid. Defaults to the server's chart limits. */
  validate?: (from: Date, to: Date) => string | undefined;
}

/**
 * Local start and end fields for a custom time range, validated against the server's limits
 * and applied together, after Application Insights' time range picker.
 */
export default function CustomRangeForm({
  initial,
  onApply,
  onCancel,
  label,
  className,
  validate = customRangeError,
}: CustomRangeFormProps) {
  const [start, setStart] = React.useState(() =>
    moment(initial.from).format(LOCAL_INPUT),
  );
  const [end, setEnd] = React.useState(() =>
    moment(initial.to).format(LOCAL_INPUT),
  );
  const from = moment(start, LOCAL_INPUT, true).toDate();
  const to = moment(end, LOCAL_INPUT, true).toDate();
  const error = validate(from, to);
  const startId = React.useId();
  const endId = React.useId();

  return (
    <form
      aria-label={label}
      onSubmit={(e) => {
        e.preventDefault();
        if (!error) onApply({ from, to });
      }}
      className={cn("flex flex-col gap-2.5", className)}
    >
      <div>
        <label
          htmlFor={startId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          Start time (local)
        </label>
        <Input
          id={startId}
          type="datetime-local"
          value={start}
          onChange={(e) => setStart(e.target.value)}
          error={!!error}
        />
      </div>
      <div>
        <label
          htmlFor={endId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          End time (local)
        </label>
        <Input
          id={endId}
          type="datetime-local"
          value={end}
          onChange={(e) => setEnd(e.target.value)}
          error={!!error}
        />
      </div>
      {error && (
        <p role="alert" className="m-0 text-[12.5px] text-red-600">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <Button
          type="submit"
          size="sm"
          colorScheme="primary"
          disabled={!!error}
        >
          Apply
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}
