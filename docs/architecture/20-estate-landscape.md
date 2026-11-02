# The estate around QuadraSense - who owns what

**Status:** 2026-11-02. This page is drawn from the owner's *SIM & eSIM estate* landscape, a draft:
a C4 Level 1 system landscape with nine Level 2 container views. It is the roadmap for the
operator's current and new systems. The landscape itself is kept off the repository, and this page
carries roles only (section 6).

Two kinds of statement appear below. Some are marked **owner, 2026-11-02**: the owner confirmed them
in the session that wrote this page. Everything else comes from the landscape, which says of itself
that its systems, names and stated flows are the owner's, while its protocol detail and any
relationship it does not state explicitly are a reconstruction, proposals to correct rather than
findings.

**Read after:** `01-overview.md`. **Read with:** `05-roadmap.md` (what is scheduled) and
`docs/backlog.md` (what was suggested and not built).

---

## 1. Where QuadraSense sits

QuadraSense is the estate's **PI**, Primary Inventory, drawn as *Quadrasense (PI)* in the
landscape. Its role there is to be the source of truth for which assets exist and what state they
are in:
- the SIM and eUICC inventory;
- the device inventory, an IMEI resolved through the GSMA TAC database to a make, model and
  capability;
- the live binding between the two.

The landscape calls PI **a join, not a database**. The commercial systems know which SIM was sold,
the network knows which IMEI is attached now, and the TAC database knows what that IMEI is. PI is the
only place where those meet.

Its neighbours split the questions between them:

| Question | Who answers it |
|---|---|
| What is true about this SIM, handset or number? | **PI** (QuadraSense) |
| What may be done to it: is it eligible, blocked, grey-listed, frozen for maintenance? | **Tessera** (policy decision center) |
| How does anyone else reach either of them? | **Lodestone** (integration hub) |

PI is defined just as much by what it does **not** do:

- **It never writes to the network.** Provisioning is the only system that writes a subscriber to the
  HSS.
- **It never sends anything to a card or device.** That is the job of Skyconnect, Velafabric and
  Cyanshyft.
- **It never decides policy.** Tessera does.
- **It never generates keys, and it holds key references only, never key values (owner,
  2026-11-02).** It may record which keyset, and which version, a card uses. The values stay inside
  the hardware security modules of the systems that use them, so PI stays outside the cryptographic
  boundary.
- **It is not the estate's front door for people.** In the landscape, every person reaches the estate
  through CRM. Today's QuadraSense has a UI of its own (section 5, question 1).

## 2. Status of the systems

| Status | Systems |
|---|---|
| **New** (owner, 2026-11-02) | QuadraSense (PI), Tessera, Velafabric, Lodestone, SIMplicity (the landscape: in development) |
| **In service** (owner, 2026-11-02) | Cyanshyft, which is **ACS**; Skyconnect, which is **AOTA**; **DMC**; CRM; Provisioning; HSS/HLR/UDM; BI; SNQM; SMS Gateway; Pardis; Nova; Martian |
| **Not confirmed** | Fusionflow, Secureseed. The owner is not sure whether they are in service yet |
| Not ours to deploy | the SS7 and Diameter signalling planes, the packet core and the radio network, the GSMA TAC database, the cards and devices themselves, and partners |

**DMC is in service but not in the landscape (owner, 2026-11-02).** Its scope has not been given
(section 5, question 3).

## 3. The systems and their scope

They are grouped roughly as the landscape draws them. "With QuadraSense" is the system's interface to
PI. Where it reads **not built**, that is the landscape's intended interface, and QuadraSense does not
have it today.

### Business systems

| System | Owns, and is responsible for | With QuadraSense |
|---|---|---|
| **CRM**: customer relationship management | Owns the customer, contract, order and case. Every commercially motivated change starts here: a new line, a SIM swap, an eSIM download, a port, a plan change, a suspension, a termination. It never touches the network itself: it emits an order, and Provisioning carries it out. It is the single human front door to the estate. | Publishes swap, activation and deactivation events onto Lodestone's event backbone. PI subscribes, so these commercial facts are **declared** and never inferred from signalling. It also reads the real device and eSIM state back for the agent view; the interface inventory routes that query through Lodestone. **Not built.** |
| **Provisioning**: subscriber provisioning gateway | Owns provisioning state, task orchestration and rollback. It turns one CRM order into an ordered set of network tasks: the subscriber on HSS/HLR/UDM, APN, QoS and service profile, registration on Pardis, an eSIM order, an OTA campaign, a CPE record. It owns what happens when task four of six fails. **It is the only system that writes a subscriber to the HSS, and it does so directly, not through Lodestone.** | None drawn. |
| **BI**: warehouse and analytics | Owns historical fact and commercial reporting: revenue, ARPU, churn, campaign effectiveness, device mix. | Sends PI CDR and usage aggregates as a batch feed, so device intelligence can be joined to commercial behaviour, as in "which models correlate with churn?". **Not built.** |
| **SNQM**: service and network quality management | Owns quality baselines and degradation alerts: probes and KPIs across RAN and core, sliced by cell, service and device model. | **Today's daily binding dumps come from SNQM (owner, 2026-11-02).** Every number, SIM and handset QuadraSense knows arrives this way, but the landscape does not draw this feed. The arrow it does draw, per-model quality, QoE and failure rates as a KPI feed: **not built.** |

