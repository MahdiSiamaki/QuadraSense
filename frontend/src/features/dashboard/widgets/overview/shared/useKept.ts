/**
 * Keeps a widget's last data on screen while the next arrives.
 *
 * A changed lens or config is a new query key, and TanStack answers a new key with no data until it
 * loads - so the card would drop to a skeleton on every filter change and the grid around it would
 * jump. The previous figures stay, marked as updating, until the new ones replace them.
 */
import { computed, shallowRef, watch, type ComputedRef } from 'vue'
import type { WidgetData } from '@/features/dashboard/composables/useWidgetData'

export interface Kept<T> {
  /** The current data, or the last data while the next loads. */
  value: ComputedRef<T | undefined>
  /** Nothing to show yet: a skeleton. */
  loading: ComputedRef<boolean>
  /** Showing kept or current data while a request runs. */
  updating: ComputedRef<boolean>
}

export function useKept<T>(source: WidgetData<T>): Kept<T> {
  const kept = shallowRef<T | undefined>(source.data.value)
  watch(source.data, (next) => {
    if (next !== undefined) kept.value = next
  })

  const value = computed(() => source.data.value ?? kept.value)
  return {
    value,
    loading: computed(() => value.value === undefined && source.isPending.value && !source.isError.value),
    updating: computed(
      () => source.isUpdating.value || (source.data.value === undefined && kept.value !== undefined && source.isPending.value),
    ),
  }
}
