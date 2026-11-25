import { useId, useState } from "react";
import { Badge } from "components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { cn } from "lib/utils";
import {
  composeGuidance, guidanceRules, otherwise, FAILURE_CATEGORIES, GUIDANCE_BADGE, GUIDANCE_MEANING, AGENT_QUESTION_SET_VERSION, SAMPLE_FAILURES,
  type FailureGuidance, type GuidanceThresholds,
} from "lib/failure-classification";

type Props = {
  thresholds: GuidanceThresholds;
  onChange: <K extends keyof GuidanceThresholds>(key: K, value: number) => void;
};

const fixed = (value: number) => value.toFixed(2);

const steps = [
  { who: "NimBus", title: "Collect evidence", text: "Exception, retries, history and the redacted payload." },
  { who: "NimBus", title: `Question set v${AGENT_QUESTION_SET_VERSION}`, text: "Defines the category, fault origin and three likelihood questions." },
  { who: "Agent", title: "Answers", text: "Classifies the failure and records its answers over MCP." },
  { who: "NimBus", title: "Validate", text: "Unknown categories or bad distributions are rejected." },
  { who: "NimBus", title: "Guidance rules", text: "Fixed rules using the thresholds below." },
];

const thresholdFields = [
  { key: "minimumCategoryConfidence", label: "Minimum category confidence", effect: "Below this, the result is Uncertain whatever the category." },
  { key: "retryLikely", label: "Retry-likely threshold", effect: "A transient_dependency at or above this is RetryMayHelp." },
  { key: "changeRequired", label: "Change-required threshold", effect: "At or above this, the result is ChangeLikelyRequired." },
] as const;

