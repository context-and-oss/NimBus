import { useRef, useEffect } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { cn } from "lib/utils";

interface IPage {
  title: string;
  /** Small caps label above the title (e.g. "Event"). */
  eyebrow?: string;
  /** Rendered beside the title, e.g. a status badge. */
  titleAdornment?: React.ReactNode;
  subtitle?: React.ReactNode;
  actions?: React.ReactNode;
  offsetDimensionsHandler?: (height: number, width: number) => void;
  children: React.ReactNode;
  backbutton?: boolean;
  backUrl?: string;
  backIndex?: string;
}

const BackArrow = () => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 16 16"
    fill="none"
    className="w-4 h-4"
    aria-hidden="true"
  >
    <path
      d="M10 3l-5 5 5 5"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  </svg>
);

const Page: React.FC<IPage> = (props) => {
  const ref = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const location = useLocation();

  // Prefer browser back so we return to the *exact* previous SPA entry — for
  // a row click in /Messages?endpointId=... the user lands back on that
  // pre-filtered list, not the generic backUrl. location.key is "default"
  // only for an initial deep-linked entry (no SPA history yet); in that case
  // we fall back to the explicit backUrl so the button still does something.
  const handleBack = () => {
    if (location.key !== "default") {
      navigate(-1);
    } else if (props.backUrl) {
      navigate(props.backUrl, { state: { tabIndex: props.backIndex } });
    }
  };

  useEffect(() => {
    if (ref.current && props.offsetDimensionsHandler !== undefined) {
      props.offsetDimensionsHandler(
        ref.current.offsetWidth,
        ref.current.offsetHeight,
      );
    }
  }, [ref.current, ref.current?.clientHeight]);

  return (
    <div className="flex flex-col flex-1 min-h-0 px-7 py-7 gap-6">
      <header className="flex flex-wrap items-end justify-between gap-x-6 gap-y-4">
        <div className="flex items-center gap-3.5 min-w-0">
          {props.backbutton && (
            <button
              type="button"
              onClick={handleBack}
              aria-label="Go back"
              className={cn(
                "shrink-0 inline-flex items-center justify-center",
                "w-8 h-8 rounded-nb-md bg-card border border-border",
                "text-primary hover:bg-muted transition-colors",
              )}
            >
              <BackArrow />
            </button>
          )}
          <div className="min-w-0">
            {props.eyebrow && (
              <div className="mb-1 font-mono text-[10.5px] uppercase tracking-[0.12em] text-muted-foreground">
                {props.eyebrow}
              </div>
            )}
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 min-w-0">
              <h1 className="text-[28px] font-bold tracking-tight m-0 truncate">
                {props.title}
              </h1>
              {props.titleAdornment}
            </div>
            {props.subtitle && (
              <div className="mt-1 text-sm text-muted-foreground m-0 truncate">
                {props.subtitle}
              </div>
            )}
          </div>
        </div>
        {props.actions && (
          <div className="flex gap-2 items-center">{props.actions}</div>
        )}
      </header>
      <div ref={ref} className="flex flex-1 min-h-0">
        {props.children}
      </div>
    </div>
  );
};

export default Page;
