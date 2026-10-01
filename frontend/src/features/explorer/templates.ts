import type { ExplorerQueryRequest } from '@/api/explorer'
import { daysBefore } from './model'

/*
  Ready-made questions. Each one is only a query the builder could compose by hand: choosing a
  template fills the builder and runs it, so what it asked is visible and can be changed.

  The defaults are measured, not guessed. With them, every template plans within the budget on
  the real data - asserted by ExplorerRealDataPlanTests.Every_template_plans_within_the_budget_
  with_its_defaults, which repeats these shapes; change one here, change it there. Estimated rows
  on 2026-09-30, for identifiers sampled from the data (so they move a little between runs):

    number history, 90 days             1.3M   Light
    SIM history, 90 days            0.9-1.2M   Light
    handset history, 90 days                   needs the event log's IMEI index; without it 648M, refused
    SIMs on many handsets, 1 day       11.4M   Moderate - ran in 2.4-5.5 s
    handsets of a model (Galaxy A01)    7.4M   Moderate
    numbers with many SIMs, prefix  0.16-0.23M Light
    unknown TAC, number prefix      0.4-0.5M   Light
    one number, SIM or handset      0.1-0.5M   Light (the look-up templates)
*/

export type TemplateCategory = 'Look up' | 'History' | 'Observations' | 'Data quality'

export interface TemplateParam {
  key: string
  label: string
  kind: 'msisdn' | 'imsi' | 'imei' | 'prefix' | 'text' | 'date' | 'count'
  placeholder?: string
  help?: string
}

export interface TemplateContext {
  /** The latest day in the event log, yyyy-MM-dd. */
  dataThrough: string
}

export interface ExplorerTemplate {
  id: string
  category: TemplateCategory
  title: string
  description: string
  /** Shown beside the results: what the answer is not. */
  caution?: string
  params: TemplateParam[]
  defaults: (ctx: TemplateContext) => Record<string, string>
  build: (values: Record<string, string>, ctx: TemplateContext) => ExplorerQueryRequest
}

const eq = (field: string, value: string) => ({ field, operator: 'Equals' as const, values: [value] })
const between = (field: string, from: string, to: string) => ({
  field,
  operator: 'Between' as const,
  values: [from, to],
})
const and = (...children: object[]) => ({ logic: 'And' as const, children })

const NOT_A_DEVICE =
  'An IMEI identifies one radio, not one phone: a dual-SIM handset has two, and nothing in the feed says which two belong together.'

const ACTIVE_MEANS =
  'Active means the feed has not yet removed the binding - not that the SIM is in the handset today.'

const history = (field: 'msisdn' | 'imsi' | 'imei', columns: string[]) =>
  (v: Record<string, string>): ExplorerQueryRequest => ({
    dataset: 'Events',
    where: and(eq(field, v['id'] ?? ''), between('date', v['from'] ?? '', v['to'] ?? '')),
    columns,
    sort: [{ field: 'date', descending: true }],
    pageSize: 100,
  })

