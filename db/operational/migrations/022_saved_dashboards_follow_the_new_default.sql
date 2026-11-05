-- ---------------------------------------------------------------------------
-- A saved dashboard stops carrying the old default for unknown devices.
--
-- Rows with no handset are left out of every count since 2026-11-05 (ADR-020),
-- and the default for showing them turned from true to false. The Device
-- population template used to write that default out, so a copy of it says
-- `true` without anyone having chosen it, and would go on counting those rows.
--
-- Taking the key away leaves the copy on the default. A dashboard someone set to
-- hide them says `false` and is left alone. Nobody could have saved `true` as a
-- choice that differs from the old default, so nothing chosen is lost.
--
-- Measured before writing this: two dashboards on the laptop, none on the
-- department VM.
-- ---------------------------------------------------------------------------

UPDATE dashboard.dashboard
   SET definition = definition #- '{defaults,includeUnknownDevice}'
 WHERE definition #>> '{defaults,includeUnknownDevice}' = 'true';
