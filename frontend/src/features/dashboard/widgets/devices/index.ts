/**
 * Device Intelligence: what the population is made of - vendors, models, types, capability - and
 * which models are new to the network.
 */
import { defineWidget } from '@/features/dashboard/types/widget'
import type {
  CapabilitiesConfig,
  ClassMixConfig,
  DistributionConfig,
  ModelArrivalsConfig,
  NewModelsConfig,
  TopConfig,
} from './config'
import CapabilitiesWidget from './CapabilitiesWidget.vue'
import ClassMixWidget from './ClassMixWidget.vue'
import DistributionWidget from './DistributionWidget.vue'
import ModelArrivalsWidget from './ModelArrivalsWidget.vue'
import NewModelsWidget from './NewModelsWidget.vue'
import TopWidget from './TopWidget.vue'

const COUNT_UNIT = 'Bindings, or estimated subscribers or handsets - the dashboard’s Count by.'
const ESTIMATE = 'Subscribers and handsets are HyperLogLog estimates (about 0.5% error).'

const TOP_DIMENSIONS = {
  vendorCanonical: 'Vendor',
  manufacturer: 'Manufacturer (as GSMA names it)',
  marketingName: 'Model',
  tac: 'TAC (model code)',
}

export const definitions = [
  defineWidget<TopConfig>({
    type: 'device.top',
    name: 'Top values',
    description: 'The largest vendors, manufacturers, models or TACs, as a bar chart or a table.',
    tags: ['top', 'ranking', 'vendor', 'model', 'brand', 'tac', 'manufacturer'],
    aliases: ['top vendors', 'top models', 'top brands', 'top TACs', 'market share'],
    icon: 'bar',
    unit: COUNT_UNIT,
    source: 'GET /api/v1/dashboard/top',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: [
      ESTIMATE,
      'Subscribers overlap across vendors: someone with two handsets is counted under both.',
      'Click to filter works for vendors and TACs; manufacturers and models are not dashboard filters.',
    ],
    presets: [
      {
        key: 'vendors',
        name: 'Top vendors',
        description: 'The ten largest vendors. Click a bar to filter the dashboard.',
        config: { dimension: 'vendorCanonical', excludeUnknownBucket: false, clickToFilter: true },
        title: 'Top vendors',
      },
      {
        key: 'models',
        name: 'Top models',
        description: 'The ten most common models, without the unknown-device bucket that would dwarf them.',
        tags: ['popular models', 'handset models'],
        config: { dimension: 'marketingName', excludeUnknownBucket: true, clickToFilter: false },
        title: 'Top models',
      },
      {
        key: 'tacs',
        name: 'Top TACs',
        description: 'The most common type allocation codes - one model variant each.',
        config: { dimension: 'tac', display: 'table', excludeUnknownBucket: false, clickToFilter: true },
        title: 'Top TACs',
      },
    ],
    component: TopWidget,
    fields: {
      dimension: { label: 'Rank by', section: 'data', options: TOP_DIMENSIONS },
      limit: { label: 'How many', section: 'data' },
      excludeUnknownBucket: {
        label: 'Leave out the unknown-device bucket',
        section: 'data',
        help: 'Recommended for models: 8.8M unknown-device bindings would dwarf every real one.',
      },
      display: { label: 'Show as', section: 'display', options: { bar: 'Bar chart', table: 'Table' }, visual: true },
      sort: { label: 'Order', section: 'display', options: { count: 'Largest first', name: 'Alphabetical' }, visual: true },
      showValues: { label: 'Show counts', section: 'display', visual: true },
      showPercent: { label: 'Show shares', section: 'display', visual: true },
      clickToFilter: {
        label: 'Click to filter the dashboard',
        section: 'display',
        visual: true,
        visibleWhen: (c) => c.dimension === 'vendorCanonical' || c.dimension === 'tac',
      },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<DistributionConfig>({
    type: 'device.distribution',
    name: 'Distribution',
    description: 'How the population splits over device types or operating systems.',
    tags: ['distribution', 'breakdown', 'device type', 'operating system', 'os', 'split'],
    aliases: ['device types', 'operating systems', 'os share', 'platform'],
    icon: 'table',
    unit: COUNT_UNIT,
    source: 'GET /api/v1/dashboard/distribution',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: [ESTIMATE, 'Operating systems are normalised for case and whitespace.'],
    presets: [
      {
        key: 'deviceTypes',
        name: 'Device types',
        description: 'Smartphones, feature phones, tablets, modems and the rest. Click to filter.',
        config: { dimension: 'deviceType' },
        title: 'Device types',
      },
      {
        key: 'operatingSystems',
        name: 'Operating systems',
        description: 'Android, iOS and the rest, normalised for case and whitespace. Click to filter.',
        config: { dimension: 'operatingSystem' },
        title: 'Operating systems',
      },
    ],
    component: DistributionWidget,
    fields: {
      dimension: { label: 'Split by', section: 'data', options: { deviceType: 'Device type', operatingSystem: 'Operating system' } },
      limit: { label: 'How many', section: 'data' },
      display: { label: 'Show as', section: 'display', options: { table: 'Table', bar: 'Bar chart' }, visual: true },
      showPercent: { label: 'Show shares', section: 'display', visual: true },
      clickToFilter: { label: 'Click to filter the dashboard', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<ClassMixConfig>({
    type: 'device.classMix',
    name: 'Device class mix',
    description: 'Smartphones, feature phones, tablets and IoT/M2M as one composition bar.',
    tags: ['class', 'mix', 'iot', 'm2m', 'smartphone', 'feature phone', 'composition'],
    aliases: ['device classes', 'iot share', 'machine share'],
    icon: 'composition',
    unit: COUNT_UNIT,
    source: 'GET /api/v1/dashboard/device-class-mix',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: [ESTIMATE, 'Network-wide: the pre-computed table has no vendor or device breakdown.'],
    component: ClassMixWidget,
    fields: {
      display: { label: 'Show as', section: 'display', options: { composition: 'Composition bar', table: 'Table' }, visual: true },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<CapabilitiesConfig>({
    type: 'device.capabilities',
    name: 'Network & SIM capability',
    description: 'How much of the population supports LTE, 5G, eSIM and IMS emergency calling.',
    tags: ['capability', 'lte', '5g', 'esim', 'ims', 'readiness', 'network'],
    aliases: ['5G readiness', 'eSIM support', '4G', 'VoLTE'],
    icon: 'capability',
    unit: COUNT_UNIT,
    source: 'GET /api/v1/dashboard/capabilities',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: [
      ESTIMATE,
      'Network-wide: the pre-computed table has no vendor or device breakdown.',
      'Capabilities overlap, so the bars are independent and do not sum.',
      'VoLTE is not in the GSMA dataset and is never shown.',
    ],
    component: CapabilitiesWidget,
    fields: {
      capabilities: {
        label: 'Capabilities',
        section: 'data',
        options: { LTE: 'LTE', '5G': '5G', eSIM: 'eSIM', 'IMS emergency calling': 'IMS emergency calling' },
      },
      showVolteNote: {
        label: 'Explain why VoLTE is missing',
        section: 'display',
        visual: true,
        help: 'The GSMA row count in the note needs import.view; without it the sentence leaves the number out.',
      },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<NewModelsConfig>({
    type: 'device.newModels',
    name: 'New models',
    description: 'Models the daily files named for the first time in the dashboard’s date range.',
    tags: ['new', 'models', 'arrivals', 'first seen', 'launch', 'network age'],
    aliases: ['new devices', 'new handsets', 'recent models', 'first seen'],
    icon: 'device',
    unit: 'Models (TACs); handsets now are estimated distinct IMEIs.',
    source: 'GET /api/v1/devices/new-models',
    timeScope: 'First seen within the dashboard’s date range, counted back from the arrivals’ last day.',
    caveats: [
      'First seen is the first daily file that named the model - not a launch date.',
      'Models in the initial dump were seen before the data starts and never appear.',
      'The first weeks after the data starts are mostly models the dump’s month happened to miss.',
    ],
    component: NewModelsWidget,
    fields: {
      brand: { label: 'Brand contains', section: 'data', help: 'Leave empty for every brand.' },
      deviceType: { label: 'Device type', section: 'data', help: 'Exactly as GSMA names it, e.g. Smartphone. Empty for all.' },
      knownOnly: { label: 'Known to GSMA only', section: 'data' },
      limit: { label: 'How many', section: 'data' },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<ModelArrivalsConfig>({
    type: 'device.modelArrivals',
    name: 'Model arrivals',
    description: 'How many models were first seen per month or week since the first daily file.',
    tags: ['arrivals', 'new models', 'over time', 'trend', 'first seen'],
    aliases: ['models per month', 'new model trend'],
    icon: 'calendar',
    unit: 'Models (TACs), and how many of them GSMA knows.',
    source: 'GET /api/v1/devices/arrivals',
    timeScope: 'Every period since the first daily file.',
    caveats: ['The first weeks after the data starts are mostly models the initial dump’s month happened to miss.'],
    component: ModelArrivalsWidget,
    fields: {
      grain: { label: 'Period', section: 'data', options: { month: 'Month', week: 'Week' } },
      display: { label: 'Show as', section: 'display', options: { chart: 'Chart', table: 'Table' }, visual: true },
    },
    exports: { csv: true, image: true },
  }),
]
