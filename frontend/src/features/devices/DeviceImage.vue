<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { apiUrl } from '@/api/client'

/**
 * A device photograph, or a drawn stand-in when there is none.
 *
 * **Where the pictures come from.** The GSMA TAC database has 26 columns and not one of them is an
 * image, a URL, or a reference to one — there is no imagery to import — and this deployment is
 * internal-network-only, so a device-catalogue CDN is unreachable rather than merely undesirable.
 * Photographs are therefore curated: an administrator uploads one per model, and until they do,
 * this draws the placeholder.
 *
 * The placeholder is drawn rather than fetched, for three reasons. It stays crisp at any size, it
 * follows the theme, and it costs no request — a catalogue page shows forty devices and most of
 * them will have no photograph for a long while. It is also *informative*: the silhouette is the
 * device's own type, so a tablet, a module and a wearable are told apart at a glance in a list
 * where most rows are grey rectangles.
 */
const props = withDefaults(
  defineProps<{
    tac: string
    hasImage: boolean
    /** Drives the initials. The curated vendor name, falling back to the brand. */
    name?: string | null
    /** One of the 19 GSMA device types. Drives which silhouette is drawn. */
    deviceType?: string | null
    size?: 'sm' | 'md' | 'lg'
    /**
     * Whether a person has approved this picture.
     *
     * Only surfaced where a reader can act on it - the detail page and the review queue - and
     * deliberately not on catalogue tiles, where forty amber dots would say nothing useful.
     */
    imageStatus?: 'verified' | 'needs_review' | null
    /** Serve a candidate's bytes instead of the live image. Used by the review screen. */
    src?: string | null
  }>(),
  { name: null, deviceType: null, size: 'md', imageStatus: null, src: null },
)

/**
 * The image is requested only when the catalogue says there is one, and a failure falls back
 * rather than showing a broken image: the photograph can be deleted between the list query and
 * the img request, and a torn icon is a worse answer than the placeholder.
 */
const failed = ref(false)
const loaded = ref(false)

watch(
  () => [props.tac, props.src],
  () => {
    failed.value = false
    loaded.value = false
  },
)

const showPhoto = computed(() => (props.hasImage || !!props.src) && !failed.value)

/** A candidate is served by id from the review endpoint; everything else by TAC. */
const source = computed(() => props.src ?? apiUrl(`/api/v1/devices/${props.tac}/image`))

/**
 * The 19 GSMA device types, folded into the five shapes worth drawing differently.
 *
 * Coarse on purpose. Drawing nineteen silhouettes would be nineteen chances to be subtly wrong
 * about a category nobody looks at, and "Handheld" and "Smartphone" do not need different
 * pictures. The one distinction that earns its place is machine-versus-person: an IoT module is
 * not a phone, and the dashboard measures that segment at 9.56M bindings.
 */
const shape = computed(() => {
  const t = (props.deviceType ?? '').toLowerCase()
  if (t.includes('tablet') || t.includes('e-book')) return 'tablet'
  if (t.includes('wearable') || t.includes('watch')) return 'wearable'
  if (t.includes('module') || t.includes('m2m') || t.includes('iot') || t.includes('modem')
      || t.includes('router') || t.includes('dongle') || t.includes('vehicle')) return 'module'
  if (t.includes('feature') || t.includes('basic')) return 'feature'
  return 'phone'
})

/** At most two letters. More turns the tile into a word nobody reads. */
const initials = computed(() => {
  const source = (props.name ?? '').trim()
  if (source.length === 0) return ''

  const words = source.split(/[\s-]+/).filter((w) => /[a-z0-9]/i.test(w))
  if (words.length === 0) return ''
  if (words.length === 1) return words[0]!.slice(0, 2).toUpperCase()
  return (words[0]![0]! + words[1]![0]!).toUpperCase()
})

const box = computed(() => ({
  sm: 'size-10',
  md: 'size-16',
  lg: 'size-full',
}[props.size]))
</script>

<template>
  <div
    class="relative grid shrink-0 place-items-center overflow-hidden rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)]"
    :class="box"
  >
    <!--
      contain and centre, never cover. These are catalogue product images: cover would crop a
      handset to fill the tile, which is the one thing a device picture must never do.
      The plate underneath is white in both themes because the normalised images are rendered on
      white or on transparency, and a dark tile behind a transparent PNG shows a black phone.
    -->
    <img
      v-if="showPhoto"
      :src="source"
      :alt="name ? `${name} device photograph` : 'Device photograph'"
      class="size-full bg-white object-contain object-center transition-opacity duration-150"
      :class="loaded ? 'opacity-100' : 'opacity-0'"
      loading="lazy"
      decoding="async"
      @load="loaded = true"
      @error="failed = true"
    />

    <!--
      A quiet skeleton while the bytes arrive, rather than the silhouette: flashing the
      placeholder and then replacing it reads as a failure that corrected itself.
    -->
    <div
      v-if="showPhoto && !loaded"
      class="absolute inset-0 animate-pulse bg-[var(--c-surface-hover)]"
      aria-hidden="true"
    />

    <!-- Says the picture has not been checked, where a reader can do something about it. -->
    <span
      v-if="showPhoto && imageStatus === 'needs_review'"
      class="absolute top-1 right-1 rounded-full bg-[var(--c-warning)] px-1.5 py-0.5 text-[var(--text-2xs)] font-medium text-white"
      title="This image was sourced automatically and nobody has verified it."
    >
      unverified
    </span>

    <template v-else>
      <!--
        The silhouette. Stroke-only and low-contrast: it is a stand-in, and a placeholder that
        draws the eye harder than the real photographs beside it has the emphasis backwards.
      -->
      <svg
        class="size-[55%] text-[var(--c-text-muted)] opacity-45"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="1.25"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <template v-if="shape === 'tablet'">
          <rect x="3" y="2.5" width="18" height="19" rx="2" />
          <line x1="10.5" y1="19" x2="13.5" y2="19" />
        </template>
        <template v-else-if="shape === 'wearable'">
          <rect x="6.5" y="6.5" width="11" height="11" rx="3" />
          <path d="M9.5 6.5V3.5h5v3M9.5 17.5v3h5v-3" />
        </template>
        <template v-else-if="shape === 'module'">
          <rect x="4.5" y="7" width="15" height="10" rx="1.5" />
          <path d="M8 7V4M12 7V4M16 7V4M8 17v3M12 17v3M16 17v3" />
          <circle cx="12" cy="12" r="1.75" />
        </template>
        <template v-else-if="shape === 'feature'">
          <rect x="6" y="2" width="12" height="20" rx="2" />
          <rect x="8.5" y="4.5" width="7" height="5" rx="0.5" />
          <path d="M9 13h1.5M13.5 13H15M9 16h1.5M13.5 16H15" />
        </template>
        <template v-else>
          <rect x="6" y="2" width="12" height="20" rx="2.5" />
          <line x1="10.5" y1="19" x2="13.5" y2="19" />
        </template>
      </svg>

      <!--
        Initials sit under the silhouette rather than replacing it, so the tile answers two
        questions at once: what kind of thing, and whose.
      -->
      <span
        v-if="initials && size !== 'sm'"
        class="absolute bottom-1 text-[var(--text-2xs)] font-semibold tracking-wide text-[var(--c-text-muted)]"
      >
        {{ initials }}
      </span>
    </template>
  </div>
</template>
