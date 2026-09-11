-- 002_vendor_map_seed.sql
-- Initial curated vendor normalisation.
--
-- Every raw string below was observed in the real GSMA export. This is a starting point, not a finished
-- mapping: the table is editable through the UI and audited, because the manufacturer field has a
-- 10,529-value long tail that no seed can anticipate.
--
-- Unmapped manufacturers fall back to their raw string at query time (see the coalesce in
-- ClickHouseAnalyticsStore), so a new vendor shows up as itself rather than disappearing.
--
-- One INSERT per group: ClickHouse's VALUES parser does not accept comments between value rows.

-- Samsung: 6 spellings, 35,942 TACs, 40.8% of all enriched bindings.
INSERT INTO sqm.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group, updated_by) VALUES
('Samsung Korea', 'Samsung', 'Samsung Electronics', 'seed'),
('Samsung Korea (PO Box 105, Gyeonggi-Do)', 'Samsung', 'Samsung Electronics', 'seed'),
('Samsung Euro QA Lab', 'Samsung', 'Samsung Electronics', 'seed'),
('Samsung', 'Samsung', 'Samsung Electronics', 'seed'),
('Samsung Electronics America', 'Samsung', 'Samsung Electronics', 'seed'),
('Samsung Electronics Co Ltd', 'Samsung', 'Samsung Electronics', 'seed');

-- Motorola: 7 spellings across two unrelated corporate owners. Collapsed to one canonical vendor but
-- distinguished by group, because Motorola Solutions is a different business making different devices.
INSERT INTO sqm.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group, updated_by) VALUES
('Motorola Mobility LLC, a Lenovo Company', 'Motorola', 'Lenovo', 'seed'),
('Motorola Inc.', 'Motorola', 'Motorola Legacy', 'seed'),
('Motorola', 'Motorola', 'Motorola Legacy', 'seed'),
('Motorola UK', 'Motorola', 'Motorola Legacy', 'seed'),
('Motorola Ltd UK', 'Motorola', 'Motorola Legacy', 'seed'),
('Motorola Electronic GmbH', 'Motorola', 'Motorola Legacy', 'seed'),
('Motorola Solutions, Inc', 'Motorola Solutions', 'Motorola Solutions', 'seed');

-- Nokia: the brand moved to HMD Global in 2016, so modern "Nokia" handsets are HMD devices.
-- HMD is 4.62% of enriched bindings and is kept distinct rather than merged into Nokia.
INSERT INTO sqm.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group, updated_by) VALUES
('Nokia Corporation', 'Nokia', 'Nokia', 'seed'),
('Nokia', 'Nokia', 'Nokia', 'seed'),
('Nokia Mobile Phones Ltd', 'Nokia', 'Nokia', 'seed'),
('Nokia Solutions and Networks Oy', 'Nokia', 'Nokia', 'seed'),
('Microsoft Mobile Oy, Nokia Corporation', 'Nokia', 'Microsoft Mobile', 'seed'),
('Microsoft Mobile Oy', 'Microsoft', 'Microsoft Mobile', 'seed'),
('HMD Global Oy', 'HMD', 'HMD Global', 'seed');

-- Remaining top vendors by measured binding volume.
INSERT INTO sqm.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group, updated_by) VALUES
('Xiaomi Communications Co Ltd', 'Xiaomi', 'Xiaomi', 'seed'),
('Apple Inc', 'Apple', 'Apple', 'seed'),
('HUAWEI Technologies Co Ltd', 'Huawei', 'Huawei', 'seed'),
('Huawei Device Company Limited', 'Huawei', 'Huawei', 'seed'),
('Honor Device Company Limited', 'Honor', 'Honor', 'seed'),
('Guangdong Oppo Mobile Telecommunications Corp Ltd', 'OPPO', 'BBK', 'seed'),
('Vivo Mobile Communication Co Ltd', 'vivo', 'BBK', 'seed'),
('Realme Chongqing Mobile Telecommunications Corp Ltd', 'realme', 'BBK', 'seed'),
('LG Electronics Inc.', 'LG', 'LG', 'seed'),
('ZTE Corporation', 'ZTE', 'ZTE', 'seed'),
('Tecno Telecom (HK) Limited', 'TECNO', 'Transsion', 'seed'),
('Itel Technology Limited', 'itel', 'Transsion', 'seed'),
('TCL Communication Ltd', 'TCL', 'TCL', 'seed'),
('Sony Ericsson', 'Sony', 'Sony', 'seed'),
('Ericsson Mobile Comms AB', 'Ericsson', 'Ericsson', 'seed');

-- IoT / M2M module vendors. Together ~4.2% of bindings — a real segment in this subscriber base,
-- worth keeping visible rather than lost in the long tail.
INSERT INTO sqm.tac_vendor_map (raw_manufacturer, vendor_canonical, vendor_group, updated_by) VALUES
('Quectel Wireless Solutions Co Ltd', 'Quectel', 'Quectel', 'seed'),
('Fibocom Wireless Inc', 'Fibocom', 'Fibocom', 'seed'),
('SIMCOM Wireless Solutions Co Ltd', 'SIMCom', 'SIMCom', 'seed');