### Integration, policy and security

| System | Owns, and is responsible for | With QuadraSense |
|---|---|---|
| **Lodestone (IEH)**: integration and exposure hub | Owns contracts, tenancy, correlation and mediation: one door for internal callers and partners (OAuth2 / mTLS, quotas, tenant isolation), a contract registry, the versioned product API, protocol mediation between REST, SOAP, SMPP and files, and the event backbone (provisioning, eSIM, OTA and SIM-lifecycle events). It has one south-bound adapter per owning platform, not one per caller. | Routes inventory and device queries in. Delivers the SIM-lifecycle events. Carries PI's answers out: device profile, SIM and eSIM state, stock position. **Not built.** |
| **Tessera (PDC)**: policy decision center | Owns rules, decisions and the reason for each: eligibility, block lists, grey lists, maintenance windows. **It decides and never executes.** Signed policy bundles go down to a small module inside each enforcer (Skyconnect, Velafabric, Cyanshyft, Fusionflow), which evaluates in-process. Only cache misses, the volatile lists and the decision logs come back up. | Reads the device, SIM and contract facts its rules are written against from PI, rather than making every caller pass them in. **Not built.** |
| **Secureseed (AKG)**: key generation | Owns key generation, and the input and output files it produces for SIM and eSIM batches. | Takes **free IMSI and ICCID ranges** from PI before every generation run. That makes PI a dependency of key generation, not just a report on it. **Not built** (section 4). |
| **Nautiluskeep (HSM)**: hardware security modules | Owns the cryptographic boundary: every cryptographic operation happens inside one. | None. PI holds no key values. |

### SIM and eSIM lifecycle

| System | Owns, and is responsible for | With QuadraSense |
|---|---|---|
| **Fusionflow**: Enhanced ES2+ orchestration | Gives the three RSPs one normalised ES2+ interface on the north side and speaks each vendor's dialect on the south side. Owns the routing policy: which RSP serves an order, given the specification, the eUICC and device, the market, stock, commercial terms and vendor health. Also failover, activation codes and QR payloads, a profile stock ledger, and one event and audit stream. | The landscape lists PI among the systems it talks to but describes no flow. Profile stock is given both to Fusionflow and to PI (section 5, question 4). |
| **Nova** (RSP, purchased) | Remote SIM provisioning for SGP.02 (M2M), SGP.22 (consumer) and SGP.32 (IoT). | None. |
| **Martian** (RSP, purchased) | SGP.22 only: consumer devices. No M2M or IoT order may be routed to it, and Fusionflow enforces that rule. | None. |
| **SIMplicity** (RSP, in-house, new) | The operator's own RSP for all three specifications, built behind Fusionflow so it can take traffic a share, a market or a specification at a time. | None. |
| **Skyconnect**, which is **AOTA** (owner, 2026-11-02): SIM OTA | Owns campaign state per card and proof-of-receipt tracking. It does remote file and applet management for cards in the field (ETSI TS 102 225/226, 3GPP TS 31.115/116): PLMN lists, SMS parameters, IMSI refresh, Java Card applets, across campaigns of millions of cards. | Its target resolver asks PI which cards match a campaign. PI supplies the SIM triplet, the keyset references and the contract data for those cards. **Not built.** The nearest thing built is the planning module's campaign list (section 4). |

### Delivery and device management

| System | Owns, and is responsible for | With QuadraSense |
|---|---|---|
| **Velafabric (UPE)**: unified protocol engine | Owns protocol delivery: one engine for OTA binary, TR-069, OMA-CP and OMA-DM. A job says *what* to send and to which devices; Velafabric chooses *how*, by protocol and bearer. It was pivoted out of Skyconnect. | The same SIM data as Skyconnect, for the platform that now sends the binary messages. **Not built.** |
| **Cyanshyft**, which is **ACS** (owner, 2026-11-02): auto configuration server | Owns CPE configuration state and firmware campaigns. It manages routers, FWA gateways, ONTs and set-top boxes over TR-069 and TR-369: zero-touch bootstrap, TR-181 parameters, staged firmware, remote diagnostics. Provisioning creates the CPE record, and Cyanshyft owns everything from first contact onward. | None drawn. |
| **DMC** (in service, not drawn) | Scope not given yet (owner, 2026-11-02). | Unknown. |
| **Pardis (SDP)**: service delivery platform | Owns rate limits, service routing and priority. It is the mediation between applications and network capability, and the submission point for OTA binary SMS, where throttling keeps a campaign from taking ordinary messaging down. | None. |
| **SMS Gateway**: SMSC and router | Owns the message queue and delivery receipts. It must be binary-safe end to end for OTA. | None. |

