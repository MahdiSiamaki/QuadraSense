import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from './client'

/*
  The controlled export of campaign lists. Mirrors Sqm.Contracts.Campaigns.

  The product owner's decision of 2026-10-21: a list of numbers for a campaign leaves the system only
  with its own permission, a recorded reason, and a second person's approval above a size threshold.
  The server decides all three; this file asks, shows what it said, and downloads what it allows.
*/

export interface CampaignExport {
  id: number
  view: 'sunset' | 'esim'
  class: string
  reason: string | null
  filter: { vendor: string | null; model: string | null; deviceType: string | null }
  asOfSequence: number
  asOfDate: string | null
  rowCount: number
  purpose: string
  requestedBy: string
  requestedAt: string
  needsApproval: boolean
  status: 'pending' | 'approved' | 'rejected' | 'withdrawn'
  decidedBy: string | null
  decidedAt: string | null
  decisionNote: string | null
  expiresAt: string | null
  downloads: number
  canDownload: boolean
  canDecide: boolean
}

export interface CampaignExportList {
  requests: CampaignExport[]
  /** Lists larger than this need a second person; 0 means every list does. */
  approvalThreshold: number
  validHours: number
}

export interface CampaignExportCreate {
  view: 'sunset' | 'esim'
  class: string
  reason?: string | null
  vendor?: string
  model?: string
  deviceType?: string
  runId: number | null
  purpose: string
}

const KEY = ['campaign-exports']

export function useCampaignExports(enabled = true) {
  return useQuery({
    queryKey: KEY,
    queryFn: ({ signal }) => api.get<CampaignExportList>('/api/v1/campaign-exports', undefined, signal),
    enabled,
    staleTime: 15_000,
  })
}

export function useRequestCampaignExport() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (request: CampaignExportCreate) => api.post<CampaignExport>('/api/v1/campaign-exports', request),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  })
}

export function useDecideCampaignExport() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, approve, note }: { id: number; approve: boolean; note?: string }) =>
      api.post<CampaignExport>(`/api/v1/campaign-exports/${id}/${approve ? 'approve' : 'reject'}`, { note }),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  })
}

export function useWithdrawCampaignExport() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<void>(`/api/v1/campaign-exports/${id}/withdraw`, {}),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  })
}

export function useDownloadCampaignExport() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.download(`/api/v1/campaign-exports/${id}/download`, {}),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  })
}
