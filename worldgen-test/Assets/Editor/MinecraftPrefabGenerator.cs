using UnityEngine;
using UnityEditor;
using System.IO;

public class MinecraftPrefabGenerator : EditorWindow
{
    [MenuItem("Tools/Genera Prefab Minecraft")]
    public static void CreatePrefabs()
    {
        string[] rawTokens = {
    // --- I TUOI TOKEN SPECIFICI (Merge) ---
    "minecraft:terracotta", "minecraft:brown_terracotta", "minecraft:white_terracotta", 
    "minecraft:light_gray_terracotta", "minecraft:orange_terracotta", "minecraft:yellow_terracotta",
    "minecraft:coarse_dirt", "minecraft:grass_block", "minecraft:dirt", "minecraft:stone",
    "minecraft:oak_log", "minecraft:oak_leaves", "minecraft:dead_bush", "minecraft:grass",
    "minecraft:netherrack", "minecraft:magma_block", "minecraft:stone_slab", "minecraft:stone_bricks",
    "minecraft:cracked_stone_bricks", "minecraft:stone_brick_slab", "minecraft:mossy_stone_brick_slab",
    "minecraft:stone_brick_stairs", "minecraft:mossy_stone_bricks", "minecraft:obsidian", 
    "minecraft:crying_obsidian", "minecraft:air", "minecraft:cave_air",

    // --- TERRENO E ROCCE ESSENZIALI ---
    "minecraft:sand", "minecraft:red_sand", "minecraft:gravel", "minecraft:water", "minecraft:lava",
    "minecraft:bedrock", "minecraft:andesite", "minecraft:diorite", "minecraft:granite",
    "minecraft:sandstone", "minecraft:red_sandstone", "minecraft:clay", "minecraft:snow_block",
    "minecraft:ice", "minecraft:blue_ice", "minecraft:packed_ice", "minecraft:podzol", "minecraft:mycelium",

    // --- NETHER UPDATE (1.16 Specifici) ---
    "minecraft:blackstone", "minecraft:polished_blackstone", "minecraft:polished_blackstone_bricks",
    "minecraft:basalt", "minecraft:polished_basalt", "minecraft:soul_sand", "minecraft:soul_soil",
    "minecraft:crimson_nylium", "minecraft:warped_nylium", "minecraft:crimson_stem", "minecraft:warped_stem",
    "minecraft:shroomlight", "minecraft:ancient_debris", "minecraft:nether_gold_ore", "minecraft:nether_quartz_ore",
    "minecraft:glowstone", "minecraft:soul_lantern", "minecraft:soul_fire",

    // --- MINERALI (ORES) ---
    "minecraft:coal_ore", "minecraft:iron_ore", "minecraft:gold_ore", "minecraft:redstone_ore",
    "minecraft:lapis_ore", "minecraft:diamond_ore", "minecraft:emerald_ore", "minecraft:netherite_block",

    // --- VEGETAZIONE E LEGNO ---
    "minecraft:spruce_log", "minecraft:birch_log", "minecraft:jungle_log", "minecraft:acacia_log", "minecraft:dark_oak_log",
    "minecraft:spruce_leaves", "minecraft:birch_leaves", "minecraft:jungle_leaves", "minecraft:acacia_leaves", "minecraft:dark_oak_leaves",
    "minecraft:pumpkin", "minecraft:melon", "minecraft:brown_mushroom", "minecraft:red_mushroom", "minecraft:lily_pad",

    // --- COSTRUZIONE E STRUTTURE ---
    "minecraft:cobblestone", "minecraft:mossy_cobblestone", "minecraft:bricks", "minecraft:glass",
    "minecraft:oak_planks", "minecraft:spruce_planks", "minecraft:birch_planks", "minecraft:jungle_planks",
    "minecraft:iron_block", "minecraft:gold_block", "minecraft:diamond_block", "minecraft:tnt"
};

        string matFolder = "Assets/Materials";
        string prefabFolder = "Assets/Prefabs";

        // 1. Crea la cartella Prefabs se non esiste
        if (!AssetDatabase.IsValidFolder(prefabFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Prefabs");
            AssetDatabase.CreateFolder("Assets/Prefabs", "Blocks");
        }

        int count = 0;

        foreach (string rawName in rawTokens)
        {
            // 2. Pulizia nome e filtri
            string cleanName = rawName.Replace("minecraft:", "");
            if (cleanName.Contains("air")) continue;

            string prefabPath = $"{prefabFolder}/{cleanName}.prefab";
            string matPath = $"{matFolder}/{cleanName}.mat";

            // 3. Controlla se il prefab esiste già per non sovrascriverlo
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                // Crea un cubo temporaneo nella scena
                GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tempCube.name = cleanName;

                // 4. Carica e applica il materiale creato in precedenza
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null)
                {
                    tempCube.GetComponent<Renderer>().material = mat;
                }
                else
                {
                    Debug.LogWarning($"Material not found for: {cleanName}. The prefab will have the default material.");
                }

                // 5. Salva il cubo come Prefab e rimuovilo dalla scena
                PrefabUtility.SaveAsPrefabAsset(tempCube, prefabPath);
                GameObject.DestroyImmediate(tempCube);
                
                count++;
            }
        }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
        
                EditorUtility.DisplayDialog("Prefabs Created", $"{count} new prefabs created in {prefabFolder}", "Great!");
            }
        }