### Network and signalling, and outside our control

| System | Role | With QuadraSense |
|---|---|---|
| **HSS / HLR / UDM** | The subscriber and authentication authority for 2G/3G (HLR), 4G (HSS) and 5G (UDM). Every attach, SMS routing lookup and roaming registration touches it. | None directly. PI sees it through the signalling planes. |
| **Diameter S6a** (a signalling plane, not a platform) | Between the MME and the HSS: AIR, ULR, IDR, NOR. | A tap on ULR and AIR gives the IMEI-SV, the software version and the visited network **on every attach**. The landscape calls it PI's most valuable input. **Not built.** |
| **SS7** (a signalling plane) | MAP over SIGTRAN: HLR interrogation, location updates, mobile-terminated SMS. | SRI-SM and ATI probes for reachability and location. **Not built.** |
| **GSMA TAC database** | An IMEI prefix maps to a make, model and capability. | A periodic sync in the landscape. Today: **a versioned upload by hand** (section 4). |
| **Securelement** (SIM), **Continuum** (eSIM: eUICC and LPA) | The cards in the customers' hands. Once a profile is installed, an eSIM lives on the network exactly like a physical SIM. | They are the assets PI records. |
| CPE / FWA fleet, handsets, packet core, radio network | Endpoints and transport. | None. |
| Enterprise, MVNO and partners | API consumers with their own tenancy, through Lodestone only. | Through Lodestone. **Not built.** |

## 4. QuadraSense today, against its role in the landscape

The landscape opens PI into eight containers:
1. a MAP tap collector;
2. an S6a tap collector;
3. a CRM event consumer;
4. a TAC sync service;
5. a binding engine;
6. a range allocator;
7. an asset store;
8. a query API.

Today's QuadraSense is mostly the **analytical half** of PI: daily batches, read by people through
its own UI. The operational half, the inputs that arrive as they happen and the machine interfaces
that other systems depend on, is not built. **No date has been set for any of it.** Sequencing is
the owner's decision, and `05-roadmap.md` records only what is scheduled.

| Responsibility in the landscape | QuadraSense today | Where |
|---|---|---|
| The live binding: which SIM is in which handset, under which number | **Built, daily.** It is folded from SNQM's daily add/remove dumps into current state and a dated history. | `09-import-platform.md`, ADR-013 |
| Device identity from the TAC database | **Built, by hand.** The GSMA catalogue is uploaded, versioned, diffed and rolled back. The periodic sync is not built. | ADR-009, `09-import-platform.md` |
| SIM inventory: the ICCID | **Partial.** It is derived from the IMSI by the operator's numbering rule, labelled *derived*, never delivered. | `18-explorer-modes.md` |
| eUICC inventory: EID, profile state | **Not built.** No feed QuadraSense receives carries them. | README |
| Stock lot, vendor, free IMSI and ICCID ranges | **Not built.** No feed carries them. | - |
| S6a tap: IMEI-SV, software version, visited network on every attach | **Not built.** QuadraSense takes daily batches only. The non-goal "no streaming" in `01-overview.md` stands until a feed arrives more often than once a day. | `01-overview.md` §1 |
| MAP tap: reachability and location | **Not built.** | - |
| CRM's declared swap, activation and deactivation events | **Not built.** SIM changes are *observed* in the binding feed (risk signals). The declared commercial fact is not received. | ADR-014 |
| BI's CDR and usage aggregates; SNQM's per-model quality | **Not built.** | - |
| Query API for Lodestone, and facts for Tessera | **Not built.** The API serves QuadraSense's own UI, behind its sign-in sessions. There is no machine-to-machine contract. | `12-identity-and-access.md` |
| SIM data for the OTA platforms' campaign targets | **Partial.** The campaign list is an approved, audited CSV of the numbers in a planning class, carried by a person. It is not an interface. | `19-network-planning.md` §4 |
| Range allocator for Secureseed | **Not built.** It is also a different kind of work: a write path that reserves ranges and must never hand one out twice. A wrong answer is a card personalised wrong. | - |

The landscape's answers that "only PI can give" are covered as follows:

