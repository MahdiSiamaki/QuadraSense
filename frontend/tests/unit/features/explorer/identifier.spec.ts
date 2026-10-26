import { describe, expect, it } from 'vitest'
import { nationalNumber, readIdentifier, type Reading } from '@/features/explorer/identifier'

/*
  The Explorer's search box reads what it is given with the server's own rules (DeviceSearchTerm,
  Msisdn, the IMSI term check). These are the cases those rules settle - each one a way an operator
  actually pastes an identifier.
*/
function resolved(r: Reading) {
  if (!r.ok) throw new Error(`not read: ${r.problem}`)
  return r.resolved
}

describe('a subscriber number', () => {
  it.each([
    ['9121234567', '9121234567'],
    ['09121234567', '9121234567'],
    ['+98 912 123 4567', '9121234567'],
    ['00989121234567', '9121234567'],
    ['+98 0912 123 4567', '9121234567'],
    ['(0912) 123-4567', '9121234567'],
  ])('%s is read as %s', (raw, national) => {
    expect(resolved(readIdentifier(raw, 'msisdn'))).toEqual({ kind: 'msisdn', value: national, prefix: false })
  })

  it('is found by Auto in every written form but one', () => {
    expect(resolved(readIdentifier('09121234567', 'auto')).kind).toBe('msisdn')
    expect(resolved(readIdentifier('+98 912 123 4567', 'auto')).kind).toBe('msisdn')
    expect(resolved(readIdentifier('989121234567', 'auto')).kind).toBe('msisdn')
    // 0098 and ten digits is fourteen digits - an IMEI's length - and the server keeps it one.
    expect(resolved(readIdentifier('00989121234567', 'auto')).kind).toBe('imei')
  })

  it('strips a country code only where a national number remains', () => {
    expect(nationalNumber('9812345678')).toBe('9812345678')
    expect(nationalNumber('98123')).toBeNull()
  })

  it('says what a number is when it is not one', () => {
    const r = readIdentifier('912123456', 'msisdn')
    expect(r.ok).toBe(false)
    expect(!r.ok && r.problem).toMatch(/10 digits/)
  })
})

describe('a SIM', () => {
  it('is one SIM at 15 digits, and a prefix below', () => {
    expect(resolved(readIdentifier('432113991761332', 'auto'))).toEqual({ kind: 'imsi', value: '432113991761332', prefix: false })
    expect(resolved(readIdentifier('4321139917', 'imsi'))).toEqual({ kind: 'imsi', value: '4321139917', prefix: true })
  })

  it('is a prefix in Auto when it starts 43211 and has no other identifier length', () => {
    expect(resolved(readIdentifier('43211399176', 'auto'))).toEqual({ kind: 'imsi', value: '43211399176', prefix: true })
    // Ten digits is a number's length, so Auto reads it as one; choosing IMSI makes it a prefix.
    expect(resolved(readIdentifier('4321139917', 'auto')).kind).toBe('msisdn')
  })

  it('refuses a prefix too short to narrow anything, and says why', () => {
    const r = readIdentifier('43211', 'imsi')
    expect(r.ok).toBe(false)
    expect(!r.ok && r.problem).toMatch(/starts 43211/)
  })

  it('keeps the 16- and 17-digit rows reachable for review', () => {
    expect(resolved(readIdentifier('4321139917613321', 'imsi'))).toEqual({ kind: 'imsi', value: '4321139917613321', prefix: false })
  })
})

describe('a handset', () => {
  it('is 14 digits', () => {
    expect(resolved(readIdentifier('35004012345678', 'auto'))).toEqual({ kind: 'imei', value: '35004012345678', prefix: false })
    expect(resolved(readIdentifier('3500 4012 3456 78', 'imei')).value).toBe('35004012345678')
  })

  it('explains a 15-digit IMEI: the feed has no check digit', () => {
    const r = readIdentifier('350040123456789', 'imei')
    expect(!r.ok && r.problem).toMatch(/check digit/)
  })
})

describe('what is not an identifier', () => {
  it('a TAC is a model, and is pointed at its page', () => {
    const r = readIdentifier('35004012', 'auto')
    expect(r.ok).toBe(false)
    expect(!r.ok && r.tac).toBe('35004012')
  })

  it('an ICCID is searched as the IMSI it was derived from', () => {
    // 8998 + IMSI digits 4-7 + 000 + its last eight: the owner's numbering, 2026-10-26.
    for (const choice of ['iccid', 'auto'] as const) {
      const r = readIdentifier('8998 1101 000 23456789', choice)
      expect(r.ok && r.resolved).toEqual({ kind: 'imsi', value: '432110123456789', prefix: false })
    }
  })

  it('a 19-digit value that is not of that numbering is refused, not guessed at', () => {
    expect(readIdentifier('8998110112323456789', 'iccid').ok).toBe(false) // no 000 in digits 9-11
    expect(readIdentifier('8997110100023456789', 'iccid').ok).toBe(false)
  })

  it('letters are a typo, not noise', () => {
    expect(readIdentifier('0912 l23 4567', 'auto').ok).toBe(false)
  })

  it('blank is nothing to say yet', () => {
    expect(readIdentifier('   ', 'auto')).toEqual({ ok: false, problem: null })
  })

  it('a length Auto cannot place asks for the type', () => {
    const r = readIdentifier('123456789012', 'auto')
    expect(!r.ok && r.problem).toMatch(/choose IMSI/)
  })
})
