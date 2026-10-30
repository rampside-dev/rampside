# MSFS 2024: spawnable native ground vehicles

Read out on 2026-10-30 with `vehicles` (SimConnect `EnumerateSimObjectsAndLiveries`, type `GROUND`) on a
MSFS 2024 installation with many add-ons. Spawned with `SimConnect_AICreateSimulatedObject_EX1` and the
title from the first column (`spawn <title> <fwd> <right> [hdg]` in the console).

**Descriptions are derived from the titles** – the definitions of the base content are streamed by MSFS
and not readable on disk. Anything marked with `?` is a guess and still needs checking in the sim.
Tested so far (spawns correctly): `ASO_Catering_Truck_01`, `Aso_Baggage_Loader_01`, `ASO_Pushback_White`.

Out of 35,224 titles, about 450 are native. Everything else comes from add-ons (see the end).

**People (ground crew, pedestrians):** SimConnect has no SimObject type for people – the types are
`ALL`, `AIRCRAFT`, `HELICOPTER`, `BOAT`, `GROUND`, `HOT_AIR_BALLOON`, `ANIMAL`, `USER_AVATAR`. Not checked yet
in the sim. `people` in the console lists every `ANIMAL` title plus all `ALL` titles that sound like a person
(worker, crew, marshal, pedestrian, …); `simobjects <type> [filter]` lists any one type. If people turn up,
they spawn with `spawn <title> …` like a vehicle. Walking would be done by moving them each frame
(as with the tug); whether a walk animation plays then is open.

## 1. Ground handling – Asobo set (`ASO_*`)

The vehicles MSFS 2024 itself uses for its airport services. Best first choice for Rampside.

| Title | Description |
|---|---|
| `ASO_Pushback_White`, `ASO_Pushback_Blue` | Pushback tug (towbar tractor) ✔ tested |
| `ASO_Tug01_White`, `ASO_Tug02_White` | Tractor / tug (baggage tractor, `?` possibly also aircraft tug) |
| `ASO_BaggageTruck01` | Baggage tractor (`?` pulls the baggage carts) |
| `ASO_Baggage_Cart01`, `ASO_Baggage_Cart02` | Baggage cart / dolly |
| `Aso_Baggage_Loader_01` | Belt loader ✔ tested |
| `ASO_LoaderCab_White`, `ASO_LoaderCab_Red` | `?` Loader with cab (container / high loader) |
| `ASO_Catering_Truck_01` | Catering truck with lift box ✔ tested |
| `ASO_Boarding_Stairs`, `_Red`, `_Yellow` | Mobile passenger stairs |
| `ASO_Shuttle_01`, `_Gray`, `_Yellow` | Apron bus |
| `ASO_Operation_Truck_White`, `_Yellow` | Operations vehicle (`?` follow-me / ramp agent) |
| `ASO_Aircraft_Caddy` | `?` Small electric cart (caddy) |
| `ASO_CarFacillity01` (+ `_Black`, `_Blue`, `_White`) | Airport service car |
| `ASO_CarUtility01` | Utility vehicle / pickup |
| `ASO_TruckFacility01_White`, `_Black`, `_Yellow` | Service / maintenance truck |
| `ASO_TruckUtility01` | Utility truck |
| `ASO_Firetruck02` | Airport fire engine |

## 2. Ground handling – Microsoft set (`Microsoft_*` + old generic names)

Base models with paint variants. The generic names in the right column (`Truck Fuel Short Europe` …)
are presumably the older titles of the same models (FS2020 compatibility) – `?`, not compared yet.

