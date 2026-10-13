import { describe, expect, it } from 'vitest'
import { migrateDefinition, type ConfigMigrations } from '@/features/dashboard/registry/migrations'
import { normalizeConfig, validateConfig, validateDefinition, validateInstance } from '@/features/dashboard/registry/validate'
import { CONTRACT, defaultConfig, requiredPermissions } from '@/features/dashboard/registry/contract'
import { allWidgets } from '@/features/dashboard/registry'
import { instantiateTemplate, TEMPLATES, visibleTemplateDefinition } from '@/features/dashboard/templates'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

function instance(type: string, patch: Partial<WidgetInstance> = {}): WidgetInstance {
  const spec = CONTRACT.types[type]!
  return {
    id: 'w_test0001',
    type,
    configVersion: spec.configVersion,
    title: null,
    subtitle: null,
    config: defaultConfig(type),
    overrides: {},
    layout: { x: 0, y: 0, w: spec.size.w, h: spec.size.h },
    ...patch,
  }
}

describe('validation mirrors the server', () => {
  it('accepts every type at its defaults where nothing is required without one', () => {
    for (const type of Object.keys(CONTRACT.types)) {
      if (type === 'tools.modelNetworkAge' || type === 'tools.explorerAggregate') continue
      expect(validateInstance(instance(type)), type).toEqual([])
    }
  })

  it('refuses unknown settings, values out of bounds and duplicates in a list', () => {
    expect(validateConfig('device.top', { ...defaultConfig('device.top'), sql: 'x' })).toContainEqual({ field: 'sql', message: 'Not a setting of this widget.' })
    expect(validateConfig('device.top', { ...defaultConfig('device.top'), limit: 51 }).map((e) => e.field)).toEqual(['limit'])
    expect(validateConfig('change.daily', { ...defaultConfig('change.daily'), series: ['added', 'added'] }).map((e) => e.field)).toEqual(['series'])
    expect(validateConfig('tools.modelNetworkAge', { tac: '3500000X', showDetail: true }).map((e) => e.field)).toEqual(['tac'])
  })

  it('refuses an override the type cannot take, and a layout off the grid', () => {
    expect(validateInstance(instance('change.daily', { overrides: { vendor: 'Samsung' } })).map((e) => e.field)).toEqual(['overrides.vendor'])
    expect(validateInstance(instance('change.daily', { layout: { x: 1, y: 0, w: 12, h: 10 } })).map((e) => e.field)).toEqual(['layout.x'])
  })

  it('keeps identifiers out of a stored Explorer query', () => {
    const query = { dataset: 'Bindings', where: { field: 'msisdn', operator: 'Equals', values: ['9121234567'] }, measures: [{ name: 'n', aggregate: 'Count' }] }
    const errors = validateConfig('tools.explorerAggregate', { ...defaultConfig('tools.explorerAggregate'), query })
    expect(errors.map((e) => e.field)).toEqual(['query'])
  })

  it('refuses two widgets with one id', () => {
    const def = { ...devicePopulation.definition, widgets: [instance('metric.kpi'), instance('metric.kpi', { layout: { x: 2, y: 0, w: 2, h: 3 } })] }
    expect(validateDefinition(def).map((e) => e.field)).toContain('widgets[1].id')
  })

  it('normalises a config to every field of the type and nothing else', () => {
    expect(Object.keys(normalizeConfig('device.top', { limit: 5, junk: 1 }))).toEqual(Object.keys(CONTRACT.types['device.top']!.config))
  })
})