| Question | Today |
|---|---|
| Is this handset VoLTE-capable? | **Cannot be answered.** The GSMA catalogue has no VoLTE field (`19-network-planning.md` §1). A per-model flag or IMS registrations would have to come from the operator (`docs/backlog.md`). |
| Is this customer's eSIM installed and enabled? | **No.** eSIM *capability* is known per model (planning, N3). An installed and enabled profile needs the EID and the profile state. |
| Which models fail on this firmware? | **No.** It needs the software version (IMEI-SV) and SNQM's per-model KPIs. |
| How much profile stock is left per RSP? | **No.** Who owns this figure is also open (section 5, question 4). |

QuadraSense also does things the landscape does not ask of PI:
- the dashboards and their builder;
- the Explorer;
- risk signals;
- feed quality;
- device images;
- network and product planning, from N1 to N6.

They are analytics on the binding, and they are why today's product has a UI of its own.

## 5. Open questions - responsibilities left open or given twice

| # | Question | State |
|---|---|---|
| 1 | **Human access.** The landscape routes every person through CRM ("nobody reaches PI or the network directly"). QuadraSense has its own UI for analysts and planners, and that UI is today's whole product. Is the UI an analytics surface outside that rule, or does it move behind CRM? | Open |
| 2 | **Fusionflow and Secureseed:** in service or new? | Not confirmed (owner, 2026-11-02) |
| 3 | **DMC's scope.** It is in service, and the landscape leaves it out. | Open (owner, 2026-11-02) |
| 4 | **Profile stock per RSP.** Fusionflow's stock ledger, or PI's "stock position" reported to Lodestone? | Open (owner, 2026-11-02) |
| 5 | **SIM stock and logistics.** Does PI own vendor orders, the receipt of output files, and warehouse and retail stock, or only the deployed estate? | Open (owner, 2026-11-02) |
| 6 | **Number and resource inventory.** Who allocates MSISDN ranges and EID pools, and enforces that nothing is issued twice? The range allocator covers free IMSI and ICCID ranges only. | Open (the landscape's "possible gaps") |
| 7 | **The CPE fleet.** Cyanshyft and Velafabric both speak TR-069 to the same devices: by design, by device class or market, or by accident? | Open (the landscape) |
| 8 | **The SIM OTA send path.** Skyconnect and Velafabric both send binary messages, because one was pivoted out of the other. Does Skyconnect keep its own path, or delegate to Velafabric? | Open (the landscape) |
| 9 | **A device-change loop.** PI can see an IMEI change, but nothing in the landscape acts on it: an eSIM offer, an APN profile re-push, a possible-SIM-swap-fraud flag. QuadraSense's risk signals are the nearest thing built, and they run daily. | Open (the landscape) |
| 10 | **A CPE that carries a SIM.** It is a TR-069 subject, an OTA subject and possibly an RSP subject at once, and PI would have to reconcile its serial number with an IMEI and an ICCID. | Open (the landscape) |
| 11 | **Lodestone's event backbone.** Is it Lodestone itself, or a separate broker? This decides how lifecycle events reach PI and BI. | Open (the landscape) |
| 12 | **Outside PI.** The landscape flags further gaps: a GSMA TS.43 entitlement server, SM-DS discovery, CDR mediation and charging, and whether Pardis also does online charging. | Open (the landscape) |

## 6. What is published here, and what is not

**Published:** each system's role and its interface to QuadraSense.

**Not published:**
- the landscape's security internals: key custody, where the hardware security modules sit, the
  routes that key material and transport keys take;
- the open security questions.

The repository is public, and this split is the owner's decision of 2026-11-02. The landscape itself
(the HTML, all nine container views and the full interface inventory) and a summary of those
internals are kept on the owner's machine, in the git-ignored `.private/` folder:
`sim-estate-landscape.html` and `estate-landscape-security.md`. Nothing from them is to be copied
into the repository beyond what this page already says.

When the landscape changes, update this page: its roles, its statuses and the gap table in
section 4.

## Terms

| Term | Meaning |
|---|---|
| ACS | auto configuration server (TR-069 / TR-369): here, Cyanshyft |
| AOTA | the SIM OTA platform: here, Skyconnect |
| EID | the identifier of an eUICC, the chip itself. Each profile on it has its own ICCID |
| ES2+ | the GSMA interface between an operator and an SM-DP+ |
| eUICC | an embedded SIM, which takes downloaded profiles |
| IEH, PDC, AKG, UPE | integration and exposure hub (Lodestone), policy decision center (Tessera), advanced key guardian (Secureseed), unified protocol engine (Velafabric) |
| IMEI-SV | the handset identity plus its software version, as carried on S6a |
| PI | Primary Inventory: QuadraSense |
| RSP | remote SIM provisioning: SGP.02 (M2M, pushed), SGP.22 (consumer, pulled by the LPA), SGP.32 (IoT, driven by an eIM) |
| SDP | service delivery platform: here, Pardis |
