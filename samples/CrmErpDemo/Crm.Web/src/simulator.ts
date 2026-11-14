// Browser-side "Simulate mode" engine. When running, it mimics real users
// working in the CRM by performing randomized create/update/delete activity on
// accounts and contacts through the same /api calls the UI uses — which publish
// CrmAccount*/CrmContact* events to the Service Bus. Up to MAX_WORKERS actions
// run in parallel, paced to the configured target rate (messages per minute,
// header slider) with ±30% jitter so the traffic keeps a human rhythm. The
// rate is a target: each action is a real HTTP round-trip, so very high
// settings top out at whatever the API sustains.

import { useSyncExternalStore } from 'react';
import { api } from './api';
import { randomCompany, randomPerson, randomPick } from './fakeData';

const MAX_WORKERS = 2;

export const MIN_RATE_PER_MINUTE = 10;
export const MAX_RATE_PER_MINUTE = 300;
export const DEFAULT_RATE_PER_MINUTE = 60;

export interface SimStats {
  running: boolean;
  actions: number;
  lastAction: string | null;
  /** Target actions per minute across all workers. */
  ratePerMinute: number;
}

type ActionKind =
  | 'account.create'
  | 'account.update'
  | 'account.delete'
  | 'contact.create'
  | 'contact.update'
  | 'contact.delete';

// Biased toward creates so the dataset stays populated; deletes stay occasional.
const WEIGHTS: ReadonlyArray<readonly [ActionKind, number]> = [
  ['account.create', 25],
  ['account.update', 20],
  ['account.delete', 10],
  ['contact.create', 25],
  ['contact.update', 15],
  ['contact.delete', 5],
];

/** Pure weighted pick — exported so the distribution is unit-testable. */
export function pickWeighted(
  weights: ReadonlyArray<readonly [ActionKind, number]>,
  rnd: number = Math.random(),
): ActionKind {
  const total = weights.reduce((sum, [, w]) => sum + w, 0);
  let r = rnd * total;
  for (const [kind, w] of weights) {
    if (r < w) return kind;
    r -= w;
  }
  return weights[weights.length - 1][0];
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

class Simulator {
  private running = false;
  private actions = 0;
  private lastAction: string | null = null;
  private ratePerMinute = DEFAULT_RATE_PER_MINUTE;
  // Bumped on every start() so workers from a previous run exit instead of
  // doubling up if the toggle is flipped off then on again quickly.
  private generation = 0;
  private listeners = new Set<() => void>();
  private snapshot: SimStats = {
    running: false,
    actions: 0,
    lastAction: null,
    ratePerMinute: DEFAULT_RATE_PER_MINUTE,
  };

  // Stable identities required by useSyncExternalStore.
  subscribe = (cb: () => void): (() => void) => {
    this.listeners.add(cb);
    return () => {
      this.listeners.delete(cb);
    };
  };

  getSnapshot = (): SimStats => this.snapshot;

  toggle(): void {
    if (this.running) this.stop();
    else this.start();
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.generation += 1;
    const gen = this.generation;
    this.emit();
    for (let i = 0; i < MAX_WORKERS; i++) void this.worker(gen);
  }

  stop(): void {
    if (!this.running) return;
    this.running = false;
    this.emit();
  }

  /** Sets the target rate; workers read it live, so a running sim adapts on its next beat. */
  setRate(perMinute: number): void {
    const clamped = Math.min(
      MAX_RATE_PER_MINUTE,
      Math.max(MIN_RATE_PER_MINUTE, Math.round(perMinute)),
    );
    if (clamped === this.ratePerMinute) return;
    this.ratePerMinute = clamped;
    this.emit();
  }

  private emit(): void {
    this.snapshot = {
      running: this.running,
      actions: this.actions,
      lastAction: this.lastAction,
      ratePerMinute: this.ratePerMinute,
    };
    this.listeners.forEach(l => l());
  }

  private async worker(gen: number): Promise<void> {
    while (this.running && this.generation === gen) {
      try {
        this.lastAction = await this.act();
        this.actions += 1;
        this.emit();
      } catch {
        // Swallow transient failures (e.g. two workers racing to delete the
        // same row → 404) so the loop keeps simulating.
      }
      // Target rate spread across the workers, ±30% jitter for a human rhythm.
      // Read per beat so slider changes apply without restarting the sim.
      const baseMs = (60_000 / this.ratePerMinute) * MAX_WORKERS;
      await sleep(baseMs * (0.7 + Math.random() * 0.6));
    }
  }

  private act(): Promise<string> {
    switch (pickWeighted(WEIGHTS)) {
      case 'account.create': return this.createAccount();
      case 'account.update': return this.updateAccount();
      case 'account.delete': return this.deleteAccount();
      case 'contact.create': return this.createContact();
      case 'contact.update': return this.updateContact();
      case 'contact.delete': return this.deleteContact();
    }
  }

  private async createAccount(): Promise<string> {
    const a = await api.createAccount(randomCompany());
    return `Created account ${a.legalName}`;
  }

  private async updateAccount(): Promise<string> {
    const target = randomPick((await api.listAccounts({ deleted: false })).items);
    if (!target) return this.createAccount();
    const a = await api.updateAccount(target.id, randomCompany());
    return `Updated account ${a.legalName}`;
  }

  private async deleteAccount(): Promise<string> {
    const target = randomPick((await api.listAccounts({ deleted: false })).items);
    if (!target) return this.createAccount();
    await api.deleteAccount(target.id);
    return `Deleted account ${target.legalName}`;
  }

  private async createContact(): Promise<string> {
    const accounts = (await api.listAccounts({ deleted: false })).items;
    // ~70% of contacts belong to an existing account; the rest are unlinked.
    const account = Math.random() < 0.7 ? randomPick(accounts) : undefined;
    const c = await api.createContact({ ...randomPerson(), accountId: account?.id ?? null });
    return `Created contact ${c.firstName} ${c.lastName}`;
  }

  private async updateContact(): Promise<string> {
    const target = randomPick((await api.listContacts({ deleted: false })).items);
    if (!target) return this.createContact();
    const c = await api.updateContact(target.id, { ...randomPerson(), accountId: target.accountId ?? null });
    return `Updated contact ${c.firstName} ${c.lastName}`;
  }

  private async deleteContact(): Promise<string> {
    const target = randomPick((await api.listContacts({ deleted: false })).items);
    if (!target) return this.createContact();
    await api.deleteContact(target.id);
    return `Deleted contact ${target.firstName} ${target.lastName}`;
  }
}

export const simulator = new Simulator();

/** React binding so the header button reflects sim state across route changes. */
export function useSimulator(): SimStats {
  return useSyncExternalStore(simulator.subscribe, simulator.getSnapshot);
}
