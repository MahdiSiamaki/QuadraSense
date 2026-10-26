import { IMSI_LENGTH } from './imsi'

/*
  ICCID, derived from the IMSI by the operator's numbering: the feed carries no ICCID.

  Duplicated from the server's Sqm.Domain.Identifiers.Iccid on purpose, as describeTerm is: this
  copy shows a SIM's ICCID beside its IMSI and reads an ICCID typed into the search box; the
  server's is the one exports use. The rule is the product owner's (2026-10-26):
  CONCATENATE("8998", MID(imsi, 4, 4), "000", RIGHT(imsi, 8)). It keeps every IMSI digit after the
  MCC, and every IMSI in the feed has the MCC 432, so it runs backwards exactly.
*/

/** An ICCID's length under this numbering. */
export const ICCID_LENGTH = 19

const PREFIX = '8998'
const FILLER = '000'
const MCC = '432'

/** What every view says about the ICCID it shows. */
export const ICCID_NOTE = "Derived from the IMSI by the operator's numbering; the feed carries no ICCID."

/** The ICCID of a well-formed, 15-digit IMSI; null for anything else, a masked IMSI included. */
export function iccidFromImsi(imsi: string | null | undefined): string | null {
  if (!imsi || imsi.length !== IMSI_LENGTH || !/^\d+$/.test(imsi)) return null
  return `${PREFIX}${imsi.slice(3, 7)}${FILLER}${imsi.slice(-8)}`
}

/** The IMSI an ICCID of this numbering came from; null when it is not one. */
export function imsiFromIccid(iccid: string): string | null {
  if (iccid.length !== ICCID_LENGTH || !/^\d+$/.test(iccid)) return null
  if (!iccid.startsWith(PREFIX) || iccid.slice(8, 11) !== FILLER) return null
  return `${MCC}${iccid.slice(4, 8)}${iccid.slice(11)}`
}

/** Whether digits look like an ICCID of this numbering - what "Auto" reads them as. */
export function looksLikeIccid(digits: string): boolean {
  return digits.length === ICCID_LENGTH && digits.startsWith(PREFIX)
}
