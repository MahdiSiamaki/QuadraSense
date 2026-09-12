-- 006_tac_real_column_names.sql
--
-- Restores the real GSMA column names on sqm.tac.
--
-- THE BUG. The table was created with eleven columns named c13..c23 as placeholders.
-- TAC data is loaded with FORMAT CSVWithNames, which matches columns by *header name* —
-- so every one of those eleven columns silently received nothing and sat empty, while
-- the load reported success and the row count was correct.
--
-- It surfaced only when a widget asked for eSIM support and got zero. Zero is a
-- plausible-looking answer, which is what makes this class of bug dangerous: nothing
-- errored, the row count matched, and the number was simply absent rather than wrong.
--
-- The columns lost, and what they carry:
--
--   authenticatedIMSEmergencyCallSupport    IMS emergency calling (Y / N / Not Known)
--   unauthenticatedIMSEmergencyCallSupport  "
--   imsEmergencyCallWithoutUICC             "
--   removableUICC / nonremovableUICC        physical SIM slot counts
--   removableEUICC / nonremovableEUICC      eUICC counts -- this is the eSIM signal
--   networkSpecificIdentifier
--   ntnConnectivity                         satellite (non-terrestrial network)
--   simSlot / imeiQuantity
--
-- Recreating is safe: sqm.tac is reference data reloaded from the source file in ~3 s.

DROP TABLE IF EXISTS sqm.tac;

CREATE TABLE sqm.tac
(
    tac                                    String,
    manufacturer                           String,
    modelName                              String,
    marketingName                          String,
    brandName                              String,
    allocationDate                         String,
    lastUpdatedDate                        String,
    organisationId                         String,
    deviceType                             String,
    bluetooth                              String,
    nfc                                    String,
    wlan                                   String,
    authenticatedIMSEmergencyCallSupport   String,
    unauthenticatedIMSEmergencyCallSupport String,
    imsEmergencyCallWithoutUICC            String,
    removableUICC                          String,
    removableEUICC                         String,
    nonremovableUICC                       String,
    nonremovableEUICC                      String,
    networkSpecificIdentifier              String,
    ntnConnectivity                        String,
    simSlot                                String,
    imeiQuantity                           String,
    operatingSystem                        String,
    oem                                    String,
    bandDetails                            String
)
ENGINE = MergeTree
ORDER BY tac;
