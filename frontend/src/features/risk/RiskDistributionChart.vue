<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart } from 'echarts/charts'
import { GridComponent, LegendComponent, MarkAreaComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { RiskDistribution } from '@/api/risk'
import { chartColor } from '@/lib/chart-colors'
import { formatCompact, formatFull } from '@/lib/format'
import { useTheme } from '@/lib/theme'

echarts.use([BarChart, GridComponent, LegendComponent, MarkAreaComponent, TooltipComponent, CanvasRenderer])

/**
 * Where a rule's threshold sits in the spread of its values.
 *
 * Bars count entities per value range; the vertical axis is logarithmic, because the ranges hold
 * from a million entities down to a handful and a linear axis would show one bar. Clean counts
 * are drawn strong from the threshold up and pale below it, and the count before the screens sits
 * beside each in neutral grey - the gap between the two is what the feed defects added.
 */
const props = withDefaults(defineProps<{ data: RiskDistribution; height?: string }>(), { height: '16rem' })

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

const labels = computed(() =>
  props.data.buckets.map((b) =>
    b.to === null ? `${formatFull(b.from)}+` : b.to - b.from === 1 ? formatFull(b.from) : `${formatFull(b.from)}–${formatFull(b.to - 1)}`,
  ),
)

/** The first bucket wholly above the threshold. */
const firstListed = computed(() => {
  const t = props.data.threshold
  return t === null ? -1 : props.data.buckets.findIndex((b) => b.from > t)
})

function render() {
  if (!chart.value) return

  const muted = chartColor('--c-text-muted')
  const border = chartColor('--c-border')
  const clean = chartColor('--viz-1')
  const before = chartColor('--viz-null')
  const hasRaw = props.data.buckets.some((b) => b.raw !== null)
  const t = props.data.threshold

  chart.value.setOption(
    {
      animation: false,
      legend: hasRaw
        ? { top: 0, right: 0, textStyle: { color: chartColor('--c-text-secondary'), fontSize: 11 }, itemWidth: 10, itemHeight: 10 }
        : undefined,
      grid: { left: 8, right: 8, top: hasRaw ? 28 : 12, bottom: 8, containLabel: true },
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        backgroundColor: chartColor('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: chartColor('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const i = params[0]?.dataIndex ?? 0
          const b = props.data.buckets[i]
          if (!b) return ''
          const lines = [`<strong>${labels.value[i]} ${props.data.unit}</strong>`, `${formatFull(b.clean)} entities, clean`]
          if (b.raw !== null) lines.push(`${formatFull(b.raw)} before the screens`)
          if (t !== null) lines.push(b.from > t ? '<em>listed at the threshold</em>' : '<em>under the threshold</em>')
          return lines.join('<br/>')
        },
      },
      xAxis: {
        type: 'category',
        data: labels.value,
        name: props.data.unit,
        nameLocation: 'middle',
        nameGap: 28,
        nameTextStyle: { color: muted, fontSize: 10 },
        axisLabel: { color: muted, fontSize: 10, interval: 0, rotate: labels.value.length > 9 ? 35 : 0 },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'log',
        logBase: 10,
        min: 1,
        name: 'entities (log scale)',
        nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      series: [
        ...(hasRaw
          ? [
              {
                name: 'Before screens',
                type: 'bar',
                data: props.data.buckets.map((b) => (b.raw ? b.raw : null)),
                itemStyle: { color: before, borderRadius: [4, 4, 0, 0] },
                barMaxWidth: 14,
                barGap: '15%',
              },
            ]
          : []),
        {
          name: 'Clean',
          type: 'bar',
          data: props.data.buckets.map((b, i) => ({
            value: b.clean ? b.clean : null,
            itemStyle: { color: clean, opacity: firstListed.value >= 0 && i >= firstListed.value ? 1 : 0.4 },
          })),
          itemStyle: { borderRadius: [4, 4, 0, 0] },
          barMaxWidth: 14,
          // The listed range, shaded, with the threshold as its label: a category axis has no
          // position between two bars for a line to stand on.
          markArea:
            firstListed.value >= 0
              ? {
                  silent: true,
                  itemStyle: { color: chartColor('--c-accent'), opacity: 0.07 },
                  label: {
                    show: true,
                    position: 'insideTop',
                    formatter: `listed: more than ${formatFull(t ?? 0)}`,
                    color: chartColor('--c-text-secondary'),
                    fontSize: 10,
                  },
                  data: [[{ xAxis: labels.value[firstListed.value] }, { xAxis: labels.value[labels.value.length - 1] }]],
                }
              : undefined,
        },
      ],
    },
    { notMerge: true },
  )
}

let observer: ResizeObserver | null = null

onMounted(() => {
  if (!container.value) return
  chart.value = echarts.init(container.value, undefined, { renderer: 'canvas' })
  render()
  observer = new ResizeObserver(() => chart.value?.resize())
  observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  chart.value?.dispose()
  chart.value = null
})

watch(() => props.data, render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div>
    <div ref="container" :style="{ height }" class="w-full" role="img" :aria-label="`Distribution of ${data.unit}`" />
    <!-- The same figures as a table, for screen readers and for reading exact values. -->
    <details class="mt-1 text-2xs text-[var(--c-text-muted)]">
      <summary class="cursor-pointer">As a table</summary>
      <table class="mt-1 w-full">
        <thead>
          <tr class="text-left">
            <th scope="col" class="py-0.5 font-medium">{{ data.unit }}</th>
            <th scope="col" class="py-0.5 text-right font-medium">Clean</th>
            <th v-if="data.buckets.some((b) => b.raw !== null)" scope="col" class="py-0.5 text-right font-medium">Before screens</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(b, i) in data.buckets" :key="b.from">
            <td class="tabular py-0.5">{{ labels[i] }}</td>
            <td class="tabular py-0.5 text-right">{{ formatFull(b.clean) }}</td>
            <td v-if="b.raw !== null" class="tabular py-0.5 text-right">{{ formatFull(b.raw) }}</td>
          </tr>
        </tbody>
      </table>
    </details>
  </div>
</template>
