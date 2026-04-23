using UnityEngine;
using UnityEditor;
using System.IO;

public class MinecraftMaterialGenerator : EditorWindow
{
    [MenuItem("Tools/GenerateMaterials")]
    public static void CreateMaterials()
    {
        // La tua lista aggiornata dei blocchi
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

        string folderPath = "Assets/Materials";

        // 1. Creazione cartella se mancante
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            AssetDatabase.CreateFolder("Assets", "Materials");
        }

        int count = 0;
        // Troviamo lo shader URP una volta sola per efficienza
        Shader urpLitShader = Shader.Find("Universal Render Pipeline/Lit");

        if (urpLitShader == null)
        {
            Debug.LogError("Errore: Shader URP Lit non trovato! Assicurati che il progetto sia configurato per URP.");
            return;
        }

        foreach (string rawName in rawTokens)
        {
            // 2. Pulizia nome
            string cleanName = rawName.Replace("minecraft:", "");
            
            // Saltiamo i blocchi d'aria
            if (cleanName.Contains("air")) continue;

            string assetPath = $"{folderPath}/{cleanName}.mat";

            // 3. Creazione se non esiste
            if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) == null)
            {
                Material mat = new Material(urpLitShader);
                
                // Opzionale: impostiamo il materiale come opaco di default
                mat.SetFloat("_Surface", 0); // 0 = Opaque, 1 = Transparent
                
                AssetDatabase.CreateAsset(mat, assetPath);
                count++;
            }
        }

        // 4. Salvataggio
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("URP Materials", $"{count} URP Lit materials created successfully!", "Ottimo");
    }
}