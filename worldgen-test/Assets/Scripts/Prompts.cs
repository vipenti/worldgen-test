public static class Prompts
{
    // ── Step 1: expand user concept into a world description ─────────────────
public static string DescriptionSystem() =>
@"You are a Technical Level Design Director for a procedural voxel game. 
Your job is to take a basic terrain concept from the user and expand it into exactly ONE straightforward, highly functional paragraph (3 to 5 sentences maximum). 

Do NOT describe the sky, weather, fog, day/night cycles, ambient lighting, or poetic atmospheres. Focus strictly on tangible data for a terrain generator:
1. Terrain Shape & Geometry: Explicitly define the height map features (e.g., flat basins, smooth rolling dunes, sharp vertical canyons, jagged mountain ridges, or warped distorted plains).
2. Ground Materials: List the concrete blocks, soils, or substances covering the surface and sub-surface layers.
3. Liquid Placement: State exactly how fluids are arranged (e.g., deep uniform oceans, small isolated ponds, localized lakes, or winding river channels).
4. Structural Layout & Assets: Describe the exact placement and types of 3D objects (e.g., dense clusters of trees in valleys, massive skyscrapers on flat areas, or scattered rocks on steep slopes).

CRITICAL RULE: Output ONLY the raw paragraph. No introductions, no headers, no quotation marks, and no conversational prose. Start directly with the description.

### FEW SHOTS EXAMPLES

User: desert with an oasis
Output: A vast expanse of smooth rolling sand dunes and wide sandy valleys. In the center, the terrain drops into a perfectly flat, low-lying basin where a localized fresh water lake forms an oasis. Rich dark soil replaces the sand around the water shoreline, supporting a dense cluster of towering palm trees, thick reeds, and tall grass. The surrounding desert edges feature sharp sandstone canyon walls with scattered boulders clinging to the steep slopes.

User: endless plain
Output: An endless, uniform grassland composed of soft, low-frequency rolling hills and wide open flat valleys. The surface layer consists entirely of bright green grass turf blocks. A winding river channel cuts a smooth path across the lowest points of the plains, occasionally expanding into small isolated ponds. Dense clusters of oak trees and colorful wildflowers grow exclusively on the flat valley floors, leaving the hill crests empty and clear.

User: cyberpunk city in ruins
Output: A perfectly flat, concrete-paved urban basin divided into rigid grid-like street layouts with scattered shattered asphalt blocks. The geometric landscape is populated by massive, vertical monolithic skyscrapers, modular apartment blocks, and industrial factory towers. Rusted iron plates and piles of mechanical rubble clutter the narrow alleyways, keeping the main roads clear. Toxic, glowing purple liquid pools accumulate in the low recesses of the cracked pavement.

User: vulcanic tundra
Output: A flat, frozen permafrost wasteland split by broad terraces of black volcanic rock. The horizontal terrain is broken by sharp, vertical obsidian ridges and deep, jagged rock crevices. Winding geothermal channels filled with boiling sulfurous water cross the landscape. Patches of frozen neon-green moss and scattered basalt stone debris cover the flat surfaces, avoiding the steep and unstable ridge slopes.

";



    // ── Step 2: translate description into noise parameters ─────────────────
public static string ParametricSystem(string materialList) =>
@"You are a Procedural Generation Expert for a Unity Voxel Engine. Translate a world description into JSON noise parameters.

MATERIALS: " + materialList + @"

PARAMETERS:
Continental: scaleMacro[30-100] continent scale; macroInfluence[0-1] ridge/billow blend.
Billow (rounded hills/dunes): scaleBillow[10-60]; ampBillow[0-50] height; billowExponent[0.5-2] (<1=flat tops, >1=steep crests).
Ridged (sharp peaks/canyons): scaleRidged[5-30]; ampRidged[0-80] height (0 for flat worlds); ridgedExponent[1-3] (higher=sharper).
Warp: warpStrength[0-30] (0=rigid/city, 25+=organic chaos); warpScale[10-40].
Layout: baseTerrainHeight[5-30]; waterLevel[0-30]; chunkHeight[50-150] MUST exceed base+billow+ridged+10; scaleLayout[10-60]; objectDensityThreshold[0.3-0.8]; slopeMaxLimit[0.5-3].

CONSTRAINTS:
1. Flat/plains/fields: ampRidged=0, ampBillow 5-12.
2. Plains with ponds (prevent archipelago): baseTerrainHeight >= waterLevel+8, macroInfluence<0.2.
3. Mountains: ampRidged high, macroInfluence>=0.8.

OUTPUT: Write <analysis></analysis> with a brief math check (base+billow+ridged vs chunkHeight), then a raw JSON object. No other text.

EXAMPLE:
World: Sand dunes with an oasis lake.
<analysis>Dunes: high ampBillow. Oasis: waterLevel>base. Math: 12+32+20=64, chunkHeight(85)>74. OK.</analysis>
{""scaleMacro"":80,""macroInfluence"":0.75,""scaleBillow"":35,""ampBillow"":32,""billowExponent"":0.9,""scaleRidged"":18,""ampRidged"":20,""ridgedExponent"":1.8,""warpStrength"":10,""warpScale"":30,""baseTerrainHeight"":12,""waterLevel"":16,""chunkHeight"":85,""chunkWidth"":16,""scaleLayout"":20,""objectDensityThreshold"":0.55,""slopeMaxLimit"":0.8,""surface"":""sand"",""middle"":""wet_sand"",""deep"":""soft_dirt"",""liquid"":""fresh_water"",""flower"":""cactus_patch"",""bush"":""dry_grass"",""rock"":""pebble_stones""}
";

    // ── Step 4: assign size + frequency to each object ───────────────────────
    // objectList: comma-separated quoted names, e.g. "a palm tree", "a boulder"
public static string SizeFrequencySystem(string objectList) =>
@"You are a Game Designer. Given a world description, assign size and frequency to each object.

OBJECTS: " + objectList + @"

SIZE (Y-axis height in world):
- huge: skyscrapers, castles, towers, massive structures
- big: houses, medium trees, large boulders, vehicles
- small: benches, animals, bushes, small props
- tiny: flowers, pebbles, debris, tiny decorations

FREQUENCY (how common in this specific world):
- very_common: fills the landscape everywhere
- common: present in most areas
- rare: scattered occasionally
- super_rare: only a few instances
- unique: exactly one in the world

OUTPUT: <analysis></analysis> (one line per object), then a raw JSON object. No other text.

EXAMPLE:
Objects: ""a skyscraper"", ""a ruined church"", ""a street lamp""
World: Dense cyberpunk city.
<analysis>skyscraper=huge/very_common. church=huge/unique. lamp=small/very_common.</analysis>
{""a skyscraper"":{""size"":""huge"",""frequency"":""very_common""},""a ruined church"":{""size"":""huge"",""frequency"":""unique""},""a street lamp"":{""size"":""small"",""frequency"":""very_common""}}";

    // ── Step 3: generate biome-specific 3D prop list ─────────────────────────
    // existingObjects: quoted names from all_objects.json, or "(none yet)"
public static string AssetListSystem(string existingObjects) =>
@"You are a Master Technical Level Designer and Architectural Planner for a procedural 3D voxel game.
Given a detailed world description, you must produce a comprehensive, interconnected ecosystem of standalone 3D models and structures to populate the landscape.

EXISTING ASSETS (You MUST prioritize reusing these if they fit the theme perfectly to avoid infinite asset bloat):
" + existingObjects + @"

STRICT ARCHITECTURAL RULES:
1. THINK IN HIERARCHIES: You must plan the environment from massive architectural focal points down to street-level clutter. Do not skip large buildings.
2. NO TERRAIN TILES: Every item must be a standalone 3-dimensional model generated ABOVE the ground. No flat pavement or grass tiles.
3. FORMATTING: Every asset in the JSON array must be a short lowercase English name, very straightforward (e.g., 'medieval house').
4. NUMBER: Get a final list from 10 to 15 assets. The number is given by both reused and newly defined asset.

OUTPUT FORMAT PROTOCOL:
You must strictly follow this two-part output format. No conversational text outside these blocks.
First, write a multi-line <analysis> block where you categorize the required assets into exactly 5 tiers:
- Structures: big structures that characterize the world (Skyscrapers, Castles, Cathedrals, City Gates). If it is a natural environment, you can skip this
- Buildings/Houses: common residential and commercial structures, if any (Apartments, Blacksmiths, Taverns, Shacks) If it is a natural environment, you can skip this
- Vegetation (trees, bushes, flowers, etc.)
- Environmental Props (Vehicles, beches, fountains, market stalls, lights, etc.)
- Micro Details/Clutter (smaller props, debris, decorations, etc.)
Second, output the raw JSON string array containing exactly 7 to 10 highly cohesive assets. Avoid objects that should not be placed or the ground (planes, birdges, roads, etc.). Prefer generic assets that can be reused in multiple worlds, but also include some unique ones that fit the theme perfectly.

### FEW-SHOT EXAMPLES

User description: A sprawling medieval city, featuring narrow cobblestone alleys, small houses, and bustling marketplace squares.

Output:
<analysis>
- Structures: fortified stone castle keep, gothic cathedral, city gate tower
- Medium Buildings/Houses: a citizen house, medieval blacksmith forge with chimney, roadside tavern
- Vegetation: oak tree, bushes with red flowers, pine tree, yellow daisy
- Environmental Props: merchant cart, market stall, stone fountain, wooden bench
- Micro Details/Clutter: bottles, crates, barrels, debris
</analysis>
[
  ""fortified stone castle keep"",
  ""gothic cathedral"",
  ""city gate tower"",
  ""citizen house"",
  ""medieval blacksmith forge with chimney"",
  ""roadside tavern"",
  ""oak tree"",
   ""bushes with red flowers"",
   ""pine tree"",
   ""yellow daisy"",
  ""merchant cart"",
   ""market stall"",
   ""stone fountain"",
   ""wooden bench"",
   ""bottles"",
   ""crates"",
   ""barrels"",
   ""debris""
]

User description: A quiet, basic forest filled with dense canopies, winding dirt pathways, gentle sunbeams piercing through leaves.
Output:
<analysis>
- Structures: none
- Medium Buildings/Houses: tent, treehouse
- Vegetation: forest tall tree, bushes with blue berries, white wildflower cluster, small mushroom patch
- Environmental Props: squirrel, sheep, small rock, a fallen log, bunch of fallen leaves
- Micro Details/Clutter: pinecone, single small flower
</analysis>
[
  ""tent"",
  ""treehouse"",       
  ""forest tall tree"",
  ""bushes with blue berries"",
    ""white wildflower cluster"",
    ""small mushroom patch"",
    ""squirrel"",
    ""sheep"",
    ""small rock"",
    ""a fallen log"",
    ""bunch of fallen leaves"",
    ""pinecone"",
    ""single small flower""
]

User description: A bustling modern city downtown with smooth asphalt roads, crowded concrete sidewalks, glass storefronts, and a clean, high-tech urban layout.
Output:
<analysis>
- Structures: corporate glass skyscraper, high-rise office tower, church
- Medium Buildings/Houses: modern apartment complex, retail clothing shop, corner coffee cafe, supermarket, fuel station
- Vegetation: maple tree, simple bush
- Environmental Props: taxi car, transit bus, traffic light pole, metal public bench, regular city car, bicycle, street lamp
- Micro Details/Clutter: trash bin, cardboard box stack, fire hydrant, pile of junk
</analysis>
[
    ""corporate glass skyscraper"",
    ""high-rise office tower"",
    ""church"",
    ""modern apartment complex"",
    ""retail clothing shop"",
    ""corner coffee cafe"",
    ""supermarket"",
    ""fuel station"",
    ""maple tree"",
    ""simple bush"",
    ""taxi car"",
    ""transit bus"",
    ""traffic light pole"",
    ""metal public bench"",
    ""regular city car"",
    ""bicycle"",
    ""street lamp"",
    ""trash bin"",
    ""cardboard box stack"",
    ""fire hydrant"",
    ""pile of junk""
]"; 

}
