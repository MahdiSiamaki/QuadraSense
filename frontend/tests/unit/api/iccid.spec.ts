import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { iccidFromImsi, imsiFromIccid, looksLikeIccid } from '@/api/iccid'
import DerivedIccid from '@/features/lookup/DerivedIccid.vue'

/*
  The ICCID the operator's numbering gives an IMSI - the same rule as the server's
  Sqm.Domain.Identifiers.Iccid, whose tests use the same made-up identifiers.
*/
describe('the ICCID derived from an IMSI', () => {
  it('is 8998, the IMSI digits 4 to 7, 000 and its last eight', () => {
    expect(iccidFromImsi('432110123456789')).toBe('8998110100023456789')
  })

  it('gives back the IMSI it came from', () => {
    expect(imsiFromIccid('8998110100023456789')).toBe('432110123456789')
    expect(imsiFromIccid(iccidFromImsi('432119999999999')!)).toBe('432119999999999')
  })

  it.each([['4321101234567890'], ['43211012345678'], ['43211******6789'], [''], [null]])(
    'exists only for a well-formed IMSI, not %s',
    (imsi) => {
      expect(iccidFromImsi(imsi)).toBeNull()
    },
  )

  it.each([['8998110112323456789'], ['8997110100023456789'], ['899811010002345678']])(
    'is not read into %s',
    (iccid) => {
      expect(imsiFromIccid(iccid)).toBeNull()
    },
  )

  it('is what Auto takes 19 digits beginning 8998 for', () => {
    expect(looksLikeIccid('8998110100023456789')).toBe(true)
    expect(looksLikeIccid('432110123456789')).toBe(false)
  })

  it('is shown under an IMSI, said to be derived - and not at all for a masked one', () => {
    const shown = mount(DerivedIccid, { props: { imsi: '432110123456789' } })
    expect(shown.text()).toBe('ICCID 8998110100023456789')
    expect(shown.attributes('title')).toContain('Derived from the IMSI')

    expect(mount(DerivedIccid, { props: { imsi: '43211******6789' } }).html()).toBe('<!--v-if-->')
  })
})
