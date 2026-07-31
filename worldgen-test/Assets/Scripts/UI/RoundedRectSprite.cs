using UnityEngine;

// Generates a simple rounded-rectangle Sprite procedurally at runtime, so UI panels can have
// soft corners without depending on Unity's built-in editor UI sprites (which aren't reliably
// reachable via Resources.GetBuiltinResource across Unity versions) or any imported art asset.
public static class RoundedRectSprite
{
    public static Sprite Create(int size = 32, int radius = 10)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float alpha = RoundedMaskAlpha(x, y, size, radius);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, false);

        return Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
    }

    static float RoundedMaskAlpha(int x, int y, int size, int radius)
    {
        float px = x + 0.5f, py = y + 0.5f;
        float nearestX = Mathf.Clamp(px, radius, size - radius);
        float nearestY = Mathf.Clamp(py, radius, size - radius);
        float dx = px - nearestX, dy = py - nearestY;
        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        return Mathf.Clamp01(radius - dist + 0.5f);
    }
}
