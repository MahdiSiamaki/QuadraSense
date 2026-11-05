function leave(event: WheelEvent) {
  if (!event.ctrlKey) event.stopPropagation()
}

/**
 * Leaves a plain mouse wheel to the page and keeps zoom on Ctrl + wheel, or a touchpad's pinch, which
 * the browser sends as the same event. Dragging inside the chart still pans it. Every chart with an
 * inside zoom calls this.
 *
 * ECharts' inside zoom cancels every wheel event over the plot, so the page stopped dead under the
 * pointer on each of these charts (2026-11-05). Its option cannot stop that: `zoomOnMouseWheel:
 * 'ctrl'` turns the zoom off, but in 6.1 the roam controller runs with every behaviour on and cancels
 * the event before it reads the option. So the event has to stop before it reaches the chart.
 *
 * One shared listener, so a second call on the same element adds nothing.
 */
export function leaveWheelToPage(element: HTMLElement) {
  element.addEventListener('wheel', leave, { capture: true, passive: true })
}
