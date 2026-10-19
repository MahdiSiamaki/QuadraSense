import { describeTerm, IMSI_CONSTANT_PREFIX, IMSI_LENGTH, MIN_IMSI_PREFIX } from '@/api/imsi'
import { Permission } from '@/features/auth/useAuth'

/**
 * What the Explorer's simple search box was given, read with the rules the server applies.
 *
 * Duplicated from the server on purpose, as `describeTerm` is: this copy exists to say what the
 * input will be taken as before anything is sent, and the server's - `DeviceSearchTerm.Classify`
 * and `Msisdn` - is the one that decides. A mistake here shows the wrong hint or keeps a tab
 * closed; it cannot let a request through that the server would not refuse itself.
 */

/** The identifier types the search box offers. ICCID is listed, and cannot be chosen yet. */
export type IdentifierKind = 'msisdn' | 'imsi' | 'imei' | 'iccid'
export type KindChoice = 'auto' | IdentifierKind

/** A searchable identifier: one number, one handset, one SIM - or a SIM prefix. */
export interface Resolved {
  kind: 'msisdn' | 'imsi' | 'imei'
  /** Exactly what is sent to the server: digits only, a number in its national form. */
  value: string
  /** An IMSI shorter than a complete one: a search over many SIMs, not one SIM. */
  prefix: boolean
}

export type Reading =
  | { ok: true; resolved: Resolved; note: string }
  | { ok: false; problem: string | null; tac?: string }

/** The permission each kind of lookup needs, server-side and here. */
export const KIND_PERMISSION: Record<Resolved['kind'], string> = {
  msisdn: Permission.LookupSubscriber,
  imsi: Permission.LookupImsi,
  imei: Permission.LookupImei,
}

export const KIND_NAME: Record<Resolved['kind'], string> = {
  msisdn: 'subscriber number',
  imsi: 'SIM (IMSI)',
  imei: 'handset (IMEI)',
}

/** The feed's lengths, which do not overlap - the reason "Auto" can work at all (DeviceSearchTerm). */
const MSISDN_LENGTH = 10
const IMEI_LENGTH = 14
const TAC_LENGTH = 8

/** The separators people paste with an identifier. Anything else that is not a digit is a typo. */
const SEPARATORS = /[\s\-._()/+]/g

/**
 * A number in the national form the feed stores, from any way people write one, or null.
 * The same order and guards as `Msisdn.ToNationalSignificant`: 00, then 98, then the trunk 0.
 */
export function nationalNumber(digits: string): string | null {
  let d = digits
  if (d.length > 2 && d.startsWith('00')) d = d.slice(2)
  if (d.startsWith('98') && (d.length === 12 || (d.length === 13 && d[2] === '0'))) d = d.slice(2)
  if (d.length > 1 && d.startsWith('0')) d = d.slice(1)
  return d.length === MSISDN_LENGTH ? d : null
}

function asNumber(digits: string): Reading {
  const national = nationalNumber(digits)
  return national
    ? { ok: true, resolved: { kind: 'msisdn', value: national, prefix: false }, note: 'A subscriber number.' }
    : {
        ok: false,
        problem: `A subscriber number is ${MSISDN_LENGTH} digits, or written with 0, +98 or 0098 in front.`,
      }
}

function asImsi(raw: string): Reading {
  const term = describeTerm(raw)
  if (!term.ok) return { ok: false, problem: term.problem }
  return {
    ok: true,
    resolved: { kind: 'imsi', value: term.digits, prefix: !term.isExact },
    note: term.isExact
      ? term.digits.length === IMSI_LENGTH
        ? 'A complete IMSI: one SIM.'
        : `${term.digits.length} digits: an IMSI is ${IMSI_LENGTH}, so this is shown for review.`
      : `${term.digits.length} digits: a prefix search over many SIMs.`,
  }
}

function asImei(digits: string): Reading {
  return digits.length === IMEI_LENGTH
    ? { ok: true, resolved: { kind: 'imei', value: digits, prefix: false }, note: 'A handset (IMEI).' }
    : {
        ok: false,
        problem:
          digits.length === IMEI_LENGTH + 1
            ? `This feed carries IMEIs as ${IMEI_LENGTH} digits, without the check digit. Leave off the last digit.`
            : `A handset (IMEI) is ${IMEI_LENGTH} digits in this feed.`,
      }
}

/** Reads `raw` as the chosen kind, or - for "Auto" - as whichever kind its shape allows. */
export function readIdentifier(raw: string, choice: KindChoice): Reading {
  const text = raw.trim()
  if (text === '') return { ok: false, problem: null }

  const digits = text.replace(SEPARATORS, '')
  if (!/^\d+$/.test(digits)) return { ok: false, problem: 'Digits only: a number, an IMSI or an IMEI.' }

  switch (choice) {
    case 'msisdn':
      return asNumber(digits)
    case 'imsi':
      return asImsi(text)
    case 'imei':
      return asImei(digits)
    case 'iccid':
      return { ok: false, problem: 'ICCID is not in the feed yet.' }
  }

  // Auto. A dialled form (a plus, or 11-13 digits) is tried as a number first, as the server does;
  // 0098 plus ten digits is fourteen, an IMEI's length, and stays an IMEI.
  if ((text.includes('+') || [11, 12, 13].includes(digits.length)) && nationalNumber(digits)) {
    return asNumber(digits)
  }
  if (digits.length === MSISDN_LENGTH) return asNumber(digits)
  if (digits.length === IMEI_LENGTH) return asImei(digits)
  if (digits.length >= IMSI_LENGTH) return asImsi(text)
  if (digits.length === TAC_LENGTH) {
    return { ok: false, problem: '8 digits is a model (TAC), not one identifier.', tac: digits }
  }
  if (digits.startsWith(IMSI_CONSTANT_PREFIX) && digits.length >= MIN_IMSI_PREFIX) return asImsi(text)

  return {
    ok: false,
    problem:
      `${digits.length} digits does not say what it is: a number is ${MSISDN_LENGTH}, a handset ` +
      `${IMEI_LENGTH}, a SIM ${IMSI_LENGTH}. To search SIMs by the start of their IMSI, choose IMSI.`,
  }
}