describe('reading stored definitions', () => {
  it('leaves a complete v1 definition alone', () => {
    const { definition, changed, problems } = migrateDefinition(JSON.parse(JSON.stringify(devicePopulation.definition)))
    expect(problems).toEqual([])
    expect(changed).toBe(false)
    expect(definition).toEqual(devicePopulation.definition)
  })

  it('fills absent optional parts', () => {
    const { definition, changed } = migrateDefinition({
      schemaVersion: 1,
      widgets: [{ id: 'w_abc12345', type: 'device.top', configVersion: 1, config: { dimension: 'tac' }, layout: { x: 0, y: 0, w: 6, h: 9 } }],
    })
    expect(changed).toBe(true)
    expect(definition.defaults).toEqual({})
    expect(definition.refreshMinutes).toBe(0)
    expect(definition.widgets[0]!.overrides).toEqual({})
    expect(definition.widgets[0]!.config.limit).toBe(10)
  })

  it('keeps an unknown widget and says so, rather than dropping it', () => {
    const { definition, problems } = migrateDefinition({
      schemaVersion: 1,
      widgets: [{ id: 'w_gone0001', type: 'fraud.detected', configVersion: 1, config: { x: 1 }, layout: { x: 0, y: 0, w: 4, h: 4 } }],
    })
    expect(definition.widgets).toHaveLength(1)
    expect(definition.widgets[0]!.config).toEqual({ x: 1 })
    expect(problems.join(' ')).toContain('fraud.detected')
  })

  it('reports a definition from a newer version without guessing', () => {
    const raw = { schemaVersion: 2, widgets: [] }
    const { problems, changed } = migrateDefinition(raw)
    expect(changed).toBe(false)
    expect(problems[0]).toContain('newer version')
  })

  it('applies config upgrades in order, deterministically', () => {
    const migrations: ConfigMigrations = {
      'device.top': { 0: (c) => ({ ...c, limit: Number(c.top ?? 10) }) },
    }
    const raw = { schemaVersion: 1, widgets: [{ id: 'w_old00001', type: 'device.top', configVersion: 0, config: { dimension: 'tac', top: 7 }, layout: { x: 0, y: 0, w: 6, h: 9 } }] }
    const a = migrateDefinition(JSON.parse(JSON.stringify(raw)), migrations)
    const b = migrateDefinition(JSON.parse(JSON.stringify(raw)), migrations)
    expect(a.definition).toEqual(b.definition)
    expect(a.definition.widgets[0]!.configVersion).toBe(1)
    expect(a.definition.widgets[0]!.config.limit).toBe(7)
    expect(a.definition.widgets[0]!.config).not.toHaveProperty('top')
  })
})

describe('templates', () => {
  it('every template is a valid definition', () => {
    for (const template of TEMPLATES) expect(validateDefinition(template.definition), template.key).toEqual([])
  })

  it('Device Population is the old page: freshness, the KPI row, then the cards in page order', () => {
    const ordered = [...devicePopulation.definition.widgets].sort((a, b) => a.layout.y - b.layout.y || a.layout.x - b.layout.x)

    // The KPI row is one widget, as it was one row: one menu and one export, not six.
    const row = ordered[1]!
    expect(row.type).toBe('metric.kpiSummary')
    expect(row.config.metrics).toEqual(['activeBindings', 'distinctSubscribers', 'distinctSims', 'distinctDevices', 'unknownDeviceBindings', 'tacCoveragePercent'])
    expect(ordered.filter((w) => w.type === 'metric.kpi')).toEqual([])

    expect(ordered.map((w) => w.title)).toEqual([
      'Data freshness',
      'Key figures',
      'Daily change',
      'SIM and handset changes', 'Vendor movement',
      'Device class mix',
      'Network & SIM capability',
      'Top vendors', 'Top models',
      'Device types', 'Operating systems',
      'Enrichment breakdown',
    ])
  })

  it('a template grants nothing: what the person may not use is left out and named', () => {
    const viewer = new Set(['dashboard.view', 'device.view'])
    const { definition, omitted } = visibleTemplateDefinition(devicePopulation, (p) => viewer.has(p))
    expect(definition.widgets.some((w) => w.type === 'ops.freshness')).toBe(false)
    expect(omitted).toEqual([{ title: 'Data freshness', missing: ['import.view'] }])

    const risk = TEMPLATES.find((t) => t.key === 'security-risk')!
    const analystWithoutSims = new Set(['dashboard.view', 'risk.view', 'lookup.subscriber'])
    const visible = visibleTemplateDefinition(risk, (p) => analystWithoutSims.has(p)).definition.widgets
    expect(visible.filter((w) => w.type === 'risk.list').map((w) => w.config.rule)).toEqual(['RepeatedSimChange7'])
  })

  it('a copy gets new ids and keeps no id in common with the template', () => {
    const { definition } = instantiateTemplate(devicePopulation, () => true)
    const original = new Set(devicePopulation.definition.widgets.map((w) => w.id))
    expect(definition.widgets.some((w) => original.has(w.id))).toBe(false)
    expect(new Set(definition.widgets.map((w) => w.id)).size).toBe(definition.widgets.length)
    expect(validateDefinition(definition)).toEqual([])
  })

  it('a risk list needs the lookup permission of its rule', () => {
    expect(requiredPermissions('risk.list', { rule: 'HighDeviceCount30' })).toEqual(['risk.view', 'lookup.imsi'])
  })
})

describe('the registry against the contract', () => {
  it('registers only contract types, and presents every config field', () => {
    for (const def of allWidgets()) {
      expect(CONTRACT.types[def.type], def.type).toBeDefined()
      const presented = new Set([...Object.keys(def.fields), ...(def.customFields ?? [])])
      for (const field of Object.keys(def.spec.config)) expect(presented.has(field), `${def.type}.${field}`).toBe(true)
    }
  })

  it('has a definition for every contract type', () => {
    const registered = new Set(allWidgets().map((d) => d.type))
    const missing = Object.keys(CONTRACT.types).filter((t) => !registered.has(t))
    expect(missing).toEqual([])
  })
})
