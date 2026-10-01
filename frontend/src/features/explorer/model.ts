import type {
  ExplorerAggregate,
  ExplorerDataset,
  ExplorerField,
  ExplorerFieldType,
  ExplorerFilter,
  ExplorerOperator,
  ExplorerQueryRequest,
} from '@/api/explorer'

/*
  The builder's working copy of a query.

  The request the API takes is plain data; the builder needs a little more - an id per node so
  Vue can keep focus in the right input while nodes are added and removed, and In lists kept as
  the text the user typed. `toRequest` turns a draft into a request and remembers which node
  produced which request path, so the server's validation messages ("where.children[1].values[0]")
  land next to the condition they are about rather than in a list at the top.
*/

let counter = 0
export const nextId = (): string => `n${++counter}`

export interface ConditionDraft {
  kind: 'condition'
  id: string
  field: string
  operator: ExplorerOperator
  /** One entry per input; for In and NotIn, one entry holding the typed list. */
  values: string[]
  not: boolean
}

export interface GroupDraft {
  kind: 'group'
  id: string
  logic: 'And' | 'Or'
  not: boolean
  children: NodeDraft[]
}

export type NodeDraft = ConditionDraft | GroupDraft

export interface MeasureDraft {
  id: string
  name: string
  aggregate: ExplorerAggregate
  field: string | null
  activeOnly: boolean
}

export interface SortDraft {
  field: string
  descending: boolean
}

export interface QueryDraft {
  dataset: ExplorerDataset
  where: GroupDraft
  /** Groups and measures rather than rows. */
  grouped: boolean
  columns: string[]
  groupBy: string[]
  measures: MeasureDraft[]
  having: GroupDraft
  sort: SortDraft[]
  pageSize: number
}

export function emptyGroup(logic: 'And' | 'Or' = 'And'): GroupDraft {
  return { kind: 'group', id: nextId(), logic, not: false, children: [] }
}

export function emptyDraft(dataset: ExplorerDataset = 'Bindings'): QueryDraft {
  return {
    dataset,
    where: emptyGroup(),
    grouped: false,
    columns: [],
    groupBy: [],
    measures: [],
    having: emptyGroup(),
    sort: [],
    pageSize: 50,
  }
}

// ------------------------------------------------------------------ operators

/** How many values an operator takes. */
export function arity(op: ExplorerOperator): { min: number; max: number } {
  switch (op) {
    case 'IsNull':
    case 'IsNotNull':
      return { min: 0, max: 0 }
    case 'Between':
      return { min: 2, max: 2 }
    case 'In':
    case 'NotIn':
      return { min: 1, max: Number.POSITIVE_INFINITY }
    default:
      return { min: 1, max: 1 }
  }
}

export const isList = (op: ExplorerOperator): boolean => op === 'In' || op === 'NotIn'

const DATE_WORDS: Partial<Record<ExplorerOperator, string>> = {
  Equals: 'on',
  NotEquals: 'not on',
  GreaterThan: 'after',
  GreaterOrEqual: 'on or after',
  LessThan: 'before',
  LessOrEqual: 'on or before',
  Between: 'between',
}

const WORDS: Record<ExplorerOperator, string> = {
  Equals: 'is',
  NotEquals: 'is not',
  GreaterThan: 'more than',
  GreaterOrEqual: 'at least',
  LessThan: 'fewer than',
  LessOrEqual: 'at most',
  In: 'is any of',
  NotIn: 'is none of',
  Contains: 'contains',
  StartsWith: 'starts with',
  Between: 'between',
  IsNull: 'is empty',
  IsNotNull: 'is not empty',
}

/** An operator in words, for the field it applies to. */
export function operatorLabel(op: ExplorerOperator, type: ExplorerFieldType | undefined): string {
  return (type === 'Date' ? DATE_WORDS[op] : undefined) ?? WORDS[op]
}

/** The operators a Having condition takes: comparisons of a count or a date. */
export const MEASURE_OPERATORS: ExplorerOperator[] = [
  'Equals',
  'NotEquals',
  'GreaterThan',
  'GreaterOrEqual',
  'LessThan',
  'LessOrEqual',
  'Between',
]

