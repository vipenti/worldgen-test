using System.IO;
using UnityEngine;

public static class PathSettings
{
    public static string GetJsonFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "Json");
#else
        return Path.Combine(Application.persistentDataPath, "Json");
#endif
    }

    public static string GetTextureFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "GeneratedTexture");
#else
        return Path.Combine(Application.persistentDataPath, "GeneratedTexture");
#endif
    }

    public static string GetObjectsFolder()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "Json", "objects");
#else
        return Path.Combine(Application.persistentDataPath, "Json", "objects");
#endif
    }
}
