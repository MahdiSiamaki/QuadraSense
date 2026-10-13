/**
 * Whether a risk list can be run at a threshold.
 *
 * A list holds values *more than* T, so the smallest value it asks for is T + 1. Values below the
 * rule's storage floor are never stored, so the list is complete only when T + 1 >= floor - the same
 * rule the risk page and the server apply. Below it, the widget refuses rather than show a list that
 * silently leaves out everything between T + 1 and the floor.
 */
import type { RiskRule } from '@/api/risk'

export function thresholdProblem(rule: Pick<RiskRule, 'floor' | 'threshold'>, override: number | null): string | null {
  if (override === null) {
    return rule.threshold === null ? 'This rule is not calibrated, so nothing is judged at its configured threshold.' : null
  }
  if (override + 1 < rule.floor) {
    return `Values below ${rule.floor} are not stored, so a threshold under ${rule.floor - 1} cannot be listed. The Explorer can count smaller values from the history.`
  }
  return null
}
