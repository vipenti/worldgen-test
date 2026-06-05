using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class BillboardPlaceholder : MonoBehaviour
{
    public string objectName;

    private MeshRenderer _renderer;
    private Transform    _cam;

    void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
        var shader = Shader.Find("Unlit/Transparent");
        if (shader != null)
            _renderer.material = new Material(shader);
    }

    void Start() => _cam = Camera.main?.transform;

    void Update()
    {
        if (_cam != null)
            transform.rotation = _cam.rotation;
    }

    public void SetTexture(Texture2D tex)
    {
        if (_renderer != null)
            _renderer.material.mainTexture = tex;
    }
}