export const TEMPLATES: ExplorerTemplate[] = [
  {
    id: 'number-bindings',
    category: 'Look up',
    title: 'SIMs and handsets of a number',
    description: 'Every SIM and handset a phone number has been bound to, and which are still active.',
    caution: ACTIVE_MEANS,
    params: [{ key: 'id', label: 'Phone number', kind: 'msisdn', placeholder: '0912 345 6789' }],
    defaults: () => ({ id: '' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(eq('msisdn', v['id'] ?? '')),
      columns: ['imsi', 'imei', 'brand', 'model', 'active', 'lastChangeDate'],
      sort: [{ field: 'lastChangeDate', descending: true }],
    }),
  },
  {
    id: 'sim-bindings',
    category: 'Look up',
    title: 'Numbers and handsets of a SIM',
    description: 'Every number and handset a SIM has been bound to.',
    caution: ACTIVE_MEANS,
    params: [{ key: 'id', label: 'SIM (IMSI)', kind: 'imsi', placeholder: '432 11 …' }],
    defaults: () => ({ id: '' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(eq('imsi', v['id'] ?? '')),
      columns: ['msisdn', 'imei', 'brand', 'model', 'active', 'lastChangeDate'],
      sort: [{ field: 'lastChangeDate', descending: true }],
    }),
  },
  {
    id: 'handset-bindings',
    category: 'Look up',
    title: 'Numbers and SIMs of a handset',
    description: 'Every number and SIM seen with one IMEI.',
    caution: NOT_A_DEVICE,
    params: [{ key: 'id', label: 'Handset (IMEI, 14 digits)', kind: 'imei' }],
    defaults: () => ({ id: '' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(eq('imei', v['id'] ?? '')),
      columns: ['msisdn', 'imsi', 'model', 'active', 'lastChangeDate'],
      sort: [{ field: 'lastChangeDate', descending: true }],
    }),
  },
  {
    id: 'number-counts',
    category: 'Look up',
    title: 'How many SIMs and handsets a number has had',
    description: 'One row of counts - ever, and still active - without listing any identifier.',
    caution: NOT_A_DEVICE,
    params: [{ key: 'id', label: 'Phone number', kind: 'msisdn', placeholder: '0912 345 6789' }],
    defaults: () => ({ id: '' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(eq('msisdn', v['id'] ?? '')),
      measures: [
        { name: 'bindings', aggregate: 'Count' },
        { name: 'active_bindings', aggregate: 'Count', activeOnly: true },
        { name: 'sims', aggregate: 'CountDistinct', field: 'imsi' },
        { name: 'handsets', aggregate: 'CountDistinct', field: 'imei' },
        { name: 'last_change', aggregate: 'Max', field: 'lastChangeDate' },
      ],
    }),
  },
  {
    id: 'number-history',
    category: 'History',
    title: 'History of a number',
    description: 'Each day the feed added or removed a binding of this number.',
    params: [
      { key: 'id', label: 'Phone number', kind: 'msisdn', placeholder: '0912 345 6789' },
      { key: 'from', label: 'From', kind: 'date' },
      { key: 'to', label: 'To', kind: 'date' },
    ],
    defaults: (ctx) => ({ id: '', from: daysBefore(ctx.dataThrough, 89), to: ctx.dataThrough }),
    build: history('msisdn', ['date', 'change', 'imsi', 'imei', 'model']),
  },
  {
    id: 'sim-history',
    category: 'History',
    title: 'History of a SIM',
    description: 'Each day the feed added or removed a binding of this SIM: its numbers and handsets over time.',
    params: [
      { key: 'id', label: 'SIM (IMSI)', kind: 'imsi' },
      { key: 'from', label: 'From', kind: 'date' },
      { key: 'to', label: 'To', kind: 'date' },
    ],
    defaults: (ctx) => ({ id: '', from: daysBefore(ctx.dataThrough, 89), to: ctx.dataThrough }),
    build: history('imsi', ['date', 'change', 'msisdn', 'imei', 'model']),
  },
  {
    id: 'handset-history',
    category: 'History',
    title: 'History of a handset',
    description: 'Each day the feed added or removed a binding of this IMEI: the SIMs that passed through it.',
    caution: NOT_A_DEVICE,
    params: [
      { key: 'id', label: 'Handset (IMEI, 14 digits)', kind: 'imei' },
      { key: 'from', label: 'From', kind: 'date' },
      { key: 'to', label: 'To', kind: 'date' },
    ],
    defaults: (ctx) => ({ id: '', from: daysBefore(ctx.dataThrough, 89), to: ctx.dataThrough }),
    build: history('imei', ['date', 'change', 'msisdn', 'imsi']),
  },
  {
    id: 'sims-many-handsets',
    category: 'Observations',
    title: 'SIMs seen with many handsets in one day',
    description: 'SIMs whose bindings on one day of the feed named more than a given number of IMEIs.',
    caution:
      'An observation, not a finding. A SIM with many IMEIs in one day can be the feed itself - 87,229 SIMs were above 2 on 2026-09-10 and 520,442 on 2026-09-26, after the feed changed on 2026-09-15 - a test or lab SIM, or a handset being re-flashed. Check the day on the dashboard, where days the feed looked wrong are shaded, before reading more into it.',
    params: [
      { key: 'day', label: 'Day', kind: 'date' },
      { key: 'n', label: 'More than … handsets', kind: 'count' },
    ],
    defaults: (ctx) => ({ day: ctx.dataThrough, n: '5' }),
    build: (v) => ({
      dataset: 'Events',
      where: and(between('date', v['day'] ?? '', v['day'] ?? '')),
      groupBy: ['imsi'],
      measures: [{ name: 'handsets', aggregate: 'CountDistinct', field: 'imei' }],
      having: { field: 'handsets', operator: 'GreaterThan', values: [v['n'] ?? '5'] },
      sort: [{ field: 'handsets', descending: true }],
    }),
  },
  {
    id: 'model-handsets-many-sims',
    category: 'Observations',
    title: 'Handsets of a model with many SIMs',
    description: 'IMEIs of one model that have been bound to more than a given number of SIMs.',
    caution: `${NOT_A_DEVICE} Shops, repair benches and shared phones all put many SIMs through one handset.`,
    params: [
      { key: 'model', label: 'Model (GSMA marketing name)', kind: 'text', placeholder: 'Galaxy A01' },
      { key: 'n', label: 'More than … SIMs', kind: 'count' },
    ],
    defaults: () => ({ model: '', n: '10' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(eq('model', v['model'] ?? '')),
      groupBy: ['imei'],
      measures: [
        { name: 'sims', aggregate: 'CountDistinct', field: 'imsi' },
        { name: 'numbers', aggregate: 'CountDistinct', field: 'msisdn' },
      ],
      having: { field: 'sims', operator: 'GreaterThan', values: [v['n'] ?? '10'] },
      sort: [{ field: 'sims', descending: true }],
    }),
  },
  {
    id: 'numbers-many-sims',
    category: 'Observations',
    title: 'Numbers with many SIMs, by number prefix',
    description: 'Numbers in a range that have been bound to more than a given number of SIMs.',
    caution: 'A SIM replacement is ordinary; several in a short time is worth a look, not a conclusion.',
    params: [
      { key: 'prefix', label: 'Number prefix (at least 6 digits)', kind: 'prefix', placeholder: '912345' },
      { key: 'n', label: 'More than … SIMs', kind: 'count' },
    ],
    defaults: () => ({ prefix: '', n: '3' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and({ field: 'msisdn', operator: 'StartsWith', values: [v['prefix'] ?? ''] }),
      groupBy: ['msisdn'],
      measures: [
        { name: 'sims', aggregate: 'CountDistinct', field: 'imsi' },
        { name: 'active_sims', aggregate: 'CountDistinct', field: 'imsi', activeOnly: true },
      ],
      having: { field: 'sims', operator: 'GreaterThan', values: [v['n'] ?? '3'] },
      sort: [{ field: 'sims', descending: true }],
    }),
  },
  {
    id: 'unknown-tac',
    category: 'Data quality',
    title: 'Handsets GSMA does not know, by number prefix',
    description: 'Bindings whose IMEI starts with a TAC that is not in the GSMA database.',
    caution:
      'A data-quality question, not a risk one: an unknown TAC is most often a new or grey-market model, or an IMEI the feed garbled.',
    params: [{ key: 'prefix', label: 'Number prefix (at least 6 digits)', kind: 'prefix', placeholder: '912345' }],
    defaults: () => ({ prefix: '' }),
    build: (v) => ({
      dataset: 'Bindings',
      where: and(
        { field: 'msisdn', operator: 'StartsWith', values: [v['prefix'] ?? ''] },
        { field: 'model', operator: 'IsNull', values: [] },
      ),
      columns: ['msisdn', 'imei', 'tac', 'active', 'lastChangeDate'],
    }),
  },
]
