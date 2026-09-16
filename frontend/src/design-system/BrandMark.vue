<script setup lang="ts">
/**
 * The QuadraSense mark: a ring cut by a rising bar.
 *
 * Drawn as SVG rather than shipped as a raster, for the reasons any mark in an application
 * interface should be: it is crisp at 20px in a header and at 64px on the sign-in page from one
 * file, it costs no request, and it can answer to the theme.
 *
 * **On the theme.** The deep navy is the brand colour and stays the brand colour in light. On the
 * application's dark surface it would be very nearly invisible, so the token resolves to a
 * lighter tint of the same hue there. The cyan is constant in both: it carries enough luminance
 * to hold its own either way, and keeping one of the two fixed is what stops the mark reading as
 * two different logos.
 *
 * The geometry is a Q read as a system rather than a letter — a closed ring for the inventory,
 * and the bar leaving it for what the inventory is for.
 */
withDefaults(defineProps<{ size?: number }>(), { size: 28 })
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 64 64"
    fill="none"
    role="img"
    aria-label="QuadraSense"
    class="shrink-0"
  >
    <defs>
      <!--
        The gap between the ring and the bar.

        It is not decoration: without it the cyan meets the navy edge to edge and the two read as
        one shape, which is exactly the point at which a mark stops being legible at 20 pixels.
        The band follows the bar's own slope, 1.4 units wider on each side, and starts at the
        bar's top edge rather than above it. That last detail is not fussiness: the bar begins
        INSIDE the ring's hole and does not reach the stroke until roughly a sixth of the way
        down, so a band that starts higher cuts a long stretch of ring with nothing behind it.
      -->
      <mask :id="`qs-cut-${size}`">
        <rect width="64" height="64" fill="white" />
        <path d="M27.2 34 L37.8 34 L56.8 55.6 L46.3 55.6 Z" fill="black" />
      </mask>
    </defs>

    <circle
      cx="32.05"
      cy="30.6"
      r="15.1"
      stroke="var(--brand-deep)"
      stroke-width="6.2"
      :mask="`url(#qs-cut-${size})`"
    />

    <path d="M29.2 34.6 L36.8 34.6 L50.0 49.4 L42.4 49.4 Z" fill="var(--brand-accent)" />
  </svg>
</template>
