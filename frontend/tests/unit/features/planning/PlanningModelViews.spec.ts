import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { ref } from 'vue'

/*
  The band (N2) and satellite (N6) tabs show the server's classes. What they must never do: call a
  band the operator's when no list is configured, or fold a satellite radio into 3GPP NTN.
*/
const run = { runId: 1790000000000, asOfSequence: 246, asOfDate: '2026-10-10', publishedAt: '2026-10-27T05:00:00Z', hasNumbers: true }
const cls = (c: string, label: string, numbers: number) => ({ class: c, label, numbers, bindings: numbers, handsets: numbers, sharePercent: numbers / 10 })
const band = (technology: 'lte' | 'nr', b: number, operator: boolean, numbers: number) =>
  ({ technology, band: b, operator, numbers, bindings: numbers, handsets: numbers, sharePercent: numbers / 10 })

const notConfigured = {
  run,
  filtered: false,
  totalNumbers: 1000,
  operatorLte: [],
  operatorNr: [],
  lteMatch: [],
  nrMatch: [],
  architecture: [cls('arch-both', 'Both SA and NSA stated', 40), cls('arch-not-stated', 'Neither stated', 5)],
  ambiguous: cls('ambiguous', 'A band entry the parser could not read', 2),
  lteBands: [band('lte', 3, false, 600), band('lte', 20, false, 30)],
  nrBands: [band('nr', 78, false, 45)],
  noData: [
    { topic: 'A band in use', reason: 'The operator’s files never say which band was used.' },
    { topic: 'The operator’s bands', reason: 'Not configured for LTE or NR.' },
  ],
}

const configured = {
  ...notConfigured,
  operatorLte: [3],
  operatorNr: [78],
  lteMatch: [cls('lte-all', 'Every one of the operator’s LTE bands', 600), cls('lte-none', 'None of them', 30)],
  nrMatch: [cls('nr-all', 'Every one of the operator’s NR bands', 45)],
  lteBands: [band('lte', 3, true, 600), band('lte', 20, false, 30)],
  nrBands: [band('nr', 78, true, 45)],
  noData: [notConfigured.noData[0]!],
}

const ntn = {
  run,
  filtered: false,
  totalNumbers: 1000,
  classes: [
    cls('ntn', '3GPP NTN capable', 12),
    cls('satellite', 'A satellite radio listed, not 3GPP NTN', 3),
    cls('not-capable', 'Stated: no NTN', 300),
    cls('not-stated', 'Not stated', 680),
  ],
  byMode: [{ key: 'NTN and TN', label: 'NTN and TN', tac: null, numbers: 12, bindings: 12, handsets: 12 }],
  byOrbit: [],
  byBand: [],
  notClassified: cls('not-classified', 'Not classifiable', 5),
  modelTrend: [],
  noData: [{ topic: 'Satellite service in use', reason: 'Capability is not service.' }],
}

const bands = ref<unknown>(notConfigured)
const query = <T>(value: T) => ({ data: value, isPending: ref(false), isError: ref(false), error: ref(null) })

vi.mock('@/api/planning', () => ({
  useBands: () => query(bands),
  useNtn: () => query(ref(ntn)),
}))

const { default: PlanningBandsView } = await import('@/features/planning/PlanningBandsView.vue')
const { default: PlanningNtnView } = await import('@/features/planning/PlanningNtnView.vue')

const stubs = { PlanningBreakdown: true, PlanningTrendChart: true }
const props = { run: run.runId, filter: {}, canExport: false, canOpenDevice: false }

describe('Planning bands view', () => {
  it('matches nothing and says why when the operator’s bands are not configured', () => {
    bands.value = notConfigured
    const text = mount(PlanningBandsView, { props, global: { stubs } }).text()

    expect(text).toContain('The operator’s bands: no data')
    expect(text).toContain('The operator’s LTE bands are not configured.')
    expect(text).toContain('Band 3')
    expect(text).not.toContain('operator\n')
    expect(text).not.toMatch(/Band 3\s*operator/)
  })

  it('marks the operator’s bands once they are configured', () => {
    bands.value = configured
    const text = mount(PlanningBandsView, { props, global: { stubs } }).text()

    expect(text).toMatch(/Band 3\s*operator/)
    expect(text).not.toMatch(/Band 20\s*operator/)
    expect(text).toContain('Every one of the operator’s LTE bands')
    expect(text).toContain('LTE 3; NR n78')
  })
})

describe('Planning satellite view', () => {
  it('keeps a satellite radio apart from 3GPP NTN and says capability is not service', () => {
    const text = mount(PlanningNtnView, { props, global: { stubs } }).text()

    expect(text).toContain('3GPP NTN capable')
    expect(text).toContain('A satellite radio listed, not 3GPP NTN')
    expect(text).toContain('Satellite service in use: no data')
    expect(text).toContain('No NTN model on the network.')
  })
})