/** Explains how a failure is classified and previews the guidance rules against the draft thresholds. */
export default function FailureClassificationExplainer({ thresholds, onChange }: Props) {
  const id = useId();
  const [sampleIndex, setSampleIndex] = useState(0);
  const sample = SAMPLE_FAILURES[sampleIndex];
  const { rule: matched, guidance } = composeGuidance(sample, thresholds);
  const signalLabels = { categoryConfidence: "Category confidence", retryLikelihood: "retry", changeRequiredLikelihood: "Change required" };
  const rules = guidanceRules.map((row): [string, FailureGuidance] => {
    const category = row.category ? `Category is ${row.category} and ` : "";
    const comparison = row.comparison === "lt" ? "<" : "≥";
    return [`${category}${signalLabels[row.field]} ${fixed(sample[row.field])} ${comparison} ${fixed(thresholds[row.threshold])}`, row.guidance];
  });
  rules.push(["Otherwise", otherwise]);
  const likelihoods = [
    { label: "Retry unchanged", value: sample.retryLikelihood, threshold: thresholds.retryLikely, question: "Likely to succeed if processed again without any change?" },
    { label: "Change required", value: sample.changeRequiredLikelihood, threshold: thresholds.changeRequired, question: "Must data, configuration, permissions or code change first?" },
    { label: "External dependency", value: sample.externalDependencyLikelihood, threshold: undefined, question: "Does it originate outside the handler? Shown to operators, not used by the rules." },
  ];

  return <Card><CardHeader><CardTitle>04 · How failures are classified</CardTitle>
    <p className="text-xs text-muted-foreground">The agent answers the NimBus question set. Try a sample to see how NimBus turns those answers into guidance using your draft thresholds.</p>
  </CardHeader><CardContent className="space-y-6">
    <ol aria-label="Classification steps" className="grid gap-2 sm:grid-cols-5">
      {steps.map(step => <li key={step.title} className={cn("rounded-md border bg-background p-3", step.who === "Agent" && "border-dashed border-status-info")}>
        <p className={cn("text-[10px] font-semibold uppercase tracking-wider", step.who === "Agent" ? "text-status-info" : "text-primary")}>{step.who}</p>
        <p className="text-sm font-semibold">{step.title}</p><p className="text-xs text-muted-foreground">{step.text}</p>
      </li>)}
    </ol>

    <section aria-label="Guidance thresholds" className="grid gap-4 sm:grid-cols-3">
      {thresholdFields.map(field => <div key={field.key} className="text-sm">
        <span className="flex justify-between gap-2"><label htmlFor={`${id}-${field.key}`}>{field.label}</label>
          <output htmlFor={`${id}-${field.key}`} className="font-mono">{fixed(thresholds[field.key])}</output></span>
        <input id={`${id}-${field.key}`} type="range" min={0} max={1} step={0.01} value={thresholds[field.key]} className="w-full accent-primary"
          aria-describedby={`${id}-${field.key}-effect`} onChange={event => onChange(field.key, Number(event.target.value))} />
        <span id={`${id}-${field.key}-effect`} className="text-xs text-muted-foreground">{field.effect}</span>
      </div>)}
    </section>

    <section aria-label="Sample failure" className="space-y-4">
      <div className="flex flex-wrap items-center gap-2"><span className="text-sm font-semibold">Try a sample failure</span>
        {SAMPLE_FAILURES.map((item, index) => <button key={item.label} type="button" aria-pressed={index === sampleIndex} onClick={() => setSampleIndex(index)}
          className={cn("rounded-full border px-3 py-1 text-xs", index === sampleIndex ? "border-primary bg-primary/10 font-semibold text-primary" : "bg-background")}>{item.label}</button>)}
      </div>
      <p className="rounded-md border bg-background p-3 text-sm"><code className="text-status-danger">{sample.exceptionType}</code>: {sample.exceptionMessage}
        <span className="block text-xs text-muted-foreground">{sample.context} · illustrative answers, not live data</span></p>

      <div><p className="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Category probabilities</p>
        <div className="space-y-1">{FAILURE_CATEGORIES.map(category => {
          const value = sample.categoryProbabilities[category.id];
          const top = category.id === sample.category;
          return <div key={category.id} className="grid grid-cols-[minmax(0,14rem)_1fr_2.5rem] items-center gap-2 text-xs">
            <span className={cn("truncate font-mono", top && "font-semibold")}>{category.id}</span>
            <span className="relative h-3 rounded-sm bg-muted"><span className={cn("block h-full rounded-sm", top ? "bg-primary" : "bg-muted-foreground opacity-40")} style={{ width: `${value * 100}%` }} />
              {top && <span aria-hidden className="absolute -inset-y-0.5 w-0.5 bg-foreground opacity-60" style={{ left: `${thresholds.minimumCategoryConfidence * 100}%` }} />}</span>
            <span className="text-right font-mono">{fixed(value)}</span>
          </div>;
        })}</div>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">{likelihoods.map(item => <div key={item.label} className="rounded-md border bg-background p-3">
        <p className="text-xs text-muted-foreground">{item.label}{item.threshold !== undefined && <span className="font-mono"> ≥ {fixed(item.threshold)}</span>}</p>
        <p className="font-mono text-lg font-semibold">{fixed(item.value)}</p>
        <span className="relative mt-1 block h-2 rounded-full bg-muted"><span className="block h-full rounded-full bg-status-info" style={{ width: `${item.value * 100}%` }} />
          {item.threshold !== undefined && <span aria-hidden className="absolute -inset-y-1 w-0.5 bg-foreground opacity-60" style={{ left: `${item.threshold * 100}%` }} />}</span>
        <p className="mt-2 text-xs text-muted-foreground">{item.question}</p>
      </div>)}</div>

      <div><p className="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Guidance rules · first match wins</p>
        <ol aria-label="Guidance rules" className="space-y-1.5">{rules.map(([text, result], index) =>
          <li key={result} aria-current={index === matched ? "step" : undefined}
            className={cn("grid grid-cols-[1.5rem_1fr_auto] items-center gap-2 rounded-md border p-2 text-xs",
              index === matched && "border-primary bg-primary/10", index < matched && "opacity-50", index > matched && "opacity-30")}>
            <span className="font-mono text-muted-foreground">{index + 1}</span><span>{text}</span>
            <Badge variant={GUIDANCE_BADGE[result]} size="sm">{result}</Badge>
          </li>)}</ol>
      </div>
      <p role="status" aria-label="Sample guidance" className="text-sm"><Badge variant={GUIDANCE_BADGE[guidance]}>{guidance}</Badge> <span className="font-mono text-xs">{sample.category}</span> — {GUIDANCE_MEANING[guidance]}</p>
    </section>

    <details><summary className="cursor-pointer text-sm font-semibold">Failure categories · question set v{AGENT_QUESTION_SET_VERSION}</summary>
      <div className="mt-3 overflow-x-auto"><table className="w-full text-left text-xs">
        <thead><tr className="border-b text-muted-foreground"><th className="p-2">Category</th><th className="p-2">Meaning</th><th className="p-2">Typical evidence</th></tr></thead>
        <tbody>{FAILURE_CATEGORIES.map(category => <tr key={category.id} className={cn("border-b align-top", category.id === sample.category && "bg-primary/10")}>
          <td className="p-2 font-mono">{category.id}</td><td className="p-2">{category.meaning}</td><td className="p-2 text-muted-foreground">{category.examples}</td>
        </tr>)}</tbody>
      </table></div>
    </details>
  </CardContent></Card>;
}