/** A condition on a field, with the values that operator needs. */
export function newCondition(field: ExplorerField | undefined, operator?: ExplorerOperator): ConditionDraft {
  const op = operator ?? field?.operators.find((o) => o === 'Equals') ?? field?.operators[0] ?? 'Equals'
  return {
    kind: 'condition',
    id: nextId(),
    field: field?.name ?? '',
    operator: op,
    values: blankValues(op, field?.type),
    not: false,
  }
}

export function blankValues(op: ExplorerOperator, type?: ExplorerFieldType): string[] {
  const { min } = arity(op)
  if (isList(op)) return ['']
  if (type === 'Boolean') return ['true']
  return Array.from({ length: min }, () => '')
}

// ------------------------------------------------------------------ measures

const IDENTITY_NAMES: Record<string, string> = { msisdn: 'numbers', imsi: 'sims', imei: 'handsets' }

/** A readable, valid, unused measure name: letters, digits and _, starting with a letter. */
export function suggestMeasureName(
  m: Pick<MeasureDraft, 'aggregate' | 'field' | 'activeOnly'>,
  taken: Iterable<string>,
): string {
  const base =
    m.aggregate === 'Count'
      ? 'count'
      : m.aggregate === 'CountDistinct'
        ? (IDENTITY_NAMES[m.field ?? ''] ?? `distinct_${m.field ?? 'values'}`)
        : `${m.aggregate === 'Min' ? 'first' : 'last'}_${m.field ?? 'date'}`
  const stem = (m.activeOnly ? `active_${base}` : base).replace(/[^A-Za-z0-9_]/g, '_').slice(0, 28)
  const used = new Set(taken)
  if (!used.has(stem)) return stem
  for (let i = 2; ; i++) if (!used.has(`${stem}_${i}`)) return `${stem}_${i}`
}

// ------------------------------------------------------------------ draft -> request

/** Splits a typed list on commas, semicolons and line breaks - not spaces, which model names contain. */
export function splitList(text: string): string[] {
  return text
    .split(/[,;\n\r]+/)
    .map((v) => v.trim())
    .filter((v) => v.length > 0)
}

export interface BuiltRequest {
  request: ExplorerQueryRequest
  /** Request path -> the draft node it came from. */
  paths: Map<string, string>
}

function toFilter(node: NodeDraft, path: string, paths: Map<string, string>): ExplorerFilter {
  paths.set(path, node.id)
  if (node.kind === 'group') {
    return {
      logic: node.logic,
      not: node.not,
      children: node.children.map((child, i) => toFilter(child, `${path}.children[${i}]`, paths)),
    }
  }

  const values = isList(node.operator)
    ? splitList(node.values[0] ?? '')
    : node.values.slice(0, arity(node.operator).max).map((v) => v.trim())

  return { field: node.field, operator: node.operator, values, not: node.not }
}

/** The root group, or nothing when it is empty. Always sent as a group, so paths stay stable. */
function root(group: GroupDraft, path: string, paths: Map<string, string>): ExplorerFilter | null {
  return group.children.length === 0 ? null : toFilter(group, path, paths)
}

export function toRequest(draft: QueryDraft, page = 1): BuiltRequest {
  const paths = new Map<string, string>()

  const request: ExplorerQueryRequest = {
    dataset: draft.dataset,
    where: root(draft.where, 'where', paths),
    columns: draft.grouped || draft.columns.length === 0 ? null : [...draft.columns],
    groupBy: draft.grouped && draft.groupBy.length > 0 ? [...draft.groupBy] : null,
    measures: draft.grouped
      ? draft.measures.map((m) => ({
          name: m.name.trim(),
          aggregate: m.aggregate,
          field: m.aggregate === 'Count' ? null : m.field,
          activeOnly: m.activeOnly,
        }))
      : null,
    having: draft.grouped ? root(draft.having, 'having', paths) : null,
    sort: draft.sort.length > 0 ? draft.sort.map((s) => ({ field: s.field, descending: s.descending })) : null,
    page,
    pageSize: draft.pageSize,
  }

  return { request, paths }
}