| Title (+ variants) | Generic name | Description |
|---|---|---|
| `Microsoft_Pushback_02` (`_blue`, `_green`, `_orange`) | `Pushback 02` | Pushback tug |
| `Microsoft_Pushback_03` (`_Blue`, `_Green`, `_Orange`) | `Pushback 03` | Pushback tug (second model) |
| – | `Car Ground Power Unit` | Ground power unit (GPU) |
| `Microsoft_Truck_EUR_Fuel_Short` (`_Blue`, `_Green`) | `Truck Fuel Short Europe` | Short fuel truck (EU) |
| `Microsoft_Truck_NA_Fuel_Short` (`_Blue`, `_Orange`) | `Truck Fuel Short NorthAm` | Short fuel truck (US) |
| `Microsoft_Truck_Fuel_Long_02_Blue`, `_White`, `_Yellow` | `Truck Fuel Long 02`, `Fuel Truck Long 01`, `Fuel Truck Long 02` | Large fuel truck / tanker |
| `Microsoft_Truck_EUR_Boarding` (`_Blue`, `_Purple`) | `Truck Boarding Europe` | Stairs truck (EU) |
| `Microsoft_Truck_NA_Boarding` (`_Gray`) | `Truck Boarding NorthAm` | Stairs truck (US) |
| `Microsoft_Loader_Ramp_02` (`_Blue`, `_Red`, `_Yellow`) | `Loader Ramp 02` | `?` Belt loader / ramp loader |
| `Microsoft_Loader_Elevator_01`, `_02` | `Loader Elevator`, `Loader Elevator 02` | High loader for containers / pallets |
| `Microsoft_Truck_Container` (`_Blue`, `_Gray`, `_Red`) | `Truck Container` | Container truck |
| `Microsoft_Forklift_Large` (`_Blue`, `_Yellow`) | `Forklift Large` | Large forklift |
| `Microsoft_Forklift_Medium` (`_Blue`, `_Red`) | `Forklift Medium` | Forklift |
| `Microsoft_Truck_Lavatory` (`_Blue`, `_Red`, `_Yellow`) | – | Lavatory service truck |
| `Microsoft_Van_NA_Lavatory` (`_Blue`) | `Van Lavatory NorthAm` | Lavatory van |
| `Microsoft_Truck_Water_02` (`_Blue`, `_Red`, `_Yellow`) | `Truck Water 02` | Potable water truck |
| `Microsoft_Truck_Water_03` | `Truck Water 03` | Potable water truck (second model) |
| `Microsoft_Truck_DeIce_Large` (`_Blue`, `_Green`, `_Orange`, `_Yellow`) | `Truck Deicing Large` | Large de-icing truck |
| `Microsoft_Truck_EUR_DeIce_Small` (`_Blue`, `_Gray`, `_Orange`, `_Yellow`) | `Truck Deicing Small Europe` | Small de-icing truck (EU) |
| `Microsoft_Truck_NA_DeIce_Small` (`_Blue`, `_Green`, `_Yellow`) | `Truck Deicing Small NorthAm` | Small de-icing truck (US) |
| `Microsoft_Bus_Modern` (`_Black`, `_Brown`, `_Gray`, `_Red`) | `Bus`, `Bus Apron 02` (`?`) | Modern bus / apron bus |
| `Microsoft_Truck_Fire_Medium_Green`, `_Red` | `Truck Fire Airport Medium` | Airport fire engine |
| `Microsoft_Truck_Fire_Long` | `Truck Fire Long 02` | Long fire engine |
| `Microsoft_Truck_Fire_Short_02` | `Truck Fire Short`, `Truck Fire Short 02` | Short fire engine |
| – | `Dreamlifter Utility 01`, `02` | `?` Special vehicle for the Boeing Dreamlifter |

## 3. Other vehicles (road, farming, construction, special)

Not ground handling, but spawnable the same way (e.g. for a livelier apron or staff parking).

