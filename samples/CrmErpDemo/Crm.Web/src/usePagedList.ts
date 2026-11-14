import { useCallback, useEffect, useRef, useState } from 'react';

/** One page of a list and the number of rows the query matches (the X-Total-Count header). */
export interface Page<T> {
  items: T[];
  total: number;
}

/** Rows on a page. The APIs use the same default and will not return more. */
export const PAGE_SIZE = 100;

const FILTER_DELAY_MS = 300;

/**
 * State for a list the server pages: the current page, a keyword filter that is sent to the
 * server once typing pauses, and an optional background poll of the page in view.
 *
 * `load` fetches one page. `scope` names everything else the query depends on, such as the
 * view; when it or the filter changes, the list goes back to page 1.
 */
export function usePagedList<T>(
  load: (page: number, q: string) => Promise<Page<T>>,
  scope = '',
  pollMs?: number,
) {
  const [page, setPage] = useState(1);
  const [filter, setFilter] = useState('');
  const [q, setQ] = useState('');
  // The page number travels with the rows, so the pager never names a page whose rows have
  // not arrived yet.
  const [data, setData] = useState<(Page<T> & { page: number }) | null>(null);
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    const t = setTimeout(() => setQ(filter.trim()), FILTER_DELAY_MS);
    return () => clearTimeout(t);
  }, [filter]);

  useEffect(() => {
    setPage(1);
  }, [q, scope]);

  const fetchPage = useCallback(async (isCurrent: () => boolean) => {
    try {
      const result = await loadRef.current(page, q);
      if (!isCurrent()) return;
      setData({ ...result, page });
      // The page in view can empty out: rows were deleted, or the list shrank under a poll.
      const lastPage = Math.max(1, Math.ceil(result.total / PAGE_SIZE));
      if (page > lastPage) setPage(lastPage);
    } catch {
      /* keep what is on screen; the next poll or Refresh tries again */
    }
  }, [page, q]);

  useEffect(() => {
    let current = true;
    const isCurrent = () => current;
    void fetchPage(isCurrent);
    const t = pollMs ? setInterval(() => void fetchPage(isCurrent), pollMs) : undefined;
    return () => {
      current = false;
      if (t) clearInterval(t);
    };
  }, [fetchPage, scope, pollMs]);

  return {
    rows: data?.items ?? [],
    total: data?.total ?? 0,
    loading: data === null,
    page: data?.page ?? page,
    setPage,
    filter,
    setFilter,
    q,
    refresh: () => void fetchPage(() => true),
  };
}