// ------------------------------------------------------------------ request -> draft

function fromFilter(filter: ExplorerFilter): NodeDraft {
  if (filter.logic) {
    return {
      kind: 'group',
      id: nextId(),
      logic: filter.logic,
      not: filter.not ?? false,
      children: (filter.children ?? []).map(fromFilter),
    }
  }

  const operator = filter.operator ?? 'Equals'
  const values = filter.values ?? []
  return {
    kind: 'condition',
    id: nextId(),
    field: filter.field ?? '',
    operator,
    values: isList(operator) ? [values.join(', ')] : values.length > 0 ? [...values] : blankValues(operator),
    not: filter.not ?? false,
  }
}

/** A root group: a group as it is, anything else wrapped in an And. */
function rootFrom(filter: ExplorerFilter | null | undefined): GroupDraft {
  if (!filter) return emptyGroup()
  const node = fromFilter(filter)
  return node.kind === 'group' && !node.not ? node : { ...emptyGroup(), children: [node] }
}

export function fromRequest(request: ExplorerQueryRequest): QueryDraft {
  const grouped = (request.groupBy?.length ?? 0) > 0 || (request.measures?.length ?? 0) > 0
  return {
    dataset: request.dataset,
    where: rootFrom(request.where),
    grouped,
    columns: request.columns ? [...request.columns] : [],
    groupBy: request.groupBy ? [...request.groupBy] : [],
    measures: (request.measures ?? []).map((m) => ({
      id: nextId(),
      name: m.name,
      aggregate: m.aggregate,
      field: m.field ?? null,
      activeOnly: m.activeOnly ?? false,
    })),
    having: rootFrom(request.having),
    sort: (request.sort ?? []).map((s) => ({ field: s.field, descending: s.descending ?? false })),
    pageSize: request.pageSize ?? 50,
  }
}

// ------------------------------------------------------------------ server problems -> nodes

export interface PlacedProblems {
  /** Node id -> messages about it. */
  byNode: Map<string, string[]>
  /** Messages about the query as a whole: columns, measures, sort, the date range. */
  general: string[]
}

/**
 * Puts each validation message next to the node it is about: the node with the longest path
 * that the message's path starts with. "where" itself - the date-range rule - stays general.
 */
export function placeProblems(errors: Record<string, string[]>, paths: Map<string, string>): PlacedProblems {
  const byNode = new Map<string, string[]>()
  const general: string[] = []
  const known = [...paths.keys()].filter((p) => p !== 'where' && p !== 'having').sort((a, b) => b.length - a.length)

  for (const [key, messages] of Object.entries(errors)) {
    const path = known.find((p) => key === p || key.startsWith(`${p}.`))
    const id = path ? paths.get(path) : undefined
    if (id) byNode.set(id, [...(byNode.get(id) ?? []), ...messages])
    else general.push(...messages)
  }

  return { byNode, general }
}

// ------------------------------------------------------------------ what a query needs

/** The lookup permissions a request needs: fields it filters on, shows or groups by. */
export function permissionsNeeded(request: ExplorerQueryRequest, fields: ExplorerField[]): string[] {
  const byName = new Map(fields.map((f) => [f.name, f]))
  const used = new Set<string>()

  const walk = (f: ExplorerFilter | null | undefined) => {
    if (!f) return
    if (f.field) used.add(f.field)
    f.children?.forEach(walk)
  }
  walk(request.where)
  request.columns?.forEach((c) => used.add(c))
  request.groupBy?.forEach((g) => used.add(g))

  return [...new Set([...used].map((n) => byName.get(n)?.permission).filter((p): p is string => !!p))].sort()
}

/** A date n days before a yyyy-MM-dd date, as yyyy-MM-dd. UTC throughout: a business date has no zone. */
export function daysBefore(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() - days)
  return d.toISOString().slice(0, 10)
}
