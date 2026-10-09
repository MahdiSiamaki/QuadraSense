/**
 * Config per widget type of the Device Intelligence group, field for field as
 * `registry/widget-contract.json` declares them. Type aliases, not interfaces: an interface has no
 * implicit index signature and would not satisfy `WidgetConfig`.
 */
export type TopDimension = 'vendorCanonical' | 'manufacturer' | 'marketingName' | 'tac'

/** device.top */
export type TopConfig = {
  dimension: TopDimension
  limit: number
  display: 'bar' | 'table'
  sort: 'count' | 'name'
  showValues: boolean
  showPercent: boolean
  excludeUnknownBucket: boolean
  clickToFilter: boolean
}

/** device.distribution */
export type DistributionConfig = {
  dimension: 'deviceType' | 'operatingSystem'
  limit: number
  display: 'table' | 'bar'
  showPercent: boolean
  clickToFilter: boolean
}

/** device.classMix */
export type ClassMixConfig = {
  display: 'composition' | 'table'
}

export type Capability = 'LTE' | '5G' | 'eSIM' | 'IMS emergency calling'

/** device.capabilities */
export type CapabilitiesConfig = {
  capabilities: Capability[]
  showVolteNote: boolean
}

/** device.newModels */
export type NewModelsConfig = {
  brand: string
  deviceType: string
  knownOnly: boolean
  limit: number
}

/** device.modelArrivals */
export type ModelArrivalsConfig = {
  grain: 'month' | 'week'
  display: 'chart' | 'table'
}
