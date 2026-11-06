-- 036_small_merge_blocks.sql
--
-- A merge of the per-model mart reads every part at once, and a block of uniq() states is not small:
-- 972 MiB of them took up to 3.5 GiB to merge, most of what the laptop's ClickHouse may use at all.
-- The refresh now merges the delivery's partition itself, so that readers meet one part, and the
-- background merges of this table cost the same.
--
-- Measured on delivery 247's states, written as the refresh writes them, 11 parts:
--
--   default                                3.50 GiB   123 s
--   merge_max_block_size 1024              2.88 GiB   154 s
--   vertical merge                         1.23 GiB    96 s
--   vertical, and merge_max_block_size     1.01 GiB    87 s
--
-- A vertical merge takes one column at a time, and ClickHouse only chooses it from 131,072 rows; this
-- table has 90,000 rows of very wide columns. Copies of the same states made with INSERT ... SELECT
-- merged in 439 MiB under every setting, so measure this on parts an aggregation wrote.
--
-- Table settings rewrite no parts, so this does not hold the site down.

ALTER TABLE sqm.agg_tac_population MODIFY SETTING
    merge_max_block_size = 1024,
    vertical_merge_algorithm_min_rows_to_activate = 1,
    vertical_merge_algorithm_min_columns_to_activate = 1;