| Title (+ variants) | Generic name | Description |
|---|---|---|
| `Microsoft_Car_EUR_01` (`_black`) … `_04` (with colours) | `Car Europe 03`, `Car Europe 04`, `Car Europe Vintage` | Cars (EU) |
| `Microsoft_Car_NA_02` (`_Black`, `_Copper`, `_Red`) | `Car NorthAm 02` | Car (US) |
| `Microsoft_Car_JPN_01` (`_Green`, `_Pink`) | `Car Japan 01` | Car (Japan) |
| `Microsoft_MiniCar_01` (7 colours) | `Car Mini` | Small car |
| – | `Car Limousine Modular` | Limousine |
| `Microsoft_SportCar_01`, `Microsoft_SportsCar_*` (Convertible, Sharp, Smooth, Vintage + colours) | `SportCar Sharp`, `Sports Car British`, `Sports Car Smooth`, `Sports Car Vintage`, `SportsCar Convertible` | Sports cars |
| `Microsoft_SUV_NA_01` (`_Black`, `_Chrome`, `_Orange`), `Microsoft_SUV_NA_Vintage` (+ colours) | `SUV_NorthAm`, `SUV NorthAm 02`, `SUV NorthAm Vintage` | SUVs |
| – | `Pickup 01` | Pickup |
| `Microsoft_Van_EUR` (+ colours), `Microsoft_Van_NA_Modern` (`_Green`), `Microsoft_Van_ASIA_02` (+ colours) | `Van Europe`, `Van NorthAm`, `Van Asia *` (Medic, Passenger, Vintage) | Vans, some as ambulances |
| `Microsoft_MiniBus_ASIA_01` (`_Red`), `Microsoft_MiniBus_IND` | `MiniBus Asia`, `Minibus Asia Vintage`, `MiniBus India`, `Minibus NorthAm` | Minibuses |
| `Microsoft_Bus_EUR_Vintage` (…), `Microsoft_Bus_IND` (…), `Microsoft_Bus_NA_Vintage` (`_02`) | `Bus Vintage Europe`, `Bus India`, `Bus NorthAm Vintage` (`02`) | Buses (regional / vintage) |
| `Microsoft_Truck_NA_*` (Black, Blue, Green, Red, White, Vintage_01/02 + colours), `Microsoft_EUR_Truck` (`_Blue`, `_Orange`), `Microsoft_Truck_EUR_Vintage` (…) | `Truck NorthAm`, `Truck Europe`, `Truck Europe Vintage`, `Truck Large *` | Trucks |
| `Microsoft_Truck_EUR_Utility_Vintage` (…), `Microsoft_Truck_JPN_Utility` (…) | `Truck Utility *` (incl. Medic), `Truck utility Japan` | Utility trucks, some medical |
| – | `Car Bush Firefighting`, `Car Bush Medic`, `Car Bush Passengers`, `Car Bush Utility` | Off-road vehicles for bush strips |
| `Microsoft_Truck_Military_01` (`_Green`, `_Tan`), `Microsoft_Car_Military` (`_Camo`, `_Tan`) | `Truck Military` (`Cover`, `No Cover`), `Car_Military` | Military vehicles |
| `Microsoft_Truck_Crane_Small` (…) | `Truck Crane Small` | Small truck crane |
| `Microsoft_Bulldozer` | `Bulldozer` | Bulldozer |
| `Microsoft_Tractor` (`_Red`) | `Tractor` | `?` Farm tractor (not an aircraft tug) |
| `Microsoft_Harvester_Green`, `_Red`, `_Yellow` | `Harvester` | Combine harvester |
| `Microsoft_Aerial_Tank` (+ colours), `ASOBO AerialApp Platform Tank` | `AerialApp TankAndCrane` | Tanks / crane for crop-dusting and firefighting aircraft |
| `Microsoft_Helicopter_Aerial` (+ variants) | – | `?` Support vehicle for aerial-work helicopters |
| `Microsoft_Motorbike_01` … `_03` (+ colours) | `Motorbike`, `Motorbike_02`, `Motorbike_03` | Motorbikes |
| `Microsoft_Quad` (`_Green`, `_Orange`) | `Quad` | Quad |
| – | `Skyship Mast Truck` | Mooring-mast truck for the airship |
| – | `WRIGHT_CATAPULT` | Launch catapult for the Wright Flyer |

## 4. Origin unclear

Not found on disk and not obviously Asobo – possibly native (streamed), possibly from a Marketplace add-on.

| Title | Description |
|---|---|
| `GroundVehicles_Catering_Truck`, `_GPU`, `_GPUTruck`, `_LuggageCart`, `_Pushback`, `_Stairs`, `_Tractor_1_White`, `_Tractor_1_Yellow`, `_Tractor_2` | Complete ground handling set (catering, GPU, baggage carts, pushback, stairs, tractors) – check this |
| `FSR_Electric_Tug`, `FSR_Vehicle_Sprinter_Van` | Electric tug, Sprinter van |
| `cobus_js`, `cobus_new_js`, `fire_js`, `fuelcar` | Cobus apron bus, fire engine, fuel vehicle |
| `BER_Simobject_VH_PolizeiPassat_01`, `_PolizeiT6_01`, `_Sedan_01` | Berlin: police cars, sedan |

## 5. Add-ons (not native, left out)

They exist only when the add-on is installed, so Rampside must not rely on them:

- **GSX** (`FSDT*`): 34,582 titles
- **PMDG** 737 and 777 (`PMDG_*`, `PMDG777_*`): 62 titles, full GSE fleets of their own (stairs, GPU, ASU, catering, lavatory, water)
- Airport add-ons: FlyTampa Amsterdam, Aerosoft EDDF/EDDL/EDDN/ENGM/LDZA, MXI LTFJ, SiamFlight VTBS/VTSM,
  plus `MK_LIRF_*`, `EDDB_LS_*`, `noolaero ESSA *` (not in Community, presumably Marketplace)
- Aircraft: iniBuilds A350 (3), Fenix A32x (1)
