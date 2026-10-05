import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'
import { formatFull } from '@/lib/format'

/*
  New models and network age. Mirrors Sqm.Contracts.Devices (ModelArrivalContracts).

  A model's first appearance is the first daily file that named one of its handsets; models the
  initial dump listed were seen before the data starts and are never "new". Network age is days
  since first seen in this data - never an age of the handset, and a lower bound for the dump.
*/

export interface ModelArrivalStatus {
  ready: boolean
  notReady: string | null
  dataThrough: string | null
  /** The first daily file: anything in the initial dump was seen before it. */
  dataStart: string
}

export interface NewModel {
  tac: string
  brand: string | null
  model: string | null
  deviceType: string | null
  firstSeen: string
  networkAgeDays: number | null
  daysSeen: number
  firstDayImeis: number
  /** Handsets now, estimated, from the latest delivery. */
  handsets: number | null
  sims: number | null
}

export interface NewModelsResponse {
  rows: NewModel[]
  total: number
  page: number
  pageSize: number
  from: string
  to: string
  dataThrough: string | null
}

export interface NewModelsQuery {
  from?: string
  to?: string
  brand?: string
  deviceType?: string
  knownOnly?: boolean
  page?: number
  pageSize?: number
}

export interface ModelArrivals {
  grain: 'month' | 'week'
  periods: { periodStart: string; models: number; knownModels: number }[]
  dataStart: string
}

export interface ModelNetworkAge {
  tac: string
  inDump: boolean
  firstSeen: string | null
  networkAgeDays: number | null
  /** The model was in the initial dump: its network age is at least this. */
  atLeast: boolean
  daysSeen: number
  dataThrough: string | null
}

export function useModelArrivalStatus() {
  return useQuery({
    queryKey: ['model-arrivals', 'status'],
    queryFn: ({ signal }) => api.get<ModelArrivalStatus>('/api/v1/devices/arrivals/status', undefined, signal),
    staleTime: 60_000,
  })
}

export function useNewModels(query: MaybeRefOrGetter<NewModelsQuery>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['model-arrivals', 'list', toValue(query)]),
    queryFn: ({ signal }) => api.get<NewModelsResponse>('/api/v1/devices/new-models', { ...toValue(query) }, signal),
    enabled: computed(() => toValue(enabled)),
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  })
}

export function useModelArrivals(grain: MaybeRefOrGetter<'month' | 'week'>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => ['model-arrivals', 'periods', toValue(grain)]),
    queryFn: ({ signal }) => api.get<ModelArrivals>('/api/v1/devices/arrivals', { grain: toValue(grain) }, signal),
    enabled: computed(() => toValue(enabled)),
    staleTime: 5 * 60_000,
  })
}

export function useModelNetworkAge(tac: MaybeRefOrGetter<string | null>) {
  return useQuery({
    queryKey: computed(() => ['model-arrivals', 'age', toValue(tac)]),
    queryFn: ({ signal }) => api.get<ModelNetworkAge>(`/api/v1/devices/${toValue(tac)}/network-age`, undefined, signal),
    enabled: computed(() => /^\d{8}$/.test(toValue(tac) ?? '')),
    staleTime: 5 * 60_000,
    retry: false,
  })
}

/** "47 days", or "at least 244 days" for anything first seen in the initial dump. */
export function networkAgeText(days: number | null, atLeast: boolean): string {
  if (days === null) return 'unknown'
  const n = formatFull(days)
  return `${atLeast ? 'at least ' : ''}${n} day${days === 1 ? '' : 's'}`
}
