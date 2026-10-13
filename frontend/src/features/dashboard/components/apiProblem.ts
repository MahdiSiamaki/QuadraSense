import { ApiError } from '@/api/client'

/** What went wrong with a dashboard request, in words, the way the other dialogs say it. */
export function problemText(error: unknown, fallback = 'Something went wrong.'): string | null {
  if (!error) return null
  if (error instanceof ApiError) {
    if (error.status === 404) return 'This dashboard no longer exists, or it is no longer shared with you.'
    return error.fieldErrors[0]?.message ?? error.problem?.detail ?? error.problem?.title ?? `Server responded ${error.status}.`
  }
  return error instanceof Error && error.message ? `${fallback} ${error.message}` : 'Could not reach the server.'
}

/** The revision that won a save, from a 409 "Changed since you opened it". */
export function conflictOf(error: unknown): { currentRevision: number; updatedAt: string | null; updatedBy: string | null } | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null
  const problem = error.problem as (Record<string, unknown> & { title?: string }) | null
  if (!problem || typeof problem.currentRevision !== 'number') return null
  return {
    currentRevision: problem.currentRevision,
    updatedAt: typeof problem.updatedAt === 'string' ? problem.updatedAt : null,
    updatedBy: typeof problem.updatedBy === 'string' ? problem.updatedBy : null,
  }
}
