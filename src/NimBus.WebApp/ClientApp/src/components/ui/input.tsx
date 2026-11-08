import { forwardRef, type InputHTMLAttributes } from "react";
import { cn } from "lib/utils";

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  error?: boolean;
  leftElement?: React.ReactNode;
  rightElement?: React.ReactNode;
}

const DATE_TYPES = new Set(["date", "datetime-local", "time", "month", "week"]);

const Input = forwardRef<HTMLInputElement, InputProps>(
  (
    { className, error, leftElement, rightElement, type = "text", ...props },
    ref,
  ) => {
    const hasLeftElement = !!leftElement;
    const hasRightElement = !!rightElement;
    // display:flex shrinks a date input's inner box in Chromium, leaving the
    // picker icon right after the text instead of at the right edge.
    const isDateLike = DATE_TYPES.has(type);

    const inputElement = (
      <input
        type={type}
        className={cn(
          isDateLike ? "block" : "flex",
          "h-10 w-full rounded-md border bg-background px-3 py-2 text-sm text-foreground",
          "placeholder:text-muted-foreground",
          "focus:outline-hidden focus:ring-2 focus:ring-offset-0",
          "disabled:cursor-not-allowed disabled:opacity-50 disabled:bg-muted",
          error
            ? "border-red-500 focus:border-red-500 focus:ring-red-200"
            : "border-input focus:border-primary focus:ring-primary-200",
          hasLeftElement && "pl-10",
          hasRightElement && "pr-10",
          className,
        )}
        ref={ref}
        {...props}
      />
    );

    if (hasLeftElement || hasRightElement) {
      return (
        <div className="relative">
          {hasLeftElement && (
            <div className="absolute inset-y-0 left-0 flex items-center pl-3 pointer-events-none text-muted-foreground">
              {leftElement}
            </div>
          )}
          {inputElement}
          {hasRightElement && (
            <div className="absolute inset-y-0 right-0 flex items-center pr-3 text-muted-foreground">
              {rightElement}
            </div>
          )}
        </div>
      );
    }

    return inputElement;
  },
);

Input.displayName = "Input";

export { Input };
