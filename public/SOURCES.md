# RoadChron central price data — source credits and reuse

Only the following sources are enabled by the collector. Prices are for game simulation, not advice or live station quotes. No station-level records, source HTML, locations or customer details are published. All prices in the signed payload use EUR/litre (ETS2), USD/litre (ATS fuel) or the stated currency/unit (AdBlue); exchange rates have EUR as base. No direct DEF observations are supplied. Client computes a labelled simulation index from signed EIA diesel data; this is not an EIA DEF series. Missing AdBlue observations use a labelled model.

- U.S. Energy Information Administration (EIA), Weekly Retail Gasoline and Diesel Prices, https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epmr_pte_dpgal_w.htm and the EPD2D/EPMM/EPMP weekly tables. Reuse: https://www.eia.gov/about/copyrights_reuse.php. RoadChron converts US gallons to litres and derives regional/national estimates for unsurveyed states (including Alaska/Hawaii; not local observations). No EIA endorsement.
- EU EnergyPrices, Petrol & diesel pump prices, https://www.euenergyprices.eu/en/data, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. Original sources: European Commission Weekly Oil Bulletin https://energy.ec.europa.eu/data-and-analysis/weekly-oil-bulletin_en; SSB https://www.ssb.no/; DESNZ https://www.gov.uk/government/organisations/department-for-energy-security-and-net-zero; BFS https://www.bfs.admin.ch/. RoadChron extracts the latest valid country observations, normalises units and retains dates. No endorsement.
- UK Department for Energy Security and Net Zero, Weekly road fuel prices, https://www.gov.uk/government/statistics/weekly-road-fuel-prices. Contains public sector information licensed under the Open Government Licence v3.0 https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/. RoadChron converts pence/litre to EUR/litre using ECB rates.
- Statistics Norway (SSB), table 09654, https://www.ssb.no/en/statbank/table/09654. CC BY 4.0 https://www.ssb.no/en/diverse/lisens. Monthly observation, converted from NOK/litre using ECB rates; not a weekly price.
- MITECO (Spain), public fuel station data https://datos.gob.es/en/catalogo/e05068001-instalaciones-de-suministro-de-combustibles-a-vehiculos-con-venta-publica. Terms/dataset attribution: https://www.datosabiertos.miteco.gob.es/es/aviso-legal.html. RoadChron calculates a simple arithmetic mean of valid public-sale AdBlue prices; no individual station rows published.
- PretCarburant.ro (Romania), public REST station sample https://pretcarburant.ro/api/v1/statii?tip=adblue, documentation https://pretcarburant.ro/api, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. Attribution: PretCarburant.ro / Andrei-Șerban Stoian. RoadChron computes an unweighted arithmetic mean of at least three unique, dated, non-expired, non-flagged station observations in RON/litre from the free sample (max 150), not a nationally representative survey. Only Romania is approved. No station rows published. One scheduled call per weekly workflow; manual runs and reruns skip it. No endorsement.
- European Central Bank, euro foreign exchange reference rates https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html. Reuse terms: https://www.ecb.europa.eu/services/using-our-site/disclaimer/html/index.en.html. Rates reproduced accurately, EUR base, informational use.

Legacy AAA/OPIS, AMZS, TA/Petro, Pilot, Aral and other operator quotes are excluded from central collection/publication. They remain recognised as local cache provenance only. Third-party data rights remain with the respective owners; this notice does not grant any extra rights. Source code licensing is separate from the dataset licenses.

## AdBlue candidates excluded from the publisher

Except the explicit VATservice opt-in below, the collector does not request or publish these candidates. Free access alone is not a redistribution license.

| Source | Area | Status |
|---|---|---|
| Nakordoni | Europe | API terms require separate permission for public redistribution |
| VATservice | Europe | Adapter implemented; disabled by default; requires written permission from relevant rights holders |
| Circle K / AS24 | Europe | Operator prices; no verified redistribution permission |
| Repostamos | Spain | Use original MITECO instead |
| OIL! / FuelPrices.dk | Denmark | No verified redistribution permission |
| FuelMaps | Spain, France, Portugal | Free trial/access does not establish redistribution rights |
| MapaPump / Palivko | Czechia | Public API or reuse permission not established |
| Fuelis | Lithuania | Upstream price-data reuse permission not established |
| TIR Petroleum | Slovakia | Operator price list, no verified reuse permission |
| Fuel Media Service | Belgium, Luxembourg | Free access and reuse not established |
| Prix-Carburants / Tankerkönig / DGEG / MIMIT / goriva.si / MINGO | France, Germany, Portugal, Italy, Slovenia, Croatia | No verified open AdBlue pump-price feed |
| Aral, OK, DS Paliwa, HAS, Zdar, Nates, Hoffelner, AVIA | Legacy | Local provenance only; no central collection |

This list is a candidate register, not an implementation or claim of coverage.

## Optional VATservice adapter

Source: https://vatservice.eu/index.php — Circle K/other network catalogue prices including tax, not representative country averages. No publication date is present in the individual responses. Six exact HTTPS endpoints, one request each per eligible collection, no station data. Disabled unless owner sets ROADCHRON_VATSERVICE_APPROVED=true after securing collection and redistribution/derived-use rights from the relevant rights holders. If permission is withdrawn, remove the variable, remove old publicly deployed copies where possible, and republish without these observations. Sweden may return HTTP 500; that is a missing observation, not a zero price.

## RoadChron simulation models

AdBlue: median of fresh country anchors divided by explicitly assumed gross-price group factors (0.85–1.30); otherwise cached valid anchors up to 30 days; otherwise 0.95 EUR/l scenario. Measured country observations take precedence; catalogue prices are labelled separately. No extra VAT added to gross quotes. DEF: existing signed EIA USD/l diesel converted to USD/US gal only for an indirect proxy: scenario base 3.50 USD/gal, at diesel reference 4 USD/gal, with assumed 20% absolute pass-through and 25% regional offset bounded to +/-0.75 USD/gal. These coefficients are not empirical calibrations. Existing valid local DEF observations override the estimate. Models are calculated client-side from the signed feed (no new user connections), and labelled index in local JSON and Tools. Legacy plugin bridge retains existing estimate tokens for compatibility.
