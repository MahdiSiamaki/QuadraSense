/**
 * The count unit in words.
 *
 * The grain is a binding - number + SIM + handset. Subscribers and handsets are distinct counts the
 * marts estimate with uniq() (HyperLogLog, ~0.5% error), so wherever one is shown it says so.
 */
import type { CountBy } from '@/api/dashboard'

/** "Counted by bindings." / "Counted by subscribers (estimated)." - the first words of a caption. */
export function countedBy(countBy: CountBy): string {
  return countBy === 'bindings' ? 'Counted by bindings.' : `Counted by ${countBy} (estimated).`
}

/** The unit beside a figure: a tooltip's "1,234 subscribers (est.)", a column heading. */
export function countUnit(countBy: CountBy): string {
  return countBy === 'bindings' ? 'bindings' : `${countBy} (est.)`
}
