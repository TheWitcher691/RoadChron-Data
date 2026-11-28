# RoadChron central price data — source credits and reuse

Active and historical sources are listed below. OpenVan is the sole active European petrol/diesel provider. EU EnergyPrices, UK DESNZ and SSB are historical credits only; their collection is disabled. Prices are for game simulation, not advice or live station quotes. No station-level records, source HTML, locations or customer details are published. All prices in the signed payload use EUR/litre (ETS2), USD/litre (ATS fuel) or the stated currency/unit (AdBlue); exchange rates have EUR as base. No direct DEF observations are supplied. Client computes a labelled simulation index from signed EIA diesel data; this is not an EIA DEF series. Missing AdBlue observations use a labelled model.

- U.S. Energy Information Administration (EIA), Weekly Retail Gasoline and Diesel Prices, https://www.eia.gov/dnav/pet/pet_pri_gnd_a_epmr_pte_dpgal_w.htm and the official four EPMR/EPD2D/EPMM/EPMP weekly XLS downloads. Reuse: https://www.eia.gov/about/copyrights_reuse.php. RoadChron converts US gallons to litres and derives PADD estimates for unsurveyed states, with national fallback if necessary. Alaska and Hawaii use the West Coast proxy; EIA diesel sampling is for the contiguous United States. No EIA endorsement.
- **OpenVan.camp** — https://openvan.camp/ · https://openvan.camp/en/developers · CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. Thank you for making country-average fuel prices freely available to community developers! RoadChron reads only national diesel and petrol averages, converts per-grade currencies and units to EUR/litre using OpenVan exchange rates, selects supported game countries and adapts values for simulation. Provider fetch date is not independently verified observation date. No station records or price history imported. No affiliation or endorsement.
- Historical only: EU EnergyPrices, Petrol & diesel pump prices, https://www.euenergyprices.eu/en/data, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. Original sources: European Commission Weekly Oil Bulletin https://energy.ec.europa.eu/data-and-analysis/weekly-oil-bulletin_en; SSB https://www.ssb.no/; DESNZ https://www.gov.uk/government/organisations/department-for-energy-security-and-net-zero; BFS https://www.bfs.admin.ch/. RoadChron extracts the latest valid country observations, normalises units and retains dates. No endorsement.
- Historical only: UK Department for Energy Security and Net Zero, Weekly road fuel prices, https://www.gov.uk/government/statistics/weekly-road-fuel-prices. Contains public sector information licensed under the Open Government Licence v3.0 https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/. RoadChron converts pence/litre to EUR/litre using ECB rates.
- Historical only: Statistics Norway (SSB), table 09654, https://www.ssb.no/en/statbank/table/09654. CC BY 4.0 https://www.ssb.no/en/diverse/lisens. Monthly observation, converted from NOK/litre using ECB rates; not a weekly price.
- MITECO (Spain), public fuel station data https://datos.gob.es/en/catalogo/e05068001-instalaciones-de-suministro-de-combustibles-a-vehiculos-con-venta-publica. Terms/dataset attribution: https://www.datosabiertos.miteco.gob.es/es/aviso-legal.html. RoadChron calculates a median from unique public-sale pump observations through the official filtered endpoint https://energia.serviciosmin.gob.es/ServiciosRestCarburantes/PreciosCarburantes/EstacionesTerrestres/FiltroProducto/26. No individual station rows are published.
- PretCarburant.ro (Romania), public REST station sample https://pretcarburant.ro/api/v1/statii?tip=adblue, documentation https://pretcarburant.ro/api, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. Attribution: PretCarburant.ro / Andrei-Șerban Stoian. RoadChron computes an unweighted arithmetic mean of at least three unique, dated, non-expired, non-flagged station observations in RON/litre from the free sample (max 150), not a nationally representative survey. Only Romania is approved. No station rows published. One scheduled call per weekly workflow; manual runs and reruns skip it. No endorsement.
- European Central Bank, euro foreign exchange reference rates https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html. Reuse terms: https://www.ecb.europa.eu/services/using-our-site/disclaimer/html/index.en.html. Rates reproduced accurately, EUR base, informational use.

- Frankfurter v2, https://frankfurter.dev/ (https://frankfurter.dev/license/; FAQ https://frankfurter.dev/#faq): supplementary blended EUR reference rates for ALL, BAM, MKD, RSD, RUB only. The API FAQ expressly permits use and storage of its own blended rates, including products/exports. Individual provider rates are not retrieved or republished. SupplementDates records the original date of each rate; missing/stale rates are not invented. ECB remains the primary source.

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

AdBlue: median of fresh measured country anchors normalised by assumed gross-price factors (0.85–1.30); current catalogues are a fallback, then cached valid anchors up to 30 days. With fewer than three anchors the model blends the median toward the 0.95 EUR/l scenario baseline. Measured country observations take precedence; catalogue prices are labelled separately. No extra VAT added to gross quotes. Country factors are scenario settings without independent enough samples for calibration. DEF: existing signed EIA USD/l diesel converted to USD/US gal only for an indirect proxy: scenario base 3.50 USD/gal, at diesel reference 4 USD/gal, with assumed 10% absolute pass-through and 15% regional offset bounded to +/-0.45 USD/gal. Median measured DEF takes priority as the national anchor when available. These coefficients are not empirical calibrations. Existing valid local DEF observations override the estimate. Models are calculated client-side from the signed feed (no new user connections), and labelled index in local JSON and Tools. Legacy plugin bridge retains existing estimate tokens for compatibility.

## US Gas Gauge (ATS)

Thanks to [US Gas Gauge](https://usgasgauge.com/gas-price-data) for free open CSVs ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)). Regular state prices include EIA observations and provider estimates; diesel national and California use EIA-derived CSV values. Other diesel regions and grades use EIA directly. RoadChron converts units and adapts regional values for simulation. A row tagged US Gas Gauge can include EIA grades. No affiliation or endorsement.

Final cleanup: deprecated fuel parsers removed. Historical source identifiers are accepted solely for verification of pre-migration signed datasets, not for new collection or central publication. Estimated inverted grades are repaired using EIA spreads only; no separate new economic model.
