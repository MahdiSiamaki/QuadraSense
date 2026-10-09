/**
 * One widget's data, live or from an export (ADR-016 §11-§12).
 *
 * Live: the widget's own TanStack query, which runs only while `useQueryActive` is true - the card is
 * in view, and it is not drawing an export. Instances with equal parameters share one query by key.
 *
 * Export: the server-prepared `WidgetSnapshotData`, the same shape the live endpoint returns, so the
 * component draws one and the same way from either.
 */
import { computed, type Ref } from 'vue'
import type { WidgetViewProps } from '../types/widget'

/** The parts of a TanStack query result a widget reads. */
export interface QueryLike<T> {
  data: Ref<T | undefined>
  isPending: Ref<boolean>
  isError: Ref<boolean>
  error: Ref<unknown>
  isFetching: Ref<boolean>
  refetch: () => unknown
}

export interface WidgetData<T> {
  data: Ref<T | undefined>
  /** No data yet: a skeleton, not an empty state. */
  isPending: Ref<boolean>
  isError: Ref<boolean>
  error: Ref<unknown>
  /** Refreshing with the previous data still shown. */
  isUpdating: Ref<boolean>
  refetch: () => void
  /** Drawing an export's server-prepared data. */
  fromSnapshot: Ref<boolean>
}

type AnyProps = WidgetViewProps<any>

/** Whether a widget's live queries may run. Pass it as the hook's `enabled`. */
export function useQueryActive(props: AnyProps): Ref<boolean> {
  return computed(() => props.active && props.mode !== 'export' && !props.snapshot)
}

/**
 * Live or snapshot data behind one interface.
 *
 * @param select Turns the snapshot's raw data into what the component draws, when the live query
 *   also transforms (most do not: the snapshot carries the endpoint's own shape).
 */
export function useWidgetData<T>(props: AnyProps, query: QueryLike<T>, select?: (raw: unknown) => T): WidgetData<T> {
  const fromSnapshot = computed(() => props.snapshot !== undefined && props.snapshot !== null)

  const data = computed<T | undefined>(() => {
    const snap = props.snapshot
    if (snap) {
      if (snap.status !== 'ok') return undefined
      return select ? select(snap.data) : (snap.data as T)
    }
    return query.data.value
  })

  const isError = computed(() =>
    fromSnapshot.value ? props.snapshot!.status === 'error' : query.isError.value,
  )

  const error = computed<unknown>(() =>
    fromSnapshot.value
      ? props.snapshot!.status === 'error'
        ? new Error(props.snapshot!.reason ?? 'The server could not prepare this widget.')
        : null
      : query.error.value,
  )

  // A query that is held back (off screen) reports pending; that is a skeleton until it scrolls in.
  const isPending = computed(() => (fromSnapshot.value ? false : query.isPending.value))
  const isUpdating = computed(() => !fromSnapshot.value && query.isFetching.value && query.data.value !== undefined)

  return {
    data,
    isPending,
    isError,
    error,
    isUpdating,
    refetch: () => {
      if (!fromSnapshot.value) void query.refetch()
    },
    fromSnapshot,
  }
}
